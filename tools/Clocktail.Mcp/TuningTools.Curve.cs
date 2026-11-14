using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

public sealed partial class TuningTools
{
    [McpServerTool(Name = "curve_around", ReadOnly = true, OpenWorld = false), Description(
        "Read this GPU's core V/F points around a voltage, with stock/current offsets and the user's daily offsets when a profile exists. No universal voltage bands or default curve are supplied.")]
    public string CurveAround(
        [Description("Centre voltage in mV; omit for the present NVVDD.")] double? mv = null,
        [Description("How many mV each side of the centre (default 40).")] double spanMv = 40) => Guard(() =>
    {
        var look = card.Read();
        var daily = DailyOffsets(out _);
        uint centre = mv is { } m ? (uint)(m * 1000) : look.NvvddUv ?? throw new InvalidOperationException("NVVDD is unreadable; give mv");
        uint lo = centre - (uint)Math.Min(centre, spanMv * 1000), hi = centre + (uint)(spanMv * 1000);
        return Out(new
        {
            centre_mv = centre / 1000.0,
            operating_point = look.NvvddUv is { } uv ? Nearest(look.Curve, uv)?.Index : null,
            points = look.Curve.Points.Where(p => p.VoltageUv >= lo && p.VoltageUv <= hi).Select(p => Row(p, daily)),
        });
    });

    [McpServerTool(Name = "adjust_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Move core V/F point offsets in a selected voltage band as a trial. Use this GPU's curve, reported offset budget and actual operating point, not a model-specific template. Readback and measured workload results decide the outcome.")]
    public string AdjustCurve(
        [Description("MHz to add to each point in the window (negative lowers).")] double deltaMhz,
        [Description("Lowest voltage of the window, mV; omit for the operating point minus 20 mV.")] double? fromMv = null,
        [Description("Highest voltage of the window, mV; omit for the operating point plus 10 mV.")] double? toMv = null) => Guard(() =>
    {
        if (deltaMhz == 0) throw new ArgumentException("deltaMhz is 0: nothing to try");
        var look = card.Read();
        var daily = DailyOffsets(out _);
        // Where the card ran under load: the last 3 s, else the last load within 2 minutes (a stress run that just ended). An
        // idle card's NVVDD (~800-835 mV) is not an operating point.
        uint? at = residency.MedianUv(OperatingSpan) ?? residency.LastLoadMedianUv(LastLoadWithin, LastLoadSpan);
        const string Idle = "the card has not been under load in the last 2 minutes, so there is no operating point: give fromMv and toMv (e.g. around the last stress run's p50 nvvdd_mv, or the crash voltage in data_points)";
        uint lo = fromMv is { } f ? (uint)(f * 1000) : (at ?? throw new InvalidOperationException(Idle)) - WindowBelowMv * 1000;
        uint hi = toMv is { } h ? (uint)(h * 1000) : (at ?? throw new InvalidOperationException(Idle)) + WindowAboveMv * 1000;
        var pts = look.Curve.Points;
        uint floor = FloorUv(look);
        if (hi < floor) throw new ArgumentException($"the window ({lo / 1000.0}-{hi / 1000.0} mV) is all under NVVDD's VMIN ({floor / 1000.0} mV): the card runs no point there");
        lo = Math.Max(lo, floor);
        var window = pts.Where(p => p.VoltageUv >= lo && p.VoltageUv <= hi).ToList();
        if (window.Count == 0) throw new ArgumentException($"no curve point between {lo / 1000.0} and {hi / 1000.0} mV");

        int delta = (int)Math.Round(deltaMhz * 1000);
        var next = look.Curve.Offsets();
        foreach (var p in window) next[p.Index] += delta;
        // A point raised past the one above it would cap the whole curve there: lift each point above the window that
        // would be overtaken just enough to match (onto the 7.5 MHz grid). That runs a clock already being tested at a
        // higher voltage, so it adds no risk.
        int top = window[^1].Index, lifted = 0;
        for (int k = top + 1; delta > 0 && k < pts.Count; k++)
        {
            long prevLanded = VfShapes.Landing(pts[k - 1].BaseKhz, next[k - 1]);
            if (VfShapes.Landing(pts[k].BaseKhz, next[k]) >= prevLanded) break;
            long rawNeed = prevLanded - pts[k].BaseKhz;
            int candidate = (int)(Math.Ceiling(rawNeed / 7500.0) * 7500);
            while (VfShapes.Landing(pts[k].BaseKhz, candidate) < prevLanded) candidate += 7500;
            next[k] = candidate;
            lifted = k;
        }
        if (lifted > 0) top = lifted;

        string change = string.Create(CultureInfo.InvariantCulture,
            $"{(delta > 0 ? "+" : "")}{delta / 1000.0} MHz on points {window[0].Index}-{window[^1].Index} ({window[0].VoltageUv / 1000.0}-{window[^1].VoltageUv / 1000.0} mV)")
            + (lifted > 0 ? $", points {window[^1].Index + 1}-{lifted} lifted to stay above" : "");
        // Everything this side can see goes into the tuning log with the trial: the readings, and each point before and after.
        var t = look.Telemetry;
        var context = new List<string> { Readings(look) };
        foreach (var p in pts.Where(p => p.Index >= window[0].Index && p.Index <= top))
            context.Add(string.Create(CultureInfo.InvariantCulture,
                $"point {p.Index} ({p.VoltageUv / 1000.0} mV): offset {p.OffsetKhz / 1000.0} → {next[p.Index] / 1000.0} MHz (daily {daily.ElementAtOrDefault(p.Index) / 1000.0}, above it by {(next[p.Index] - daily.ElementAtOrDefault(p.Index)) / 1000.0}), stock {p.BaseKhz / 1000.0} MHz, effective {p.EffectiveKhz / 1000.0} → about {(p.EffectiveKhz + next[p.Index] - p.OffsetKhz) / 1000.0} MHz"));
        var r = card.Broker(BrokerRequest.OpExperiment, "try",
            JsonSerializer.Serialize(new { curve = VfOffsets.Encode(next), change, at_uv = at, at_mhz = t?.GraphicsClockMhz, context }));
        if (!r.Ok) return Reply(r);
        var after = ReadAfter(out var unread);
        return Out(new
        {
            success = true,
            change,
            written = r.Written,
            landed = after?.Curve.Points.Where(p => p.Index >= window[0].Index && p.Index <= top).Select(p => Row(p, daily)),
            warning = unread,
        });
    });

    /// <summary>
    /// The offsets a flat top at <paramref name="targetKhz"/> needs: every point from <paramref name="fromUv"/> up, and any
    /// point under it already above the target (so the curve never falls), lowered to run at most the target, onto the
    /// 7.5 MHz grid, rounding down. Never raised; points under <paramref name="minUv"/> untouched. A point's clock without
    /// its offset is its effective clock less its offset, temperature compensation included.
    /// </summary>
    public static int[] FlattenPlan(IReadOnlyList<VfPoint> points, int[] offsets, uint fromUv, long targetKhz, uint minUv)
    {
        var next = (int[])offsets.Clone();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.VoltageUv < minUv || p.Index >= next.Length) continue;
            bool liftedByLeft = i > 0 && p.EffectiveKhz <= points[i - 1].EffectiveKhz && (long)p.EffectiveKhz - p.OffsetKhz > p.BaseKhz && p.BaseKhz > 0;
            long bare = liftedByLeft ? p.BaseKhz : (long)p.EffectiveKhz - p.OffsetKhz;
            if (p.VoltageUv < fromUv && bare + next[p.Index] <= targetKhz) continue;
            long want = (long)(Math.Floor((targetKhz - bare) / 7500.0) * 7500);
            if (want < next[p.Index]) next[p.Index] = (int)want;
        }
        return next;
    }

    [McpServerTool(Name = "flatten_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Lower the core curve to a flat top from a user-selected voltage, as a trial. Uses this GPU's actual curve; no stock voltage, knee or stable target clock is assumed.")]
    public string FlattenCurve(
        [Description("Voltage the flat top starts at, mV.")] double fromMv,
        [Description("MHz under that point's clock to flatten to (default 0).")] double marginMhz = 0,
        [Description("Or flatten to this clock, MHz, instead of the point's.")] double? ceilingMhz = null,
        [Description("Why (where the chip stopped scaling).")] string? reason = null) => Guard(() =>
    {
        if (marginMhz < 0) throw new ArgumentException("marginMhz lowers the top: give it as a positive number");
        var look = card.Read();
        var pts = look.Curve.Points;
        uint fromUv = (uint)Math.Round(fromMv * 1000), floor = FloorUv(look);
        if (fromUv < floor) throw new ArgumentException($"fromMv is at least NVVDD's VMIN, {floor / 1000.0} mV: the card runs no point under it");
        var at = pts.Where(p => p.VoltageUv >= fromUv).MinBy(p => p.VoltageUv) ?? throw new ArgumentException($"no curve point at or above {fromMv} mV");
        long target = ceilingMhz is { } c ? (long)Math.Round(c * 1000) : at.EffectiveKhz - (long)Math.Round(marginMhz * 1000);
        var before = look.Curve.Offsets();
        var next = FlattenPlan(pts, before, fromUv, target, floor);
        var moved = pts.Where(p => p.Index < next.Length && next[p.Index] != before[p.Index]).ToList();
        if (moved.Count == 0) throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"the curve is already at or under {target / 1000.0} MHz from {fromMv} mV: nothing to lower"));
        string change = string.Create(CultureInfo.InvariantCulture,
            $"flat top at {target / 1000.0} MHz from {at.VoltageUv / 1000.0} mV: points {moved[0].Index}-{moved[^1].Index} lowered");
        var context = new List<string> { Readings(look) };
        if (reason != null) context.Add($"Purpose: {reason}");
        foreach (var p in moved)
            context.Add(string.Create(CultureInfo.InvariantCulture,
                $"point {p.Index} ({p.VoltageUv / 1000.0} mV): offset {p.OffsetKhz / 1000.0} → {next[p.Index] / 1000.0} MHz, effective {p.EffectiveKhz / 1000.0} → about {(p.EffectiveKhz + next[p.Index] - p.OffsetKhz) / 1000.0} MHz"));
        var r = card.Broker(BrokerRequest.OpExperiment, "try",
            JsonSerializer.Serialize(new { curve = VfOffsets.Encode(next), change, at_uv = look.NvvddUv, at_mhz = look.Telemetry?.GraphicsClockMhz, context }));
        if (!r.Ok) return Reply(r);
        var after = ReadAfter(out var unread);
        var daily = DailyOffsets(out _);
        return Out(new
        {
            success = true, change, written = r.Written,
            landed = after?.Curve.Points.Where(p => p.Index >= moved[0].Index && p.Index <= moved[^1].Index).Select(p => Row(p, daily)),
            warning = Warnings(unread, Busy(look)),
        });
    });

    [McpServerTool(Name = "set_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the whole core V/F curve as per-point offsets for this GPU, as one trial. The point count and representable offsets must match the current driver layout. No reference curve or default daily curve is shipped.")]
    public string SetCurve(
        [Description("Every editable point's offset, kHz, as runs offset*count.")] string curve,
        [Description("Why this curve (where it came from).")] string? reason = null) => Guard(() =>
    {
        int[] next;
        try { next = VfOffsets.Decode(curve); } catch (FormatException e) { throw new ArgumentException($"'{curve}' is not a curve: {e.Message}"); }
        var look = card.Read();
        if (next.Length != look.Curve.Points.Count) throw new ArgumentException($"the curve has {next.Length} points; this card's has {look.Curve.Points.Count}");
        return CurveTrial(look, next, "whole curve written", reason);
    });

    [McpServerTool(Name = "shift_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Shift this GPU's current curve left by a user-selected mV amount as a trial, using the reported curve and offset budget. Evaluate the resulting clocks and stability on the actual GPU.")]
    public string ShiftCurve(
        [Description("mV to move the curve left by.")] double mv,
        [Description("Why (what the last pass showed).")] string? reason = null) => Guard(() =>
    {
        if (mv <= 0) throw new ArgumentException("mv moves the curve left: give it as a positive number (adjust_curve lowers)");
        var look = card.Read();
        var limits = VfLimits.From(card.WithDriver(Clocktail.Domain.Clocks.Pstates20Reader.Read));
        var shape = VfShapes.ShiftCurrent(look.Curve, limits, (int)Math.Round(mv * 1000), look.NvvddMaxUv);
        if (shape.AppliedUv == 0) throw new ArgumentException($"{mv} mV is less than one regulator step ({VfShapes.VidStepUv / 1000.0} mV): it moves no point");
        return CurveTrial(look, shape.Offsets, string.Create(CultureInfo.InvariantCulture, $"curve shifted left {shape.AppliedUv / 1000.0} mV from {shape.FloorUv / 1000.0} mV"), reason);
    });

    /// <summary>A whole new curve as a trial; the answer lists the points that moved, and warns when the GPU was busy.</summary>
    private string CurveTrial(Card.Look look, int[] next, string change, string? reason)
    {
        var daily = DailyOffsets(out _);
        var before = look.Curve.Offsets();
        var moved = look.Curve.Points.Where(p => p.Index < next.Length && next[p.Index] != before[p.Index]).ToList();
        if (moved.Count == 0) throw new ArgumentException("that is the curve on the card already: nothing to try");
        var context = new List<string> { Readings(look) };
        if (reason != null) context.Add($"Purpose: {reason}");
        foreach (var p in moved)
            context.Add(string.Create(CultureInfo.InvariantCulture,
                $"point {p.Index} ({p.VoltageUv / 1000.0} mV): offset {p.OffsetKhz / 1000.0} → {next[p.Index] / 1000.0} MHz, effective {p.EffectiveKhz / 1000.0} → about {(p.EffectiveKhz + next[p.Index] - p.OffsetKhz) / 1000.0} MHz"));
        change += $": points {moved[0].Index}-{moved[^1].Index}";
        var r = card.Broker(BrokerRequest.OpExperiment, "try",
            JsonSerializer.Serialize(new { curve = VfOffsets.Encode(next), change, at_uv = look.NvvddUv, at_mhz = look.Telemetry?.GraphicsClockMhz, context }));
        if (!r.Ok) return Reply(r);
        var after = ReadAfter(out var unread);
        return Out(new
        {
            success = true, change, written = r.Written,
            landed = after?.Curve.Points.Where(p => p.Index >= moved[0].Index && p.Index <= moved[^1].Index).Select(p => Row(p, daily)),
            warning = Warnings(unread, Busy(look)),
        });
    }

    /// <summary>How far the curve moved at the point a run held, between the read before it and the read after (the card
    /// still warm), with the temperatures it ran between: an A B B A comparison is only fair at one temperature.</summary>
    public static object? CurveShift(VfCurveSnapshot before, VfCurveSnapshot? after, int? index, IReadOnlyList<Card.Sample> samples)
    {
        if (index is not { } i || after == null) return null;
        var b = before.Points.FirstOrDefault(p => p.Index == i);
        var a = after.Points.FirstOrDefault(p => p.Index == i);
        if (b == null || a == null) return null;
        var temps = samples.Select(s => s.Telemetry?.TemperatureC).OfType<double>().ToList();
        double? start = temps.Count > 0 ? temps[0] : null, end = temps.Count > 0 ? temps[^1] : null;
        double shift = (a.EffectiveKhz - (double)b.EffectiveKhz) / 1000.0;
        return new
        {
            point = i, before_mhz = b.EffectiveKhz / 1000.0, after_mhz = a.EffectiveKhz / 1000.0, shift_mhz = shift,
            temp_start_c = start, temp_end_c = end,
            note = shift != 0 ? $"the curve moved {shift:+0.#;-0.#} MHz at this point as the card warmed: compare runs at the same temperature"
                : null,
        };
    }

    private VfCurveSnapshot? ReadCurveOrNull()
    {
        try { return card.Read().Curve; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or Clocktail.Hw.NvApiException or DriverBusyException) { return null; }
    }

    private static VfPoint? Nearest(VfCurveSnapshot c, uint uv)
    {
        var pts = c.Points;
        if (pts.Count == 0) return null;
        int lo = 0, hi = pts.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (pts[mid].VoltageUv < uv) lo = mid + 1;
            else hi = mid;
        }
        if (lo == 0) return pts[0];
        long leftDist = Math.Abs((long)pts[lo - 1].VoltageUv - uv);
        long rightDist = Math.Abs((long)pts[lo].VoltageUv - uv);
        return leftDist <= rightDist ? pts[lo - 1] : pts[lo];
    }

    private static object Row(VfPoint p, int[] daily) => new
    {
        index = p.Index,
        mv = p.VoltageUv / 1000.0,
        stock_mhz = p.BaseKhz / 1000.0,
        effective_mhz = p.EffectiveKhz / 1000.0,
        offset_mhz = p.OffsetKhz / 1000.0,
        daily_offset_mhz = daily.ElementAtOrDefault(p.Index) / 1000.0,
        above_daily_mhz = (p.OffsetKhz - daily.ElementAtOrDefault(p.Index)) / 1000.0,
    };
}
