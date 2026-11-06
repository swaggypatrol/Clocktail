using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Bwt.Contracts;
using Bwt.Gpu;

namespace Bwt.Workloads;

/// <summary>
/// The C01 kernel family (kernels/compute/c01_fp32_fma.cu) and its exact CPU oracle. Shared by C01 (throughput) and M02
/// (transitions, which chains launches). Entry <c>c01_fma_a{N}</c> runs N independent accumulators per thread.
/// </summary>
internal sealed unsafe class C01Kernel
{
    public const string ModuleName = "c01_fp32_fma";
    public const string Checkpoint8ModuleName = "c01_fp32_fma_checkpoint8";
    public const int Seg = 32;                 // FMA steps between exponent resets (C01_SEG in the .cu)
    public const uint HashMul = 0x01000193u;
    public static readonly int[] Accumulators = [8, 16, 32];

    public KernelModule Module { get; }
    public nint Fn { get; }
    public int Nacc { get; }
    public int HashInterval { get; }
    public string Entry { get; }

    private C01Kernel(KernelModule mod, int nacc, int hashInterval)
    {
        Module = mod; Nacc = nacc; HashInterval = hashInterval;
        Entry = $"c01_fma_a{nacc}" + (hashInterval == 8 ? "_h8" : ""); Fn = mod.Function(Entry);
    }

    public static void ValidateHashInterval(int hashInterval)
    {
        if (hashInterval is not (32 or 8)) throw new ArgumentException("C01 hash_interval must be 32 (default) or 8");
    }

    public static C01Kernel Load(GpuContext g, int nacc, int hashInterval = 32)
    {
        ValidateHashInterval(hashInterval);
        if (Array.IndexOf(Accumulators, nacc) < 0) throw new ArgumentException($"C01 accumulators must be one of {string.Join("/", Accumulators)}, not {nacc}");
        return new C01Kernel(g.LoadModule(typeof(C01Kernel).Assembly, hashInterval == 8 ? Checkpoint8ModuleName : ModuleName), nacc, hashInterval);
    }

    /// <summary>Launches one grid; returns the CUresult. inF/inH = 0: start from (nonce, class); else continue a chain.</summary>
    public int Launch(GpuContext g, uint grid, uint block, ulong outF, ulong outH, ulong tags, ulong inF, ulong inH,
        ulong nonce, uint epoch, uint segBase, uint segs, uint classes, uint produce, uint salt)
    {
        void** args = stackalloc void*[12];
        args[0] = &outF; args[1] = &outH; args[2] = &tags; args[3] = &inF; args[4] = &inH; args[5] = &nonce;
        args[6] = &epoch; args[7] = &segBase; args[8] = &segs; args[9] = &classes; args[10] = &produce; args[11] = &salt;
        return g.Cuda.LaunchRaw(Fn, grid, block, 0, g.Stream, args);
    }

    // ---- CPU oracle: the kernel's arithmetic, bit for bit ----

    /// <summary>Start state and constants of class <paramref name="k"/> (the kernel's prologue).</summary>
    public static void Init(ulong nonce, uint k, Span<float> x, Span<float> m, Span<float> a, out uint h)
    {
        ulong b = nonce ^ ((ulong)(k + 1) * 0x9E3779B97F4A7C15UL);
        for (int j = 0; j < x.Length; j++)
        {
            ulong s = b ^ ((ulong)(j + 1) * 0xD6E8FEB86659FD93UL);
            m[j] = Hash.UnitFloat(Hash.Mix64To32(s ^ 0x1111111111111111UL));
            a[j] = Hash.UnitFloat(Hash.Mix64To32(s ^ 0x2222222222222222UL));
            x[j] = Hash.UnitFloat(Hash.Mix64To32(s ^ 0x3333333333333333UL));
        }
        h = Hash.Mix64To32(b ^ 0xC01C01C01C01C01CUL);
    }

    public static uint SegKey(uint seg) => unchecked(((seg * 0x9E3779B9u) & 0x007FFFFFu) | 0x3F800000u);

    /// <summary>Plain scalar definition of <c>segs</c> segments (the semantic reference; slow but obviously right).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AdvanceScalar(Span<float> x, ReadOnlySpan<float> m, ReadOnlySpan<float> a, ref uint h, uint segBase, uint segs, int hashInterval = 32)
    {
        ValidateHashInterval(hashInterval);
        if (hashInterval == 8) { AdvanceScalarCheckpoint8(x, m, a, ref h, segBase, segs); return; }
        for (uint s = 0; s < segs; s++)
        {
            for (int i = 0; i < Seg; i++)
                for (int j = 0; j < x.Length; j++)
                    x[j] = MathF.FusedMultiplyAdd(x[j], m[j], a[j]);
            uint key = SegKey(segBase + s);
            for (int j = 0; j < x.Length; j++)
            {
                uint bits = BitConverter.SingleToUInt32Bits(x[j]);
                h = unchecked(h * HashMul + bits);
                x[j] = BitConverter.UInt32BitsToSingle((bits & 0x007FFFFFu) ^ key);
            }
        }
    }

    private static void AdvanceScalarCheckpoint8(Span<float> x, ReadOnlySpan<float> m, ReadOnlySpan<float> a, ref uint h, uint segBase, uint segs)
    {
        for (uint s = 0; s < segs; s++)
        {
            for (int i = 0; i < Seg; i++)
            {
                for (int j = 0; j < x.Length; j++) x[j] = MathF.FusedMultiplyAdd(x[j], m[j], a[j]);
                if ((i & 7) == 7)
                    for (int j = 0; j < x.Length; j++) h = unchecked(h * HashMul + BitConverter.SingleToUInt32Bits(x[j]));
            }
            uint key = SegKey(segBase + s);
            for (int j = 0; j < x.Length; j++)
                x[j] = BitConverter.UInt32BitsToSingle((BitConverter.SingleToUInt32Bits(x[j]) & 0x007FFFFFu) ^ key);
        }
    }

    /// <summary>Same as <see cref="AdvanceScalar"/>, vectorised with FMA3 for 8/16/32 accumulators (bit-identical:
    /// vfmadd is the same single-rounding IEEE operation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Advance(Span<float> x, ReadOnlySpan<float> m, ReadOnlySpan<float> a, ref uint h, uint segBase, uint segs, int hashInterval = 32)
    {
        ValidateHashInterval(hashInterval);
        if (hashInterval == 8) { AdvanceCheckpoint8(x, m, a, ref h, segBase, segs); return; }
        if (!Fma.IsSupported || x.Length % 8 != 0 || x.Length > 32) { AdvanceScalar(x, m, a, ref h, segBase, segs); return; }
        int nv = x.Length / 8;
        var mask = Vector256.Create(0x007FFFFFu);
        Vector256<float> x0 = V(x, 0), m0 = V(m, 0), a0 = V(a, 0);
        Vector256<float> x1 = nv > 1 ? V(x, 1) : default, m1 = nv > 1 ? V(m, 1) : default, a1 = nv > 1 ? V(a, 1) : default;
        Vector256<float> x2 = nv > 2 ? V(x, 2) : default, m2 = nv > 2 ? V(m, 2) : default, a2 = nv > 2 ? V(a, 2) : default;
        Vector256<float> x3 = nv > 3 ? V(x, 3) : default, m3 = nv > 3 ? V(m, 3) : default, a3 = nv > 3 ? V(a, 3) : default;
        for (uint s = 0; s < segs; s++)
        {
            if (nv == 1)
                for (int i = 0; i < Seg; i++) x0 = Fma.MultiplyAdd(x0, m0, a0);
            else if (nv == 2)
                for (int i = 0; i < Seg; i++) { x0 = Fma.MultiplyAdd(x0, m0, a0); x1 = Fma.MultiplyAdd(x1, m1, a1); }
            else
                for (int i = 0; i < Seg; i++)
                {
                    x0 = Fma.MultiplyAdd(x0, m0, a0); x1 = Fma.MultiplyAdd(x1, m1, a1);
                    x2 = Fma.MultiplyAdd(x2, m2, a2); x3 = Fma.MultiplyAdd(x3, m3, a3);
                }
            var key = Vector256.Create(SegKey(segBase + s));
            x0 = Renorm(x0, ref h, mask, key);
            if (nv > 1) x1 = Renorm(x1, ref h, mask, key);
            if (nv > 2) { x2 = Renorm(x2, ref h, mask, key); x3 = Renorm(x3, ref h, mask, key); }
        }
        x0.CopyTo(x[..8]);
        if (nv > 1) x1.CopyTo(x.Slice(8, 8));
        if (nv > 2) { x2.CopyTo(x.Slice(16, 8)); x3.CopyTo(x.Slice(24, 8)); }

        static Vector256<float> V(ReadOnlySpan<float> s, int i) => Vector256.Create(s.Slice(i * 8, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AdvanceCheckpoint8(Span<float> x, ReadOnlySpan<float> m, ReadOnlySpan<float> a, ref uint h, uint segBase, uint segs)
    {
        if (!Fma.IsSupported || x.Length is not (8 or 16 or 32)) { AdvanceScalarCheckpoint8(x, m, a, ref h, segBase, segs); return; }
        int nv = x.Length / 8;
        var mask = Vector256.Create(0x007FFFFFu);
        Vector256<float> x0 = V(x, 0), m0 = V(m, 0), a0 = V(a, 0);
        Vector256<float> x1 = nv > 1 ? V(x, 1) : default, m1 = nv > 1 ? V(m, 1) : default, a1 = nv > 1 ? V(a, 1) : default;
        Vector256<float> x2 = nv > 2 ? V(x, 2) : default, m2 = nv > 2 ? V(m, 2) : default, a2 = nv > 2 ? V(a, 2) : default;
        Vector256<float> x3 = nv > 3 ? V(x, 3) : default, m3 = nv > 3 ? V(m, 3) : default, a3 = nv > 3 ? V(a, 3) : default;
        for (uint s = 0; s < segs; s++)
        {
            for (int checkpoint = 0; checkpoint < 4; checkpoint++)
            {
                if (nv == 1)
                    for (int i = 0; i < 8; i++) x0 = Fma.MultiplyAdd(x0, m0, a0);
                else if (nv == 2)
                    for (int i = 0; i < 8; i++) { x0 = Fma.MultiplyAdd(x0, m0, a0); x1 = Fma.MultiplyAdd(x1, m1, a1); }
                else
                    for (int i = 0; i < 8; i++)
                    {
                        x0 = Fma.MultiplyAdd(x0, m0, a0); x1 = Fma.MultiplyAdd(x1, m1, a1);
                        x2 = Fma.MultiplyAdd(x2, m2, a2); x3 = Fma.MultiplyAdd(x3, m3, a3);
                    }
                Fold(x0, ref h);
                if (nv > 1) Fold(x1, ref h);
                if (nv > 2) { Fold(x2, ref h); Fold(x3, ref h); }
            }
            // Hash checkpoints never change the recurrence. Renorm and the segment key remain exactly every 32 FMAs.
            var key = Vector256.Create(SegKey(segBase + s));
            x0 = ((x0.AsUInt32() & mask) ^ key).AsSingle();
            if (nv > 1) x1 = ((x1.AsUInt32() & mask) ^ key).AsSingle();
            if (nv > 2) { x2 = ((x2.AsUInt32() & mask) ^ key).AsSingle(); x3 = ((x3.AsUInt32() & mask) ^ key).AsSingle(); }
        }
        x0.CopyTo(x[..8]);
        if (nv > 1) x1.CopyTo(x.Slice(8, 8));
        if (nv > 2) { x2.CopyTo(x.Slice(16, 8)); x3.CopyTo(x.Slice(24, 8)); }
        static Vector256<float> V(ReadOnlySpan<float> values, int i) => Vector256.Create(values.Slice(i * 8, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fold(Vector256<float> x, ref uint h)
    {
        var bits = x.AsUInt32();
        unchecked { for (int lane = 0; lane < 8; lane++) h = h * HashMul + bits.GetElement(lane); }
    }

    private static Vector256<float> Renorm(Vector256<float> x, ref uint h, Vector256<uint> mask, Vector256<uint> key)
    {
        var b = x.AsUInt32();
        unchecked
        {
            for (int l = 0; l < 8; l++) h = h * HashMul + b.GetElement(l);
        }
        return ((b & mask) ^ key).AsSingle();
    }

    /// <summary>Final accumulator bits and hash of class <paramref name="k"/> after <paramref name="segs"/> segments from the start.</summary>
    public static (uint[] XBits, uint H) Reference(ulong nonce, uint k, int nacc, uint segs, bool scalar = false, int hashInterval = 32)
    {
        Span<float> x = stackalloc float[nacc], m = stackalloc float[nacc], a = stackalloc float[nacc];
        Init(nonce, k, x, m, a, out uint h);
        if (scalar) AdvanceScalar(x, m, a, ref h, 0, segs, hashInterval); else Advance(x, m, a, ref h, 0, segs, hashInterval);
        var bits = new uint[nacc];
        for (int j = 0; j < nacc; j++) bits[j] = BitConverter.SingleToUInt32Bits(x[j]);
        return (bits, h);
    }

    /// <summary>
    /// Compares one launch's (or chain's) outputs against per-class references: tags first (completion/freshness), then
    /// every float (sentinel → stale, non-finite → NonFinite, else bit-exact), then every hash.
    /// Layout: outBits[j * n + tid], outH[tid], class = tid % classes, refBits[class * nacc + j].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Tally Compare(uint[] outBits, uint[] outH, ReadOnlySpan<uint> tags, uint[] refBits, uint[] refH,
        int n, int nacc, int classes, uint epoch, ulong nonce, uint salt, uint outSentinel, uint tagSentinel, string what)
    {
        var t = new Tally();
        t.CheckTags(tags, epoch, nonce, salt, tagSentinel, 0, what);
        // Expected rows are periodic in tid with period `classes`: compare `classes`-long runs vectorised, and fall back
        // to the per-element classification only for a run that differs.
        var rows = new uint[nacc][];
        for (int j = 0; j < nacc; j++) { rows[j] = new uint[classes]; for (int k = 0; k < classes; k++) rows[j][k] = refBits[k * nacc + j]; }
        int runs = (n + classes - 1) / classes;
        var e = Tally.For((long)nacc * runs, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (b0, b1, tl) =>
        {
            for (long r = b0; r < b1; r++)
            {
                int j = (int)(r / runs), t0 = (int)(r % runs) * classes, len = Math.Min(classes, n - t0);
                var got = outBits.AsSpan(j * n + t0, len);
                var want = rows[j].AsSpan(0, len);
                tl.Checked += len;
                if (got.SequenceEqual(want)) continue;
                for (int c = 0; c < len; c++)
                {
                    uint g = got[c], wv = want[c];
                    if (g == wv) continue;
                    int tid = t0 + c;
                    long key = 1_000_000_000L + (long)j * n + tid;
                    if (g == outSentinel) { tl.Stale++; if (tl.Wants(key)) tl.Fail(key, $"{what} out[acc={j},tid={tid}] never written (sentinel)"); continue; }
                    float gf = BitConverter.UInt32BitsToSingle(g), wf = BitConverter.UInt32BitsToSingle(wv);
                    if (!float.IsFinite(gf))
                    {
                        tl.NonFinite++;
                        if (tl.Wants(key)) tl.Fail(key, $"{what} out[acc={j},tid={tid}] class={c} non-finite actual=0x{g:x8} expected=0x{wv:x8} ({wf:R})");
                        continue;
                    }
                    tl.Mismatches++;
                    double abs = Math.Abs((double)gf - wf);
                    tl.MaxAbs = Math.Max(tl.MaxAbs, abs);
                    tl.MaxRel = Math.Max(tl.MaxRel, abs / Math.Max(Math.Abs((double)wf), double.Epsilon));
                    if (tl.Wants(key)) tl.Fail(key, $"{what} out[acc={j},tid={tid}] class={c} expected=0x{wv:x8} ({wf:R}) actual=0x{g:x8} ({gf:R}) xor=0x{wv ^ g:x8}");
                }
            }
        }, 256);
        t.Merge(e);
        for (int t0 = 0; t0 < n; t0 += classes)
        {
            int len = Math.Min(classes, n - t0);
            var got = outH.AsSpan(t0, len);
            t.Checked += len;
            if (got.SequenceEqual(refH.AsSpan(0, len))) continue;
            for (int c = 0; c < len; c++)
            {
                uint want = refH[c], g = got[c];
                if (g == want) continue;
                if (g == outSentinel) t.Stale++; else t.Mismatches++;
                if (t.Wants(3_000_000_000L + t0 + c)) t.Fail(3_000_000_000L + t0 + c, $"{what} hash[tid={t0 + c}] class={c} expected=0x{want:x8} actual=0x{g:x8}");
            }
        }
        return t;
    }
}

/// <summary>
/// C01 fp32_fma_throughput — many independent FP32 FMA accumulators per thread at full occupancy. Expanding bounded
/// recurrence with exact power-of-two renormalisation; exact (bit-for-bit) CPU reference per class, every output checked.
/// Params: <c>acc</c> 8|16|32 (register pressure / occupancy), <c>classes</c> (default 32), <c>target_ms</c> (20),
/// <c>hash_interval</c> 32 (unchanged default) or 8 (experimental extra hash checkpoints, same FMA trajectory).
/// </summary>
public sealed unsafe class C01Fp32Fma : IWorkload
{
    public const uint OutSentinel = 0xDEADBEEFu;   // as float: -6.3e18, never an output (outputs are in [1,2))
    public const uint TagSentinel = 0u;

    private static readonly WorkloadInfo DefaultInfo = new(
        Id: "C01", Name: "fp32_fma_throughput", Version: 1, Phase: "P1",
        OracleVersion: "c01-cpu-fma-exact-1", Backend: "cuda-driver",
        Description: "FP32 FMA throughput: 8/16/32 independent accumulators per thread, expanding x*m+a map with exact exponent reset every 32 steps; bit-exact CPU fmaf reference per class, every element checked",
        SupportedFaults: ["device_bit_flip", "skip_output", "replay_previous", "stale_epoch", "non_finite", "comparator_fixture", "short_output"]);
    public WorkloadInfo Info { get; private set; } = DefaultInfo;

    internal static WorkloadInfo InfoForHashInterval(int hashInterval)
    {
        C01Kernel.ValidateHashInterval(hashInterval);
        return hashInterval == 32 ? DefaultInfo : DefaultInfo with
        {
            Version = 2, OracleVersion = "c01-cpu-fma-exact-hash8-1",
            Description = "Experimental C01 hash checkpoint every 8 FMAs; original 32-FMA renorm/key/accumulator trajectory unchanged; independent exact scalar/AVX oracle; sensitivity and added hash overhead require hardware A/B measurement",
        };
    }

    private GpuContext? _gpu;
    private C01Kernel? _k;
    private LaunchClock? _clock;
    private uint _grid, _block, _classes, _segs;
    private int _n, _nacc, _hashInterval = 32;
    private ulong _dOutF, _dOutH, _dTags;
    private PinnedArray? _outPinned;
    private uint[] _outBits = [], _outH = [], _tags = [];

    public WorkloadSupport Probe(GpuContext gpu) => new(Info.Id, gpu.Info.CcMajor >= 12, gpu.Info.CcMajor >= 12 ? null : "needs CUDA compute capability 12.0 or later");

    public PrepareResult Prepare(GpuContext gpu, WorkloadParams p) => Prepare(gpu, p, CancellationToken.None);

    public PrepareResult Prepare(GpuContext gpu, WorkloadParams p, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        _gpu = gpu;
        long hashInterval = Evidence.ParamL(p, "hash_interval", 32);
        if (hashInterval is not (32 or 8)) throw new ArgumentException("C01 hash_interval must be 32 or 8");
        _hashInterval = (int)hashInterval; Info = InfoForHashInterval(_hashInterval);
        _nacc = (int)Evidence.ParamL(p, "acc", 8);
        _k = C01Kernel.Load(gpu, _nacc, _hashInterval);
        _block = 256;
        int perSm = gpu.Cuda.MaxActiveBlocksPerSm(_k.Fn, (int)_block);
        if (perSm < 1) return new PrepareResult(false, Info.Id, "kernel cannot be resident (occupancy 0)", Info.Backend, null, new Dictionary<string, string>());
        _grid = (uint)Math.Max(1, (int)Math.Round(gpu.Info.SmCount * perSm * Math.Clamp(p.Intensity, 0.01, 1.0)));
        _classes = (uint)Evidence.ParamL(p, "classes", 32);
        _n = (int)(_grid * _block);
        _dOutF = gpu.Cuda.MemAlloc((ulong)_n * (ulong)_nacc * 4);
        _dOutH = gpu.Cuda.MemAlloc((ulong)_n * 4);
        _dTags = gpu.Cuda.MemAlloc((ulong)_grid * 4);
        _outPinned = new PinnedArray(gpu, (long)_n * _nacc);
        _outBits = _outPinned.Data;
        _outH = new uint[_n];
        _tags = new uint[_grid];
        _clock = new LaunchClock(gpu);
        double targetMs = Evidence.Param(p, "target_ms", 20);
        double maxMs = Math.Min(50, Evidence.Param(p, "max_ms", 50));
        _segs = (uint)LaunchCalibrator.Calibrate(s => { cancel.ThrowIfCancellationRequested(); return TimedOnce((uint)s); }, 4, 10_000_000, targetMs, maxMs);
        cancel.ThrowIfCancellationRequested();
        double launchMs = TimedOnce(_segs);
        var res = _k.Module.Resources(_k.Entry);
        var d = Evidence.Module(_k.Module, res);
        d["grid"] = _grid.ToString(); d["block"] = _block.ToString(); d["blocks_per_sm"] = perSm.ToString();
        d["threads"] = _n.ToString(); d["accumulators"] = _nacc.ToString(); d["classes"] = _classes.ToString();
        d["seg_steps"] = C01Kernel.Seg.ToString(); d["segments_per_launch"] = _segs.ToString();
        d["hash_interval"] = _hashInterval.ToString(); d["renorm_interval"] = C01Kernel.Seg.ToString();
        d["profile_version"] = Info.Version.ToString(); d["oracle_version"] = Info.OracleVersion;
        d["fma_per_launch"] = ((long)_n * _nacc * C01Kernel.Seg * _segs).ToString();
        d["calibrated_launch_ms"] = Evidence.F(launchMs);
        d["cpu_fma3"] = Fma.IsSupported.ToString();
        d["readback_page_locked"] = _outPinned.Registered.ToString();
        d["prepare_warmup_batch"] = Evidence.WarmUp(this, cancel);
        return new PrepareResult(true, Info.Id, null, Info.Backend, null, d);
    }

    private double TimedOnce(uint segs)
    {
        var g = _gpu!;
        _clock!.Reset();
        _clock.Run("cuLaunchKernel(c01)", () => _k!.Launch(g, _grid, _block, _dOutF, _dOutH, _dTags, 0, 0, 0x1234, 0, 0, segs, _classes, _grid, 0));
        _clock.Drain();
        return _clock.TotalMs;
    }

    public BatchResult RunBatch(BatchRequest req)
    {
        var g = _gpu ?? throw new InvalidOperationException("not prepared");
        var c = g.Cuda;
        var clock = _clock!;
        uint epoch = (uint)req.Epoch;
        long nf = (long)_n * _nacc;
        long submit = Mono.Qpc();
        clock.Reset();
        bool launched = false;

        // 1. Poison (except when replaying: the previous batch's buffers are presented as this batch's).
        if (req.Fault != FaultKind.ReplayPrevious)
        {
            g.Check("cuMemsetD32Async", c.MemsetD32AsyncRaw(_dOutF, OutSentinel, (ulong)nf, g.Stream));
            g.Check("cuMemsetD32Async", c.MemsetD32AsyncRaw(_dOutH, OutSentinel, (ulong)_n, g.Stream));
            g.Check("cuMemsetD32Async", c.MemsetD32AsyncRaw(_dTags, TagSentinel, _grid, g.Stream));
        }
        // 2. Launch (or the fault's variant of it), asynchronously.
        if (req.Fault is not (FaultKind.SkipOutput or FaultKind.ReplayPrevious))
        {
            uint tagEpoch = req.Fault == FaultKind.StaleEpoch ? epoch - 1 : epoch;
            uint produce = req.Fault == FaultKind.ShortOutput ? Math.Max(1, _grid / 2) : _grid;
            clock.Run("cuLaunchKernel(c01)", () => _k!.Launch(g, _grid, _block, _dOutF, _dOutH, _dTags, 0, 0, req.Nonce, tagEpoch, 0, _segs, _classes, produce, 0));
            launched = true;
        }
        // 3. Independent CPU reference for this batch's nonce, computed while the GPU runs.
        long r0 = Stopwatch.GetTimestamp();
        var refBits = new uint[_classes * _nacc];
        var refH = new uint[_classes];
        Parallel.For(0, (int)_classes, k =>
        {
            var (bits, h) = C01Kernel.Reference(req.Nonce, (uint)k, _nacc, _segs, hashInterval: _hashInterval);
            bits.CopyTo(refBits, k * _nacc);
            refH[k] = h;
        });
        double refMs = Evidence.Ms(r0);
        // 4. Completion.
        long w0 = Stopwatch.GetTimestamp();
        clock.Drain();
        g.Synchronize();
        long h0 = Stopwatch.GetTimestamp();
        double waitMs = (h0 - w0) * 1000.0 / Stopwatch.Frequency;
        // 5. Post-completion device corruption.
        if (req.Fault == FaultKind.DeviceBitFlip) FlipBit(_dOutF + (ulong)(nf / 3) * 4, 17);
        if (req.Fault == FaultKind.NonFinite) { uint nan = 0x7FC00000u; c.MemcpyHtoD(_dOutF + (ulong)(nf / 2) * 4, &nan, 4); }
        // 6. Read back everything.
        c.MemcpyDtoH<uint>(_outBits, _dOutF);
        c.MemcpyDtoH<uint>(_outH, _dOutH);
        c.MemcpyDtoH<uint>(_tags, _dTags);
        long complete = Mono.Qpc();
        if (req.Fault == FaultKind.ComparatorFixture) refBits[0] ^= 1u << 5;
        // 7. Compare: tags, then every element.
        var t = C01Kernel.Compare(_outBits, _outH, _tags, refBits, refH, _n, _nacc, (int)_classes, epoch, req.Nonce, 0, OutSentinel, TagSentinel, "c01");
        double checkMs = Evidence.Ms(h0);
        long work = launched ? (long)_n * _nacc * C01Kernel.Seg * _segs : 0;
        // Reference time not hidden behind the GPU (it ran concurrently) counts as host time too.
        double refExposedMs = Math.Max(0, refMs - (launched ? clock.TotalMs : 0));
        string detail = Evidence.Kv(("gpu_ms", clock.TotalMs), ("ref_ms", refMs), ("ref_exposed_ms", refExposedMs), ("wait_ms", waitMs),
            ("host_check_ms", checkMs), ("host_share", clock.TotalMs > 0 ? (checkMs + refExposedMs) / clock.TotalMs : 0.0), ("segs", _segs));
        return Evidence.Result(req, launched ? 1 : 0, work, t, nf + _n + _grid, CheckScope.Full, submit, complete, clock, detail, floatOracle: true);
    }

    private void FlipBit(ulong at, int bit)
    {
        uint v;
        _gpu!.Cuda.MemcpyDtoH(&v, at, 4);
        v ^= 1u << bit;
        _gpu.Cuda.MemcpyHtoD(at, &v, 4);
    }

    public void Dispose()
    {
        if (_gpu is { Poisoned: false } g)
        {
            g.Cuda.MemFree(_dOutF); g.Cuda.MemFree(_dOutH); g.Cuda.MemFree(_dTags);
            _clock?.Dispose();
        }
        _outPinned?.Dispose();
        _gpu = null;
    }
}
