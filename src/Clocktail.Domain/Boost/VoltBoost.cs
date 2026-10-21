using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Boost;

/// <summary>The two places a voltage-boost percentage can be written.</summary>
public enum BoostPath
{
    /// <summary>ClientVoltRailsGet/SetControl (0x28 r1, +4 = percent, gateway kind 0x17).</summary>
    Independent,
    /// <summary>The byte at VoltRails control +8 (0xac8 r2, gateway kind 1).</summary>
    Legacy,
}

/// <summary>
/// One look at everything a boost write can move: both percentages and every rail's evaluated limits.
/// On the reference card (617.42, drill 18) 1 % and 10 % moved no rail's MAX, the V/F table or where the card ran; mVolt+
/// 0.50.6 describes the boost as headroom for Boost, not a voltage it sets. The look is kept so a card where it acts shows it.
/// </summary>
public sealed record BoostObservation(
    uint? IndependentPercent,
    byte LegacyPercent,
    uint NvvddKind,
    IReadOnlyList<(Rail Rail, uint MaxUv, uint CeilingUv)> Rails,
    byte[]? IndependentRaw,
    byte[] RailsControlRaw,
    byte[] RailsStatusRaw)
{
    /// <summary>The default path: independent only when its getter works and NVVDD is not kind 3.</summary>
    public BoostPath? DefaultRule(bool railsWritable) =>
        IndependentPercent != null && NvvddKind != 3 ? BoostPath.Independent : railsWritable ? BoostPath.Legacy : null;

    public string? CeilingViolation() =>
        Rails.Where(r => r.MaxUv > r.CeilingUv).Select(r => $"{r.Rail} MAX {r.MaxUv} µV is above the {r.CeilingUv} µV ceiling").FirstOrDefault();

    /// <summary>
    /// The post-write form: a rail's MAX is a violation only when it is above the ceiling <em>and</em> above where it
    /// stood in <paramref name="before"/>, the observation the write was planned on. The ceiling bounds what a write
    /// may raise, not where another tool already left a rail: a MAX found above the ceiling can still be lowered, or
    /// left alone by a write to the other rail, without the write being undone. A rail absent from
    /// <paramref name="before"/> is judged against the ceiling alone.
    /// </summary>
    public string? CeilingViolation(BoostObservation before)
    {
        foreach (var r in Rails)
        {
            uint? was = before.Rails.Where(b => b.Rail == r.Rail).Select(b => (uint?)b.MaxUv).FirstOrDefault();
            if (r.MaxUv > r.CeilingUv && (was == null || r.MaxUv > was))
                return $"{r.Rail} MAX {r.MaxUv} µV is above the {r.CeilingUv} µV ceiling" + (was is { } w ? $" (was {w} µV)" : "");
        }
        return null;
    }
}

/// <summary>What one write through one path did to the other path and to the rails.</summary>
public enum BoostVerdict
{
    /// <summary>Nothing moved anywhere, the written path included: the driver ignored or refused it.</summary>
    Ignored,
    /// <summary>The written path holds the value and the other path reads the same value: one driver variable, two views.</summary>
    Alias,
    /// <summary>The written path holds the value, the other path did not move, and a rail MAX moved: this path acts.</summary>
    Effective,
    /// <summary>The written path holds the value, the other path did not move, and no rail MAX moved: stored, effect not visible.</summary>
    StoredNoEffect,
    /// <summary>Anything else (the other path moved to a different value, MAX moved without the value landing...).</summary>
    Inconsistent,
}

public static class BoostReader
{
    public static BoostObservation Observe(NvClient hw, GpuSession gpu, bool xoc = false)
    {
        uint? indep = null;
        byte[]? indepRaw = null;
        if (hw.Api.Resolve(Slot.ClientVoltRailsGetControl) != 0)
        {
            var b = NvClient.NewBuffer(Slot.ClientVoltRailsGetControl);
            var r = hw.Call(gpu, Slot.ClientVoltRailsGetControl, b);
            uint pct = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
            // The getter check: version echoed (NvClient), percent below 101.
            if (r.IsOk && pct <= 100) { indep = pct; indepRaw = b; }
        }
        var rails = RailsReader.Read(hw, gpu);
        var nvvdd = rails.Get(Rail.Nvvdd) ?? throw new InvalidDataException("NVVDD is missing");
        return new BoostObservation(indep, rails.LegacyBoostPercent, nvvdd.Kind,
            rails.Rails.Select(r => (r.Rail, r.Limits.MaxUv, RailRules.Ceiling(r, xoc))).ToList(),
            indepRaw, rails.RawControl, rails.RawStatus);
    }

    public static BoostVerdict Classify(BoostPath written, uint value, BoostObservation before, BoostObservation after)
    {
        uint? Own(BoostObservation o) => written == BoostPath.Independent ? o.IndependentPercent : o.LegacyPercent;
        uint? Other(BoostObservation o) => written == BoostPath.Independent ? o.LegacyPercent : o.IndependentPercent;
        bool maxMoved = before.Rails.Zip(after.Rails).Any(p => p.First.MaxUv != p.Second.MaxUv);
        bool landed = Own(after) == value;
        bool otherMoved = Other(before) != Other(after);

        if (!landed) return !otherMoved && !maxMoved ? BoostVerdict.Ignored : BoostVerdict.Inconsistent;
        if (otherMoved) return Other(after) == value ? BoostVerdict.Alias : BoostVerdict.Inconsistent;
        return maxMoved ? BoostVerdict.Effective : BoostVerdict.StoredNoEffect;
    }
}

public sealed record BoostApplyResult(
    BoostPath Path,
    uint RequestedPercent,
    BoostObservation Before,
    BoostObservation? After,
    TxResult Tx,
    string Ownership,
    /// <summary>Set when the post-check (every rail MAX under its ceiling) failed and the old value was put back.</summary>
    string? CeilingRollback,
    bool SentinelCleared,
    /// <summary>A rail-control byte other than the boost byte changed across the write. Nobody owns that change, so it is
    /// reported, not hidden behind "Restored" (the transaction only reverts the fields it edited).</summary>
    string? SideEffect = null)
{
    public bool Succeeded => Tx.Succeeded && CeilingRollback == null && SideEffect == null;
    public BoostVerdict? Verdict => After == null || Tx.Outcome != TxOutcome.Applied ? null : BoostReader.Classify(Path, RequestedPercent, Before, After);
}

/// <summary>
/// Voltage boost through either path as one owned cell each, with a post-check: after the write every
/// rail's MAX must sit under its ceiling, otherwise the previous percentage goes back (or 0 % when the previous
/// state was itself above the ceiling).
/// </summary>
public sealed class VoltBoost(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "boost";
    public const uint MaxPercent = 100;

    public static OwnedCell Cell(BoostPath path, uint railMask) => path switch
    {
        BoostPath.Independent => new(Kind, "independent", "%", "voltage boost, independent API (ClientVoltRails +4)",
            new TransactionSpec(Slot.ClientVoltRailsGetControl, Slot.ClientVoltRailsSetControl),
            raw => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)).ToString(CultureInfo.InvariantCulture)),
        _ => new(Kind, "legacy", "%", "voltage boost, legacy byte (VoltRails control +8)",
            new TransactionSpec(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl,
                PrepareRead: b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), railMask)),
            raw => raw[RailsLayout.ControlBoostByte].ToString(CultureInfo.InvariantCulture)),
    };

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(BoostPath path, uint percent) => raw =>
    {
        if (percent > MaxPercent) return new Mutation.Abort($"voltage boost must be between 0 and {MaxPercent} %");
        var s = raw.Span;
        if (path == BoostPath.Independent)
        {
            if (s[8..].IndexOfAnyExcept((byte)0) >= 0) return new Mutation.Abort("the independent boost structure carries unknown non-zero fields; it will not be overwritten");
            uint cur = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
            if (cur > MaxPercent) return new Mutation.Abort($"the driver reports an unrecognized boost of {cur} %");
            return cur == percent ? new Mutation.Apply([]) : Mutation.Of(Edit.UInt32(4, percent));
        }
        byte legacy = s[RailsLayout.ControlBoostByte];
        if (legacy > MaxPercent) return new Mutation.Abort("the voltage percentage delta is out of range");
        return legacy == percent ? new Mutation.Apply([]) : Mutation.Of(Edit.Byte(RailsLayout.ControlBoostByte, (byte)percent));
    };

    /// <summary>
    /// The write-path registry's entry: the cell for <paramref name="key"/> (independent | legacy) and the plan that
    /// sets it to <paramref name="value"/> percent; null plan when the value is not 0..100. The plan alone cannot hold
    /// the rail ceiling, because what a percent does to MAX is only known after the write: a write that raises boost
    /// belongs in <see cref="Apply"/>, which re-reads the rails and puts it back. Recovery (back to the original) is
    /// safe through the bare plan.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        var path = key switch
        {
            "independent" => BoostPath.Independent,
            "legacy" => BoostPath.Legacy,
            _ => throw new ArgumentException($"'{key}' is not a boost path (independent, legacy)"),
        };
        uint mask = path == BoostPath.Legacy ? BinaryPrimitives.ReadUInt32LittleEndian(BoostReader.Observe(hw, gpu).RailsControlRaw.AsSpan(4)) : 0;
        var plan = uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pct) && pct <= MaxPercent ? Plan(path, pct) : null;
        return (Cell(path, mask), plan);
    }

    public BoostApplyResult Apply(GpuSession gpu, BoostPath path, uint percent, bool takeOver = false, bool xoc = false)
    {
        var before = BoostReader.Observe(hw, gpu, xoc);
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(before.RailsControlRaw.AsSpan(4));
        var cell = Cell(path, mask);
        var writer = new OwnedWriter(hw, journal, sentinel, host);
        var r = writer.Apply(gpu, cell, Plan(path, percent), takeOver);
        if (r.Tx.Outcome is TxOutcome.Aborted or TxOutcome.NoChange)
            return new BoostApplyResult(path, percent, before, before, r.Tx, r.Ownership, null, r.SentinelCleared);

        BoostObservation? after;
        try { after = BoostReader.Observe(hw, gpu, xoc); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { after = null; }

        string? side = after == null ? null : SideEffect(before, after);
        string? violation = after == null ? "the rails could not be read after the write" : after.CeilingViolation();
        if (violation == null || r.Tx.Outcome != TxOutcome.Applied)
            return new BoostApplyResult(path, percent, before, after, r.Tx, r.Ownership, null, r.SentinelCleared, side);

        // Post-check failed: put the old percentage back, or 0 % when the old state already broke the ceiling.
        uint old = uint.Parse(cell.StateIn(r.Tx.Before) ?? "0", CultureInfo.InvariantCulture);
        uint back = before.CeilingViolation() == null ? old : 0;
        var undo = writer.Apply(gpu, cell, Plan(path, back), takeOver: true);
        BoostObservation? final = null;
        try { final = BoostReader.Observe(hw, gpu, xoc); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
        string rollback = $"{violation}; boost set back to {back} % ({undo.Tx.Outcome}, {undo.Ownership})";
        return new BoostApplyResult(path, percent, before, final, r.Tx, undo.Ownership, rollback, undo.SentinelCleared, final == null ? null : SideEffect(before, final));
    }

    /// <summary>Rail offsets and every other control field must not move when only the boost percentage was asked to.</summary>
    public static string? SideEffect(BoostObservation before, BoostObservation after)
    {
        var d = Transaction.FirstDifference(before.RailsControlRaw, after.RailsControlRaw, [new ByteRange(RailsLayout.ControlBoostByte, 1)]);
        return d == null ? null : $"the rail control changed at +0x{d.Value:x}, outside the boost byte; that change is not owned by anyone";
    }
}

public sealed record EqualValueProof(BoostPath Path, bool Written, NvResult? Driver, string? FirstDifference, BoostObservation Before, BoostObservation After)
{
    /// <summary>The setter accepted the call and nothing anywhere changed, byte for byte.</summary>
    public bool Proven => Written && Driver is { IsOk: true } && FirstDifference == null;
}

/// <summary>
/// The equal-value proof for a boost path: write exactly what was just read, through the real setter, and show that
/// the independent structure, the rails control and the rails status are byte-identical afterwards. The transaction
/// template cannot do this (it skips equal writes, correctly), and nothing is owed because nothing changes, so the
/// journal is not involved; the sentinel still covers the window, since the call does reach the driver.
/// </summary>
public static class BoostProof
{
    public static EqualValueProof EqualValue(NvClient hw, GpuSession gpu, ApplySentinel sentinel, BoostPath path)
    {
        if (sentinel.IsBlocked(out var what)) throw new InvalidOperationException($"a previous application did not complete ({what}); run --recover first");
        if (!gpu.TryEnter(TimeSpan.FromSeconds(5), out var lockScope)) throw new InvalidOperationException("another operation holds this GPU");
        using (lockScope)
        {
            var before = BoostReader.Observe(hw, gpu);
            byte[] payload = path == BoostPath.Independent
                ? (byte[])(before.IndependentRaw ?? throw new InvalidOperationException("the independent boost getter did not answer")).Clone()
                : (byte[])before.RailsControlRaw.Clone();
            var scope = sentinel.Begin("boost-equal-value", takeOver: false) ?? throw new InvalidOperationException("the apply sentinel could not be created");
            var set = path == BoostPath.Independent ? Slot.ClientVoltRailsSetControl : Slot.VoltVoltRailsSetControl;
            var w = hw.Call(gpu, set, payload);
            bool reached = w.Outcome is not (NvOutcome.Rejected or NvOutcome.Missing or NvOutcome.Poisoned);
            var after = BoostReader.Observe(hw, gpu);
            string? diff = Diff("independent", before.IndependentRaw, after.IndependentRaw)
                ?? Diff("rails control", before.RailsControlRaw, after.RailsControlRaw)
                ?? Diff("rails status", before.RailsStatusRaw, after.RailsStatusRaw);
            if (diff == null) scope.Complete();
            return new EqualValueProof(path, reached, w, diff, before, after);
        }
    }

    private static string? Diff(string what, byte[]? a, byte[]? b)
    {
        if (a == null && b == null) return null;
        if (a == null || b == null) return $"{what}: readable before {a != null}, after {b != null}";
        var d = Transaction.FirstDifference(a, b, []);
        return d == null ? null : $"{what} differs at +0x{d.Value:x}";
    }
}
