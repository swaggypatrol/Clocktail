using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Power;

/// <summary>Rules for a watt request: the gate refuses 0 and 0xFFFFFFFF; FE is bounded by the board's
/// [min, max]; F8 has an upper bound only. Both bounds come from the fresh INFO read, never from a cache.</summary>
public static class PowerRequestRules
{
    public static string? Validate(BoardPowerInfo board, PowerRequestKind kind, uint mw)
    {
        if (mw is 0 or uint.MaxValue) return "a request of 0 or 0xFFFFFFFF is what the driver uses for 'absent'; it cannot be written";
        if (mw > board.MaxMw) return $"{mw / 1000.0:0.###} W is above the driver maximum {board.MaxMw / 1000.0:0.###} W";
        if (kind == PowerRequestKind.Ordinary && mw < board.MinMw) return $"{mw / 1000.0:0.###} W is below the driver minimum {board.MinMw / 1000.0:0.###} W";
        return null;
    }

    /// <summary>The set request: the query shape plus the value.</summary>
    public static byte[] BuildSetRequest(int board, PowerRequestKind kind, uint mw)
    {
        var b = PowerRequestsParser.BuildControlQuery(board, kind);
        b[0..4].CopyTo(b, 0);   // version word is shared by GetControl and SetControl (both 0x528 r1)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PowerRequestsLayout.ControlEntry(board)), mw);
        return b;
    }

    /// <summary>
    /// Decide on the fresh query answer. Equal value: nothing to write. Otherwise a request whose expected effect is
    /// the value field of that board's entry; the restore request puts the previous value back when there was one.
    /// An absent request cannot be recreated (no delete API), so the restore is honestly null then, and creating
    /// one is refused unless <paramref name="allowCreate"/> says a person chose that step (on the 5090: F8).
    /// </summary>
    public static Mutation Plan(BoardPowerInfo board, PowerRequestKind kind, uint mw, byte[] freshControl, bool allowCreate = false)
    {
        if (Validate(board, kind, mw) is { } why) return new Mutation.Abort(why);
        var current = PowerRequestsParser.ParseControl(freshControl, board.Board, kind);
        if (current.Mw == mw) return new Mutation.Apply([]);
        if (!current.Present && !allowCreate)
            return new Mutation.Abort($"no {PowerCap.Key(kind).ToUpperInvariant()} request exists yet, and the driver offers no way to delete one once created; add --allow-new-request to create it deliberately");
        return new Mutation.Request(
            BuildSetRequest(board.Board, kind, mw),
            [Edit.UInt32(current.ValueOffset, mw)],
            current.Present ? BuildSetRequest(board.Board, kind, current.RawValue) : null);
    }
}

public sealed record PowerCapResult(
    PowerRequestKind Kind,
    uint RequestedMw,
    /// <summary>The request in the snapshot the write was planned on: null when absent or when there was no snapshot.</summary>
    uint? BeforeMw,
    uint? AfterMw,
    TxResult Tx,
    string Ownership,
    bool SentinelCleared)
{
    public bool Succeeded => Tx.Succeeded;
}

/// <summary>
/// A watt request is one owned cell: the CONTROL structure read as a query (board bit + command) and written as a
/// request, the board entry's value as the cell, <see cref="PowerRequestRules"/> as the plan. The board's range
/// comes from the INFO read, which is not owned and is read fresh for every call. Ownership is <see cref="OwnedWriter"/>.
/// </summary>
public sealed class PowerCap(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "power";
    public const string Absent = "absent";

    public static string Key(PowerRequestKind k) => k == PowerRequestKind.Ordinary ? "fe" : "f8";

    /// <summary>F8 is read back until it shows twice in a row, up to 16 times 100 ms apart, as the original program did
    /// (re/power-thermal.md §6); FE lands at once (drills 12/13).</summary>
    public static TransactionSpec Spec(int board, PowerRequestKind kind) => new(
        Slot.ClientPwrPoliciesGetControl, Slot.ClientPwrPoliciesSetControl,
        PrepareRead: b => PowerRequestsParser.BuildControlQuery(board, kind).CopyTo(b, 0),
        ReadbackTries: kind == PowerRequestKind.Additional ? 16 : 1);

    public static string Encode(uint? mw) => mw?.ToString(CultureInfo.InvariantCulture) ?? Absent;
    public static uint? Decode(string? s) => s is null or Absent ? null : uint.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>The request as an owned cell. An absent F8 reads as the board's maximum: the driver cannot delete a request,
    /// so once one is created the closest it can go back to is non-limiting, and that is the original the journal keeps
    /// and a give-back writes.</summary>
    public static OwnedCell Cell(BoardPowerInfo board, PowerRequestKind kind) => new(
        Kind, Key(kind), "mW", $"{kind} power request, mW", Spec(board.Board, kind),
        raw => Encode(PowerRequestsParser.ParseControl(raw, board.Board, kind).Mw ?? (kind == PowerRequestKind.Additional ? board.MaxMw : null)));

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(BoardPowerInfo board, PowerRequestKind kind, uint mw, bool allowCreate = false) =>
        raw => PowerRequestRules.Plan(board, kind, mw, raw.ToArray(), allowCreate);

    public PowerCapResult Apply(GpuSession gpu, PowerRequestKind kind, uint mw, bool takeOver = false, bool allowCreate = false)
    {
        var board = PowerRequestsReader.ReadBoard(hw, gpu);
        var r = new OwnedWriter(hw, journal, sentinel, host).Apply(gpu, Cell(board, kind), Plan(board, kind, mw, allowCreate), takeOver, mw.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new PowerCapResult(kind, mw, Decode(r.Before), Decode(r.After), r.Tx, r.Ownership, r.SentinelCleared);
    }
}
