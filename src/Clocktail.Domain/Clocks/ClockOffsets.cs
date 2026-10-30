using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

public sealed record ClockApplyResult(
    ClockDomain Domain,
    int RequestedKhz,
    /// <summary>The offset in the snapshot the write was planned on; null when the transaction got no snapshot.</summary>
    int? BeforeKhz,
    int? AfterKhz,
    /// <summary>The entry's reported frequency (data +0x18) before and after: the driver's own statement of what the offset did.</summary>
    int? FreqBeforeKhz,
    int? FreqAfterKhz,
    TxResult Tx,
    /// <summary>What the ownership journal says about this domain after the call.</summary>
    string Ownership,
    bool SentinelCleared)
{
    public bool Succeeded => Tx.Succeeded;
}

/// <summary>
/// A P0 clock offset is one owned cell: the Pstates20 table read and written through request mode, the offset
/// field as the cell, <see cref="Pstates20Rules"/> as the plan. Everything about ownership is <see cref="OwnedWriter"/>.
/// </summary>
public sealed class ClockOffsets(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "pstates20";

    public static string Key(ClockDomain d) => d.ToString().ToLowerInvariant();

    public static OwnedCell Cell(ClockDomain domain) => new(
        Kind, Key(domain), "kHz", "P0 clock offset, kHz", Pstates20Layout.Spec,
        raw => Pstates20Parser.Parse(raw).P0(domain)?.DeltaKhz.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The plan: the driver's own range on the offset; for the core clock, the budget it shares with the V/F points;
    /// and for any domain, the curve a Pstates20 write would wipe.
    /// <para>
    /// Budget: every point runs at base + global + point offset and the driver states one range for the core clock,
    /// so the two offsets spend one budget and whichever moves second checks the other: the V/F path bounds
    /// global + point when it writes points, and this plan bounds it against the points as they stand
    /// (<see cref="VfBudget"/>). Recovery is held to it too: the original global was a state the firmware held, but
    /// not necessarily together with the points that are there now.
    /// </para>
    /// <para>
    /// Wipe: any Pstates20 set, memory offset included, resets every V/F point offset to 0 (live, 2026-10-18: a memory
    /// +358 MHz written 22 s after a curve cleared it during a stability run; reproduced by the V/F thread, while a
    /// power-limit write leaves the curve alone). So a P0 offset that would change is refused while any point offset is
    /// non-zero: the order is P0 offsets, then the power limit, then the curve, and the rule lives here so no caller
    /// has to remember it. Recovery restores P0 offsets before the captured curve, with CurveFollows set.
    /// </para>
    /// </summary>
    /// <param name="curve">The V/F point offsets read for this write (<see cref="Curve"/>), or null when the driver has
    /// none: then nothing else spends the budget, nothing can be wiped, and the driver's range is the whole limit.</param>
    /// <param name="canRecordCurveOriginal">Readable controls must have a recoverable ownership encoding before
    /// changing P0. This is separate from the existing bounded-return rule when controls are genuinely unreadable.</param>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(ClockDomain domain, int deltaKhz, VfBudget? curve = null, bool canRecordCurveOriginal = true) =>
        raw =>
        {
            var snap = Pstates20Parser.Parse(raw.ToArray());
            var own = Pstates20Rules.Plan(snap, domain, deltaKhz);
            if (own is not Mutation.Request || curve == null) return own;
            if (WipeReason(curve) is { } wipe) return new Mutation.Abort(wipe);
            if (curve.Offsets != null && !canRecordCurveOriginal)
                return new Mutation.Abort("the readable V/F controls cannot be recorded as a recoverable original (unsupported ownership topology); no P0 write was made");
            if (domain != ClockDomain.Core) return own;
            return curve.Check(VfLimits.From(snap), deltaKhz) is { } over ? new Mutation.Abort(over) : own;
        };

    /// <summary>Why a Pstates20 write may not run over this curve: it would reset the point offsets in force. Null when
    /// every offset is 0, or when they cannot be read (the read failing does not make a curve exist; the budget rule
    /// still holds the global for the core clock).</summary>
    public static string? WipeReason(VfBudget curve)
    {
        if (curve.Offsets == null) return null;
        int set = curve.Offsets.Count(o => o.OffsetKhz != 0);
        return set == 0 ? null
            : $"{set} V/F point offset(s) are in force, and any Pstates20 write resets every one of them to 0: write the P0 offsets before the curve (P0 offsets, power limit, curve last), or give the curve back to 0 first";
    }

    /// <summary>
    /// The V/F point offsets a P0 offset write must look at, read before planning. Null when the driver has no V/F
    /// point structure (no read is made). Offsets that exist but cannot be read come back as such: for the core clock
    /// the plan then allows only a global between 0 and the one in force, which no curve the range admits can turn
    /// into a violation, so nothing is written blind and the way back to 0 stays open.
    /// </summary>
    public static VfBudget? Curve(NvClient hw, GpuSession gpu) =>
        hw.Api.Resolve(Slot.ClockClientClkVfPointsGetInfo) == 0 ? null : VfBudget.Read(hw, gpu);

    /// <summary>One Info/Control read supplies the budget and an optional coupled original. A known budget is
    /// retained even when its topology cannot encode an original. Only unreadable controls use the unknown-budget rule.</summary>
    public static (VfBudget? Budget, VfControlSnapshot? Controls) CurveForWrite(NvClient hw, GpuSession gpu)
    {
        if (hw.Api.Resolve(Slot.ClockClientClkVfPointsGetInfo) == 0) return (null, null);
        try
        {
            var (offsets, original) = VfReader.ReadControls(hw, gpu);
            return (new VfBudget(offsets, null), original);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            return (new VfBudget(null, e.Message), null);
        }
    }

    // The direct setter and default registry route share failure settlement. A readable control original is
    // enough to restore native side effects; no effective-frequency Status read is required for this path.
    internal OwnedWriteResult ApplyOwned(GpuSession gpu, ClockDomain domain, int deltaKhz, bool takeOver = false)
    {
        var writer = new OwnedWriter(hw, journal, sentinel, host);
        var (budget, controls) = CurveForWrite(hw, gpu);
        var coupled = controls == null ? null : VfWrites.ControlCell(controls);
        var r = writer.Apply(gpu, Cell(domain), Plan(domain, deltaKhz, budget, canRecordCurveOriginal: controls != null), takeOver, deltaKhz.ToString(CultureInfo.InvariantCulture),
            beforeWrite: coupled == null ? null : () => writer.ClaimOriginal(coupled, controls!.Control),
            settle: controls == null ? null : tx => SettleFailedControls(gpu, tx, controls));
        if (coupled != null && r.StateKnown) writer.ReleaseIfRestored(gpu, coupled);
        return r;
    }

    public ClockApplyResult Apply(GpuSession gpu, ClockDomain domain, int deltaKhz, bool takeOver = false)
    {
        var r = ApplyOwned(gpu, domain, deltaKhz, takeOver);
        var before = Entry(r.Tx.Before, domain);
        // The readback describes the hardware unless the write failed and was verifiably restored (then: before).
        var after = r.Tx.Outcome == TxOutcome.Aborted || r.Tx.Restore == RestoreState.Restored ? before
            : r.Tx.After != null ? Entry(r.Tx.After, domain) : null;
        return new ClockApplyResult(domain, deltaKhz, before?.DeltaKhz, after?.DeltaKhz, before?.FreqKhz, after?.FreqKhz, r.Tx, r.Ownership, r.SentinelCleared);
    }

    private TxResult SettleFailedControls(GpuSession gpu, TxResult tx, VfControlSnapshot original)
    {
        // An abort / NoChange did not reach a setter. Successful ordinary writes may legitimately generate
        // new controls; only a failed native write must return both controls to their pre-operation values.
        if (tx.Outcome != TxOutcome.Failed) return tx;
        var log = tx.Log.ToList();
        try
        {
            if (tx.Restore is not (RestoreState.NotNeeded or RestoreState.Restored))
                throw new InvalidOperationException("the P0 rollback is not known, so the coupled controls cannot be settled");

            void VerifyP0()
            {
                if (Transaction.FirstDifference(tx.Before, Pstates20Reader.Read(hw, gpu).Raw, Pstates20Layout.StatusRanges) != null)
                    throw new InvalidOperationException("the pre-operation P0 controls did not return");
            }
            void VerifyTopology()
            {
                var (_, current) = VfReader.ReadControls(hw, gpu);
                if (current == null || current.Layout != original.Layout || !current.Mask.AsSpan().SequenceEqual(original.Mask)
                    || !current.Points.Select(p => p.Index).SequenceEqual(original.Points.Select(p => p.Index)))
                    throw new InvalidDataException("the V/F control topology changed during the P0 write");
            }

            VerifyP0();
            VerifyTopology();
            // This is rollback to the captured pre-write state, not a new curve request. Restore only changed
            // Core offsets, selecting those points in the request mask; normal transaction readback proves it.
            var restore = new Transaction(hw).Run(gpu,
                VfWrites.Spec(original.Mask, (_, _) => { VerifyTopology(); return null; }),
                raw => VfWrites.RestoreControls(original, raw));
            log.AddRange(restore.Log.Select(line => "coupled V/F rollback: " + line));
            if (!restore.Succeeded) throw new InvalidOperationException(restore.Detail);
            VerifyP0();
            return tx with
            {
                Restore = restore.Outcome == TxOutcome.Applied ? RestoreState.Restored : tx.Restore,
                Detail = tx.Detail + "; pre-operation P0 and V/F controls verified restored", Log = log,
            };
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or OverflowException)
        {
            log.Add("coupled V/F rollback could not be verified: " + e.Message);
            return tx with { Restore = RestoreState.RestoreFailed,
                Detail = tx.Detail + "; coupled V/F rollback could not be verified: " + e.Message, Log = log };
        }
    }

    private static ClockOffset? Entry(byte[]? raw, ClockDomain domain)
    {
        if (raw == null || raw.Length == 0) return null;
        try { return Pstates20Parser.Parse(raw).P0(domain); }
        catch (InvalidDataException) { return null; }
    }
}
