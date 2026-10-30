using System.Globalization;
using System.Text;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Vf;

/// <summary>
/// The canonical encoding of the editable points' offsets (kHz, point order) with runs collapsed: "0*127",
/// "15000*40,0*87". Maximal runs make the encoding unique, so equal strings mean equal offsets (the journal's
/// notion of equality), and a stock curve is one short token in logs and records.
/// </summary>
public static class VfOffsets
{
    public static string Encode(IReadOnlyList<int> offsets)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < offsets.Count;)
        {
            int j = i;
            while (j < offsets.Count && offsets[j] == offsets[i]) j++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(offsets[i].ToString(CultureInfo.InvariantCulture));
            if (j - i > 1) sb.Append('*').Append((j - i).ToString(CultureInfo.InvariantCulture));
            i = j;
        }
        return sb.ToString();
    }

    public static int[] Decode(string s)
    {
        var list = new List<int>();
        foreach (var token in s.Split(','))
        {
            var parts = token.Split('*');
            if (parts.Length > 2) throw new FormatException($"'{token}' is neither an offset nor offset*count");
            int value = int.Parse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            int count = parts.Length == 2 ? int.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture) : 1;
            if (count < 1 || list.Count + count > VfLayout.MaxPoints) throw new FormatException($"'{token}' repeats an offset {count} times");
            for (int k = 0; k < count; k++) list.Add(value);
        }
        return list.ToArray();
    }
}

/// <summary>
/// The envelope the firmware reports for the core clock, which every V/F write stays inside. A point runs at
/// base + global offset + point offset, and the driver states one offset range for the core
/// clock (P-state table, P0 core: ±1000 MHz on the RTX 5090, driver 617.42). Whether the firmware bounds each
/// offset or their sum is not visible from here, so both are bounded: the sum is the larger displacement, the
/// range is far beyond any stable setting, so the stricter reading costs nothing and the looser one could cost
/// the user's one hard rule.
/// </summary>
public sealed record VfLimits(int MinKhz, int MaxKhz, int GlobalKhz)
{
    public static VfLimits From(Pstates20Snapshot p)
    {
        var core = p.P0(ClockDomain.Core) ?? throw new InvalidDataException("P0 has no core clock entry, so the firmware's core offset range is unknown");
        // Not editable: the driver allows no core offset at all, which the range [0, 0] says exactly.
        return core.Editable ? new VfLimits(core.MinKhz, core.MaxKhz, core.DeltaKhz) : new VfLimits(0, 0, core.DeltaKhz);
    }

    /// <summary>The point offsets that keep both the offset and global + offset inside the range.</summary>
    public int LowestKhz => (int)Math.Max(MinKhz, (long)MinKhz - GlobalKhz);
    public int HighestKhz => (int)Math.Min(MaxKhz, (long)MaxKhz - GlobalKhz);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"core offset range [{MinKhz / 1000.0:0.###}, {MaxKhz / 1000.0:0.###}] MHz, global {GlobalKhz / 1000.0:0.###} MHz");
}

public static class VfRules
{
    /// <summary>Kind 7 lets a set address only points 0..127.</summary>
    public const int WritablePoints = 128;

    /// <summary>
    /// Every point the write changes, against the snapshot's bases and the firmware envelope. Points the write
    /// does not change are not judged: refusing because of them would not make them safer, it would only block
    /// putting the others right (a give-back included).
    /// </summary>
    public static string? Check(VfCurveSnapshot s, VfLimits limits, IReadOnlyList<int> current, IReadOnlyList<int> target, bool enforceRange)
    {
        if (target.Count != s.EditableCount)
            return $"the request carries {target.Count} offsets; this curve has {s.EditableCount} editable points";
        for (int i = 0; i < target.Count; i++)
        {
            if (target[i] == current[i]) continue;
            if (i >= WritablePoints) return $"point {i} cannot be written: the driver's V/F setter addresses points 0..{WritablePoints - 1}";
            long withGlobal = (long)s.Points[i].BaseKhz + limits.GlobalKhz + target[i];
            long alone = (long)s.Points[i].BaseKhz + target[i];
            if (withGlobal is < 1 or > uint.MaxValue || alone is < 1 or > uint.MaxValue)
                return $"point {i} would run at {withGlobal / 1000.0:0.###} MHz; a V/F frequency must stay between 1 kHz and 4294967.295 MHz";
            if (!enforceRange) continue;
            if (target[i] < limits.MinKhz || target[i] > limits.MaxKhz)
                return string.Create(CultureInfo.InvariantCulture,
                    $"point {i}: offset {target[i] / 1000.0:0.###} MHz is outside the firmware's core offset range [{limits.MinKhz / 1000.0:0.###}, {limits.MaxKhz / 1000.0:0.###}] MHz");
            long total = (long)limits.GlobalKhz + target[i];
            if (total < limits.MinKhz || total > limits.MaxKhz)
                return string.Create(CultureInfo.InvariantCulture,
                    $"point {i}: global {limits.GlobalKhz / 1000.0:0.###} MHz + point {target[i] / 1000.0:0.###} MHz = {total / 1000.0:0.###} MHz, outside the firmware's core offset range [{limits.MinKhz / 1000.0:0.###}, {limits.MaxKhz / 1000.0:0.###}] MHz");
        }
        return null;
    }

    /// <summary>
    /// The write on a fresh control snapshot: the fresh control
    /// structure, its point mask replaced by the points that change, their offsets replaced. Asking the driver
    /// about every advertised point would select the non-Core entries too, which kind 7 refuses; selecting only
    /// what changes leaves every other point exactly as the driver holds it. The restore request is the same
    /// selection with the snapshot's offsets.
    /// </summary>
    public static Mutation Plan(VfCurveSnapshot s0, VfLimits limits, IReadOnlyList<int> target, byte[] control, bool enforceRange)
    {
        var shape = Slots.Info(Slot.ClockClientClkVfPointsGetControl).Shape!.Value;
        if (control.Length != shape.Size || BitConverter.ToUInt32(control) != shape.Word)
            return new Mutation.Abort("The driver returned an unsupported V/F control layout.");
        if (!control.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes).SequenceEqual(s0.Mask))
            return new Mutation.Abort("the V/F point mask changed between the curve read and the write snapshot; nothing written, try again");
        foreach (var p in s0.Points)
            if (BitConverter.ToUInt32(control, VfLayout.Control(p.Index)) != 0)
                return new Mutation.Abort($"point {p.Index} is no longer a Core frequency entry in the write snapshot; nothing written");

        var current = VfParser.Offsets(control, s0.EditableCount);
        if (!current.AsSpan().SequenceEqual(s0.Offsets()))
            return new Mutation.Abort("the V/F offsets changed between the curve read and the write snapshot (another tool?); nothing written, try again");
        if (Check(s0, limits, current, target, enforceRange) is { } why) return new Mutation.Abort(why);

        var changed = Enumerable.Range(0, target.Count).Where(i => target[i] != current[i]).ToArray();
        if (changed.Length == 0) return new Mutation.Apply([]);

        byte[] Request(Func<int, int> offset)
        {
            var b = (byte[])control.Clone();
            b.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes).Clear();
            foreach (int i in changed)
            {
                b[VfLayout.MaskOffset + (i >> 3)] |= (byte)(1 << (i & 7));
                BitConverter.TryWriteBytes(b.AsSpan(VfLayout.OffsetField(i)), offset(i));
            }
            return b;
        }
        return new Mutation.Request(
            Request(i => target[i]),
            changed.Select(i => Edit.Int32(VfLayout.OffsetField(i), target[i])).ToList(),
            Request(i => current[i]));
    }

    /// <summary><see cref="VfBudget.Check"/> on a curve already read. The global core offset writer should use
    /// <see cref="VfBudget.Read"/>, which needs neither the Status nor a curve that validates.</summary>
    public static string? CheckGlobal(VfCurveSnapshot s, VfLimits limits, int globalKhz) => VfBudget.Of(s).Check(limits, globalKhz);
}

/// <summary>
/// The V/F point offsets as the global core offset writer must see them. The two offsets share one firmware range
/// (<see cref="VfLimits"/>), so whichever moves second checks the other, by the project's relative rule: a global
/// may not take any point's total (global + point offset) out of the range, nor further out than it is now, so a
/// total another tool left outside can be walked back. A point at offset 0 is not judged: its total is the global
/// itself, which the P-state rules bound.
/// <para>
/// Only Info and Control are read. The budget needs nothing but the offsets, and the full curve read proves things
/// about the Status that can fail for reasons of their own: a voltage-demand offset off the regulator's 6.25 mV grid
/// may make the demand shift non-uniform, and a global below −180 MHz runs the 5090's 47 bottom points at or below
/// zero, which the status may report as a zero frequency. Neither may decide whether the global offset can move.
/// </para>
/// <para>
/// When even the offsets cannot be read, the same rule is held against every curve the range admits (each offset,
/// and its total now, inside the range). Between 0 and the global in force each point's total lies between its
/// offset alone and its total now, both inside, so nothing gets worse. Any other global, some admissible curve
/// cannot take: a raise over a point already at the edge, a sign change under a point at the opposite edge. So
/// unread offsets allow exactly the globals between 0 and the one in force: never one that knowing the curve would
/// refuse, and never a state with no way back, since 0 is always among them.
/// </para>
/// </summary>
public sealed record VfBudget(IReadOnlyList<(int Index, int OffsetKhz)>? Offsets, string? Unreadable)
{
    /// <summary>Every Core point's offset as the driver holds it now, or why it cannot be read. Never throws.</summary>
    public static VfBudget Read(NvClient hw, GpuSession gpu)
    {
        try { return new(VfReader.ReadOffsets(hw, gpu), null); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return new(null, e.Message); }
    }

    public static VfBudget Of(VfCurveSnapshot s) => new([.. s.Points.Select(p => (p.Index, p.OffsetKhz))], null);

    /// <summary>Why the global core offset may not move from the one in force (<see cref="VfLimits.GlobalKhz"/>) to
    /// <paramref name="globalKhz"/>; null when it may.</summary>
    public string? Check(VfLimits limits, int globalKhz)
    {
        long now = limits.GlobalKhz, next = globalKhz;
        if (Offsets == null)
            return (now >= 0 ? next >= 0 && next <= now : next <= 0 && next >= now) ? null
                : string.Create(CultureInfo.InvariantCulture,
                    $"the V/F point offsets cannot be read ({Unreadable}); without them only a global between 0 and the {now / 1000.0:0.###} MHz in force is sure to keep every point inside the firmware's core offset range [{limits.MinKhz / 1000.0:0.###}, {limits.MaxKhz / 1000.0:0.###}] MHz");
        long Outside(long total) => total < limits.MinKhz ? limits.MinKhz - total : total > limits.MaxKhz ? total - limits.MaxKhz : 0;
        foreach (var (i, o) in Offsets)
        {
            if (o == 0) continue;
            long total = next + o, outside = Outside(total), before = Outside(now + o);
            if (outside == 0 || outside <= before) continue;
            return before == 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"V/F point {i} carries {o / 1000.0:0.###} MHz; with a global {next / 1000.0:0.###} MHz the total {total / 1000.0:0.###} MHz leaves the firmware's core offset range [{limits.MinKhz / 1000.0:0.###}, {limits.MaxKhz / 1000.0:0.###}] MHz")
                : string.Create(CultureInfo.InvariantCulture,
                    $"V/F point {i} carries {o / 1000.0:0.###} MHz; with a global {next / 1000.0:0.###} MHz the total {total / 1000.0:0.###} MHz is further outside the firmware's core offset range [{limits.MinKhz / 1000.0:0.###}, {limits.MaxKhz / 1000.0:0.###}] MHz than its {(now + o) / 1000.0:0.###} MHz now");
        }
        return null;
    }
}

/// <summary>A generated set of point offsets and what shaping it took.</summary>
public sealed record VfShape(
    int[] Offsets,
    /// <summary>µV the curve was asked to move left by.</summary>
    int UndervoltUv,
    /// <summary>µV it moves: the request floored to whole regulator steps when the curve's voltages are in steps (a
    /// point between two steps would need a voltage the regulator cannot make), else the request. No point below the
    /// cap moves further; on a curve with a point on every step (the 5090), each of them moves exactly this far on the
    /// curve that binds it (with reference curves, the one that allows least), but for the points in <see cref="Short"/>.</summary>
    int AppliedUv,
    /// <summary>The voltage of the lowest point that moved, as the driver reports it, µV; points below it kept their
    /// stock frequency. Like every voltage in a shape, a firmware point voltage (<see cref="VfPoint.ReferenceVoltageUv"/>).</summary>
    uint FloorUv,
    /// <summary>The voltage ceiling whose stock frequency tops the new curve, µV; null when the curve was not capped.</summary>
    uint? CapUv,
    /// <summary>The stock frequency at <see cref="CapUv"/> as the driver reports it, kHz: the highest the new curve asks
    /// for (before the global offset).</summary>
    uint? CapKhz,
    /// <summary>What each point should report after the write, kHz, before the global offset: base + offset as the driver
    /// lands it (<see cref="VfShapes.Landing"/>), which for a shape is base + offset exactly, floored to whole MHz.</summary>
    IReadOnlyList<long> NewKhz,
    /// <summary>Points whose offset had to be pulled into the firmware range. A raise pulled in asks for less than the
    /// shape did (the safe side). A cut pulled in leaves the point above the cap, and only a point whose stock frequency
    /// is already above the cap is cut: on a rising curve, one above the voltage ceiling, which the card cannot reach.</summary>
    IReadOnlyList<int> Clamped,
    /// <summary>Points whose target the driver cannot reach from their base: from a half-step base a raise lands in whole
    /// 15 MHz units only (<see cref="VfShapes.Landing"/>), so they ask for, and run, one 7.5 MHz step below the shape's
    /// target, the safe side.</summary>
    IReadOnlyList<int> Short);

/// <summary>
/// The Core curve's bases as read at another time (a <c>--vf</c> read taken at the hottest steady state, say), for
/// <see cref="VfShapes.Undervolt"/>: each editable point's firmware voltage and base, in index order, as the driver
/// reported them.
/// </summary>
public sealed record VfReference(string Label, IReadOnlyList<uint> ReferenceVoltageUv, IReadOnlyList<uint> BaseKhz)
{
    public static VfReference Of(VfCurveSnapshot s, string label) =>
        new(label, [.. s.Points.Select(p => p.ReferenceVoltageUv)], [.. s.Points.Select(p => p.BaseKhz)]);
}

/// <summary>
/// Same frequency, lower voltage. A V/F point's voltage is fixed by the firmware; only its frequency moves. So
/// "reach frequency f at ΔV less" means: each point takes the stock frequency of the voltage ΔV above it,
/// f'(v) = f(v + ΔV), where f(v) is the highest stock frequency of any point at or below v: the stock curve says
/// nothing between its points, and a card held between two runs the lower one. Left alone that also invents
/// frequencies the card never ran (the stock curve continues above the firmware's voltage ceiling, and v + ΔV
/// reaches into it), so the new curve is capped at the stock frequency of the ceiling: f'(v) = min(f(v + ΔV), f(Vmax)).
/// Then the set of frequencies is exactly the stock set and each is reached ΔV lower.
/// <para>
/// The shift starts at a floor, not at the bottom of the curve. On the RTX 5090 (read live) the stock curve
/// climbs 15.6–16.8 MHz per mV from 0.745 to 0.865 V and 1.2–4.8 MHz per mV above: a firmware ramp, not silicon.
/// Shifting the ramp by 25 mV would ask for +400 MHz there, at voltages a full-load stability run never visits
/// (it sits on the power limit near 1.0 V), and transistor delay is most sensitive to voltage exactly there, near
/// threshold. Below the knee the curve keeps its stock frequencies, which costs nothing: the power-limited
/// operating point the undervolt is for lies far above it. The knee is found from the curve itself (see
/// <see cref="Knee"/>); on a curve without a ramp it is the first point and the whole curve moves.
/// </para>
/// <para>
/// The arithmetic is done in the hardware's own steps. The driver reports each point's voltage rounded up to 5 mV
/// and its frequency floored to whole MHz, and on the 5090 both hide a grid: every voltage is a step of the Core
/// regulator (450 mV + 6.25 mV × index: 0.86875 V reads 0.870 V) and every base a step of the clock (k × 7.5 MHz:
/// 2932.5 MHz reads 2932). On the reported numbers the 5 mV / 10 mV alternation fakes slopes off by up to 2×, a
/// ΔV that is not a multiple of 5 mV moves some points one step and their neighbours none, and an offset taken
/// between two reported bases can fall 0.5 MHz short of a whole clock step and be floored a full 7.5 MHz low (for
/// 25 mV it did at 8 of the 37 points from the knee to the ceiling). In steps, an undervolt is a whole number of regulator steps, every point moves
/// by exactly that many, and every offset is a whole number of clock steps: what the hardware can run, and what
/// the driver has no reason to round (a rounded offset reads back different and is rolled back). An axis whose
/// points do not all fit its grid is taken as reported, with offsets floored to the clock step.
/// </para>
/// <para>
/// Offsets are computed from the bases (the curve without any offset), so the same request on the same bases always
/// yields the same curve: repeating it never accumulates.
/// </para>
/// <para>
/// The bases move with temperature and the offsets do not. On the 5090 in a 538 W burn at 75 °C the firmware had
/// lowered every base from the knee up, one clock step at the knee growing to ten to twelve near the top, and left the
/// points below the knee alone (effective = base on every point). So a shape is exact only at the temperature its
/// bases were read at: read cooler and run hotter, it reaches deeper than ΔV, and its capped points pass the stock
/// maximum of the hotter curve (100 mV computed cold tops the 75 °C curve at 2880 MHz; that curve's own top is 2850).
/// Run cooler than it was read, it only reaches less deep and lower. A reference curve is held to the same two bounds
/// as the bases read now: each point takes the smallest offset any of the curves allows, so the new curve shifts by
/// at most ΔV and stays under the cap at the temperature of every curve given, exactly ΔV on the one that binds (on
/// the 5090 the hottest, give or take a clock step where its drop is uneven). A reference can only lower offsets, so
/// a wrong one costs frequency, never margin.
/// </para>
/// <para>
/// The driver does not add every offset exactly (<see cref="Landing"/>): from a base on a half step of its 15 MHz grid
/// a raise lands in whole 15 MHz units, and the half step between is out of reach. So a shape asks only for what
/// lands: where one step less lands on the same frequency, it asks for one step less. At the temperature it is
/// written at that is the same curve, and the readback equals the preview. It is also what lets a verdict cover the
/// cooler temperatures. The bases move with temperature, so a half-step base at one temperature is a whole-step base
/// a few degrees away, and an offset that landed a step short where it was judged lands in full there: one clock step
/// further ahead than its base moved (25 mV shaped in the 75 °C burn with the 33 °C read as reference: points 68, 81
/// and 89 at 33 °C). An offset that lands exactly where it is written lands at or below base + offset anywhere else,
/// so no point ever runs further ahead of its own base than it did when it was judged. Shape at the temperature the
/// curve will be judged at, then.
/// </para>
/// <para>
/// Every voltage here is a point's firmware voltage (<see cref="VfPoint.ReferenceVoltageUv"/>), not the voltage it
/// runs at. A Core voltage-demand offset X in force moves every point to firmware voltage + X, and the two knobs are
/// meant to add: on this axis the shape depends on the stock curve and the ceiling alone, so with X in force the card
/// runs exactly the curve of an undervolt by ΔV − X, and its top never passes the stock frequency of the ceiling,
/// whether the firmware applies the ceiling before X or after it. Drawn on the running voltages instead, a negative X
/// would lift the cap to the stock frequency of Vmax + |X|: X's margin spent twice, once as lower voltage and again
/// as frequencies above the stock top. Below the cap the two axes give the same offsets (X is the same on every point).
/// </para>
/// </summary>
public static class VfShapes
{
    /// <summary>The Core clock's step: every base the 5090 reports is a multiple of it, floored to whole MHz.</summary>
    public const int StepKhz = 7_500;

    /// <summary>The Core regulator's step: every 5090 point sits on one, reported rounded up to 5 mV.</summary>
    public const int VidStepUv = 6_250;

    /// <summary>A segment steeper than this many times the curve's typical slope near the top is ramp, not silicon.
    /// On the 5090, in hardware steps, the chip's own segments reach 1.33× typical and the ramp starts at 3.67×;
    /// 2.5 sits between them.</summary>
    public const double RampFactor = 2.5;

    public static VfShape Undervolt(VfCurveSnapshot s, VfLimits limits, int undervoltUv, uint? capUv, uint? floorUv = null, IReadOnlyList<VfReference>? references = null)
    {
        if (undervoltUv < 0) throw new ArgumentOutOfRangeException(nameof(undervoltUv), undervoltUv, "an undervolt moves the curve left by a non-negative voltage");
        long lo = CeilStep(limits.LowestKhz), hi = FloorStep(limits.HighestKhz);
        if (lo > hi) throw new InvalidDataException($"no point offset fits the firmware envelope ({limits})");
        var hw = Steps.Of(s.Points);
        Steps[] curves = [hw, .. (references ?? []).Select(hw.With)];
        var caps = curves.Select(curve => capUv is { } c ? curve.Stock(c) ?? curve.F.Min() : (long?)null).ToArray();
        long? cap = caps[0];
        int knee = Knee(hw, capUv);
        bool Moves(int i) => floorUv is { } fl ? s.Points[i].ReferenceVoltageUv >= fl : i >= knee;
        int applied = hw.VoltSteps ? undervoltUv / VidStepUv * VidStepUv : undervoltUv;
        // Less than one regulator step reaches no point any lower: that is the stock curve, not a cap with nothing to bound.
        bool shift = applied > 0;

        var offsets = new int[s.EditableCount];
        var news = new long[s.EditableCount];
        var clamped = new List<int>();
        var shorted = new List<int>();
        for (int i = 0; i < offsets.Length; i++)
        {
            long allowed = long.MaxValue;
            for (int c = 0; c < curves.Length; c++)
            {
                var curve = curves[c];
                long want = shift && Moves(i) ? curve.Stock(curve.V[i] + undervoltUv) ?? curve.F[i] : curve.F[i];
                if (shift && caps[c] is { } top && want > top) want = top;
                allowed = Math.Min(allowed, want - curve.F[i]);
            }
            long o = FloorStep(allowed);
            if (o < lo || o > hi)
            {
                clamped.Add(i);
                o = Math.Clamp(o, lo, hi);
            }
            // Ask only for what lands: a step that lands where one step less does was never reached from this base.
            long asked = o;
            if (hw.ClockSteps)
                while (o - StepKhz >= lo && Landing(s.Points[i].BaseKhz, o - StepKhz) == Landing(s.Points[i].BaseKhz, o)) o -= StepKhz;
            if (o != asked) shorted.Add(i);
            offsets[i] = (int)o;
            news[i] = hw.ClockSteps ? Landing(s.Points[i].BaseKhz, o) : hw.Reported(hw.F[i] + o);
        }
        uint floor = floorUv ?? s.Points[knee].ReferenceVoltageUv;
        return new VfShape(offsets, undervoltUv, applied, floor, capUv, cap is { } k ? (uint)hw.Reported(k) : null, news, clamped, shorted);
    }

    /// <summary>
    /// The curve on the card moved left by ΔV, not the stock curve (W14: on the reference card the stock shift would have
    /// put 895 mV at 2407 MHz under a daily curve that ran it at 2797, every point 300-450 MHz lower). It is
    /// <see cref="Undervolt"/> run on the curve as it runs now, each point's base plus its offset taken as if it were stock,
    /// so the knee, the regulator and clock steps and the cap at the ceiling hold as they do there, and each point's own
    /// offset is added back: from the knee up each point runs what the current curve ran ΔV above it, never past what it
    /// runs at the ceiling. Below the knee the curve stays as it is.
    /// </summary>
    public static VfShape ShiftCurrent(VfCurveSnapshot s, VfLimits limits, int shiftUv, uint? capUv)
    {
        var asStock = s with { Points = [.. s.Points.Select(p => p with { BaseKhz = (uint)Math.Max(0, (long)p.BaseKhz + p.OffsetKhz), OffsetKhz = 0 })] };
        var shape = Undervolt(asStock, limits, shiftUv, capUv);
        return shape with { Offsets = [.. shape.Offsets.Select((o, i) => o + s.Points[i].OffsetKhz)] };
    }

    /// <summary>
    /// What the driver reports for a point after its offset, kHz, as measured live (drill 19: 64 of 64 points). A base is
    /// k × 7.5 MHz, so with k odd it sits on a half step of the 15 MHz grid. A raise adds its whole 15 MHz units and ORs
    /// the half steps: a half step on both the base and the raise does not carry, and the point lands one 7.5 MHz step
    /// short (2317.5 + 22.5 reports 2332, not 2340; + 7.5 does nothing). A cut lands exactly. The report is floored to
    /// whole MHz. From a half-step base a raise thus reaches only base + 15 MHz × j. A base off the 7.5 MHz grid is
    /// taken as base + offset.
    /// <para>
    /// An offset that is not whole 7.5 MHz steps (Afterburner writes whole MHz) is stored as given, and Control reads it
    /// back unchanged, but the driver floors a raise to whole steps before it lands it (V/F thread, live 2026-10-18):
    /// +10 MHz lands as +7.5. Cuts off the grid are not measured yet and are taken as base + offset.
    /// </para>
    /// </summary>
    public static long Landing(uint reportedBaseKhz, long offsetKhz)
    {
        if (offsetKhz > 0) offsetKhz -= offsetKhz % StepKhz;
        if (Steps.Clock(reportedBaseKhz) is not { } b || offsetKhz % StepKhz != 0) return reportedBaseKhz + offsetKhz;
        const long Whole = 2 * StepKhz;
        long f = offsetKhz <= 0 ? b + offsetKhz : Whole * (b / Whole + offsetKhz / Whole) + StepKhz * Math.Max(b / StepKhz % 2, offsetKhz / StepKhz % 2);
        return f / 1000 * 1000;
    }

    /// <summary>
    /// The lowest point from which the stock curve rises no faster than <see cref="RampFactor"/> times its typical
    /// slope, all the way up to the ceiling. "Typical" is the median slope over the upper half of the voltages
    /// below the ceiling, where the curve is the chip's own. On the 5090, in hardware steps, that median is
    /// 3.6 MHz/mV (three clock steps per regulator step), the steepest segment above the knee 4.8, the ramp segment
    /// below it 13.2: the knee is point 67 (0.86875 V, reported as 0.870 V).
    /// </summary>
    public static int Knee(VfCurveSnapshot s, uint? ceilingUv = null) => Knee(Steps.Of(s.Points), ceilingUv);

    private static int Knee(Steps hw, uint? ceilingUv)
    {
        var (v, f) = (hw.V, hw.F);
        int top = v.Length - 1;
        if (ceilingUv is { } ceiling)
            while (top > 0 && v[top] > ceiling) top--;
        double Slope(int j) => v[j + 1] == v[j] ? 0 : ((double)f[j + 1] - f[j]) / ((double)v[j + 1] - v[j]);
        double mid = (v[0] + (double)v[top]) / 2;
        var upper = Enumerable.Range(0, top).Where(j => v[j] >= mid).Select(Slope).Order().ToList();
        if (upper.Count == 0) return 0;
        double typical = upper[upper.Count / 2];
        int knee = top;
        while (knee > 0 && Slope(knee - 1) <= RampFactor * typical) knee--;
        return knee;
    }

    /// <summary>
    /// The stock curve in hardware steps: each point's firmware voltage and base, put back on the regulator and
    /// clock grids where every point fits them, as reported where not.
    /// </summary>
    private sealed record Steps(long[] V, long[] F, bool VoltSteps, bool ClockSteps)
    {
        public static Steps Of(IReadOnlyList<VfPoint> points) =>
            Of([.. points.Select(p => (long)p.ReferenceVoltageUv)], [.. points.Select(p => (long)p.BaseKhz)]);

        /// <summary>Another reading of the same curve, put on the grids the same way; refused when it is not this curve.</summary>
        public Steps With(VfReference r)
        {
            if (r.BaseKhz.Count != F.Length || r.ReferenceVoltageUv.Count != V.Length)
                throw new InvalidDataException($"reference '{r.Label}' has {r.BaseKhz.Count} points; this curve has {F.Length}");
            var other = Of([.. r.ReferenceVoltageUv.Select(x => (long)x)], [.. r.BaseKhz.Select(x => (long)x)]);
            for (int i = 0; i < V.Length; i++)
                if (other.V[i] != V[i])
                    throw new InvalidDataException($"reference '{r.Label}' is not this curve: point {i} sits at {r.ReferenceVoltageUv[i]} µV there");
            return other;
        }

        private static Steps Of(long[] v, long[] f)
        {
            var vs = v.Select(Volt).ToArray();
            var fs = f.Select(Clock).ToArray();
            bool onV = vs.All(x => x != null), onF = fs.All(x => x != null);
            return new(onV ? [.. vs.Select(x => x!.Value)] : v, onF ? [.. fs.Select(x => x!.Value)] : f, onV, onF);
        }

        /// <summary>The regulator step a reported voltage was rounded up from: the one multiple of 6.25 mV in (r − 5 mV, r].</summary>
        private static long? Volt(long r)
        {
            long t = r / VidStepUv * VidStepUv;
            return t > r - 5_000 ? t : null;
        }

        /// <summary>The clock step a reported frequency was floored from: k × 7.5 MHz reads as itself or 0.5 MHz less.</summary>
        public static long? Clock(long r) => (r % StepKhz) switch { 0 => r, 7_000 => r + 500, _ => null };

        /// <summary>The highest stock frequency of any point at or below voltage <paramref name="uv"/>; null below the curve.</summary>
        public long? Stock(long uv)
        {
            long? best = null;
            for (int j = 0; j < V.Length; j++)
                if (V[j] <= uv && (best == null || F[j] > best)) best = F[j];
            return best;
        }

        /// <summary>What the driver reports for a hardware frequency: floored to whole MHz when the clock is in steps.</summary>
        public long Reported(long khz) => ClockSteps ? khz / 1000 * 1000 : khz;
    }

    private static long FloorStep(long khz) => (long)Math.Floor(khz / (double)StepKhz) * StepKhz;
    private static long CeilStep(long khz) => (long)Math.Ceiling(khz / (double)StepKhz) * StepKhz;
}

public sealed record VfApplyResult(
    int[] Requested,
    /// <summary>The offsets in the snapshot the write was planned on; null when the transaction got no snapshot.</summary>
    int[]? Before,
    int[]? After,
    VfLimits Limits,
    TxResult Tx,
    string Ownership,
    bool SentinelCleared,
    /// <summary>Points whose reported frequency is not where the driver's model puts it (<see cref="VfWrites.Surprises"/>).
    /// Findings about the driver's model, not failures.</summary>
    IReadOnlyList<string> Surprises,
    /// <summary>The curve as read after a landed write; null when nothing landed or it could not be re-read.</summary>
    VfCurveSnapshot? CurveAfter)
{
    public bool Succeeded => Tx.Succeeded;
}

/// <summary>
/// The Core V/F curve's point offsets are one owned cell: the control structure read with the advertised point
/// mask, written through the kind-7 setter with only the changed points selected, the offsets of all editable
/// points as the cell. The plan reasons from a full curve read (bases and voltages live in the status structure)
/// and the firmware envelope (P-state table), both taken just before the transaction; it refuses when the
/// transaction's own control snapshot no longer matches that read. The post-write proof (the whole
/// curve validates again, with the same layout) is the cell's <see cref="TransactionSpec.CheckAfterWrite"/>, so
/// every path that writes the cell runs it, and a curve that fails it is put back by the transaction itself.
/// </summary>
public sealed class VfWrites(NvClient hw, OwnershipJournal journal, ApplySentinel sentinel, IHost host)
{
    public const string Kind = "vf";
    public const string CoreKey = "core";

    /// <summary>Control reads are queries: the advertised point mask goes in before the GET.</summary>
    public static TransactionSpec Spec(byte[] mask, Func<NvClient, GpuSession, string?>? checkAfterWrite = null) => new(
        Slot.ClockClientClkVfPointsGetControl, Slot.ClockClientClkVfPointsSetControl,
        PrepareRead: b => VfLayout.WriteMask(b, mask), CheckAfterWrite: checkAfterWrite);

    /// <summary>The offsets of every editable point in a control structure, encoded; null when the structure does
    /// not describe this curve (other mask, a point that is no longer a Core entry).</summary>
    public static OwnedCell Cell(VfCurveSnapshot s0) => new(
        Kind, CoreKey, "kHz", $"Core V/F point offsets, kHz, points 0..{s0.EditableCount - 1} ({s0.Layout})",
        Spec(s0.Mask, (hw, gpu) => CheckAfterWrite(hw, gpu, s0)),
        raw =>
        {
            if (!raw.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes).SequenceEqual(s0.Mask)) return null;
            foreach (var p in s0.Points)
                if (BitConverter.ToUInt32(raw, VfLayout.Control(p.Index)) != 0) return null;
            return VfOffsets.Encode(VfParser.Offsets(raw, s0.EditableCount));
        }, CoupledKinds: [ClockOffsets.Kind]);

    /// <summary>Ownership/readback of point controls only, for capturing implicit P0 side effects. Writing a V/F
    /// curve still requires Prepare/Cell with the full curve, firmware bounds and post-write structural proof;
    /// only rollback to a captured pre-operation control snapshot uses RestoreControls instead.</summary>
    public static OwnedCell ControlCell(VfControlSnapshot s) => new(
        Kind, CoreKey, "kHz", $"Core V/F point offsets, kHz, points 0..{s.Points.Count - 1} ({s.Layout})", Spec(s.Mask),
        raw =>
        {
            var points = VfParser.CoreOffsets(s.Info, raw);
            return points.Count == s.Points.Count && points.Select(p => p.Index).SequenceEqual(s.Points.Select(p => p.Index))
                ? VfOffsets.Encode(points.Select(p => p.OffsetKhz).ToArray()) : null;
        }, CoupledKinds: [ClockOffsets.Kind]);

    /// <summary>Rollback only: restore captured Core control offsets without interpreting effective Status.
    /// The caller verifies P0 and the Info/Control topology before and after this transaction.</summary>
    internal static Mutation RestoreControls(VfControlSnapshot original, ReadOnlyMemory<byte> raw)
    {
        var current = raw.ToArray();
        if (ControlCell(original).StateIn(current) == null)
            return new Mutation.Abort("the V/F control snapshot no longer matches the captured topology");
        var changed = original.Points.Where(p => BitConverter.ToInt32(current, VfLayout.OffsetField(p.Index)) != p.OffsetKhz).ToArray();
        if (changed.Length == 0) return new Mutation.Apply([]);

        byte[] Request(bool restore)
        {
            var request = (byte[])current.Clone();
            request.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes).Clear();
            foreach (var point in changed)
            {
                request[VfLayout.MaskOffset + (point.Index >> 3)] |= (byte)(1 << (point.Index & 7));
                BitConverter.TryWriteBytes(request.AsSpan(VfLayout.OffsetField(point.Index)),
                    restore ? BitConverter.ToInt32(current, VfLayout.OffsetField(point.Index)) : point.OffsetKhz);
            }
            return request;
        }
        return new Mutation.Request(Request(false),
            changed.Select(p => Edit.Int32(VfLayout.OffsetField(p.Index), p.OffsetKhz)).ToArray(), Request(true));
    }

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(VfCurveSnapshot s0, VfLimits limits, IReadOnlyList<int> target, bool enforceRange = true) =>
        raw => VfRules.Plan(s0, limits, target, raw.ToArray(), enforceRange);

    /// <summary>
    /// The post-write proof: the whole curve validates again, with the
    /// layout it had before the write. The control readback already proved the offsets; this proves the driver still
    /// describes the same curve after taking them. Null when it does.
    /// </summary>
    public static string? CheckAfterWrite(NvClient hw, GpuSession gpu, VfCurveSnapshot s0)
    {
        VfCurveSnapshot s1;
        try { s1 = VfReader.Read(hw, gpu); }
        catch (InvalidDataException e) { return $"the V/F curve no longer validates after the write: {e.Message}"; }
        return s1.Layout != s0.Layout || s1.EditableCount != s0.EditableCount || !s1.Mask.AsSpan().SequenceEqual(s0.Mask)
            ? $"the V/F layout changed after the write ({s0.Layout}/{s0.EditableCount} → {s1.Layout}/{s1.EditableCount})"
            : null;
    }

    /// <summary>
    /// The write path's one entry point for the registry, serving a write (value = the target) and a give-back
    /// (value = the recorded original) alike: key "core", value in the cell's own encoding (<see cref="VfOffsets"/>,
    /// kHz per editable point, "0*127" is stock). The curve and the firmware envelope are read now; the plan holds
    /// the firmware range and refuses when the transaction's snapshot no longer matches this read. Throws
    /// <see cref="InvalidDataException"/> when the key or the value cannot describe this curve (a record from
    /// another layout included), before anything is written.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation> Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        if (key != CoreKey) throw new InvalidDataException($"unknown V/F curve '{key}'; this version knows only '{CoreKey}'");
        int[] target;
        try { target = VfOffsets.Decode(value); }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new InvalidDataException($"'{value}' is not a list of V/F point offsets (kHz, runs as offset*count): {e.Message}");
        }
        var s0 = VfReader.Read(hw, gpu);
        if (target.Length != s0.EditableCount)
            throw new InvalidDataException($"'{value}' gives {target.Length} offsets; this curve has {s0.EditableCount} editable points ({s0.Layout})");
        var limits = VfLimits.From(Pstates20Reader.Read(hw, gpu));
        return (Cell(s0), Plan(s0, limits, target));
    }

    /// <summary>Set every editable point's offset (kHz) to <paramref name="target"/>.</summary>
    public VfApplyResult Apply(GpuSession gpu, IReadOnlyList<int> target, bool takeOver = false)
    {
        var s0 = VfReader.Read(hw, gpu);
        var limits = VfLimits.From(Pstates20Reader.Read(hw, gpu));
        return Apply(gpu, s0, limits, target.ToArray(), takeOver);
    }

    /// <summary>
    /// Same-frequency undervolt by <paramref name="undervoltUv"/> (see <see cref="VfShapes"/>), capped at the stock
    /// frequency of the firmware's maximum Core voltage (NVVDD MAX, the rail limit the driver evaluates) unless a
    /// cap is given, moving the curve from its knee up unless a floor is given. 0 µV is the stock curve.
    /// </summary>
    public (VfShape Shape, VfApplyResult Result) Undervolt(GpuSession gpu, int undervoltUv, uint? capUv = null, uint? floorUv = null, bool takeOver = false, IReadOnlyList<VfReference>? references = null)
    {
        var s0 = VfReader.Read(hw, gpu);
        var limits = VfLimits.From(Pstates20Reader.Read(hw, gpu));
        var shape = Shape(hw, gpu, s0, limits, undervoltUv, capUv, floorUv, references);
        return (shape, Apply(gpu, s0, limits, shape.Offsets, takeOver));
    }

    /// <summary>What <see cref="Undervolt"/> would write, from reads alone.</summary>
    public static VfShape Shape(NvClient hw, GpuSession gpu, VfCurveSnapshot s, VfLimits limits, int undervoltUv, uint? capUv = null, uint? floorUv = null, IReadOnlyList<VfReference>? references = null)
    {
        // 0 µV without a cap is the stock curve: every offset 0, nothing to cap, no ceiling needed.
        uint? cap = capUv ?? (undervoltUv == 0 ? null : FirmwareMaxUv(hw, gpu));
        return VfShapes.Undervolt(s, limits, undervoltUv, cap, floorUv, references);
    }

    /// <summary>The highest Core voltage the firmware will run: NVVDD's evaluated MAX = min(REL, ALT, OV) on this board.</summary>
    public static uint FirmwareMaxUv(NvClient hw, GpuSession gpu)
    {
        var rails = RailsReader.Read(hw, gpu);
        var nvvdd = rails.Get(Rail.Nvvdd) ?? throw new InvalidDataException("the driver does not report NVVDD, so the firmware's Core voltage ceiling is unknown; pass a cap explicitly");
        if (nvvdd.Limits.MaxUv == 0) throw new InvalidDataException("NVVDD reports no maximum voltage; pass a cap explicitly");
        return nvvdd.Limits.MaxUv;
    }

    private VfApplyResult Apply(GpuSession gpu, VfCurveSnapshot s0, VfLimits limits, int[] target, bool takeOver)
    {
        var w = new OwnedWriter(hw, journal, sentinel, host).Apply(gpu, Cell(s0), Plan(s0, limits, target), takeOver);
        VfCurveSnapshot? after = null;
        IReadOnlyList<string> surprises = [];
        if (w.Tx.Outcome == TxOutcome.Applied)
        {
            try
            {
                after = VfReader.Read(hw, gpu);
                surprises = Surprises(after, limits);
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { }   // the write stands: its proof ran inside the transaction
        }
        int[]? Off(string? s) => s == null ? null : VfOffsets.Decode(s);
        return new VfApplyResult(target, Off(w.Before), Off(w.After), limits, w.Tx, w.Ownership, w.SentinelCleared, surprises, after);
    }

    /// <summary>
    /// Points whose reported frequency is not where the driver's model puts it after a write: base + offset as the
    /// driver lands it (<see cref="VfShapes.Landing"/>) plus the global core offset, taken as exact: live it has only
    /// moved in whole 15 MHz units (drills 11 and 15), so how it meets a half step is not known yet.
    /// </summary>
    public static IReadOnlyList<string> Surprises(VfCurveSnapshot s1, VfLimits limits)
    {
        var notes = new List<string>();
        foreach (var p in s1.Points)
        {
            // Whole MHz: the driver reports frequencies floored to the MHz (a 7.5 MHz step k × 7.5 reads 0.5 MHz low for odd k),
            // so a difference under 1 MHz is the reporting, not the model.
            long predicted = VfShapes.Landing(p.BaseKhz, p.OffsetKhz) + limits.GlobalKhz;
            if (Math.Abs(p.EffectiveKhz - predicted) >= 1_000)
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"point {p.Index}: reports {p.EffectiveKhz / 1000.0:0.###} MHz, the model {predicted / 1000.0:0.###} MHz (base {p.BaseKhz / 1000.0:0.###} + global {limits.GlobalKhz / 1000.0:0.###} + offset {p.OffsetKhz / 1000.0:0.###})"));
        }
        // One line per point would bury the finding when the driver's model differs everywhere (a clamp, a global term).
        return notes.Count <= 8 ? notes : [.. notes.Take(4), $"... {notes.Count - 4} more points differ the same way"];
    }
}
