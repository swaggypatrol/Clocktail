using Clocktail.Hw;

namespace Clocktail.Domain.Ownership;

/// <summary>
/// One hardware value whose original belongs to the user: what the journal records and gives back.
/// <see cref="Read"/> returns the value's state in a snapshot in the journal's canonical encoding (the same string
/// is what gets written to disk and what equality means), or null when the snapshot does not hold it.
/// </summary>
public sealed record OwnedCell(
    string Kind,
    string Key,
    string Unit,
    string Note,
    TransactionSpec Spec,
    Func<byte[], string?> Read,
    /// <summary>Keep the original while one of these kinds is held: restoring that control can change this cell.</summary>
    IReadOnlyList<string>? CoupledKinds = null)
{
    public string Id => $"{Kind}/{Key}";

    /// <summary>The cell's state in a snapshot; null when it cannot be read from it (absent, malformed, empty).</summary>
    public string? StateIn(byte[]? snapshot)
    {
        if (snapshot == null || snapshot.Length == 0) return null;
        try { return Read(snapshot); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or FormatException) { return null; }
    }
}

/// <summary>Where the journal stands for one cell after a write.</summary>
public enum Hold
{
    /// <summary>No record for this cell: nothing was written, so nothing is owed.</summary>
    NotHeld,
    /// <summary>The cell reads back at its original value; the record was dropped.</summary>
    Released,
    /// <summary>The record stays: the known value differs from the original, or a coupled control still needs it.</summary>
    Held,
    /// <summary>The record stays and the cell's current value is unknown (or was not read).</summary>
    HeldUnknown,
}

public sealed record OwnedWriteResult(
    TxResult Tx,
    /// <summary>The cell in the snapshot the decision was made on; null when there was no snapshot.</summary>
    string? Before,
    /// <summary>The cell after the call; for Aborted, the same as <see cref="Before"/> (nothing reached the driver).</summary>
    string? After,
    /// <summary>The user's original, when the journal holds one for this cell (before or after the call).</summary>
    string? Original,
    Hold Hold,
    string Ownership,
    /// <summary>The hardware state of this cell is known after the call (nothing written, verified, or verifiably restored).</summary>
    bool StateKnown,
    bool SentinelCleared);

/// <summary>
/// The ownership discipline every tuning write shares, in one place:
/// <list type="number">
/// <item>the original belongs to the user, not to a pid: a dead owner's record is adopted, a live one is respected;</item>
/// <item>the sentinel covers only the window in which the hardware state can become unknown, the transaction;</item>
/// <item>the record of the original is saved in the transaction's write-ahead step, so it is on disk before the
/// first byte reaches the driver and comes from the very snapshot the write was planned on;</item>
/// <item>the record is dropped when the cell verifiably reads back at its original and no coupled control needs it;</item>
/// <item>marks from before this boot leave only through the crash log: what was in flight when the machine went
/// down is on record before either mark is dropped (<see cref="CrashLog.SettleReboot"/>), whichever call comes first.</item>
/// </list>
/// Clock offsets, power requests and recovery are this, with a different cell and a different plan.
/// </summary>
public sealed class OwnedWriter(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    /// <summary>
    /// A user-initiated write: refuses a stale sentinel unless taken over, and clears it when the state is known. A
    /// limit the driver only reveals after the write (a rail MAX moved by an offset or a boost percentage) is the
    /// cell's own <see cref="TransactionSpec.CheckAfterWrite"/>: the transaction judges it inside the GPU lock and
    /// restores the edited bytes itself on a veto, so here a vetoed write is a failed, restored one, which releases
    /// the record like any write that reads back at its original.
    /// </summary>
    /// <param name="target">The value the cell is being set to, in its own encoding, for the sentinel: what was in
    /// flight if the machine goes down inside the window.</param>
    /// <param name="settle">Optional coupled-state settlement, under the GPU lock before ownership is released
    /// or the sentinel is cleared. It must report an unverified rollback as an unknown transaction outcome.</param>
    public OwnedWriteResult Apply(GpuSession gpu, OwnedCell cell, Func<ReadOnlyMemory<byte>, Mutation> plan, bool takeOver = false, string? target = null, Action? beforeWrite = null, Func<TxResult, TxResult>? settle = null)
    {
        CrashLog.SettleReboot(journal, sentinel, host);   // a take-over must not overwrite the one record of a crash
        if (sentinel.IsBlocked(out var what) && !takeOver)
            throw new InvalidOperationException($"a previous application did not complete ({what}); run --recover, or rerun with --take-over after inspecting the GPU state");
        string? original = AdoptedOriginal(cell);   // refuses a live owner before any sentinel exists

        var scope = sentinel.Begin("manual", takeOver, $"{cell.Id} <- {target ?? "(planned)"} {cell.Unit}")
            ?? throw new InvalidOperationException("the apply sentinel could not be created");
        var r = Run(gpu, cell, plan, original, beforeWrite: beforeWrite, settle: settle);
        if (r.StateKnown) scope.Complete();
        return r with { SentinelCleared = r.StateKnown };
    }

    /// <summary>Read the cell as it is now, outside any transaction (the same query its transaction would ask). Null when unreadable.</summary>
    public string? Look(GpuSession gpu, OwnedCell cell) => Look(hw, gpu, cell);

    /// <summary><see cref="Look(GpuSession, OwnedCell)"/> for a reader that holds no journal (a survey of the card).</summary>
    public static string? Look(NvClient hw, GpuSession gpu, OwnedCell cell)
    {
        var buf = cell.Spec.ReadBuffer();
        var r = cell.Spec.Index is { } i ? hw.Call(gpu, cell.Spec.Get, i, buf) : hw.Call(gpu, cell.Spec.Get, buf);
        return r.IsOk ? cell.StateIn(buf) : null;
    }

    /// <summary>The journal half without the sentinel, for a caller that holds one window over several writes.
    /// retainOriginal defers release until the caller verifies the entire coupled operation and calls ReleaseIfRestored.</summary>
    public OwnedWriteResult Write(GpuSession gpu, OwnedCell cell, Func<ReadOnlyMemory<byte>, Mutation> plan, bool retainOriginal = false) =>
        Run(gpu, cell, plan, AdoptedOriginal(cell), retainOriginal);

    /// <summary>Record a coupled cell before a different setter can change it implicitly. An existing original wins.</summary>
    public void ClaimOriginal(OwnedCell cell, byte[] snapshot)
    {
        if (AdoptedOriginal(cell) != null) return;
        var value = cell.StateIn(snapshot) ?? throw new InvalidOperationException($"{cell.Id} cannot be read, so its original cannot be recorded");
        journal.Claim(new OwnedItem(cell.Kind, cell.Key, value, host.Now, cell.Note));
    }

    /// <summary>Settle a coupled original after its other control was released, from a fresh read and no write.</summary>
    public void ReleaseIfRestored(GpuSession gpu, OwnedCell cell)
    {
        var original = AdoptedOriginal(cell);
        if (original != null && !CoupledHeld(cell) && Look(gpu, cell) == original) journal.Release(cell.Kind, cell.Key);
    }

    private bool CoupledHeld(OwnedCell cell) =>
        cell.CoupledKinds?.Any(k => journal.Inspect().Items.Any(i => i.Kind == k)) ?? false;

    private string? AdoptedOriginal(OwnedCell cell) => AdoptedOriginal(cell.Kind, cell.Key);

    /// <summary>
    /// The original this process owes for a cell, from the record it owns or adopts (an earlier run's record whose
    /// owner is gone), or null when the cell is not held. Refuses a record that is not this process's to use (a live
    /// owner, another GPU) before any sentinel exists, exactly as a write would. A caller planning a write asks this
    /// first when its path judges a request against the origin as well as the current state (a rail slot may return to
    /// where the hardware stood before this program touched it, even above the ceiling); the key is matched as the
    /// paths parse it, ignoring case, because the journal holds the cell's canonical spelling.
    /// </summary>
    public string? AdoptedOriginal(string kind, string key)
    {
        CrashLog.SettleReboot(journal, sentinel, host);   // before Adopt drops a record from before the boot
        var record = journal.Adopt();
        if (record.Reason is not (RecoveryReason.Nothing or RecoveryReason.OwnedByThisProcess))
            throw new InvalidOperationException($"the ownership record is not this process's to use ({record.Reason}: {record.Detail}); run --recover");
        return record.Items.FirstOrDefault(i => string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase) && string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))?.Original;
    }

    private OwnedWriteResult Run(GpuSession gpu, OwnedCell cell, Func<ReadOnlyMemory<byte>, Mutation> plan, string? original, bool retainOriginal = false, Action? beforeWrite = null, Func<TxResult, TxResult>? settle = null)
    {
        if (!gpu.TryEnter(cell.Spec.Timeout, out var gate))
            return Describe(new TxResult(TxOutcome.Aborted, RestoreState.NotNeeded,
                "another operation holds this GPU; nothing was written", [], null, null, []));
        using (gate)
        {
            // Several writes in a row owe the driver the state before the first of them: an existing record wins.
            var tx = new Transaction(hw).Run(gpu, cell.Spec, plan, writeAhead: snapshot =>
            {
                beforeWrite?.Invoke();
                if (original != null) return null;
                var value = cell.StateIn(snapshot)
                    ?? throw new InvalidOperationException($"{cell.Id} cannot be read from the snapshot, so its original cannot be recorded");
                journal.Claim(new OwnedItem(cell.Kind, cell.Key, value, host.Now, cell.Note));
                original = value;
                return $"journal: {cell.Id} claimed, original {value} {cell.Unit}";
            });
            if (settle != null) tx = settle(tx);
            return Describe(tx);
        }

        OwnedWriteResult Describe(TxResult tx)
        {
            string? before = cell.StateIn(tx.Before);
            // Aborted is total: nothing reached the driver, so the cell is what the snapshot said.
            string? after = tx.After != null ? cell.StateIn(tx.After) : tx.Outcome == TxOutcome.Aborted ? before : null;
            bool stateKnown = tx.Succeeded || tx.Outcome == TxOutcome.Aborted || tx.Restore is RestoreState.NotNeeded or RestoreState.Restored;
            // A verified restore put the snapshot's value back; the readback in tx.After predates the restore.
            string? now = tx.Restore == RestoreState.Restored ? before : after;

            Hold hold;
            string ownership;
            if (original == null)
            {
                hold = Hold.NotHeld;
                ownership = tx.Outcome == TxOutcome.NoChange ? "nothing held: already at the requested value" : "nothing held: nothing was written";
            }
            else if (stateKnown && now == original && !retainOriginal && !CoupledHeld(cell))
            {
                journal.Release(cell.Kind, cell.Key);
                hold = Hold.Released;
                ownership = $"released: back at its original value ({original} {cell.Unit})";
            }
            else if (stateKnown && now != null)
            {
                hold = Hold.Held;
                ownership = $"held: original {original} {cell.Unit}, now {now} {cell.Unit}";
            }
            else
            {
                hold = Hold.HeldUnknown;
                ownership = stateKnown
                    ? $"held: original {original} {cell.Unit}; current value not read"
                    : $"held: original {original} {cell.Unit}; hardware state unknown";
            }
            return new OwnedWriteResult(tx, before, now, original, hold, ownership, stateKnown, SentinelCleared: false);
        }
    }
}
