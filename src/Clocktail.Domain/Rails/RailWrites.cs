using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Rails;

public sealed record RailApplyResult(
    Rail Rail,
    RailRules.Request Requested,
    /// <summary>The rail's offsets in the snapshot the write was planned on; null when there was no snapshot.</summary>
    RailOffsets? BeforeOffsets,
    RailOffsets? AfterOffsets,
    /// <summary>The evaluated limits the driver reported before planning and after the call.</summary>
    RailLimits? LimitsBefore,
    RailLimits? LimitsAfter,
    TxResult Tx,
    string Ownership,
    bool SentinelCleared,
    /// <summary>
    /// What the readback alone does not show and went wrong around the write: another rail moved as a side effect of a
    /// failed one, or the status could not be read after a landed one (MAX unverified). Null when nothing did.
    /// </summary>
    string? Violation,
    /// <summary>Findings, not failures: a requested offset rounded to the domain's step before it was written, and
    /// evaluated values that did not move by exactly the offset change (the driver's evaluation differs from
    /// "evaluated = base + offset").</summary>
    IReadOnlyList<string> Surprises)
{
    public bool Succeeded => Tx.Succeeded && Violation == null;
}

/// <summary>
/// A rail's four offsets are one owned cell: VoltVoltRailsGetControl read with the rail mask, written back
/// read-modify-write through VoltVoltRailsSetControl, the rail's control entry as the cell, and
/// <see cref="RailRules.Plan"/> as the plan. Ownership is <see cref="OwnedWriter"/>.
/// <para>
/// The plan needs more than the control structure: the evaluated limits (status), the rail identity (info) and
/// the voltage-domain bounds (devices). Those are read just before the transaction, and the plan refuses when
/// the transaction's own control snapshot holds different offsets than the ones that status was read against,
/// because then "evaluated = base + offset" would be computed from two different moments.
/// </para>
/// <para>
/// MAX is something the driver derives; it cannot be compared inside the control transaction. It is read right
/// after and shown, before and after, with any evaluated value that did not move by exactly the offset change.
/// </para>
/// </summary>
public sealed class RailWrites(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "rails";

    public static string Key(Rail r) => r.ToString().ToLowerInvariant();

    public static bool TryParseKey(string key, out Rail rail) => Enum.TryParse(key, ignoreCase: true, out rail) && Enum.IsDefined(rail);

    /// <summary>Control reads are queries: the rail mask goes in before the GET (as <see cref="RailsReader"/> does).</summary>
    public static TransactionSpec Spec(uint mask) => new(
        Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl,
        PrepareRead: b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), mask));

    /// <summary>Canonical encoding of a rail's offsets, µV: "rel=…,alt=…,ov=…,vmin=…".</summary>
    public static string Encode(RailOffsets o) => string.Create(CultureInfo.InvariantCulture, $"rel={o.RelUv},alt={o.AltUv},ov={o.OvUv},vmin={o.VminUv}");

    public static RailOffsets Decode(string s)
    {
        var map = s.Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0].Trim(), p => int.Parse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture));
        if (!new[] { "rel", "alt", "ov", "vmin" }.All(map.ContainsKey)) throw new FormatException($"'{s}' is not a complete rail state (rel, alt, ov, vmin)");
        return new RailOffsets(map["rel"], map["alt"], map["ov"], map["vmin"]);
    }

    /// <summary>The rail's offsets in a control structure alone; null when the mask does not hold the rail.</summary>
    public static RailOffsets? OffsetsIn(byte[] control, Rail rail)
    {
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(4));
        int bit = (int)rail;
        if ((mask >> bit & 1) == 0) return null;
        int n = BitOperations.PopCount(mask & ((1u << bit) - 1));
        int cb = RailsLayout.ControlBase + n * RailsLayout.ControlEntry;
        if (cb + RailsLayout.ControlEntry > control.Length) throw new InvalidDataException("rail control entry runs past the structure");
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(control.AsSpan(cb + o));
        return new RailOffsets(I(4), I(8), I(12), I(16));
    }

    public static OwnedCell Cell(Rail rail, uint mask) => new(
        Kind, Key(rail), "µV", $"{rail} voltage offsets (REL, ALT/OP, OV, VMIN), µV", Spec(mask),
        raw => OffsetsIn(raw, rail) is { } o ? Encode(o) : null);

    /// <summary>
    /// The plan, bound to the snapshot <paramref name="s0"/> it reasons from. It aborts when the transaction's
    /// control snapshot does not show every rail with the offsets <paramref name="s0"/> was read with.
    /// </summary>
    /// <param name="origin">The rail's offsets before we owned it (journal original; rollback: the pre-write snapshot); never a request.</param>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(RailsSnapshot s0, Rail rail, RailRules.Request req, RailOffsets? origin = null) => raw =>
    {
        var control = raw.ToArray();
        foreach (var r in s0.Rails)
            if (OffsetsIn(control, r.Rail) != r.Offsets)
                return new Mutation.Abort($"{Key(r.Rail)} offsets changed between the status read and the write snapshot; nothing written, try again");
        if (BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(4)) != s0.Mask)
            return new Mutation.Abort("the rail mask changed between reads");
        return Decide(s0, rail, req, origin);
    };

    /// <summary>Every rule a write must pass, on the snapshot alone (no hardware): the edits, or why not.</summary>
    public static Mutation Decide(RailsSnapshot s0, Rail rail, RailRules.Request req, RailOffsets? origin = null) =>
        s0.Get(rail) is { } record ? RailRules.Plan(record, req, origin) : new Mutation.Abort($"the driver does not report {rail}");

    public RailApplyResult Apply(GpuSession gpu, Rail rail, RailRules.Request req, bool takeOver = false)
    {
        var s0 = RailsReader.Read(hw, gpu);
        var before = s0.Get(rail);
        var writer = new OwnedWriter(hw, journal, sentinel, host);
        var cell = Cell(rail, s0.Mask);
        // The recorded original is the origin: a slot going back to it is written exactly, whoever asks.
        RailOffsets? original = journal.Load()?.Items.FirstOrDefault(i => i.Kind == Kind && i.Key == cell.Key)?.Original is { } o ? Decode(o) : null;
        var r = writer.Apply(gpu, cell, Plan(s0, rail, req, original), takeOver);

        // A failed write is restored field by field: only what this write changed goes back (Transaction step 6).
        // If the driver moved another rail as a side effect, that rail stays moved and nobody owns it; say so.
        if (r.Tx.Outcome == TxOutcome.Failed)
            return Result(r, null, OtherRailsMoved(gpu, s0, rail), []);
        // Anything else that did not land left the rails as s0 found them.
        if (r.Tx.Outcome != TxOutcome.Applied)
            return Result(r, null, null, []);
        var rounded = Rounded(before!, req, original);

        RailsSnapshot s1;
        try { s1 = RailsReader.Read(hw, gpu); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            // The control readback matched, so the offsets are known; what the driver derived from them is not.
            return Result(r, null, $"the write landed but the status could not be read afterwards ({e.Message}); MAX unverified, run --rails", rounded);
        }
        return Result(r, s1, null, [.. rounded, .. Surprises(before!, s1.Get(rail), r.Tx.Before, r.Tx.After!, s0, s1)]);

        RailApplyResult Result(OwnedWriteResult w, RailsSnapshot? after, string? v, IReadOnlyList<string> notes)
        {
            RailOffsets? Off(string? s) => s == null ? null : Decode(s);
            // Not applied and state known: nothing moved, so the limits are the ones read before.
            var limitsAfter = after?.Get(rail)?.Limits ?? (w.Tx.Outcome != TxOutcome.Applied && w.StateKnown ? before?.Limits : null);
            return new RailApplyResult(rail, req, Off(w.Before), Off(w.After), before?.Limits, limitsAfter,
                w.Tx, w.Ownership, w.SentinelCleared, v, notes);
        }
    }

    /// <summary>The slots <see cref="RailRules.OnStep"/> put on the step grid, as findings: asked, then written.</summary>
    private static IReadOnlyList<string> Rounded(RailRecord rail, RailRules.Request req, RailOffsets? origin)
    {
        var on = RailRules.OnStep(rail, req, origin);
        return [.. new[] { RailSlot.Rel, RailSlot.Alt, RailSlot.Ov, RailSlot.Vmin }
            .Where(s => req[s] != on[s])
            .Select(s => $"{RailRules.Name(s)}: {RailRules.Mv(req[s]!.Value)} mV asked, {RailRules.Mv(on[s]!.Value)} mV written (whole {RailRules.Mv(RailRules.Step(rail))} mV steps)")];
    }

    private string? OtherRailsMoved(GpuSession gpu, RailsSnapshot s0, Rail written)
    {
        var buf = Spec(s0.Mask).ReadBuffer();
        if (!hw.Call(gpu, Slot.VoltVoltRailsGetControl, buf).IsOk) return "the control structure could not be re-read after the failed write; other rails unverified";
        var moved = s0.Rails.Where(r => r.Rail != written && OffsetsIn(buf, r.Rail) != r.Offsets)
            .Select(r => $"{Key(r.Rail)} {Encode(r.Offsets)} → {Encode(OffsetsIn(buf, r.Rail)!.Value)}").ToList();
        return moved.Count == 0 ? null
            : $"the driver changed another rail as a side effect of this write and it was not given back ({string.Join("; ", moved)}); nobody holds its original";
    }

    /// <summary>
    /// For each changed slot of the written rail: did the evaluated value move by exactly the offset change? And
    /// did any other rail's evaluated limits move at all? (MSVDD shares NVVDD's REL base on the 5090; a coupling
    /// would show up here first.)
    /// </summary>
    private static IReadOnlyList<string> Surprises(RailRecord before, RailRecord? after, byte[] controlBefore, byte[] controlAfter, RailsSnapshot s0, RailsSnapshot s1)
    {
        if (after == null) return ["the rail is missing from the status read after the write"];
        var notes = new List<string>();
        foreach (var other in s0.Rails.Where(r => r.Rail != before.Rail))
            if (s1.Get(other.Rail)?.Limits is { } l && l != other.Limits)
                notes.Add($"{Key(other.Rail)} limits moved although it was not written: {other.Limits} → {l}");
        var o0 = OffsetsIn(controlBefore, before.Rail)!.Value;
        var o1 = OffsetsIn(controlAfter, before.Rail)!.Value;
        foreach (var s in new[] { RailSlot.Rel, RailSlot.Alt, RailSlot.Ov, RailSlot.Vmin })
        {
            long delta = (long)o1[s] - o0[s];
            if (delta == 0) continue;
            if (s == RailSlot.Alt && before.Limits.AltOpUv == 0) continue;
            long predicted = before.Eval(s) + delta;
            if (after.Eval(s) != predicted)
                notes.Add($"{RailRules.Name(s)}: offset {RailRules.Mv(delta)} mV, evaluated {RailRules.Mv(before.Eval(s))} → {RailRules.Mv(after.Eval(s))} mV, predicted {RailRules.Mv(predicted)} mV");
        }
        return notes;
    }

    /// <summary>For recovery: the cell and the plan that writes the recorded original back, from a fresh snapshot.</summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan) GiveBack(NvClient hw, GpuSession gpu, Rail rail, string original) =>
        Prepare(hw, gpu, Key(rail), original, original);

    /// <summary>
    /// The registry shape: key "nvvdd"|"msvdd", value in the cell's encoding ("rel=..,alt=..,ov=..,vmin=..", µV).
    /// A value may name only some slots ("rel=-5000"); the others stay as they are. Recovery passes the full original
    /// as both <paramref name="value"/> and <paramref name="original"/>.
    /// </summary>
    /// <param name="original">
    /// The journal's recorded original for this cell when we own it (complete encoding), else null: a slot going back
    /// to it is written exactly (see <see cref="RailRules.Plan"/>). Only ever a value read from the journal, never a request.
    /// </param>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value, string? original = null)
    {
        if (!TryParseKey(key, out var rail)) throw new ArgumentException($"unknown rail '{key}' (nvvdd or msvdd)");
        var req = ParseRequest(value);
        RailOffsets? origin = original != null ? Decode(original) : null;   // Decode throws on a partial value
        var s0 = RailsReader.Read(hw, gpu);
        return (Cell(rail, s0.Mask), Plan(s0, rail, req, origin));
    }

    /// <summary>"rel=-5000,vmin=0" → a request with those slots set and the rest left alone. Keys are rel, alt, ov, vmin; values µV.</summary>
    public static RailRules.Request ParseRequest(string value)
    {
        int? rel = null, alt = null, ov = null, vmin = null;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var uv))
                throw new FormatException($"'{part}' is not slot=microvolts");
            switch (kv[0].ToLowerInvariant())
            {
                case "rel": rel = uv; break;
                case "alt": alt = uv; break;
                case "ov": ov = uv; break;
                case "vmin": vmin = uv; break;
                default: throw new FormatException($"unknown rail slot '{kv[0]}' (rel, alt, ov, vmin)");
            }
        }
        if (rel == null && alt == null && ov == null && vmin == null) throw new FormatException("no rail slot given");
        return new RailRules.Request(vmin, rel, alt, ov);
    }
}
