using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bwt.Contracts;
using Bwt.Gpu;

namespace Bwt.Workloads;

/// <summary>Integer hashes shared by kernels and their CPU oracles (each .cu file carries the same definitions).</summary>
internal static class Hash
{
    public static uint Mix64To32(ulong z) => (uint)BatchRunner.SplitMix64(z);

    /// <summary>Completion tag of block <paramref name="block"/>; salt separates launches of one batch. Salt 0 is C03's tag.
    /// Always odd, so it never equals a zero sentinel.</summary>
    public static uint Tag(uint epoch, ulong nonce, uint block, uint salt)
        => Mix64To32(nonce ^ ((ulong)epoch << 32) ^ (ulong)block * 0x632BE59BD9B4E019UL ^ (ulong)salt * 0xD1B54A32D192ED03UL) | 1u;

    /// <summary>A 32-bit bijection (lowbias32, C. Wellons).</summary>
    public static uint Lowbias32(uint x)
    {
        unchecked
        {
            x ^= x >> 16; x *= 0x21F0AAADu;
            x ^= x >> 15; x *= 0x735A2D97u;
            x ^= x >> 15;
            return x;
        }
    }

    /// <summary>[1, 2) from the top 23 bits of <paramref name="r"/>, built from bits (never from float arithmetic).</summary>
    public static float UnitFloat(uint r) => BitConverter.UInt32BitsToSingle(0x3F800000u | (r >> 9));
}

/// <summary>
/// Event-timed launches on the context's stream with at most <c>depth</c> (2) launches in flight
/// (docs/KERNEL_CONTRACT.md §1): before launch k is submitted, launch k−2 must have completed.
/// </summary>
internal sealed class LaunchClock : IDisposable
{
    private readonly GpuContext _g;
    private readonly nint[] _start, _end;
    private readonly bool[] _pending;
    private int _next;

    public int Count { get; private set; }
    public double TotalMs { get; private set; }
    public double MaxMs { get; private set; }

    public LaunchClock(GpuContext g, int depth = 2)
    {
        _g = g;
        _start = new nint[depth]; _end = new nint[depth]; _pending = new bool[depth];
        for (int i = 0; i < depth; i++) { _start[i] = g.Cuda.EventCreate(); _end[i] = g.Cuda.EventCreate(); }
    }

    public void Reset()
    {
        Drain();
        Count = 0; TotalMs = 0; MaxMs = 0;
    }

    /// <summary>Records start, runs <paramref name="launch"/> (which must return the CUresult of the launch), records end.</summary>
    public void Run(string what, Func<int> launch)
    {
        int i = _next;
        _next = (_next + 1) % _start.Length;
        if (_pending[i]) Collect(i);
        _g.Check("cuEventRecord", _g.Cuda.EventRecordRaw(_start[i], _g.Stream));
        _g.Check(what, launch());
        _g.Check("cuEventRecord", _g.Cuda.EventRecordRaw(_end[i], _g.Stream));
        _pending[i] = true;
        Count++;
    }

    /// <summary>Waits for every launch still in flight and accumulates its GPU time.</summary>
    public void Drain()
    {
        for (int k = 0; k < _start.Length; k++)
        {
            int i = (_next + k) % _start.Length; // oldest first
            if (_pending[i]) Collect(i);
        }
    }

    private void Collect(int i)
    {
        _g.Check("cuEventSynchronize", _g.Cuda.EventSynchronizeRaw(_end[i]));
        double ms = _g.Cuda.EventElapsedMs(_start[i], _end[i]);
        TotalMs += ms;
        MaxMs = Math.Max(MaxMs, ms);
        _pending[i] = false;
    }

    public void Dispose()
    {
        if (_g.Poisoned) return;
        foreach (var e in _start) _g.Cuda.EventDestroy(e);
        foreach (var e in _end) _g.Cuda.EventDestroy(e);
    }
}

/// <summary>
/// Counts of one comparison. Merged from parallel chunks; the reported first failure is the one with the lowest
/// ordering key, so it is deterministic regardless of thread scheduling.
/// </summary>
internal sealed class Tally
{
    public long Checked, Mismatches, NonFinite, Stale;
    public double MaxAbs, MaxRel;
    private long _firstKey = long.MaxValue;
    public string? First { get; private set; }

    public void Fail(long key, string message)
    {
        if (key < _firstKey) { _firstKey = key; First = message; }
    }

    /// <summary>True if a failure at <paramref name="key"/> would become the first failure (format the message only then).</summary>
    public bool Wants(long key) => key < _firstKey;

    public void Merge(Tally o)
    {
        Checked += o.Checked; Mismatches += o.Mismatches; NonFinite += o.NonFinite; Stale += o.Stale;
        MaxAbs = Math.Max(MaxAbs, o.MaxAbs); MaxRel = Math.Max(MaxRel, o.MaxRel);
        if (o.First != null) Fail(o._firstKey, o.First);
    }

    public bool Clean => Mismatches == 0 && NonFinite == 0 && Stale == 0;

    /// <summary>Runs <paramref name="body"/>(begin, end, tally) over [0, n) in parallel chunks and merges the tallies.</summary>
    public static Tally For(long n, Action<long, long, Tally> body, long chunk = 1 << 16)
    {
        var total = new Tally();
        if (n <= 0) return total;
        long chunks = (n + chunk - 1) / chunk;
        if (chunks == 1) { body(0, n, total); return total; }
        object gate = new();
        Parallel.For(0L, chunks, () => new Tally(), (c, _, local) =>
        {
            body(c * chunk, Math.Min(n, (c + 1) * chunk), local);
            return local;
        }, local => { lock (gate) total.Merge(local); });
        return total;
    }

    /// <summary>Checks completion tags: tag[b] must equal <see cref="Hash.Tag"/>(epoch, nonce, b, salt).</summary>
    public void CheckTags(ReadOnlySpan<uint> tags, uint epoch, ulong nonce, uint salt, uint sentinel, long keyBase, string what)
    {
        for (int b = 0; b < tags.Length; b++)
        {
            uint want = Hash.Tag(epoch, nonce, (uint)b, salt);
            Checked++;
            if (tags[b] == want) continue;
            Stale++;
            if (Wants(keyBase + b))
                Fail(keyBase + b, $"{what} tag block={b} expected=0x{want:x8} actual=0x{tags[b]:x8}{(tags[b] == sentinel ? " (never written)" : "")}");
        }
    }
}

/// <summary>
/// A managed array on the pinned object heap, page-locked for the driver with cuMemHostRegister (bound here via
/// <see cref="CudaApi.Sym"/>), so device→host readbacks run as direct DMA while comparators keep using a plain array.
/// Falls back to an ordinary (pageable) copy if registration is unavailable.
/// </summary>
internal sealed unsafe class PinnedArray : IDisposable
{
    private readonly GpuContext _g;
    private readonly delegate* unmanaged[Cdecl]<void*, int> _unregister;
    public uint[] Data { get; }
    public bool Registered { get; private set; }

    public PinnedArray(GpuContext g, long length)
    {
        _g = g;
        Data = GC.AllocateArray<uint>(checked((int)length), pinned: true);
        try
        {
            var register = (delegate* unmanaged[Cdecl]<void*, nuint, uint, int>)g.Cuda.Sym("cuMemHostRegister_v2");
            _unregister = (delegate* unmanaged[Cdecl]<void*, int>)g.Cuda.Sym("cuMemHostUnregister");
            fixed (uint* p = Data) Registered = Data.Length > 0 && register(p, (nuint)((long)Data.Length * 4), 0) == 0;
        }
        catch (EntryPointNotFoundException) { Registered = false; }
    }

    public void Dispose()
    {
        if (Registered && !_g.Poisoned) fixed (uint* p = Data) _unregister(p);
        Registered = false;
    }
}

/// <summary>Small helpers for evidence and per-batch detail text.</summary>
internal static class Evidence
{
    public static Dictionary<string, string> Module(KernelModule mod, params KernelResources[] res)
    {
        var d = new Dictionary<string, string> { ["module"] = mod.Format, ["module_sha256"] = mod.Sha256 };
        if (res.Length == 1)
        {
            d["entry"] = res[0].Entry;
            d["registers"] = res[0].Registers.ToString(CultureInfo.InvariantCulture);
            d["local_bytes"] = res[0].LocalBytes.ToString(CultureInfo.InvariantCulture);
            d["static_shared_bytes"] = res[0].StaticSharedBytes.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            foreach (var r in res)
            {
                d[$"{r.Entry}.registers"] = r.Registers.ToString(CultureInfo.InvariantCulture);
                d[$"{r.Entry}.local_bytes"] = r.LocalBytes.ToString(CultureInfo.InvariantCulture);
                d[$"{r.Entry}.static_shared_bytes"] = r.StaticSharedBytes.ToString(CultureInfo.InvariantCulture);
            }
            d["registers"] = res.Max(r => r.Registers).ToString(CultureInfo.InvariantCulture);
            d["local_bytes"] = res.Max(r => r.LocalBytes).ToString(CultureInfo.InvariantCulture);
        }
        return d;
    }

    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"k=v k=v ..." with invariant formatting.</summary>
    public static string Kv(params (string Key, object? Value)[] items)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in items)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(k).Append('=').Append(v switch
            {
                double d => F(d),
                float f => F(f),
                IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
                null => "null",
                _ => v.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>One untimed batch at the end of Prepare (epoch 0, its own nonce): JIT-compiles and warms the oracle path.
    /// A failed end-to-end self-check rejects preparation rather than letting a later run hide the failure.</summary>
    public static string WarmUp(IWorkload w)
    {
        var r = w.RunBatch(new BatchRequest(0, 0, 0x5EED5EED5EED5EEDUL, 50, FaultKind.None, CancellationToken.None));
        if (!r.Clean)
            throw new WorkloadPreparationException($"prepare_warmup_not_clean: status={r.Status} checked={r.CheckedElements} " +
                $"mismatches={r.Mismatches} non_finite={r.NonFinite} stale_or_missing={r.StaleOrMissing} " +
                $"first_failure={r.FirstFailure ?? "(none)"} detail={r.Detail ?? "(none)"}", r);
        return $"clean (checked {r.CheckedElements})";
    }

    public static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    public static double Param(WorkloadParams p, string key, double dflt)
        => p.Extra.TryGetValue(key, out var s) ? double.Parse(s, CultureInfo.InvariantCulture) : dflt;

    public static long ParamL(WorkloadParams p, string key, long dflt)
        => p.Extra.TryGetValue(key, out var s) ? long.Parse(s, CultureInfo.InvariantCulture) : dflt;

    public static string ParamS(WorkloadParams p, string key, string dflt)
        => p.Extra.TryGetValue(key, out var s) ? s : dflt;

    /// <summary>A BatchResult for a batch that launched nothing yet still read back/checked (fault paths fill in counts).</summary>
    public static BatchResult Result(BatchRequest req, long launches, long work, Tally t, long total, CheckScope scope,
        long submitQpc, long completeQpc, LaunchClock? clock, string detail, bool floatOracle = false)
        => new(
            Status: BatchStatus.Ok, Epoch: req.Epoch, Launches: launches, WorkUnits: work,
            CheckedElements: t.Checked, TotalElements: total, Scope: scope,
            Mismatches: t.Mismatches, NonFinite: t.NonFinite, StaleOrMissing: t.Stale,
            MaxAbsErr: floatOracle ? t.MaxAbs : null, MaxRelErr: floatOracle ? t.MaxRel : null, FirstFailure: t.First,
            HostSubmitQpc: submitQpc, HostCompleteQpc: completeQpc,
            GpuElapsedNs: clock is { Count: > 0 } c ? (long)(c.TotalMs * 1e6) : null,
            MaxLaunchNs: clock is { Count: > 0 } c2 ? (long)(c2.MaxMs * 1e6) : null,
            Detail: detail);
}
