using System.Globalization;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Domain.Writes;

/// <summary>
/// What a plan may need to know beyond the value.
/// <list type="bullet">
/// <item><see cref="AllowCreate"/>: whether a request that does not exist yet may be created (a power request, which
/// the driver cannot delete afterwards). Recovery may always create: putting a recorded original back is never a
/// new creation.</item>
/// <item><see cref="Original"/>: the journal's original for this cell when this program owns it, in the cell's own
/// encoding. A path whose rules bound a direction (a rail may not be raised above the ceiling) judges a request
/// against this origin as well as the current state: a value that is no worse than where the user's hardware stood
/// before this program touched it is a return, not an excursion. Without it, a lowered stock value that sits above
/// the ceiling could never be put back by the user. Never a value the user asked for.</item>
/// <item><see cref="IsRecovery"/>: the value is a recorded original going back. Recovery is not held to the
/// derived-value limits a landed write is checked against afterwards: the original is a state the driver itself
/// held, and refusing to return to it would keep this program's own change in place, which is the one thing
/// recovery exists to undo. A path that takes an origin gets the value itself as the origin.</item>
/// </list>
/// </summary>
public sealed record WriteOptions(bool AllowCreate = false, bool IsRecovery = false, string? Original = null)
{
    public static readonly WriteOptions None = new();
    public static readonly WriteOptions Recovery = new(AllowCreate: true, IsRecovery: true);
}

/// <summary>
/// A write path's answer to "set this key to this value": the cell, and the plan that does it from a fresh
/// snapshot, or a null plan with the reason when the value is one this path cannot write from.
/// </summary>
public sealed record Prepared(OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite = null);

/// <summary>What a setter's equal-value drill found: the real setter was called with exactly what was just read,
/// and nothing the path can observe moved.</summary>
public sealed record EqualValueOutcome(bool Proven, bool Written, NvResult? Driver, string? FirstDifference, object? Before, object? After);

/// <summary>
/// One tuning write path as the registry sees it: the journal kind, the keys it accepts, the value encoding, and
/// up to two functions. <see cref="Prepare"/> gives the cell and the plan for a value; it serves a user's write
/// (value = requested) and recovery (value = the journal's original) alike. <see cref="AfterWrite"/> names, for
/// the reader of a ping, what the cell's post-write check proves (null when the path has none; the check itself
/// sits in the cell's <see cref="TransactionSpec.CheckAfterWrite"/>). <see cref="ProveEqual"/>, when present, is
/// the setter's equal-value drill: the transaction template cannot do it (it skips equal writes, correctly), so
/// a path that needs one supplies it.
/// </summary>
public sealed record WritePath(
    string Kind,
    string Keys,
    string Values,
    Func<NvClient, GpuSession, string, string, WriteOptions, Prepared> Prepare,
    string? AfterWrite = null,
    Func<NvClient, GpuSession, ApplySentinel, string, EqualValueOutcome>? ProveEqual = null);

/// <summary>
/// The one list of what this program can write, keyed by the journal's kind. Every write, whether a user's request
/// through the broker or recovery giving an original back, is "set kind/key to value" through an entry here, so
/// the set of writable things, their encodings and their limits are stated once. The broker exposes exactly this
/// list and nothing lower (no slot, no register).
/// <para>
/// Two kinds of limit live in two places, and the split is forced by when each can be known. A limit on the value
/// itself (a clock range from the driver's topology, a watt range from the board, a fan range from the firmware,
/// the rail ceiling on a requested offset) is checked in the plan, on the fresh snapshot, before any byte reaches
/// the driver. A limit on what the driver <em>derives</em> from the value (the rail MAX that a boost percentage or
/// a rail offset moves) can only be read after the write, so it is the cell's
/// <see cref="TransactionSpec.CheckAfterWrite"/>: the transaction runs it after a matching readback, still inside
/// the GPU lock, and a veto puts the edited bytes back with the transaction's own restore. That placement is not
/// a convenience. The restore is rule-free by construction (the previous bytes are a state the driver held), where
/// a give-back planned through the path's own rules can be refused by them, since those rules bound requests; and
/// nothing that writes the cell, a user's write, recovery, the broker, can skip a check the cell carries.
/// Recovery is the one caller for which the registry leaves the derived-limit check off (<see cref="WriteOptions"/>).
/// </para>
/// <para>
/// Both kinds of limit are relative where the hardware's own state can already sit past them: a rail's stock OV on
/// the RTX 5090 is 1.2 V, above the 1.15 V standard ceiling, and another tool may leave a MAX anywhere. So a rule
/// refuses what gets worse than both the current state and the origin (the ceiling bounds excursions, not
/// positions), and the post-write check judges each MAX against the ceiling or where it stood before the write,
/// whichever is higher. The reachable set is still bounded by max(ceiling, where the hardware already was).
/// </para>
/// </summary>
public static class WriteRegistry
{
    public static IReadOnlyList<WritePath> All { get; } =
    [
        new(ClockOffsets.Kind, "core | memory", "kHz, signed integer; the driver's range, and for core the budget the V/F points already spend",
            (hw, gpu, key, value, o) =>
            {
                if (!Enum.TryParse<ClockDomain>(key, ignoreCase: true, out var domain) || !Enum.IsDefined(domain))
                    throw new ArgumentException($"'{key}' is not a P0 clock domain (core, memory)");
                var cell = ClockOffsets.Cell(domain);
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var khz))
                    return new Prepared(cell, null, $"'{value}' is not a number of kilohertz");
                // The points are read here: the core offset spends one firmware budget with them, and any Pstates20
                // write resets them. Recovery too: see ClockOffsets.Plan.
                return new Prepared(cell, ClockOffsets.Plan(domain, khz, ClockOffsets.Curve(hw, gpu)));
            }),

        new(PowerCap.Kind, "fe | f8", "mW, or 'absent'",
            (hw, gpu, key, value, o) =>
            {
                var kind = key.ToLowerInvariant() switch
                {
                    "fe" => PowerRequestKind.Ordinary,
                    "f8" => PowerRequestKind.Additional,
                    _ => throw new ArgumentException($"'{key}' is not a power request (fe, f8)"),
                };
                var board = PowerRequestsReader.ReadBoard(hw, gpu);
                var cell = PowerCap.Cell(board, kind);
                // A numeric value is a request to hold; "absent" (an original recorded before our write created the
                // request) cannot be written: the driver offers no way to delete a request.
                return PowerCap.Decode(value) is { } mw
                    ? new Prepared(cell, PowerCap.Plan(board, kind, mw, o.AllowCreate))
                    : new Prepared(cell, null, $"the request was absent before and the driver offers no way to delete one; write the driver maximum ({board.MaxMw} mW) deliberately to make it non-limiting");
            }),

        new(RailWrites.Kind, "nvvdd | msvdd", "slot=µV[,slot=µV…] over rel, alt, ov, vmin; a write may name some slots, recovery names all",
            (hw, gpu, key, value, o) =>
            {
                // The plan judges the request against the current state and the origin (the journal's original, or
                // the value itself when recovery is putting it back); the post-write check is bound to the snapshot
                // the plan was made on, so MAX and VMIN as the driver derives them are judged relative to it.
                var (cell, plan, verify) = RailWrites.PrepareVerified(hw, gpu, key, value, o.IsRecovery ? value : o.Original);
                return Checked(new Prepared(cell, plan), o, verify);
            },
            AfterWrite: "every rail's MAX and VMIN, as the driver derives them from the new offsets, are within the standard ceiling or no worse than before the write"),

        new(ClockDomainOffsets.Kind, "xbar | sys | video", "kHz, signed integer; the driver's topology range is the limit",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = ClockDomainOffsets.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan, plan == null ? $"'{value}' is not a number of kilohertz" : null);
            }),

        new(ClockVdemand.Kind, "xbar | sys | video", "µV, signed integer, the standard range −25000..50000",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = ClockVdemand.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan, plan == null ? $"'{value}' is not a number of microvolts" : null);
            }),

        new(VoltBoost.Kind, "independent | legacy", "percent, 0..100",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = VoltBoost.Prepare(hw, gpu, key, value);
                var prepared = new Prepared(cell, plan, plan == null ? $"'{value}' is not a boost percentage (0..{VoltBoost.MaxPercent})" : null);
                if (plan == null || o.IsRecovery) return prepared;
                // What a percentage does to every rail's MAX is only known after the write, and is judged against
                // where each MAX stood before it: a MAX another tool left above the ceiling can still be lowered.
                var before = BoostReader.Observe(hw, gpu);
                return Checked(prepared, o, (h, g) => BoostReader.Observe(h, g).CeilingViolation(before));
            },
            AfterWrite: "every rail's MAX, as the driver derives it from the percentage, is within the standard ceiling or no higher than before the write",
            ProveEqual: (hw, gpu, sentinel, key) =>
            {
                var path = key.ToLowerInvariant() switch
                {
                    "independent" => BoostPath.Independent,
                    "legacy" => BoostPath.Legacy,
                    _ => throw new ArgumentException($"'{key}' is not a boost path (independent, legacy)"),
                };
                var p = BoostProof.EqualValue(hw, gpu, sentinel, path);
                object View(BoostObservation o) => new { independent_percent = o.IndependentPercent, legacy_percent = o.LegacyPercent, max_uv = o.Rails.Select(r => r.MaxUv).ToArray() };
                return new EqualValueOutcome(p.Proven, p.Written, p.Driver, p.FirstDifference, View(p.Before), View(p.After));
            }),

        new(FanControl.Kind, "fan channel id", "auto, or manual:LEVEL inside the firmware's [min, max]",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = FanControl.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan);
            }),

        new(FanPolicyWrites.Kind, "fan policy index (an editable one)", "three points degC:duty%[:rpm], comma-separated, or 'stock' (the journal's original); temperatures increasing, duty <= 100 % and rpm <= 0x7fff, both never falling; a point without rpm takes the policy's own duty-to-rpm pairing",
            (hw, gpu, key, value, o) =>
            {
                // The origin is the stock curve this program first found: what "stock" writes, the rpm reference, and the
                // one target a request's temperature bound does not judge. Recovery passes the original itself.
                var (cell, plan, cannot) = FanPolicyWrites.Prepare(hw, gpu, key, value, o.IsRecovery ? value : o.Original, o.IsRecovery);
                return new Prepared(cell, plan, cannot);
            }),

        new(CurrentLimitWrites.Kind, "nvvdd | msvdd", "mA; the driver's range",
            (hw, gpu, key, value, o) =>
            {
                // The record is identified afresh (one current-limit record on the rail's regulator output).
                var (cell, plan, cannot) = CurrentLimitWrites.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan, cannot);
            }),

        new(VfWrites.Kind, VfWrites.CoreKey, "kHz per editable Core V/F point, runs collapsed as offset*count (\"0*127\" is the stock curve); the firmware's core offset range bounds each point and global + point",
            (hw, gpu, key, value, o) =>
            {
                // The cell carries its own check (the curve must validate with the same layout after the write): a
                // correctness proof of the write, not a limit, so it holds for recovery too.
                var (cell, plan) = VfWrites.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan);
            },
            AfterWrite: "the whole curve validates again, with the layout it had before the write"),
    ];

    public static IEnumerable<string> Kinds => All.Select(p => p.Kind);

    /// <summary>The path for a journal kind, or null when this version has none (an older record, a typo).</summary>
    public static WritePath? Find(string? kind) =>
        kind == null ? null : All.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>A derived-limit check on the cell for a write; left off for recovery and for a value that has no plan.</summary>
    private static Prepared Checked(Prepared p, WriteOptions o, Func<NvClient, GpuSession, string?> check) =>
        p.Plan == null || o.IsRecovery ? p : p with { Cell = p.Cell with { Spec = p.Cell.Spec with { CheckAfterWrite = check } } };
}
