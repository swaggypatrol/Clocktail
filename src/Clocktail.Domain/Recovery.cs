using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Domain;

public enum RecoveryAction
{
    /// <summary>The hardware already held the original value; the record was dropped without a write.</summary>
    Released,
    /// <summary>The original value was written back through the transaction template, verified, and the record dropped.</summary>
    Written,
    /// <summary>The give-back was attempted and did not verify; the record stays.</summary>
    Failed,
    /// <summary>Nothing was written: the item needs a write this process may not make, a decision only a person
    /// can make, or it is of a kind this version cannot read.</summary>
    Refused,
}

public sealed record RecoveredItem(
    string Kind,
    string Key,
    string Original,
    string? Current,
    RecoveryAction Action,
    string Detail,
    TxResult? Tx,
    /// <summary>The item's hardware state was looked at and is known after recovery.</summary>
    bool StateKnown);

public sealed record RecoveryResult(
    RecoveryReason Reason,
    string Detail,
    IReadOnlyList<RecoveredItem> Items,
    bool JournalCleared,
    bool SentinelCleared,
    /// <summary>True when at least one item still needs a write that this process could not make (not administrator).</summary>
    bool NeedsWrite,
    /// <summary>The crash log a record was appended to: a write window was open when the machine went down.</summary>
    string? CrashRecorded = null)
{
    public bool Clean => JournalCleared && SentinelCleared;
}

/// <summary>
/// What the next start does about records an earlier process left behind. Recovery is not a second write path:
/// giving an item back is writing its original through <see cref="OwnedWriter"/>, the same discipline as any
/// other write, after looking at the item first so that only what differs is written.
/// <para>
/// The journal and the sentinel answer different questions. The journal says what is owed; a debt whose current
/// value is known is the normal state of a tuned GPU, not an emergency. The sentinel says a write window was open
/// when a process died, so the hardware may hold a state nobody has looked at. Recovery holds one sentinel window
/// over its own writes and clears it once every recorded item has been looked at and is in a known state, whether
/// or not every debt could be paid.
/// </para>
/// </summary>
public sealed class Recovery(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public RecoveryResult Run(GpuSession gpu, bool canWrite)
    {
        var plan = journal.Inspect();
        bool blocked = sentinel.IsBlocked(out _);
        var items = new List<RecoveredItem>();

        switch (plan.Reason)
        {
            case RecoveryReason.Nothing:
                // A sentinel with no record means the apply died before claiming anything or after releasing everything.
                if (blocked) sentinel.Clear();
                return new RecoveryResult(plan.Reason, blocked ? "no record; a stale sentinel was cleared" : plan.Detail, items, true, true, false);

            case RecoveryReason.OwnedByLiveProcess:
            case RecoveryReason.Foreign:
                foreach (var i in plan.Items) items.Add(new RecoveredItem(i.Kind, i.Key, i.Original, null, RecoveryAction.Refused, plan.Detail, null, false));
                return new RecoveryResult(plan.Reason, plan.Detail, items, false, !blocked, false);

            case RecoveryReason.Rebooted:
                // A window still open across a reboot is the one evidence that a write took the machine down: what was in
                // flight and what was held are written to the crash log before both marks are cleared as reset. The same
                // step runs first in every write, so the evidence survives whichever op comes first after the reboot.
                string? crashLog = CrashLog.SettleReboot(journal, sentinel, host);
                foreach (var i in plan.Items) items.Add(new RecoveredItem(i.Kind, i.Key, i.Original, null, RecoveryAction.Released, "reset by the reboot", null, true));
                journal.Clear();
                sentinel.Clear();
                return new RecoveryResult(plan.Reason, crashLog != null ? $"{plan.Detail}; a write window was open when the machine went down, recorded in {crashLog}" : plan.Detail,
                    items, true, true, false, crashLog);
        }

        // OwnerDied, or a record carrying this pid: look at every item and give back what differs, in one window.
        if (!blocked) sentinel.Begin("recovery", takeOver: false, string.Join("; ", plan.Items.Select(i => $"{i.Id} <- {i.Original}")));
        var writer = new OwnedWriter(hw, journal, sentinel, host);
        bool needsWrite = false;
        // P0 offsets first: a Pstates20 write resets the V/F point offsets, so with the curve in the set the P0 offsets go
        // back first, wiping the curve this broker holds, and the curve's original goes back after them. Giving the curve
        // back first stranded a P0 offset whenever the curve's original was not all zero: the ST08 drill of 2026-10-21 (an
        // original of +15 MHz on every point, from before Clocktail's first write) left the memory offset refused, and the
        // daily profile after it. Without the curve in the set a P0 write over a curve that is not ours is still refused.
        bool curveFollows = plan.Items.Any(i => i.Kind == VfWrites.Kind);
        foreach (var i in plan.Items.OrderBy(i => i.Kind == ClockOffsets.Kind ? 0 : 1))
        {
            var (item, needsAdmin) = GiveBack(gpu, writer, i, canWrite, curveFollows);
            items.Add(item);
            needsWrite |= needsAdmin;
        }
        // A window recovery opened itself stays up only for a write of its own whose outcome is unknown. An item it could
        // not prepare or read (a kind this version does not know, an unreadable cell) wrote nothing: its debt stays in
        // the journal and in the answer, but a sentinel for it locked out every later write with nothing in flight.
        if (items.All(i => i.StateKnown) || !blocked && !items.Any(i => !i.StateKnown && i.Tx != null)) sentinel.Clear();
        bool journalCleared = journal.Inspect().Reason == RecoveryReason.Nothing;
        return new RecoveryResult(plan.Reason, plan.Detail, items, journalCleared, !sentinel.IsBlocked(out _), needsWrite);
    }

    private (RecoveredItem Item, bool NeedsAdmin) GiveBack(GpuSession gpu, OwnedWriter writer, OwnedItem item, bool canWrite, bool curveFollows)
    {
        RecoveredItem Item(string? current, RecoveryAction action, string detail, TxResult? tx, bool known) =>
            new(item.Kind, item.Key, item.Original, current, action, detail, tx, known);

        // What this version knows how to give back is what the write-path registry can prepare: the cell, and the
        // plan that writes the original. Recovery is the same "set kind/key to value" as the write that made the
        // debt, with the journal's original as the value; an original that existed is never a new creation.
        var path = WriteRegistry.Find(item.Kind);
        if (path == null)
            return (Item(null, RecoveryAction.Refused, $"this version cannot give back '{item.Kind}/{item.Key}'", null, false), false);
        Prepared prepared;
        try { prepared = path.Prepare(hw, gpu, item.Key, item.Original, WriteOptions.Recovery with { CurveFollows = curveFollows && item.Kind == ClockOffsets.Kind }); }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidDataException or InvalidOperationException)
        {
            return (Item(null, RecoveryAction.Refused, $"the record or the driver could not be read: {e.Message}", null, false), false);
        }
        var (cell, plan, cannotWrite) = prepared;

        var current = writer.Look(gpu, cell);
        if (current == null)
            return (Item(null, RecoveryAction.Refused, $"{cell.Id} could not be read; nothing written", null, false), false);
        if (current == item.Original && !(cell.CoupledKinds?.Any(k => journal.Inspect().Items.Any(i => i.Kind == k)) ?? false))
        {
            journal.Release(item.Kind, item.Key);
            return (Item(current, RecoveryAction.Released, "already at the original value; nothing written", null, true), false);
        }
        if (plan == null)
            return (Item(current, RecoveryAction.Refused, cannotWrite!, null, true), false);
        if (!canWrite)
            return (Item(current, RecoveryAction.Refused, $"now {current} {cell.Unit}, original {item.Original} {cell.Unit}; giving it back needs an administrator process", null, true), true);

        var w = writer.Write(gpu, cell, plan);
        if (w.Tx.Succeeded && w.Hold == Hold.Released)
            return (Item(current, RecoveryAction.Written, "original value written back and verified", w.Tx, true), false);
        return (Item(current, RecoveryAction.Failed, $"give-back did not verify: {w.Tx}", w.Tx, w.StateKnown), false);
    }
}
