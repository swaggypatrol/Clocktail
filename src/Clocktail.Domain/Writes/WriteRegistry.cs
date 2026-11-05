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
/// encoding. A path that adjusts a request (a rail offset rounded to the domain's step, a fan point's rpm taken from
/// its pairing) writes a value equal to the origin as it is: going back to where the hardware stood before this
/// program touched it is exact. Never a value the user asked for.</item>
/// <item><see cref="IsRecovery"/>: the value is a recorded original going back: a state the driver itself held, so
/// what bounds a request does not refuse it (refusing would keep this program's own change in place, which is the one
/// thing recovery exists to undo). A path that takes an origin gets the value itself as the origin.</item>
/// </list>
/// </summary>
/// <param name="CurveFollows">A P0 offset write whose caller gives the curve back right after it (recovery with the
/// curve in its set): the wipe a Pstates20 write does to the V/F points is then not a reason to refuse it.</param>
public sealed record WriteOptions(bool AllowCreate = false, bool IsRecovery = false, string? Original = null, bool CurveFollows = false)
{
    public static readonly WriteOptions None = new();
    public static readonly WriteOptions Recovery = new(AllowCreate: true, IsRecovery: true);
}

/// <summary>
/// A write path's answer to "set this key to this value": the cell, and the plan that does it from a fresh
/// snapshot, or a null plan with the reason when the value is one this path cannot write from.
/// </summary>
public sealed record Prepared(OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite = null)
{
    /// <summary>Other controls the setter can change implicitly. Save these originals in the transaction's
    /// write-ahead callback, even when their current value is zero.</summary>
    public IReadOnlyList<CoupledOriginal> CoupledOriginals { get; init; } = [];
}

public sealed record CoupledOriginal(OwnedCell Cell, byte[] Snapshot);

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
/// The limits are the hardware's own and structural ones, checked in the plan on the fresh snapshot before any byte
/// reaches the driver: a clock range from the driver's topology, a watt range from the board, a fan range from the
/// firmware, a rail's reported domain range and step, an encoding the field can hold, a units slip. No software tuning
/// envelope sits on top. What the driver <em>derives</em> after a write can only be read afterwards; where a path proves
/// its write that way (the V/F curve validating again), it is the cell's <see cref="TransactionSpec.CheckAfterWrite"/>:
/// the transaction runs it after a matching readback, still inside the GPU lock, and a veto puts the edited bytes back
/// with the transaction's own restore, which is rule-free by construction (the previous bytes are a state the driver
/// held). The curve's check is a proof of the write, not a limit, so it holds for recovery too.
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
                // write resets them. Recovery too: see ClockOffsets.Plan. Not when the curve is given back right after.
                if (o.CurveFollows) return new Prepared(cell, ClockOffsets.Plan(domain, khz));
                var (budget, controls) = ClockOffsets.CurveForWrite(hw, gpu);
                return new Prepared(cell, ClockOffsets.Plan(domain, khz, budget, canRecordCurveOriginal: controls != null))
                {
                    CoupledOriginals = controls == null ? [] : [new(VfWrites.ControlCell(controls), controls.Control)],
                };
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
                // The plan holds every slot inside the firmware-reported domain range and on its voltage step; the
                // origin (the journal's original, or the value itself when recovery is putting it back) is written as it
                // was, never rounded.
                var (cell, plan) = RailWrites.Prepare(hw, gpu, key, value, o.IsRecovery ? value : o.Original);
                return new Prepared(cell, plan);
            }),

        new(ClockDomainOffsets.Kind, "xbar | sys | video", "kHz, signed integer; the driver's topology range is the limit",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = ClockDomainOffsets.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan, plan == null ? $"'{value}' is not a number of kilohertz" : null);
            }),

        new(ClockVdemand.Kind, "graphics | xbar | sys | video", "µV, signed integer; past ±1000000 is refused as a units slip, inside it the driver decides",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = ClockVdemand.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan, plan == null ? $"'{value}' is not a number of microvolts" : null);
            }),

        new(VoltBoost.Kind, "independent | legacy", "percent, 0..100",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan) = VoltBoost.Prepare(hw, gpu, key, value);
                return new Prepared(cell, plan, plan == null ? $"'{value}' is not a boost percentage (0..{VoltBoost.MaxPercent})" : null);
            },
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

        new(FanStopWrites.Kind, "fan policy index (an editable one)", "the zero-RPM band stop:start, °C; stop under start, both within 0..120 °C",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan, cannot) = FanStopWrites.Prepare(hw, gpu, key, value, o.IsRecovery);
                return new Prepared(cell, plan, cannot);
            }),

        new(ClockLimitWrites.Kind, "max | min | voltage", "kHz for the maximum and minimum clock, µV for the voltage lock (1500000 = Boost Lock); 0 or 'off' releases",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan, cannot) = ClockLimitWrites.Prepare(key, value);
                return new Prepared(cell, plan, cannot);
            }),

        new(ClockRatio.Kind, "msvdd", "the XBAR/core ratio, U16.16 (58976 = 0.8999) or a decimal; above 0, inside that the driver decides",
            (hw, gpu, key, value, o) =>
            {
                var (cell, plan, cannot) = ClockRatio.Prepare(hw, gpu, key, value);
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
}
