using Clocktail.Hw;

namespace Clocktail.Domain;

/// <summary>A contiguous byte range inside a driver structure.</summary>
public readonly record struct ByteRange(int Offset, int Length)
{
    public int End => Offset + Length;
    public bool Contains(int i) => i >= Offset && i < End;
}

/// <summary>One field change: these bytes go to this offset. The width is the value's length.</summary>
public sealed record Edit(int Offset, byte[] Value)
{
    public int Length => Value.Length;
    public int End => Offset + Value.Length;

    public static Edit Int32(int offset, int value) => new(offset, BitConverter.GetBytes(value));
    public static Edit UInt32(int offset, uint value) => new(offset, BitConverter.GetBytes(value));
    public static Edit Byte(int offset, byte value) => new(offset, [value]);
}

/// <summary>What the domain code decided after seeing the fresh snapshot.</summary>
public abstract record Mutation
{
    /// <summary>Read-modify-write: the snapshot with these bytes changed is what gets written.</summary>
    public sealed record Apply(IReadOnlyList<Edit> Edits) : Mutation;

    /// <summary>
    /// Request-style interfaces (Pstates20, power requests): what is written is a separate request structure,
    /// not the snapshot. <paramref name="ExpectedEdits"/> says how the readable structure must differ afterwards,
    /// and <paramref name="RestoreRequest"/> is the request that puts the old values back (null when the domain
    /// cannot express one; the result then says so instead of pretending).
    /// </summary>
    public sealed record Request(byte[] Payload, IReadOnlyList<Edit> ExpectedEdits, byte[]? RestoreRequest) : Mutation;

    public sealed record Abort(string Reason) : Mutation;

    public static Mutation Of(params Edit[] edits) => new Apply(edits);
}

/// <summary>Which structure a transaction reads and writes, and which bytes it may not reason about.</summary>
public sealed record TransactionSpec(
    Slot Get,
    Slot Set,
    /// <summary>Index for <see cref="Signature.GpuIndexStruct"/> slots; null otherwise.</summary>
    uint? Index = null,
    /// <summary>Bytes the driver is known to change on its own (status-like fields inside a control structure).
    /// Everything outside is compared exactly. Empty by default: strict until evidence says otherwise.</summary>
    IReadOnlyList<ByteRange>? Volatile = null,
    TimeSpan? LockTimeout = null,
    /// <summary>Query-style reads (power requests, rail status) need inputs in the buffer before the GET: a board bit, a
    /// command code, a mask. Applied to every read buffer of the transaction, so the readback asks the same question.</summary>
    Action<byte[]>? PrepareRead = null,
    /// <summary>Runs after a readback that matched, still inside the GPU lock: judges state the driver derives from the
    /// write and the readback cannot show (a rail MAX it evaluates, a V/F curve that must still validate). A non-null
    /// answer, or a check that throws, is a veto handled like a readback mismatch: the edited fields are put back and
    /// verified. A limit that only shows after the write belongs here and not in the caller, so no path that writes
    /// the cell (a user write, recovery, the broker) can skip it.</summary>
    Func<NvClient, GpuSession, string?>? CheckAfterWrite = null,
    /// <summary>A request the firmware lands a little after the call (F8): the readback is taken up to this many times,
    /// <see cref="ReadbackGap"/> apart, until two in a row match; the last one decides. 1: read back once.</summary>
    int ReadbackTries = 1,
    TimeSpan? ReadbackGap = null)
{
    public byte[] ReadBuffer()
    {
        var b = NvClient.NewBuffer(Get);
        PrepareRead?.Invoke(b);
        return b;
    }

    public IReadOnlyList<ByteRange> VolatileRanges => Volatile ?? [];
    public TimeSpan Timeout => LockTimeout ?? TimeSpan.FromSeconds(5);
}

public enum TxOutcome
{
    /// <summary>The write landed and the readback is exactly the expected structure.</summary>
    Applied,
    /// <summary>The edits equal the current state; nothing was written.</summary>
    NoChange,
    /// <summary>Nothing was written: a precondition failed (lock, read, domain rules, write-ahead, policy, missing interface, fault).</summary>
    Aborted,
    /// <summary>The driver refused, normalized or partially applied the write, or the post-write check vetoed it; see <see cref="TxResult.Restore"/>.</summary>
    Failed,
}

public enum RestoreState
{
    /// <summary>No restore was needed: the structure still reads as it did before the write.</summary>
    NotNeeded,
    /// <summary>The changed fields were written back to their previous values and the readback confirms it.</summary>
    Restored,
    /// <summary>The restore was attempted but could not be confirmed. The hardware state is uncertain.</summary>
    RestoreFailed,
    /// <summary>The structure could not be read after the write, so no restore was attempted. State unknown.</summary>
    NotAttempted,
}

public sealed record TxResult(
    TxOutcome Outcome,
    RestoreState Restore,
    string Detail,
    /// <summary>The snapshot the decision was made on; empty when the transaction never got one (busy GPU, failed read).</summary>
    byte[] Before,
    byte[]? After,
    NvResult? Driver,
    IReadOnlyList<string> Log)
{
    /// <summary>The post-write check's objection, when the write landed and what the driver derived from it failed the
    /// check (the edited bytes were then put back); null otherwise. <see cref="Detail"/> carries it too, with the
    /// restore's outcome appended, so this is the reason on its own for a program.</summary>
    public string? Veto { get; init; }

    public bool Succeeded => Outcome is TxOutcome.Applied or TxOutcome.NoChange;
    public override string ToString() => $"{Outcome} ({Restore}): {Detail}";
}

/// <summary>
/// The one write template every tuning domain shares:
/// read → decide edits on that fresh copy → write-ahead (the caller records what it is about to change) → write →
/// read back → compare whole structure → check what the driver derived (<see cref="TransactionSpec.CheckAfterWrite"/>)
/// → on mismatch or veto, put the changed fields back on a fresh copy, write, read back, compare again.
/// <para>
/// Reading fresh and editing only the chosen bytes keeps every other field as the driver currently holds it.
/// Comparing the whole structure (minus declared volatile bytes) is what catches "the driver changed another
/// rail". Restoring only the edited fields, on a fresh copy, is what keeps a concurrent tool's changes intact.
/// Domain value rules (ceilings, ranges) live in the mutate callback, which
/// sees the fresh snapshot and may abort.
/// </para>
/// <para>
/// <see cref="TxOutcome.Aborted"/> is total: it means no byte reached the driver. Domain code that throws before
/// the write (a parser meeting an unexpected layout, a journal that cannot be saved) is reported as Aborted
/// instead of unwinding through the caller as if the hardware state were in doubt.
/// </para>
/// </summary>
public sealed class Transaction
{
    private readonly NvClient _hw;

    public Transaction(NvClient hw) => _hw = hw;

    /// <param name="writeAhead">
    /// Runs once, after the decision is final and checked and immediately before the setter is called, with the
    /// snapshot the decision was made on. It is the only point that is both committed to writing and still before
    /// the write: where an ownership record must be saved. (The whitelist can still refuse the call; the result is
    /// then Aborted and the caller can drop what it recorded.) Returns a log line, or null when it had nothing to
    /// do. If it throws, nothing is written and the result is Aborted.
    /// </param>
    public TxResult Run(GpuSession gpu, TransactionSpec spec, Func<ReadOnlyMemory<byte>, Mutation> mutate, Func<byte[], string?>? writeAhead = null)
    {
        var log = new List<string>();
        if (!gpu.TryEnter(spec.Timeout, out var scope))
            return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, "another operation holds this GPU; nothing was written", [], null, null, log);
        using (scope)
        {
            // 1. Fresh snapshot.
            var before = spec.ReadBuffer();
            var r = Call(gpu, spec.Get, spec.Index, before);
            log.Add($"read {spec.Get}: {r}");
            if (!r.IsOk) return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, $"could not read {spec.Get}: {r}", [], null, r, log);   // no snapshot

            // 2. Domain decision on that snapshot.
            Mutation decision;
            try { decision = mutate(before); }
            catch (Exception e)
            {
                log.Add($"domain rules failed: {e.Message}");
                return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, $"could not decide on the snapshot ({e.Message}); nothing was written", before, null, null, log);
            }
            if (decision is Mutation.Abort abort)
            {
                log.Add($"aborted by domain rules: {abort.Reason}");
                return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, abort.Reason, before, null, null, log);
            }
            IReadOnlyList<Edit> edits;
            byte[]? request = null, restoreRequest = null;
            switch (decision)
            {
                case Mutation.Apply a: edits = a.Edits; break;
                case Mutation.Request q: edits = q.ExpectedEdits; request = q.Payload; restoreRequest = q.RestoreRequest; break;
                default: throw new InvalidOperationException($"unknown mutation {decision.GetType().Name}");
            }
            var bad = CheckEdits(edits, before.Length);
            if (bad != null)
            {
                log.Add($"rejected edits: {bad}");
                return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, bad, before, null, null, log);
            }

            var expected = ApplyEdits(before, edits);
            if (expected.AsSpan().SequenceEqual(before))
            {
                log.Add("edits equal the current state; no write");
                return new TxResult(TxOutcome.NoChange, RestoreState.NotNeeded, "already at the requested values", before, before, null, log);
            }

            // 3. Write-ahead: the write is now certain; whatever must be on disk before it goes there now.
            if (writeAhead != null)
            {
                try
                {
                    if (writeAhead(before) is { } line) log.Add(line);
                }
                catch (Exception e)
                {
                    log.Add($"write-ahead failed: {e.Message}");
                    return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, $"could not record ownership before writing ({e.Message}); nothing was written", before, null, null, log);
                }
            }

            // 4. Write the edited fresh copy, or the domain's request structure.
            var write = request != null ? (byte[])request.Clone() : (byte[])expected.Clone();
            var w = Call(gpu, spec.Set, spec.Index, write);
            log.Add($"write {spec.Set}: {w}");
            if (w.Outcome is NvOutcome.Rejected or NvOutcome.Missing or NvOutcome.Poisoned)
                return Fail(TxOutcome.Aborted, RestoreState.NotNeeded, $"write refused before reaching the driver: {w}", before, null, w, log);

            // 5. Read back, whatever the write reported: the driver may have applied part of it.
            byte[] after;
            int? diff;
            for (int i = 0, agree = 0; ; i++)
            {
                after = spec.ReadBuffer();
                var rb = Call(gpu, spec.Get, spec.Index, after);
                log.Add($"readback {spec.Get}: {rb}");
                if (!rb.IsOk)
                    return Fail(TxOutcome.Failed, RestoreState.NotAttempted, $"write returned {w}, and the readback failed ({rb}); hardware state unknown", before, null, rb, log);
                diff = FirstDifference(after, expected, spec.VolatileRanges);
                agree = diff == null ? agree + 1 : 0;
                if (spec.ReadbackTries <= 1 || agree >= 2 || i + 1 >= spec.ReadbackTries) break;
                Thread.Sleep(spec.ReadbackGap ?? TimeSpan.FromMilliseconds(100));
            }
            if (diff == null)
            {
                string detail = w.IsOk ? "readback matches" : $"driver reported {w} but the readback matches the request";
                log.Add(detail);

                // 5b. What the driver derived from the write: a veto is a mismatch the readback could not see.
                var veto = CheckAfter(gpu, spec);
                if (veto == null) return new TxResult(TxOutcome.Applied, RestoreState.NotNeeded, detail, before, after, w, log);
                log.Add($"post-write check: {veto}");
                return Restore(veto) with { Veto = veto };
            }

            string why = w.IsOk
                ? $"readback differs from the request at +0x{diff.Value:x} (driver rejected, normalized or clamped it)"
                : $"write returned {w} and the readback differs at +0x{diff.Value:x}";
            log.Add(why);

            if (FirstDifference(after, before, spec.VolatileRanges) == null)
            {
                log.Add("structure unchanged since before the write; nothing to restore");
                return new TxResult(TxOutcome.Failed, RestoreState.NotNeeded, why, before, after, w, log);
            }
            return Restore(why);

            // 6. Restore only the fields this transaction changed, on a fresh copy.
            TxResult Restore(string reason)
            {
                if (request != null && restoreRequest == null)
                {
                    log.Add("no restore request available for this interface");
                    return new TxResult(TxOutcome.Failed, RestoreState.NotAttempted, $"{reason}; this interface offers no way to restore the previous value", before, after, w, log);
                }
                var restore = RestoreEdits(gpu, spec, before, edits, restoreRequest, log);
                return new TxResult(TxOutcome.Failed, restore.State, $"{reason}; {restore.Detail}", before, after, w, log);
            }
        }
    }

    private string? CheckAfter(GpuSession gpu, TransactionSpec spec)
    {
        if (spec.CheckAfterWrite == null) return null;
        try { return spec.CheckAfterWrite(_hw, gpu); }
        catch (Exception e) { return $"the post-write check could not run ({e.Message})"; }
    }

    private (RestoreState State, string Detail) RestoreEdits(GpuSession gpu, TransactionSpec spec, byte[] before, IReadOnlyList<Edit> edits, byte[]? restoreRequest, List<string> log)
    {
        var fresh = spec.ReadBuffer();
        var r = Call(gpu, spec.Get, spec.Index, fresh);
        log.Add($"restore read {spec.Get}: {r}");
        if (!r.IsOk) return (RestoreState.RestoreFailed, $"could not re-read before restoring ({r}); previous values not restored");

        var inverse = edits.Select(e => new Edit(e.Offset, before.AsSpan(e.Offset, e.Length).ToArray())).ToList();
        var target = ApplyEdits(fresh, inverse);
        var write = restoreRequest != null ? (byte[])restoreRequest.Clone() : (byte[])target.Clone();
        var w = Call(gpu, spec.Set, spec.Index, write);
        log.Add($"restore write {spec.Set}: {w}");

        var check = spec.ReadBuffer();
        var rb = Call(gpu, spec.Get, spec.Index, check);
        log.Add($"restore readback {spec.Get}: {rb}");
        if (!rb.IsOk) return (RestoreState.RestoreFailed, $"restore write returned {w}, readback failed ({rb}); state uncertain");
        var diff = FirstDifference(check, target, spec.VolatileRanges);
        if (diff == null) return (RestoreState.Restored, "previous values restored and verified");
        return (RestoreState.RestoreFailed, $"restore could not be verified: readback differs at +0x{diff.Value:x} (write returned {w})");
    }

    private NvResult Call(GpuSession gpu, Slot slot, uint? index, byte[] buf) =>
        index is { } i ? _hw.Call(gpu, slot, i, buf) : _hw.Call(gpu, slot, buf);

    /// <summary>Empty edits are not an error: the domain looked at the fresh snapshot and found nothing to change.</summary>
    private static string? CheckEdits(IReadOnlyList<Edit> edits, int size)
    {
        foreach (var e in edits)
        {
            if (e.Length == 0) return $"empty edit at +0x{e.Offset:x}";
            if (e.Offset < 4) return "edits may not touch the version word";
            if (e.End > size) return $"edit at +0x{e.Offset:x} ({e.Length} bytes) runs past the structure (0x{size:x})";
        }
        for (int a = 0; a < edits.Count; a++)
            for (int b = a + 1; b < edits.Count; b++)
                if (edits[a].Offset < edits[b].End && edits[b].Offset < edits[a].End)
                    return $"edits at +0x{edits[a].Offset:x} and +0x{edits[b].Offset:x} overlap";
        return null;
    }

    private static byte[] ApplyEdits(byte[] source, IReadOnlyList<Edit> edits)
    {
        var copy = (byte[])source.Clone();
        foreach (var e in edits) e.Value.CopyTo(copy, e.Offset);
        return copy;
    }

    /// <summary>Offset of the first byte that differs outside the volatile ranges, or null when equal.</summary>
    public static int? FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, IReadOnlyList<ByteRange> volatileRanges)
    {
        if (a.Length != b.Length) return Math.Min(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            bool skip = false;
            foreach (var v in volatileRanges) if (v.Contains(i)) { skip = true; break; }
            if (!skip) return i;
        }
        return null;
    }

    private static TxResult Fail(TxOutcome o, RestoreState s, string detail, byte[] before, byte[]? after, NvResult? drv, List<string> log) =>
        new(o, s, detail, before, after, drv, log);
}
