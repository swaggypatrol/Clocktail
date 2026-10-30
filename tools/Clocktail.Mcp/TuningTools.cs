using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// Live tuning of the Core V/F curve while the person plays: read where the card runs, nudge the points around it (a
/// lowered wall moves it down the curve to sample other points), and turn every trial into a data point. Every write goes through the broker as an experiment trial, so the firmware
/// ceilings, the write window, the ownership journal and the stability-run lock all hold, and a crash during a trial
/// is recorded as the trial's and puts the daily profile back unchanged.
/// </summary>
[McpServerToolType]
public sealed partial class TuningTools(Card card, Residency residency, LiveWatch live)
{
    // What a write may set is the driver's and the firmware's range, no narrower (the user, 2026-10-27): until 0.4.1 the
    // tools held their own caps on top (15 MHz per adjust_curve call, 200 MHz above the daily profile, nothing under
    // 850 mV, steps per call for memory, XBAR, the boost and the ratio, a 100 A current floor), and refused a curve
    // written while the GPU was busy. A curve one scan put 83 MHz under the measured edge was 800 MHz above the daily
    // profile at 825 mV: no cap could tell that from a reckless one, and the person makes that call.
    /// <summary>The longest stress_test run (CLOCKTAIL_MCP_MAX_STRESS_S): the call blocks for the whole run.</summary>
    public static readonly int MaxStressSeconds = Env("CLOCKTAIL_MCP_MAX_STRESS_S", 300);
    /// <summary>stress_test stops the run at this temperature by default (CLOCKTAIL_MCP_STRESS_MAX_C): the agent cannot see
    /// the card while the call blocks, and a fixed low fan duty at the wall passed 85 °C in 40 s on the reference card.</summary>
    public static readonly int StressMaxC = Env("CLOCKTAIL_MCP_STRESS_MAX_C", 85);
    /// <summary>The highest maxTempC a run may ask for: under NVIDIA's own slowdown (90 °C on the reference card).</summary>
    public const int StressMaxCeilingC = 88;
    /// <summary>The default window around the operating point: further below than above, because under a wall a raised
    /// curve moves the operating point down (the card runs the same clock a point lower).</summary>
    public const int WindowBelowMv = 20, WindowAboveMv = 10;
    /// <summary>The lowest voltage adjust_curve and flatten_curve touch: NVVDD's VMIN as the driver reports it (800 mV on a
    /// 5090), since the card runs no point under it; the curve's lowest point where it is unreadable. A window that stops at
    /// the floor keeps a walled card from sliding under it, so the floor point is tested at its own voltage. It was 850 mV
    /// (the user, 2026-10-19) until the edge was measured from 800 mV up (2026-10-27).</summary>
    public static uint FloorUv(Card.Look look) => look.NvvddVminUv ?? look.Curve.Points.Min(p => p.VoltageUv);

    /// <summary>A curve written above this GPU utilization says so in its answer: on 2026-10-18 two of three hangs began
    /// with a curve written under full load. A warning, not a refusal (the user, 2026-10-27): the driver takes it.</summary>
    public const uint BusyUtilPct = 15;

    /// <summary>The warning for a curve written while the GPU is busy, or null.</summary>
    public static string? Busy(Card.Look look) => look.Telemetry?.GpuUtilPercent is { } u && u > BusyUtilPct
        ? $"written while the GPU was {u} % busy: on the reference card two of three hangs began with a curve written under full load; at idle is safer"
        : null;

    /// <summary>Why a board power limit is outside the range the driver reports for this board, or null.</summary>
    private string? OutsideBoard(double watts)
    {
        var board = card.WithDriver(PowerRequestsReader.Read).Board;
        double min = board.MinMw / 1000.0, max = board.MaxMw / 1000.0;
        return watts < min || watts > max ? string.Create(CultureInfo.InvariantCulture, $"{min:0.#}..{max:0.#} W on this board (the driver's range)") : null;
    }

    /// <summary>Several warnings as one, or null when there are none.</summary>
    private static string? Warnings(params string?[] w) => w.Where(x => x != null) is var all && all.Any() ? string.Join("; ", all) : null;
    /// <summary>The operating point is the median NVVDD under load over this long: a game's voltage moves with the scene.</summary>
    public static readonly TimeSpan OperatingSpan = TimeSpan.FromSeconds(3);
    /// <summary>adjust_curve on an idle card: the last load within this long (a stress run that just ended), its last 10 s.</summary>
    public static readonly TimeSpan LastLoadWithin = TimeSpan.FromMinutes(2), LastLoadSpan = TimeSpan.FromSeconds(10);

    /// <summary>Compact, with emoji and units as themselves (SettingsTools.Json): every token of an answer is one an agent
    /// with a small context pays for; until 0.4.0 these were indented and escaped.</summary>
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [McpServerTool(Name = "gpu_status", ReadOnly = true, OpenWorld = false), Description(
        "Where the card runs right now: NVVDD/MSVDD, core clock, the V/F point it sits on (stock, current and daily offset), " +
        "power, temperature and its margin, the memory temperature (memory_c), utilization, VRAM, NVML limit reasons, each " +
        "rail's amps and watts against its current limit, the 16-pin input (connector and slot watts, input current against " +
        "its limit, with a concern near it), the PCIe link (pcie: generation and width now and the most the slot allows, " +
        "replays since boot), what each GPC measures, and the experiment session. NVML on this card sets gpu_idle at " +
        "full load when the clock sits at the top of a curve cut flat, and sw_power_cap at ~340 W far under the 598 W wall: " +
        "read power against power_limit_w, not the reason bits.")]
    public string GpuStatus() => Guard(() =>
    {
        var look = card.Read();
        var daily = DailyOffsets(out var dailyNote);
        var t = look.Telemetry;
        object? point = null;
        if (look.NvvddUv is { } uv && Nearest(look.Curve, uv) is { } p)
            point = Row(p, daily);
        var session = card.Broker(BrokerRequest.OpExperiment, "show");
        GpcVoltages.Reading? gpc = null;
        try { gpc = card.Gpc(); } catch (InvalidOperationException) { }
        return Out(new
        {
            gpu = card.Name,
            at = DateTimeOffset.Now,
            nvvdd_mv = look.NvvddUv / 1000.0,
            msvdd_mv = look.MsvddUv / 1000.0,
            nvvdd_max_mv = look.NvvddMaxUv / 1000.0,
            clock_mhz = t?.GraphicsClockMhz,
            memory_mhz = t?.MemoryClockMhz,
            power_w = t?.PowerWatts,
            power_limit_w = t?.PowerEnforcedWatts,
            temp_c = t?.TemperatureC,
            temp_margin_c = t?.TempMarginC,
            pstate = t?.PState is { } ps ? $"P{ps}" : null,
            util_pct = t?.GpuUtilPercent,
            memory_c = card.MemoryC(),
            pcie = SettingsTools.Pcie(card.PcieLink()),
            vram_used_gib = Gib(t?.VramUsedBytes),
            vram_total_gib = Gib(t?.VramTotalBytes),
            memory_controller_pct = t?.MemUtilPercent,
            limit_reasons = t?.ThrottleReasons,
            rails = Rails(look),
            input = look.Input is { } inp ? new
            {
                connector_w = inp.ConnectorW is { } cw ? Math.Round(cw, 1) : (double?)null, pcie_slot_w = inp.PcieSlotW is { } pw ? Math.Round(pw, 1) : (double?)null,
                input_a = inp.InputA is { } ia ? Math.Round(ia, 1) : (double?)null, input_limit_a = inp.InputLimitA, concern = inp.Concern(),
            } : null,
            gpc_mv = gpc == null ? null : new
            {
                mean = Math.Round(gpc.MeanMv, 1), min = gpc.MinMv, max = gpc.MaxMv,
                mean_vs_nvvdd = look.NvvddUv is { } nv ? Math.Round(gpc.MeanMv - nv / 1000.0, 1) : (double?)null,
            },
            point,
            daily = dailyNote,
            session = session.Ok ? session.Result?.GetProperty("experiment") : null,
            broker = session.Error,
        });
    });

    [McpServerTool(Name = "curve_around", ReadOnly = true, OpenWorld = false), Description(
        "The Core V/F points around a voltage (default: where the card runs now): index, mV, stock MHz, effective MHz now, " +
        "offset now, the daily profile's offset, and how far above the daily profile each point is.")]
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

    [McpServerTool(Name = "begin_session", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Start an experiment session for a game. Every write after this (adjust_curve, set_fan_curve, set_clock_lock...) is a trial; its outcome becomes a data point " +
        "(stable, survived until replaced, or crash). A crash during a trial does not step the daily profile down. A trial " +
        "still in force from an earlier session ends first and the daily profile goes back on, as end_session does. " +
        "unattended=true when the person is away (an overnight stress mapping): after maxCrashes crashes (default 2) no " +
        "new trial is written until a new session, so the card is not hung over and over with nobody there; the daily " +
        "profile stays on and nothing steps down. warnings: another tool's settings on the card (not stock: every trial starts " +
        "from them) or another overclocking program running; tell the person before the first trial.")]
    public string BeginSession(
        [Description("The game being played.")] string game,
        [Description("Anything worth keeping with the data (settings, scene).")] string? note = null,
        [Description("The person is away: stop writing new trials after maxCrashes crashes.")] bool unattended = false,
        [Description("With unattended: the crashes allowed before trials stop, 1..10 (default 2).")] int maxCrashes = 2)
    {
        if (maxCrashes is < 1 or > 10) return Out(new { success = false, error = "maxCrashes is 1..10" });
        var r = card.Broker(BrokerRequest.OpExperiment, "begin", JsonSerializer.Serialize(new { game, note, unattended, max_crashes = maxCrashes }));
        if (!r.Ok) return Reply(r);
        var warnings = new List<string>();
        try
        {
            if (new SettingsTools(card).NotStock() is { Count: > 0 } others)
                warnings.Add($"the card is not at stock: another tool set {string.Join(", ", others.Select(o => $"{o.Name} {o.Describe(o.Found)}"))}. " +
                    "Every trial starts from these and back_to_daily returns to them: tell the person, and ask whether to tune from them, " +
                    "put the card to stock first (stock), or keep them as Clocktail's daily profile (keep_as_daily adopt=true)");
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or Clocktail.Hw.NvApiException) { }
        if (OtherTools.Running() is { Count: > 0 } running) warnings.Add(OtherTools.Advice(running));
        return warnings.Count == 0 ? Reply(r) : Merge(Reply(r), new { warnings });
    }

    [McpServerTool(Name = "adjust_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Raise or lower the Core V/F points around where the card runs now, as a new trial of the session. Any step within " +
        "the driver's and the firmware's own range, which the broker holds; the method's step is 15 MHz. The operating point " +
        "is the median NVVDD under load over the last 3 s (or the last 10 s of a load that ended within 2 minutes, such as a " +
        "stress run; an idle card needs fromMv/toMv); the default window is 20 mV below it to 10 mV above, and any point above " +
        "the window that the raise would overtake is lifted just enough to keep the curve rising. Points under NVVDD's VMIN " +
        "(800 mV on a 5090; the card runs none of them) are never touched. Under a power wall a raise shows as the same " +
        "clock at a lower voltage: the points the card then runs on are the ones tested (mark_stable records them). " +
        "The curve lands on a 7.5 MHz grid. Writing under full " +
        "load is how 2 of 3 hangs on 2026-10-18 began (the answer warns when the GPU was busy): prefer one small step, then " +
        "several minutes of play before the next, and call mark_stable or back_to_daily before moving on.")]
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
            long need = (long)pts[k - 1].BaseKhz + next[k - 1] - pts[k].BaseKhz;
            if (need <= next[k]) break;
            next[k] = (int)(Math.Ceiling(need / 7500.0) * 7500);
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
        foreach (var p in points)
        {
            if (p.VoltageUv < minUv || p.Index >= next.Length) continue;
            long bare = (long)p.EffectiveKhz - p.OffsetKhz;
            if (p.VoltageUv < fromUv && bare + next[p.Index] <= targetKhz) continue;
            long want = (long)(Math.Floor((targetKhz - bare) / 7500.0) * 7500);
            if (want < next[p.Index]) next[p.Index] = (int)want;
        }
        return next;
    }

    [McpServerTool(Name = "flatten_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Make the curve flat from a voltage up, as a new trial: every point from fromMv up runs at most the clock the point at " +
        "fromMv runs now (less marginMhz), or at most ceilingMhz. Only ever lowers, so it cannot push the card past what " +
        "it held; it is method step 4's 'flat where the chip stops scaling' in one call instead of many 15 MHz steps. Points " +
        "under NVVDD's VMIN are never touched. Write it at idle (the answer warns when the GPU was over 15 % busy). " +
        "back_to_daily undoes it; keep_as_daily keeps it.")]
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
        "Write a whole V/F curve as a new trial: every editable point's offset in kHz, in card_settings' encoding (runs as " +
        "offset*count, e.g. '0*60,30000*67'; '0*127' is the stock curve), as the CLI's --vf-offsets does. For loading a " +
        "curve worked out elsewhere, or putting back one a data point recorded; a band is adjust_curve's. Any curve within " +
        "the driver's range: the write path holds the firmware's offset range and budget, nothing narrower. Write it at idle " +
        "(the answer warns when the GPU was over 15 % busy). back_to_daily undoes it; keep_as_daily keeps it.")]
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
        "Move the curve on the card left by mv as a new trial: from the knee up each point runs the clock the current curve " +
        "runs mv above it, never past what it runs at NVVDD's MAX; below the knee nothing moves. The same clocks at less " +
        "voltage, so on a power-limited load more clock under the same wall. This is not the CLI's --vf-undervolt, which " +
        "moves the stock curve (on the reference card that put 895 mV at 2407 MHz where the daily curve ran 2797). It is a " +
        "raise at every voltage, so test it as a raise: stress_test across the loads, then games. Any shift within the " +
        "driver's range; small ones first (25 mV held on the reference card, and a 250 mV shift at 600 W bugchecked it). " +
        "Write it at idle (the answer warns when the GPU was over 15 % busy). back_to_daily undoes it; keep_as_daily keeps it.")]
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

    [McpServerTool(Name = "set_power_limit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the board power limit for the session, as a new trial with the curve left as it is. A lower wall makes the card " +
        "run lower down the curve, so adjust_curve can then sample points it never reaches at the full wall (and find where " +
        "the curve's slope bends). Later adjust_curve trials run under this wall until back_to_daily, end_session or a crash " +
        "puts the daily profile's wall back. This wall is never kept (keep_as_daily puts the daily wall back on); " +
        "set_daily_power_limit changes the daily one. Allowed: the board's range as the driver reports it (400-600 W on a 5090). " +
        "For points lower down than 400 W reaches, cap the frame rate in the game instead: a lighter load runs lower on the curve.")]
    public string SetPowerLimit(
        [Description("The board power limit, W.")] double watts,
        [Description("Why this wall (which part of the curve it is meant to reach).")] string? reason = null) => Guard(() =>
    {
        if (OutsideBoard(watts) is { } outside) throw new ArgumentException($"{watts} W is outside {outside}");
        var look = card.Read();
        var t = look.Telemetry;
        uint mw = (uint)Math.Round(watts * 1000);
        string change = string.Create(CultureInfo.InvariantCulture, $"power limit {t?.PowerEnforcedWatts:0.#} → {mw / 1000.0} W");
        var context = new List<string> { Readings(look) };
        if (reason != null) context.Add($"Purpose: {reason}");
        var before = look.Curve.Offsets();
        var r = card.Broker(BrokerRequest.OpExperiment, "try", JsonSerializer.Serialize(new
        {
            curve = VfOffsets.Encode(before), change, at_uv = look.NvvddUv, at_mhz = t?.GraphicsClockMhz,
            power_mw = mw, keep_curve = true, context,
        }));
        if (!r.Ok) return Reply(r);
        // The curve should not move with the wall (only a P-state write clears it); say so if it did.
        var after = ReadAfter(out var unread);
        bool? intact = after?.Curve.Offsets().SequenceEqual(before);
        return Out(new
        {
            success = true,
            change,
            written = r.Written,
            power_limit_w = after?.Telemetry?.PowerEnforcedWatts,
            curve_intact = intact,
            warning = unread ?? (intact == false ? "the curve changed when the wall was written: call back_to_daily before going on" : null),
        });
    });

    [McpServerTool(Name = "set_current_limit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set a rail's current limit (OCP, in amps) for the session, as a new trial with the curve left as it " +
        "is. The firmware caps the rail's current at it, so a lower NVVDD limit caps the core's power (P = V x I) and moves " +
        "the card down the curve below where the 400 W board wall stops; gpu_status shows each rail's amps and watts to " +
        "pick it from. Up to the driver's maximum (5001 A on a 5090), above the VBIOS default (NVVDD 600 A, MSVDD 180 A) " +
        "too; at the 600 W wall NVVDD peaks near 430 A on the reference card, so the board power limit binds first and a " +
        "raise changes nothing there. 'default' as amps puts the default back. Never kept: back_to_daily, end_session, " +
        "keep_as_daily, a crash and the agent disconnecting all put the default back.")]
    public string SetCurrentLimit(
        [Description("nvvdd or msvdd.")] string rail,
        [Description("The limit in amps, or 'default'.")] string amps,
        [Description("Why this limit (which part of the curve or what saving it is meant to show).")] string? reason = null) => Guard(() =>
    {
        var r = CurrentLimitWrites.ParseKey(rail);
        var look = card.Read();
        var limit = look.Limits?.Get(r) ?? throw new InvalidOperationException($"{rail} has no current limit on this card: {look.Limits?.Missing.GetValueOrDefault(r) ?? "the policy table is unreadable"}");
        uint ma = amps.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ? limit.DefaultMa
            : double.TryParse(amps, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0 ? (uint)Math.Round(a * 1000)
            : throw new ArgumentException($"'{amps}' is not a number of amps or 'default'");
        if (ma < limit.MinMa) throw new ArgumentException($"{ma / 1000.0} A is below the driver's {rail} minimum of {limit.MinMa / 1000.0} A");
        if (ma > limit.MaxMa) throw new ArgumentException($"{ma / 1000.0} A is above the driver's {rail} maximum of {limit.MaxMa / 1000.0} A");
        var t = look.Telemetry;
        string change = string.Create(CultureInfo.InvariantCulture, $"{CurrentLimitWrites.Key(r).ToUpperInvariant()} current limit {limit.LimitMa / 1000.0} → {ma / 1000.0} A");
        var context = new List<string> { Readings(look) };
        if (reason != null) context.Add($"Purpose: {reason}");
        var before = look.Curve.Offsets();
        var res = card.Broker(BrokerRequest.OpExperiment, "try", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["curve"] = VfOffsets.Encode(before), ["change"] = change, ["at_uv"] = look.NvvddUv, ["at_mhz"] = t?.GraphicsClockMhz,
            [r == Clocktail.Domain.Rails.Rail.Nvvdd ? "nvvdd_ma" : "msvdd_ma"] = ma, ["keep_curve"] = true, ["context"] = context,
        }));
        if (!res.Ok) return Reply(res);
        var after = ReadAfter(out var unread);
        bool? intact = after?.Curve.Offsets().SequenceEqual(before);
        return Out(new
        {
            success = true,
            change,
            written = res.Written,
            rails = after == null ? null : Rails(after),
            curve_intact = intact,
            warning = unread ?? (intact == false ? "the curve changed when the limit was written: call back_to_daily before going on" : null),
        });
    });

    [McpServerTool(Name = "stress_test", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Run the driver's own golden stressor (nvgwls.exe, what NVIDIA App's auto-tune runs) on what is on the card now, " +
        "sampling the operating point twice a second; no game and no person needed, so it is the way to test while the " +
        "person is away. It compares every graphics, compute and ray-tracing result bit for bit with the first, so it " +
        "catches silent errors a game never shows. load 100 runs flat out (on this card it pins the board wall: 598 W, " +
        "~2900 MHz on the daily curve); under 100 it is a 250 Hz square wave at that duty (load 50: ~385 W, ~3055 MHz, " +
        "the curve's top). Combine with set_power_limit / set_current_limit to pin it lower down the curve. If it fails " +
        "(TDR, Xid, data error, crash, hang, or a driver reset in the System log during the run) while a trial is in force, " +
        "the trial is recorded as a crash (after a driver reset by the service itself, with the voltage it hung at) and the " +
        "daily profile goes back on. It holds the card while it runs, as the lab does: no other write lands under it, and " +
        "another run must finish first. The result goes into the tuning log. Blocks for the run's length.")]
    public string StressTest(
        [Description("0..100; 100 = continuous full load (default).")] int load = 100,
        [Description("Run length in seconds, 10..300 (default 60).")] int seconds = 60,
        [Description("What this run is meant to show, for the log.")] string? note = null,
        [Description("Stop the run if the GPU reaches this temperature, °C (default 85, at most 88); a stop is neither a pass nor a crash.")] int? maxTempC = null) => Guard(() =>
    {
        if (load is < 0 or > 100) throw new ArgumentException("load is 0..100");
        int maxC = maxTempC ?? StressMaxC;
        if (maxC is < 50 or > StressMaxCeilingC) throw new ArgumentException($"maxTempC is 50..{StressMaxCeilingC}");
        if (seconds < 10 || seconds > MaxStressSeconds) throw new ArgumentException($"seconds is 10..{MaxStressSeconds} (CLOCKTAIL_MCP_MAX_STRESS_S)");
        string exe = Nvgwls.Find() ?? throw new InvalidOperationException("nvgwls.exe was not found beside the loaded driver");
        var samples = new List<Card.Sample>();
        int? code;
        bool tooHot = false;
        int hotRun = 0;
        double? stopC = null;
        bool Sample()
        {
            try
            {
                var s = card.ReadSample();
                samples.Add(s);
                hotRun = Nvgwls.TooHot(s.Telemetry?.TemperatureC, s.Telemetry?.TempMarginC, maxC) ? hotRun + 1 : 0;
                if (hotRun >= 2) { stopC = s.Telemetry?.TemperatureC; return true; }
            }
            catch (DriverBusyException) { }
            return false;
        }
        // A hang must be called only after the driver has had its TdrDelay to reset (30 s on the reference machine).
        int grace = (OperatingSystem.IsWindows() ? Clocktail.Domain.Platform.TdrSettings.Read() : null)?.StressGraceS ?? 30;
        // The run holds the card (judge.lock): no write but its own lands under it, it starts once none is landing, and its
        // result is the trial's in force then. Given back before anything is recorded: a crash puts the daily profile back.
        var hold = HoldCard("stress_test", olderToo: false);
        Card.Look before;
        var started = DateTime.UtcNow;
        try
        {
            before = card.Read();   // the card as the run finds it, once no write is landing
            started = DateTime.UtcNow;
            // A sample the driver does not answer in time is a missing one (a hang is what this run is looking for).
            residency.StressUntil(DateTimeOffset.Now.AddSeconds(seconds + 30));   // light load counts as load for this run
            code = Nvgwls.Run(exe, load, seconds, Sample, out tooHot, grace);
        }
        finally { residency.StressUntil(DateTimeOffset.MinValue); hold.Dispose(); }
        int? resets = tooHot ? Nvgwls.DriverResets(started, 0) : Nvgwls.DriverResets(started, code);
        double ranS = Math.Round((DateTime.UtcNow - started).TotalSeconds);
        string verdict = tooHot && resets is not > 0
            ? string.Create(CultureInfo.InvariantCulture, $"thermal stop at {stopC:0} °C after {ranS:0} s (maxTempC {maxC}): stopped by stress_test, not a pass or a crash")
            : Nvgwls.Verdict(code, resets);
        bool failed = tooHot ? resets > 0 : Nvgwls.Failed(code, resets);

        // The first 2 s are the ramp; the rest is what the settings were tested at. A run that hung ends in frozen readings
        // (the curve's top at ~125 W for the whole TdrDelay): they are not what it was tested at.
        IReadOnlyList<Card.Sample> held = samples.Count > 4 ? samples.Skip(4).ToList() : samples;
        if (failed) held = WithoutFrozenTail(held);
        static double? P(IEnumerable<double?> xs, double q)
        {
            var v = xs.Where(x => x != null).Select(x => x!.Value).Order().ToList();
            return v.Count == 0 ? null : Math.Round(v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)], 1);
        }
        object Stat(double q) => new
        {
            clock_mhz = P(held.Select(s => (double?)s.Telemetry?.GraphicsClockMhz), q),
            nvvdd_mv = P(held.Select(s => s.NvvddUv / 1000.0), q),
            msvdd_mv = P(held.Select(s => s.MsvddUv / 1000.0), q),
            board_w = P(held.Select(s => s.Telemetry?.PowerWatts), q),
            nvvdd_w = P(held.Select(s => s.NvvddOut?.Mw / 1000.0), q),
            nvvdd_a = P(held.Select(s => s.NvvddOut?.Ma / 1000.0), q),
            msvdd_w = P(held.Select(s => s.MsvddOut?.Mw / 1000.0), q),
            msvdd_a = P(held.Select(s => s.MsvddOut?.Ma / 1000.0), q),
            xbar_mhz = P(held.Select(s => s.Mhz(AllClocksLayout.Xbar)), q),
            sys_mhz = P(held.Select(s => s.Mhz(AllClocksLayout.Sys)), q),
            memory_mhz = P(held.Select(s => s.Mhz(AllClocksLayout.Memory)), q),
            temp_c = P(held.Select(s => (double?)s.Telemetry?.TemperatureC), q),
            memory_c = P(held.Select(s => s.MemoryC), q),
            vram_used_gib = P(held.Select(s => Gib(s.Telemetry?.VramUsedBytes)), q),
            gpc_mean_mv = P(held.Select(s => s.Gpc?.MeanMv), q),
            gpc_min_mv = P(held.Select(s => s.Gpc?.MinMv), q),
        };
        string csv = WriteSamples(samples, load, seconds);
        double? p50Mv = P(held.Select(s => s.NvvddUv / 1000.0), 0.5), p50Mhz = P(held.Select(s => (double?)s.Telemetry?.GraphicsClockMhz), 0.5);
        double? p50W = P(held.Select(s => s.Telemetry?.PowerWatts), 0.5), p50NvW = P(held.Select(s => s.NvvddOut?.Mw / 1000.0), 0.5);
        double? p50MsMv = P(held.Select(s => s.MsvddUv / 1000.0), 0.5), p50MsW = P(held.Select(s => s.MsvddOut?.Mw / 1000.0), 0.5);
        var reasons = held.SelectMany(s => s.Telemetry?.ThrottleReasons ?? []).Distinct().ToList();
        int? pointIndex = p50Mv is { } mv && Nearest(before.Curve, (uint)(mv * 1000)) is { } pt ? pt.Index : null;
        var shift = CurveShift(before.Curve, ReadCurveOrNull(), pointIndex, held);

        bool trial = hold.Trial != null;   // in force when the run began: the trial it measured
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Stress nvgwls -l {load}, {seconds} s: {verdict} (exit code {(code?.ToString(CultureInfo.InvariantCulture) ?? "none")}). P50 {p50Mhz} MHz @ NVVDD {p50Mv} mV (curve point {pointIndex}), XBAR {P(held.Select(s => s.Mhz(AllClocksLayout.Xbar)), 0.5)} / SYS {P(held.Select(s => s.Mhz(AllClocksLayout.Sys)), 0.5)} MHz, board {p50W} W, NVVDD {p50NvW} W, MSVDD {p50MsMv} mV / {p50MsW} W; max {held.Max(s => s.Telemetry?.TemperatureC)} °C; limit reasons [{string.Join(", ", reasons)}]; {held.Count} samples; per sample {csv}")
            + (note != null ? $". Purpose: {note}" : "");
        card.Broker(BrokerRequest.OpExperiment, "note", summary);
        bool crashRecorded = failed && RecordCrash(hold, resets, $"nvgwls -l {load} {seconds} s: {verdict}");
        return Out(new
        {
            passed = code == 0 && !failed,
            verdict,
            exit_code = code,
            driver_resets = resets,
            load,
            seconds,
            samples = held.Count,
            p50 = Stat(0.5),
            p10 = Stat(0.1),
            p90 = Stat(0.9),
            limit_reasons = reasons,
            pstates = PStates(held),
            efficiency = Efficiency(held, null, p50Mhz),
            curve_point = pointIndex,
            curve_shift = shift,
            samples_csv = csv,
            trial_in_force = trial,
            crash_recorded = crashRecorded,
            thermal_stop = tooHot && !failed ? new { at_c = stopC, after_s = ranS, max_temp_c = maxC } : null,
            next = failed ? "the card failed: read data_points / read_log before the next step"
                : tooHot ? "too hot to test this way: a steeper fan curve (set_fan_curve), fans back to auto, or a lower wall, then run again" : null,
        });
    });

    [McpServerTool(Name = "mark_stable", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Record the trial in force as stable in this game, with a note (minutes played, scene, anything seen). The curve stays on. " +
        "The curve points the card ran on under load during the trial (from the once-a-second residency samples) go into " +
        "the note: those are the points this trial proved, not the whole window.")]
    public string MarkStable([Description("What was played and seen.")] string note) => Guard(() =>
        Reply(card.Broker(BrokerRequest.OpExperiment, "stable", note + TrialStays(out _))));

    /// <summary>The driver's temperature-compensation steps on the reference card (warming: ~57/62/72/82 °C; at 82 °C the
    /// curve dropped 8-15 MHz at 940-990 mV, re/tuning-results §26). Two runs either side of one are not comparable.</summary>
    public static readonly double[] ThermalStepsC = [57, 62, 72, 82];

    /// <summary>The step a temperature sits within 2 °C of, or null.</summary>
    public static double? NearThermalStep(double? tempC) =>
        tempC is { } t && ThermalStepsC.FirstOrDefault(s => Math.Abs(s - t) <= 2) is var s && s > 0 ? s : null;

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
        double? step = NearThermalStep(end);
        return new
        {
            point = i, before_mhz = b.EffectiveKhz / 1000.0, after_mhz = a.EffectiveKhz / 1000.0, shift_mhz = shift,
            temp_start_c = start, temp_end_c = end,
            note = shift != 0 ? $"the curve moved {shift:+0.#;-0.#} MHz at this point as the card warmed: compare runs at the same temperature"
                : step is { } st ? $"ended within 2 °C of the {st:0} °C compensation step: a degree either way moves the curve" : null,
        };
    }

    private VfCurveSnapshot? ReadCurveOrNull()
    {
        try { return card.Read().Curve; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or Clocktail.Hw.NvApiException or DriverBusyException) { return null; }
    }

    /// <summary>
    /// What a stretch of load cost: the average board power, the energy (Wh, the samples' watts over the time between
    /// them) and, with a frame rate, frames per second per 100 W, the figure a power-limit sweep compares (manual
    /// 'power-thermal'). Null without board power.
    /// </summary>
    public static object? Efficiency(IReadOnlyList<Card.Sample> samples, double? avgFps, double? p50Mhz = null)
    {
        var w = samples.Where(s => s.Telemetry?.PowerWatts is not null).ToList();
        if (w.Count == 0) return null;
        double avgW = w.Average(s => s.Telemetry!.PowerWatts!.Value), wh = 0;
        for (int i = 1; i < w.Count; i++)
        {
            double dt = (w[i].At - w[i - 1].At).TotalSeconds;
            if (dt is > 0 and < 5) wh += w[i].Telemetry!.PowerWatts!.Value * dt / 3600;   // a gap (load stopped) is not energy spent here
        }
        return new
        {
            avg_board_w = Math.Round(avgW, 1),
            energy_wh = Math.Round(wh, 2),
            fps_per_100w = avgFps is { } f && avgW > 0 ? Math.Round(f / avgW * 100, 2) : (double?)null,
            mhz_per_w = p50Mhz is { } m && avgW > 0 ? Math.Round(m / avgW, 2) : (double?)null,
        };
    }

    /// <summary>The performance states a stretch of samples ran in, most first ("P0 98 %, P2 2 %"); null when none was read.
    /// The memory offset acts in P0 only, so a compute load sitting in P2 does not test it.</summary>
    public static string? PStates(IReadOnlyList<Card.Sample> samples)
    {
        var read = samples.Select(s => s.Telemetry?.PState).OfType<int>().ToList();
        return read.Count == 0 ? null : string.Join(", ", read.GroupBy(p => p).OrderByDescending(g => g.Count())
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"P{g.Key} {100.0 * g.Count() / read.Count:0} %")));
    }

    /// <summary>stress_test: how long a run that saw a driver reset waits for the service to record the crash (it settles
    /// 3 s, gives the cells back, then records it; a write window or a judge in the way can make it later).</summary>
    public static readonly TimeSpan ServiceRecordsWithin = TimeSpan.FromSeconds(30);

    /// <summary>Whether the experiment answer ("show") has a trial in force; null when it could not be read.</summary>
    public static bool? TrialInForce(BrokerAnswer show) =>
        show.Ok && show.Result is { } r && r.TryGetProperty("experiment", out var e)
            ? e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object
            : null;

    /// <summary>Polls once a second until no trial is in force (true) or <paramref name="within"/> passes (false). An
    /// answer that cannot be read does not count as closed.</summary>
    public static bool TrialClosedWithin(Func<bool?> trialInForce, TimeSpan within, Action<TimeSpan> sleep)
    {
        for (var waited = TimeSpan.Zero; ; waited += TimeSpan.FromSeconds(1))
        {
            if (trialInForce() == false) return true;
            if (waited >= within) return false;
            sleep(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The fields of a trial that say nothing about what the card runs: when and why it was written, how it ended,
    /// the operating point at the write, and what a hand-back takes off it (a fixed fan duty, current limits).</summary>
    private static readonly HashSet<string> NotSettings = ["At", "Change", "Marked", "Ending", "AtUv", "AtMhz", "FanPct", "NvvddMa", "MsvddMa", "Writes"];

    /// <summary>
    /// The settings the trial in force runs, from a "show" or "quiet" answer: every field but when and why it was written
    /// and what a hand-back of trial-only cells takes off it, and its writes but the trial-only ones (a clock floor or lock,
    /// the F8 request). A run's crash goes to the trial in force at its end only when these are the ones it measured: the
    /// fan guard handing the fans back mid-run writes the trial again (a new "At") with the same settings. Null when no trial
    /// is in force or the answer cannot be read.
    /// </summary>
    public static string? TrialSettings(BrokerAnswer show)
    {
        if (!show.Ok || show.Result is not { } r || !r.TryGetProperty("experiment", out var e) || e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("Trial", out var t) || t.ValueKind != JsonValueKind.Object) return null;
        var parts = t.EnumerateObject().Where(p => !NotSettings.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value.GetRawText()}").ToList();
        if (t.TryGetProperty("Writes", out var ws) && ws.ValueKind == JsonValueKind.Array)
            parts.AddRange(ws.EnumerateArray()
                .Where(w => !(Str(w, "Kind") is { } k && Str(w, "Key") is { } key && DailyProfile.TrialOnly(k, key)))
                .Select(w => w.GetRawText()));
        return string.Join("|", parts);
    }

    /// <summary>How many times a measurement asks the service to settle before it gives up (each ask waits up to the
    /// service's own 30 s for the operation in flight).</summary>
    public const int QuietTries = 4;

    /// <summary>
    /// A measurement's start, once it holds judge.lock: "quiet", which the service answers once no operation is in flight
    /// (a write that looked at the lock before it was there may still be landing), asked again while the service is busy
    /// (WindowBusy), at most <paramref name="tries"/> times. The answer is the service's "show": the trial the run measures.
    /// A service from before 0.3.7 does not know "quiet" (BadRequest, <paramref name="older"/>), and its "show" stands in;
    /// one that cannot be reached writes nothing, and its answer stands. Throws when the service stays busy or stops
    /// answering: nothing is run.
    /// </summary>
    public static BrokerAnswer Quiet(Func<string, BrokerAnswer> experiment, int tries, Action<TimeSpan> sleep, out bool older)
    {
        older = false;
        for (int ask = 1; ; ask++)
        {
            var q = experiment("quiet");
            if (q.Ok || q.Code == BrokerCode.BrokerUnreachable) return q;
            if (q.Code == BrokerCode.BadRequest)
            {
                older = true;
                return experiment("show");
            }
            if (q.Code != BrokerCode.WindowBusy || ask >= tries)
                throw new InvalidOperationException($"the service did not settle before the run ({q.Error ?? "no answer"}): nothing was run; try again");
            sleep(TimeSpan.FromSeconds(1));
        }
    }

    [McpServerTool(Name = "mark_crash", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "The game crashed to the desktop (or froze) with no driver reset: record the trial in force as a crash, with what " +
        "the player saw, and put the daily profile back at once. A driver reset is recorded by the broker by itself; use " +
        "this for the crashes it cannot see. Afterwards, read data_points before the next step.")]
    public string MarkCrash([Description("What happened and where (scene, loading, alt-tab).")] string note)
    {
        // The crash is recorded and the daily profile put back whatever the residency can say.
        string stays;
        try { stays = TrialStays(out var last) + last; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or Clocktail.Hw.NvApiException) { stays = ""; }
        var r = card.Broker(BrokerRequest.OpExperiment, "crash", note + stays);
        var back = r.Result is { } res && res.TryGetProperty("back", out var b) && b.ValueKind == JsonValueKind.Object ? b : (JsonElement?)null;
        var point = r.Result is { } res2 && res2.TryGetProperty("point", out var p) ? p : (JsonElement?)null;
        return Applied(r with { Result = back }, point);
    }

    [McpServerTool(Name = "back_to_daily", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Put the daily profile back on at once (artefacts, stutter, a doubt). The trial in force ends as survived; the session stays open.")]
    public string BackToDaily([Description("Why, for the tuning log.")] string? reason = null) =>
        Applied(card.Broker(BrokerRequest.OpProfile, "apply", reason ?? "back_to_daily (agent)"), null);

    [McpServerTool(Name = "stock", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description(
        "Put the card to stock, NVIDIA's own settings, and keep it there: every setting Clocktail wrote goes back, then every " +
        "setting another tool left (card_report's 'Not set by Clocktail': an MSI Afterburner curve, a memory offset, a power " +
        "limit, fixed fans, a clock lock) is written to NVIDIA's default (cleared lists them). The daily profile is suspended " +
        "(kept, not deleted), so nothing puts it back at the next start or driver reset until load_daily; what another tool set " +
        "is not kept anywhere, and only that tool can put it back, so then Clocktail asks the person itself, in the chat: that " +
        "first call changes nothing and answers with the question and a confirm code (code confirm_in_chat); put the question to " +
        "the person word for word and only after their yes call again with confirm set to that code. The trial in force (if any) " +
        "ends as survived. Another tool still running may write its settings back (card_report says), and NVIDIA App's " +
        "automatic tuning comes back after every restart until it is turned off in NVIDIA App. Tell the person before calling this.")]
    public async Task<string> Stock(McpServer server, [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default)
    {
        // Another tool's settings cleared are gone for good (the broker lets go of them): the person says yes to that.
        IReadOnlyList<Clocktail.Domain.FactoryCell> others;
        try { others = new SettingsTools(card).NotStock(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or Clocktail.Hw.NvApiException) { others = []; }
        if (others.Count == 0) return Stock();
        return await Asked(server,
            $"Put your graphics card to NVIDIA's stock settings. This clears what another tool set ({string.Join(", ", others.Select(o => $"{o.Name} {o.Describe(o.Found)}"))}); " +
            "Clocktail keeps no copy of it, so only that tool can put it back. Your daily profile is kept and load_daily puts it back.",
            confirm, cancellationToken, Stock);
    }

    private string Stock()
    {
        // Since 0.4.0 "factory": what "stock" did (the profile suspended, what the service holds given back), then what
        // another tool left written to NVIDIA's default. A service from 0.3.7 on knows "stock" alone (the card as Clocktail
        // found it), one from before "off" then a recovery: the answer says the other tool's settings stayed.
        var factory = card.Broker(BrokerRequest.OpProfile, "factory");
        if (factory.Code != BrokerCode.BadRequest)
        {
            var cleared = factory.Result is { } fr && fr.TryGetProperty("cleared", out var cl) && cl.ValueKind == JsonValueKind.Array && cl.GetArrayLength() > 0 ? cl : (JsonElement?)null;
            return Merge(Applied(factory, null), new { cleared });
        }
        const string older = "the service is older than this server: only Clocktail's own settings went back, and anything another tool set stays on the card; call install_service (with the person's OK) to update it, then stock again";
        var stock = card.Broker(BrokerRequest.OpProfile, "stock");
        if (stock.Code != BrokerCode.BadRequest)
            return Merge(Applied(stock.Result is { } res && res.TryGetProperty("recover", out var rec) && rec.ValueKind == JsonValueKind.Object ? stock with { Result = rec } : stock, null), new { note = older });
        var off = card.Broker(BrokerRequest.OpProfile, "off");
        return Merge(off.Ok ? Applied(card.Broker(BrokerRequest.OpRecover), null) : Applied(off, null), new { note = older });
    }

    /// <summary>One answer's JSON object with more fields; a field that is null is left out.</summary>
    private static string Merge(string answer, object more)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(answer)!.AsObject();
        foreach (var p in System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(more, Json))!.AsObject().ToList())
            if (p.Value != null) node[p.Key] = p.Value.DeepClone();
        return JsonText.Unescape(node.ToJsonString(Json));
    }

    [McpServerTool(Name = "load_daily", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "End a suspension (after stock) and put the kept daily profile on the card now; it goes on at every start and after " +
        "every driver reset again. A profile kept on another VBIOS goes on too and is bound to this one: stress_test it right after (load 100 and 50). " +
        "Fails when no daily profile is kept (a fresh install: offer a preset instead).")]
    public string LoadDaily() => Applied(card.Broker(BrokerRequest.OpProfile, "load"), null);

    [McpServerTool(Name = "preview_daily", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "What the daily profile would become, before asking the person: with no name, what keep_as_daily would keep (every " +
        "cell on the card this trial and the ones before it touched, not only the last step), and what it would not keep and " +
        "why; adopt=true, what keep_as_daily adopt=true would keep (another tool's settings too); with a preset's or saved " +
        "profile's name, what load_preset would load. Each change is daily now → becomes, with " +
        "the card's value now. Writes nothing. Show it, then ask.")]
    public string PreviewDaily(
        [Description("A preset's or saved profile's name; omit for keep_as_daily.")] string? name = null,
        [Description("Preview keep_as_daily adopt=true: another tool's settings kept too.")] bool adopt = false) =>
        adopt ? Older(card.Broker(BrokerRequest.OpProfile, "preview-adopt")) : Reply(card.Broker(BrokerRequest.OpProfile, "preview", name));

    /// <summary>A reply, unless the service is too old for the op (0.4.0's adopt): then that, and how to update it.</summary>
    private static string Older(BrokerAnswer r) => r.Code == BrokerCode.BadRequest
        ? Out(new { success = false, error = "the service is older than this server and cannot adopt another tool's settings: call install_service (with the person's OK) to update it", code = r.Code })
        : Reply(r);

    [McpServerTool(Name = "keep_as_daily", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Make what the trials set on the card the new daily profile, permanently: it goes on at every start and after " +
        "every driver reset from then on. Show preview_daily first: it lists exactly what would be kept. Kept from the card: the V/F curve, the core and memory P0 " +
        "offsets, NVVDD's and MSVDD's offsets, the XBAR/SYS/Video clock offsets and voltage demands and the core's voltage " +
        "demand, the XBAR/core clock ratio, the voltage boost, a clock cap (release one set only for a test first), the fan " +
        "curve and the fans' zero-RPM band. Not kept: the power limit (it stays the daily profile's; set_daily_power_limit " +
        "changes it), the F8 power request, current limits, fixed fan duties, and a clock floor, point lock or Boost Lock; a trial's lowered wall, limit, fixed fans or lock go back at once. " +
        "Everything a trial set is kept, not just the last change: call back_to_daily first if only the curve should be. " +
        "The curve kept is the one on the card: if the trial ran under a lowered wall or limit, the curve is what matters, " +
        "not the wall it was found under. The trial in force (if any) " +
        "is recorded as stable. adopt=true also keeps what another tool left on the card and Clocktail never wrote (card_report's " +
        "'Not set by Clocktail': an MSI Afterburner curve, offsets, voltage demands, the boost), so the service puts it back after every " +
        "boot and the person can close that tool; its power limit and fans are not kept. The old and the new profile go into the tuning log. " + AsksItself)]
    public async Task<string> KeepAsDaily(
        McpServer server,
        [Description("Why this becomes the daily profile (games and minutes it survived).")] string note,
        [Description("Keep another tool's settings on the card too (show preview_daily adopt=true first).")] bool adopt = false,
        [Description(ConfirmCode)] string? confirm = null,
        CancellationToken cancellationToken = default)
    {
        var preview = card.Broker(BrokerRequest.OpProfile, adopt ? "preview-adopt" : "preview");
        if (adopt && preview.Code == BrokerCode.BadRequest) return Older(preview);
        if (!preview.Ok) return Reply(preview);
        return await Asked(server, $"Make what is on your graphics card now your daily profile ({Changes(preview)}). Clocktail puts it back after every start and driver reset.", confirm, cancellationToken, () =>
        {
            var r = card.Broker(BrokerRequest.OpProfile, adopt ? "adopt" : "keep", note);
            if (!r.Ok || r.Result is not { } res) return Reply(r);
            var writes = res.GetProperty("profile").GetProperty("Writes").EnumerateArray()
                .Select(w => $"{w.GetProperty("Kind").GetString()}/{w.GetProperty("Key").GetString()} = {w.GetProperty("Value").GetString()}");
            return Out(new { success = true, daily = writes });
        });
    }

    /// <summary>For the description of each tool the person confirms (<see cref="Consent"/>).</summary>
    private const string AsksItself = "Clocktail asks the person itself before it does this, in the chat: the first call changes nothing " +
        "and answers with the question and a confirm code (code confirm_in_chat); put the question to the person word for word, and only " +
        "after their yes call again with the same arguments and confirm set to that code. When they do not confirm, nothing is changed.";

    /// <summary>The <c>confirm</c> parameter of each tool the person confirms.</summary>
    private const string ConfirmCode = "The code from this tool's first answer, given only after the person said yes to its question in chat; omit it to ask.";

    /// <summary>The person's yes (<see cref="Consent"/>) before <paramref name="then"/>; without it, why not, and nothing done.</summary>
    private static async Task<string> Asked(McpServer server, string what, string? confirm, CancellationToken ct, Func<string> then) =>
        await Consent.Ask(server, what, confirm, ct) is { } no
            ? Out(new { success = false, error = no.Error, code = no.Code, question = no.Question, confirm = no.ConfirmCode, written = WriteOutcome.None })
            : Guard(then);

    /// <summary>A profile preview's changes in a few words ("V/F curve, memory offset"), for the person's question.</summary>
    private static string Changes(BrokerAnswer preview)
    {
        var names = preview.Result is { } r && r.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => Setting(Str(x, "kind"), Str(x, "key"))).Distinct().ToList() : [];
        return names.Count == 0 ? "no change from the daily profile" : string.Join(", ", names);
    }

    private static string Setting(string? kind, string? key) => (kind, key) switch
    {
        ("vf", _) => "V/F curve",
        ("pstates20", "memory") => "memory offset",
        ("pstates20", _) => "core offset",
        ("clockdomain", var k) => $"{k?.ToUpperInvariant()} offset",
        ("vdemand", var k) => $"{(k == "graphics" ? "core" : k?.ToUpperInvariant())} voltage demand",
        ("boost", _) => "voltage boost",
        ("power", _) => "power limit",
        ("fancurve", _) => "fan curve",
        ("fanstop", _) => "fan stop band",
        ("rails", var k) => $"{k?.ToUpperInvariant()} offsets",
        ("clockratio", _) => "XBAR/core ratio",
        ("clocklimit", _) => "clock cap",
        _ => $"{kind}/{key}",
    };

    [McpServerTool(Name = "list_presets", ReadOnly = true, OpenWorld = false), Description(
        "The daily profiles that ship with Clocktail, each with the card model it was tuned on and whether this card is that " +
        "model, and the profiles the person saved under a name (save_profile). A fresh install writes nothing (the card is what " +
        "Clocktail found; card_report says whether that is stock); a preset is a starting point the person may choose instead. " +
        "load_preset loads either kind by name.")]
    public string ListPresets() => Guard(() =>
    {
        var saved = card.Broker(BrokerRequest.OpProfile, "saved");
        return Out(new
        {
            gpu = card.Name,
            presets = Presets.All.Select(p => new
            {
                p.Name, p.Title, tuned_on = p.Gpu, fits_this_card = p.Fits(card.Name), p.Note,
                writes = p.Writes.Select(w => $"{w.Kind}/{w.Key} = {w.Value}"),
            }),
            saved = saved.Ok && saved.Result is { } res && res.TryGetProperty("profiles", out var list) ? list : (JsonElement?)null,
            saved_error = saved.Ok ? null : saved.Error,
        });
    });

    [McpServerTool(Name = "save_profile", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Save the daily profile under a name (\"quiet\", \"games\", \"benchmark\"), so the person can keep several and switch " +
        "with load_preset and that name. It saves what is kept, not a trial on the card: keep_as_daily first when the person " +
        "wants the card's current settings saved. A name already used is replaced (say so). The daily profile itself does not " +
        "change. Any name the person likes, in any language (not a preset's name; case does not matter).")]
    public string SaveProfile([Description("The name to save it under.")] string name) =>
        Guard(() => Reply(card.Broker(BrokerRequest.OpProfile, "save", name)));

    [McpServerTool(Name = "delete_profile", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Delete a profile saved with save_profile. The daily profile on the card and the presets are not affected. " + AsksItself)]
    public Task<string> DeleteProfile(McpServer server, [Description("The saved profile's name, from list_presets.")] string name,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default) =>
        Asked(server, $"Delete the profile saved as '{name}'. It cannot be undone; the settings on your graphics card do not change.", confirm, cancellationToken,
            () => Reply(card.Broker(BrokerRequest.OpProfile, "forget", name)));

    [McpServerTool(Name = "load_preset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Make a preset, or a profile saved with save_profile, the daily profile and put it on the card now: it replaces the " +
        "daily profile (the old one goes into the tuning log; save_profile it first if the person may want it back) and goes on " +
        "at every start from then on. A preset only loads on the card model it was tuned on, and another card of that model " +
        "is still other silicon: after loading one, run stress_test before trusting it. Show preview_daily with the name " +
        "first. " + AsksItself)]
    public async Task<string> LoadPreset(McpServer server, [Description("The preset's or saved profile's name, from list_presets.")] string name,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default)
    {
        // The preview refuses an unknown name and another model's preset: then nobody is asked about a load that cannot happen.
        var preview = card.Broker(BrokerRequest.OpProfile, "preview", name);
        if (!preview.Ok) return Reply(preview);
        return await Asked(server, $"Load '{name}' as your daily profile and put it on your graphics card now ({Changes(preview)}); Clocktail puts it back after every start.",
            confirm, cancellationToken, () => Applied(card.Broker(BrokerRequest.OpProfile, "preset", name), null));
    }

    [McpServerTool(Name = "install_service", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Install (or update) the Clocktail service, the one elevated process that does every write and puts the daily " +
        "profile back after each boot and driver reset. Every other write tool needs it. Windows asks the person to " +
        "approve once (a UAC prompt). Tell the person before calling this.")]
    public string InstallService() => Guard(() =>
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Clocktail.Broker.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"the service is not beside this server ({exe}): reinstall Clocktail");
        var psi = new System.Diagnostics.ProcessStartInfo(exe, "install") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("the installer did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var ping = card.Broker(BrokerRequest.OpPing);
        return Out(new { success = p.ExitCode == 0 && ping.Ok, exit_code = p.ExitCode, output = (stdout.Result + stderr.Result).Trim(), service_answers = ping.Ok });
    });

    [McpServerTool(Name = "log_note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Write into the tuning log in your own words: what the person said, what you saw (fps, stutter, scene, settings), " +
        "what you plan next and why. The more detail the better; every write the tools make is logged by themselves.")]
    public string LogNote([Description("The note (Markdown is fine).")] string text) =>
        Reply(card.Broker(BrokerRequest.OpExperiment, "note", text));

    [McpServerTool(Name = "read_log", ReadOnly = true, OpenWorld = false), Description(
        "The last entries of the tuning log (oldest first): every trial with its readings and each point before and after, " +
        "every crash, every profile kept or put back, and the notes. Read it at the start of a session to pick up where the " +
        "last one left off.")]
    public string ReadLog([Description("How many entries (default 15).")] int entries = 15) => Guard(() =>
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        if (!r.Ok || r.Result is not { } res) return Reply(r);
        var path = TuningLog.PathBeside(res.GetProperty("path").GetString()!);
        var tail = TuningLog.Tail(path, Math.Max(1, entries));
        return tail.Length == 0 ? Out(new { success = true, path, log = "(nothing logged yet)" }) : $"<!-- {path} -->\n{tail}";
    });

    [McpServerTool(Name = "end_session", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "End the session: the trial in force ends as survived and the daily profile goes back on (stock when none is kept).")]
    public string EndSession()
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "end");
        // "end" wraps the profile's answer (or a recovery's) in "back", beside the trial's data point.
        var back = r.Result is { } res && res.TryGetProperty("back", out var b) && b.ValueKind == JsonValueKind.Object ? b : (JsonElement?)null;
        var point = r.Result is { } res2 && res2.TryGetProperty("point", out var p) ? p : (JsonElement?)null;
        return Applied(r with { Result = back }, point);
    }

    [McpServerTool(Name = "data_points", ReadOnly = true, OpenWorld = false), Description(
        "The last data points across sessions and games, oldest first: game, outcome (stable / survived / crash), minutes, " +
        "the change, the operating point when it was written, and for a crash the voltage the flight recorder found at the hang. " +
        "by_point sums every data point up per curve point, from the points the card actually ran on under load (at least " +
        "10% of the trial; for a crash, of its last 30 s): the highest offset proved stable (and for how many seconds), the " +
        "highest that survived, and the lowest that crashed; and the clock it actually ran there (P90). Under a wall a point " +
        "runs anywhere between the point below's frequency and its own, so stable_clock_mhz against curve_mhz_now says how " +
        "much of the point was proved: a point is proved only when the card ran at (or near) its curve frequency. Offsets " +
        "are absolute, as in curve_around. Read it before the next step.")]
    public string DataPoints() => Guard(() =>
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        if (!r.Ok || r.Result is not { } res) return Reply(r);
        return Out(new { data = res.GetProperty("data"), points = res.GetProperty("points"), by_point = ByPoint(res.GetProperty("data").GetString()!) });
    });

    /// <summary>One curve point's record across every data point the residency samples cover.</summary>
    private sealed class PointRecord
    {
        public int? StableKhz, SurvivedKhz, CrashKhz;
        public uint? StableMhz, CrashMhz;
        public double StableSeconds;
        public int Crashes;
    }

    private object ByPoint(string dataPath)
    {
        var all = Experiment.ReadLast(dataPath, int.MaxValue);
        if (all.Count == 0) return Array.Empty<object>();
        var look = card.Read();
        var daily = DailyOffsets(out _);
        var ticks = residency.Between(all.Min(d => d.At - TimeSpan.FromMinutes(d.Minutes)), DateTimeOffset.Now);
        var records = new SortedDictionary<int, PointRecord>();
        foreach (var d in all)
        {
            var start = d.At - TimeSpan.FromMinutes(d.Minutes);
            var during = ticks.Where(t => t.At >= start && t.At <= d.At).ToList();
            if (during.Count < 10) continue;   // before the sampler, or too short to say anything
            int[] offsets;
            try { offsets = VfOffsets.Decode(d.Curve); } catch (FormatException) { continue; }
            // A crash belongs to where the card was when it fell over, not to the whole trial.
            if (d.Outcome == Experiment.Crash)
            {
                foreach (var (index, clock) in CrashAt(during, d.CrashUv, look.Curve))
                {
                    int off = offsets.ElementAtOrDefault(index);
                    if (!records.TryGetValue(index, out var rec)) records[index] = rec = new PointRecord();
                    rec.Crashes++;
                    if (rec.CrashKhz is not { } kc || off < kc) rec.CrashKhz = off;
                    if (clock is { } c && (rec.CrashMhz is not { } cm || c < cm)) rec.CrashMhz = c;
                }
                continue;
            }
            foreach (var s in Stays(during, look.Curve).Where(s => s.Share >= 0.1))
            {
                int off = offsets.ElementAtOrDefault(s.Index);
                if (!records.TryGetValue(s.Index, out var rec)) records[s.Index] = rec = new PointRecord();
                switch (d.Outcome)
                {
                    case Experiment.Stable:
                        if (rec.StableMhz is not { } sm || s.ClockMhz > sm) rec.StableMhz = s.ClockMhz;
                        if (rec.StableKhz is not { } k || off >= k)
                        {
                            rec.StableSeconds = rec.StableKhz == off ? rec.StableSeconds + s.Samples : s.Samples;
                            rec.StableKhz = off;
                        }
                        break;
                    case Experiment.Survived when rec.SurvivedKhz is not { } ks || off > ks:
                        rec.SurvivedKhz = off;
                        break;
                }
            }
        }
        return records.Select(kv =>
        {
            var p = look.Curve.Points.FirstOrDefault(p => p.Index == kv.Key);
            var v = kv.Value;
            return new
            {
                index = kv.Key,
                mv = p?.VoltageUv / 1000.0,
                curve_mhz_now = p?.EffectiveKhz / 1000.0,
                offset_now_mhz = p?.OffsetKhz / 1000.0,
                daily_offset_mhz = daily.ElementAtOrDefault(kv.Key) / 1000.0,
                stable_up_to_mhz = v.StableKhz / 1000.0,
                stable_seconds = v.StableKhz == null ? (double?)null : v.StableSeconds,
                stable_clock_mhz = v.StableMhz,
                survived_up_to_mhz = v.SurvivedKhz / 1000.0,
                crashed_at_mhz = v.CrashKhz / 1000.0,
                crash_clock_mhz = v.CrashMhz,
                crashes = v.Crashes,
            };
        }).ToList();
    }

    /// <summary>
    /// The curve points a crash data point fell over at, with the clock the card ran there (null when no sample sat on it).
    /// A hung GPU keeps answering with frozen readings until the reset (for the whole TdrDelay, 30 s on the reference
    /// machine: 3105 MHz at 1030 mV, util 100 %, ~140 W), so the trial's samples are cut at the frozen tail first, by the
    /// flight recorder's rule, and the last 30 s before it are what counts. When the flight recorder found the crash
    /// voltage, the crash is the point nearest it alone.
    /// </summary>
    public static IReadOnlyList<(int Index, uint? ClockMhz)> CrashAt(IReadOnlyList<Residency.Tick> during, uint? crashUv, VfCurveSnapshot curve)
    {
        if (curve.Points.Count == 0 || during.Count == 0) return [];
        // A tail that is the whole trial is a load pinned at the curve's cap, not a hang: only one power fell into is cut.
        var frozen = FlightRecorder.FrozenFrom([.. during.Select(t => new FlightSample(t.At, 0, t.Mhz, t.Uv / 1000, t.W, null, t.Util, null))]);
        IReadOnlyList<Residency.Tick> live = frozen == null || frozen.At == during[0].At ? during : [.. during.Where(t => t.At < frozen.At)];
        List<Residency.Tick> last = live.Count == 0 ? [] : [.. live.Where(t => t.At >= live[^1].At - TimeSpan.FromSeconds(30))];
        var stays = Stays(last, curve);
        if (crashUv is { } uv && Nearest(curve, uv) is { } p)
            return [(p.Index, stays.FirstOrDefault(s => s.Index == p.Index)?.ClockMhz)];
        return [.. stays.Where(s => s.Share >= 0.1).Select(s => (s.Index, (uint?)s.ClockMhz))];
    }

    /// <summary>The samples before a frozen tail, by the flight recorder's rule (same clock, voltage within 5 mV, power fallen
    /// into it); all of them when there is none, or when the tail is the whole run (a pinned load, not a hang).</summary>
    public static IReadOnlyList<Card.Sample> WithoutFrozenTail(IReadOnlyList<Card.Sample> samples)
    {
        if (samples.Count == 0) return samples;
        var frozen = FlightRecorder.FrozenFrom([.. samples.Select(s => new FlightSample(s.At, 0, s.Telemetry?.GraphicsClockMhz,
            s.NvvddUv / 1000, s.Telemetry?.PowerWatts, s.Telemetry?.TemperatureC, s.Telemetry?.GpuUtilPercent, null))]);
        return frozen == null || frozen.At == samples[0].At ? samples : [.. samples.Where(s => s.At < frozen.At)];
    }

    /// <summary>A curve point a run of samples sat on, with the clock it ran there (P90 of those samples).</summary>
    private sealed record Stay(int Index, double Mv, double Share, int Samples, uint ClockMhz);

    /// <summary>The curve points a run of samples sat on (nearest by voltage), most first.</summary>
    private static List<Stay> Stays(IReadOnlyList<Residency.Tick> ticks, VfCurveSnapshot curve)
    {
        if (ticks.Count == 0 || curve.Points.Count == 0) return [];
        return ticks.GroupBy(t => Nearest(curve, t.Uv)!)
            .Select(g =>
            {
                var clocks = g.Select(t => t.Mhz).Order().ToList();
                return new Stay(g.Key.Index, g.Key.VoltageUv / 1000.0, Math.Round(g.Count() / (double)ticks.Count, 2), g.Count(),
                    clocks[(int)Math.Round(0.9 * (clocks.Count - 1))]);
            })
            .OrderByDescending(s => s.Samples).ToList();
    }

    private static string Describe(IReadOnlyList<Stay> stays) => string.Join(", ", stays.Where(s => s.Share >= 0.05).Select(s =>
        string.Create(CultureInfo.InvariantCulture, $"point {s.Index} ({s.Mv} mV) {s.Share * 100:0}%, P90 {s.ClockMhz} MHz")));

    /// <summary>The trial in force's residency, for its data point's note: where the card ran under load since the trial
    /// was written, and (in <paramref name="last"/>) over its last 30 s. Empty without a trial.</summary>
    private string TrialStays(out string last)
    {
        last = "";
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        if (!r.Ok || r.Result is not { } res || !res.TryGetProperty("experiment", out var e) || e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("Trial", out var tr) || tr.ValueKind != JsonValueKind.Object || !tr.TryGetProperty("At", out var at))
            return "";
        var ticks = residency.Between(at.GetDateTimeOffset(), DateTimeOffset.Now);
        if (ticks.Count == 0) return $"\n\nUnder load: no loaded samples (utilization stayed below {Residency.MinUtilPct}%).";
        var curve = card.Read().Curve;
        var end = ticks.Where(t => t.At >= ticks[^1].At - TimeSpan.FromSeconds(30)).ToList();
        last = $"\n\nLast 30 s: {Describe(Stays(end, curve))}";
        return $"\n\nUnder load ({ticks.Count} s): {Describe(Stays(ticks, curve))}";
    }

    /// <summary>Each rail's output from the power monitor and its current limit, for the agent to pick a limit from, and
    /// MSVDD's offsets and evaluated REL/MAX/VMIN.</summary>
    private static object Rails(Card.Look look)
    {
        object Rail(Clocktail.Domain.Rails.Rail r, MonitorReading? o)
        {
            var l = look.Limits?.Get(r);
            return new
            {
                mv = o?.Uv / 1000.0, a = o?.Ma / 1000.0, w = o?.Mw / 1000.0,
                limit_a = l?.LimitMa / 1000.0, default_a = l?.DefaultMa / 1000.0,
            };
        }
        var m = look.Msvdd;
        return new
        {
            nvvdd = Rail(Clocktail.Domain.Rails.Rail.Nvvdd, look.NvvddOut),
            msvdd = Rail(Clocktail.Domain.Rails.Rail.Msvdd, look.MsvddOut),
            msvdd_offsets = m == null ? null : new
            {
                rel_offset_mv = m.Offsets.RelUv / 1000.0, vmin_offset_mv = m.Offsets.VminUv / 1000.0,
                rel_mv = m.Limits.RelUv / 1000.0, max_mv = m.Limits.MaxUv / 1000.0, vmin_mv = m.Limits.VminUv / 1000.0,
            },
        };
    }

    /// <summary>A stress run's samples, one row each, beside the tuning log under stress\ (the MCP server writes these; the
    /// tuning log itself is the broker's alone). Returns the path, or the reason it could not be written.</summary>
    private string WriteSamples(IReadOnlyList<Card.Sample> samples, int load, int seconds)
    {
        try
        {
            var r = card.Broker(BrokerRequest.OpExperiment, "show");
            string dir = r.Ok && r.Result is { } res && res.TryGetProperty("path", out var p) && p.GetString() is { } exp
                ? Path.Combine(Path.GetDirectoryName(exp)!, "stress")
                : Path.Combine(Path.GetTempPath(), "clocktail-stress");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-l{load}-{seconds}s.csv");
            var sb = new System.Text.StringBuilder("at,clock_mhz,xbar_mhz,sys_mhz,memory_mhz,video_mhz,nvvdd_mv,msvdd_mv,board_w,limit_w,nvvdd_w,nvvdd_a,msvdd_w,msvdd_a,temp_c,util_pct,reasons,instant_w,vram_used_gib,temp_margin_c,gpc_mean_mv,gpc_min_mv,memory_c\n");
            foreach (var s in samples)
            {
                var t = s.Telemetry;
                sb.Append(string.Join(",", new object?[]
                {
                    s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), t?.GraphicsClockMhz, s.Mhz(AllClocksLayout.Xbar), s.Mhz(AllClocksLayout.Sys),
                    s.Mhz(AllClocksLayout.Memory), s.Mhz(AllClocksLayout.Video), s.NvvddUv / 1000.0, s.MsvddUv / 1000.0, t?.PowerWatts, t?.PowerEnforcedWatts,
                    s.NvvddOut?.Mw / 1000.0, s.NvvddOut?.Ma / 1000.0, s.MsvddOut?.Mw / 1000.0, s.MsvddOut?.Ma / 1000.0, t?.TemperatureC, t?.GpuUtilPercent,
                    string.Join("|", t?.ThrottleReasons ?? []), t?.PowerInstantWatts, Gib(t?.VramUsedBytes), t?.TempMarginC,
                    s.Gpc is { } g ? Math.Round(g.MeanMv, 1) : null, s.Gpc?.MinMv, s.MemoryC,
                }.Select(v => v switch { null => "", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() })));
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"(not written: {e.Message})"; }
    }

    /// <summary>The card after a write that landed: a read that cannot get to the driver must not turn the write's success
    /// into a failure. Null, with the reason in <paramref name="unread"/>, when it cannot be read.</summary>
    private Card.Look? ReadAfter(out string? unread)
    {
        unread = null;
        try { return card.Read(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or Clocktail.Hw.NvApiException or Clocktail.Hw.NvmlException)
        {
            unread = $"written, but the card could not be read back: {e.Message}";
            return null;
        }
    }

    /// <summary>One line of everything a trial was written under, for the tuning log.</summary>
    private static string Readings(Card.Look look)
    {
        var t = look.Telemetry;
        string Out(MonitorReading? o, string name) => o == null ? "" : string.Create(CultureInfo.InvariantCulture, $", {name} {o.Ma / 1000.0:0.#} A / {o.Mw / 1000.0:0.#} W");
        string Lim(Clocktail.Domain.Rails.Rail r, string name) => look.Limits?.Get(r) is { } l ? string.Create(CultureInfo.InvariantCulture, $", {name} current limit {l.LimitMa / 1000.0:0.#} A") : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"Readings: NVVDD {look.NvvddUv / 1000.0} mV, MSVDD {look.MsvddUv / 1000.0} mV (MAX {look.NvvddMaxUv / 1000.0}), {t?.GraphicsClockMhz} MHz, memory {t?.MemoryClockMhz} MHz, {t?.PowerWatts:0.#} W / limit {t?.PowerEnforcedWatts:0.#} W") +
            Out(look.NvvddOut, "NVVDD") + Out(look.MsvddOut, "MSVDD") + Lim(Clocktail.Domain.Rails.Rail.Nvvdd, "NVVDD") + Lim(Clocktail.Domain.Rails.Rail.Msvdd, "MSVDD") +
            string.Create(CultureInfo.InvariantCulture, $", {t?.TemperatureC} °C, utilization {t?.GpuUtilPercent} %, limit reasons [{string.Join(", ", t?.ThrottleReasons ?? [])}]");
    }

    /// <summary>The daily profile's Core offsets (zeros when it keeps no curve, or none is kept).</summary>
    private int[] DailyOffsets(out string note)
    {
        var r = card.Broker(BrokerRequest.OpProfile, "show");
        if (!r.Ok || r.Result is not { } res) { note = r.Error ?? "the daily profile could not be read"; return []; }
        if (res.GetProperty("profile") is not { ValueKind: JsonValueKind.Object } p) { note = "no daily profile is kept: offsets compare against stock"; return []; }
        foreach (var w in p.GetProperty("Writes").EnumerateArray())
            if (w.GetProperty("Kind").GetString() == VfWrites.Kind)
            {
                note = p.GetProperty("Suspended").GetBoolean() ? "the daily profile is suspended (stock on the card)" : "daily profile on";
                return VfOffsets.Decode(w.GetProperty("Value").GetString()!);
            }
        note = "the daily profile keeps no curve: offsets compare against stock";
        return [];
    }

    /// <summary>Bytes as GiB to 0.1 (what NVML's 32 GB card calls 31.8).</summary>
    internal static double? Gib(ulong? bytes) => bytes is { } b ? Math.Round(b / 1073741824.0, 1) : null;

    private static VfPoint? Nearest(VfCurveSnapshot c, uint uv) =>
        c.Points.Count == 0 ? null : c.Points.MinBy(p => Math.Abs((long)p.VoltageUv - uv));

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

    /// <summary>A profile apply (or a recovery) in one line per write: the full answer carries the whole profile and each
    /// transaction's log, which an agent does not need and would spend its context on.</summary>
    public static string Applied(BrokerAnswer r, JsonElement? point)
    {
        var lines = new List<string>();
        var notBack = new List<string>();
        if (r.Result is { } res)
            foreach (var name in new[] { "applied", "items" })
                if (res.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var w in arr.EnumerateArray())
                    {
                        string cell = $"{Str(w, "kind") ?? Str(w, "Kind")}/{Str(w, "key") ?? Str(w, "Key")}", outcome = Str(w, "outcome") ?? Str(w, "action") ?? "?";
                        // A cell that could not go back says why: an F8 request made by the CLI, a journal from another
                        // tool, a cell this version cannot read. Without it the agent saw only "Refused".
                        string? why = outcome is "Refused" or "Failed" or "Aborted" ? Str(w, "detail") ?? Str(w, "Detail") : null;
                        lines.Add(why != null ? $"{cell}: {outcome} ({why})" : $"{cell}: {outcome}");
                        if (why != null) notBack.Add($"{cell}: {why}");
                    }
        // "end" and "crash" carry the profile's answer in "back": what that wrote is what the card went through.
        string? written = WriteOutcome.Of(r.Ok, r.Code, r.Result) ?? r.Written;
        string? error = !r.Ok && notBack.Count > 0 && (r.Error is null or "failed") ? $"not everything went back: {string.Join("; ", notBack)}" : r.Error;
        // Nothing written on purpose (a daily profile kept on another VBIOS, a trial in force) says why.
        string? skipped = r.Result is { } sr ? Str(sr, "skipped") : null;
        // A daily profile that went on without a group of it (a cell the driver refused or dropped took the cells tuned with
        // it along): which group did not apply and its final readback, for the person.
        var notApplied = r.Result is { } nr && nr.TryGetProperty("not_applied", out var na) && na.ValueKind == JsonValueKind.Array ? na : (JsonElement?)null;
        string? next = written == WriteOutcome.Unknown ? WriteOutcome.ReadFirst
            : notApplied != null ? "tell the person which part of the daily profile did not apply, why, and its final readback (not_applied); report any unknown readback as unknown" : null;
        return Out(new { success = r.Ok, error, code = r.Ok ? null : r.Code, written, next, skipped, not_applied = notApplied, data_point = point, writes = lines });
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Reply(BrokerAnswer r) =>
        Out(new { success = r.Ok, error = r.Error, code = r.Ok ? null : r.Code, written = r.Written, next = r.Written == WriteOutcome.Unknown ? WriteOutcome.ReadFirst : null, result = r.Result });

    private static string Out(object o) => JsonText.Serialize(o, Json);

    /// <summary>A refusal or an unreadable card as the tool's answer, with the reason: the SDK would otherwise hide the
    /// message, and the agent needs it to know what to change.</summary>
    private static string Guard(Func<string> tool)
    {
        try { return tool(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or FormatException
                                      or Clocktail.Hw.NvApiException or Clocktail.Hw.NvmlException or IOException or UnauthorizedAccessException)
        {
            return Out(new { success = false, error = e.Message });
        }
    }

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}
