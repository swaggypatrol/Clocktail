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
    /// <summary>The largest change one adjust_curve call may make to a point (CLOCKTAIL_MCP_STEP_MHZ).</summary>
    public static readonly int StepMhz = Env("CLOCKTAIL_MCP_STEP_MHZ", 15);
    /// <summary>How far above the daily profile any point may go (CLOCKTAIL_MCP_MAX_ABOVE_DAILY_MHZ). The 2026-10-18
    /// positive control hung at +105 on 900-1000 mV under 598 W; below 860 mV the daily curve is barely tuned (stock
    /// under 800 mV), so the cap leaves room there (the user, 2026-10-19).</summary>
    public static readonly int MaxAboveDailyMhz = Env("CLOCKTAIL_MCP_MAX_ABOVE_DAILY_MHZ", 200);
    /// <summary>The highest wall set_power_limit may set (CLOCKTAIL_MCP_MAX_POWER_W): the daily profile's 600 W.</summary>
    public static readonly int MaxPowerW = Env("CLOCKTAIL_MCP_MAX_POWER_W", 600);
    /// <summary>The lowest wall set_power_limit may set (CLOCKTAIL_MCP_MIN_POWER_W): the driver refuses anything under 400 W on this card.</summary>
    public static readonly int MinPowerW = Env("CLOCKTAIL_MCP_MIN_POWER_W", 400);
    /// <summary>The lowest NVVDD / MSVDD current limit set_current_limit may set (CLOCKTAIL_MCP_MIN_NVVDD_A /
    /// CLOCKTAIL_MCP_MIN_MSVDD_A); the driver's own range starts at 1 mA, so this floor is the tool's.</summary>
    public static readonly int MinNvvddA = Env("CLOCKTAIL_MCP_MIN_NVVDD_A", 100);
    public static readonly int MinMsvddA = Env("CLOCKTAIL_MCP_MIN_MSVDD_A", 90);
    /// <summary>adjust_msvdd: the largest step per offset down (an undervolt can fail) and up, and how far below stock REL
    /// and VMIN may go; how high REL may go (an absolute offset: 0 is NVIDIA's own reliability limit, +20 was measured,
    /// 2026-10-20) and how far above stock VMIN may go (+50 was measured).</summary>
    public static readonly int MsvddStepMv = Env("CLOCKTAIL_MCP_MSVDD_STEP_MV", 10);
    public static readonly int MsvddRaiseStepMv = Env("CLOCKTAIL_MCP_MSVDD_RAISE_STEP_MV", 25);
    public static readonly int MsvddRelMaxMv = Env("CLOCKTAIL_MCP_MSVDD_REL_MAX_MV", 20);
    public static readonly int MsvddVminAboveMv = Env("CLOCKTAIL_MCP_MSVDD_VMIN_ABOVE_MV", 50);
    public static readonly int MsvddMaxBelowMv = Env("CLOCKTAIL_MCP_MSVDD_MAX_BELOW_MV", 250);
    public static readonly int MsvddVminBelowMv = Env("CLOCKTAIL_MCP_MSVDD_VMIN_BELOW_MV", 100);
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
    /// <summary>The lowest voltage adjust_curve touches (CLOCKTAIL_MCP_MIN_MV): points under it keep the daily profile's
    /// offset. The user, 2026-10-19: below 850 mV the card is as good as idle, so tuning starts there; and a window that
    /// stops at the floor keeps a walled card from sliding under it, so the floor point is tested at its own voltage.</summary>
    public static readonly int MinMv = Env("CLOCKTAIL_MCP_MIN_MV", 850);
    /// <summary>The operating point is the median NVVDD under load over this long: a game's voltage moves with the scene.</summary>
    public static readonly TimeSpan OperatingSpan = TimeSpan.FromSeconds(3);
    /// <summary>adjust_curve on an idle card: the last load within this long (a stress run that just ended), its last 10 s.</summary>
    public static readonly TimeSpan LastLoadWithin = TimeSpan.FromMinutes(2), LastLoadSpan = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool(Name = "gpu_status", ReadOnly = true, OpenWorld = false), Description(
        "Where the card runs right now: NVVDD/MSVDD, core clock, the V/F point it sits on (stock, current and daily offset), " +
        "power, temperature, utilization, NVML limit reasons, and the experiment session. NVML on this card sets gpu_idle at " +
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
            vram_used_gib = Gib(t?.VramUsedBytes),
            vram_total_gib = Gib(t?.VramTotalBytes),
            memory_controller_pct = t?.MemUtilPercent,
            limit_reasons = t?.ThrottleReasons,
            rails = Rails(look),
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
        "Start an experiment session for a game. Every adjust_curve after this is a trial; its outcome becomes a data point " +
        "(stable, survived until replaced, or crash). A crash during a trial does not step the daily profile down. A trial " +
        "still in force from an earlier session ends first and the daily profile goes back on, as end_session does. " +
        "unattended=true when the person is away (an overnight stress mapping): after maxCrashes crashes (default 2) no " +
        "new trial is written until a new session, so the card is not hung over and over with nobody there; the daily " +
        "profile stays on and nothing steps down.")]
    public string BeginSession(
        [Description("The game being played.")] string game,
        [Description("Anything worth keeping with the data (settings, scene).")] string? note = null,
        [Description("The person is away: stop writing new trials after maxCrashes crashes.")] bool unattended = false,
        [Description("With unattended: the crashes allowed before trials stop, 1..10 (default 2).")] int maxCrashes = 2) =>
        maxCrashes is < 1 or > 10 ? Out(new { success = false, error = "maxCrashes is 1..10" })
        : Reply(card.Broker(BrokerRequest.OpExperiment, "begin", JsonSerializer.Serialize(new { game, note, unattended, max_crashes = maxCrashes })));

    [McpServerTool(Name = "adjust_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Raise or lower the Core V/F points around where the card runs now, as a new trial of the session. Each call moves " +
        "each point by at most the step (default 15 MHz) and never above the daily profile by more than the cap (default " +
        "200 MHz); the firmware's own limits are held by the broker. The operating point is the median NVVDD under load " +
        "over the last 3 s (or the last 10 s of a load that ended within 2 minutes, such as a stress run; an idle card needs " +
        "fromMv/toMv); the default window is 20 mV below it to 10 mV above, and any point above the window that the " +
        "raise would overtake is lifted just enough to keep the curve rising. Points under 850 mV (CLOCKTAIL_MCP_MIN_MV) are " +
        "never touched. Under a power wall a raise shows as the same " +
        "clock at a lower voltage: the points the card then runs on are the ones tested (mark_stable records them). " +
        "The curve lands on a 7.5 MHz grid. Writing under full " +
        "load is how 2 of 3 hangs on 2026-10-18 began: prefer one small step, then several minutes of play before the next, " +
        "and call mark_stable or back_to_daily before moving on.")]
    public string AdjustCurve(
        [Description("MHz to add to each point in the window (negative lowers).")] double deltaMhz,
        [Description("Lowest voltage of the window, mV; omit for the operating point minus 20 mV.")] double? fromMv = null,
        [Description("Highest voltage of the window, mV; omit for the operating point plus 10 mV.")] double? toMv = null) => Guard(() =>
    {
        if (deltaMhz == 0) throw new ArgumentException("deltaMhz is 0: nothing to try");
        if (Math.Abs(deltaMhz) > StepMhz) throw new ArgumentException($"{deltaMhz} MHz is more than one step ({StepMhz} MHz); split it into several trials");
        var look = card.Read();
        var daily = DailyOffsets(out _);
        // Where the card ran under load: the last 3 s, else the last load within 2 minutes (a stress run that just ended). An
        // idle card's NVVDD (~800-835 mV) is not an operating point: the window would sit under the 850 mV floor.
        uint? at = residency.MedianUv(OperatingSpan) ?? residency.LastLoadMedianUv(LastLoadWithin, LastLoadSpan);
        const string Idle = "the card has not been under load in the last 2 minutes, so there is no operating point: give fromMv and toMv (e.g. around the last stress run's p50 nvvdd_mv, or the crash voltage in data_points)";
        uint lo = fromMv is { } f ? (uint)(f * 1000) : (at ?? throw new InvalidOperationException(Idle)) - WindowBelowMv * 1000;
        uint hi = toMv is { } h ? (uint)(h * 1000) : (at ?? throw new InvalidOperationException(Idle)) + WindowAboveMv * 1000;
        var pts = look.Curve.Points;
        if (hi < MinMv * 1000) throw new ArgumentException($"the window ({lo / 1000.0}-{hi / 1000.0} mV) is all under the {MinMv} mV floor (CLOCKTAIL_MCP_MIN_MV): points there keep the daily offsets");
        lo = Math.Max(lo, (uint)MinMv * 1000);
        var window = pts.Where(p => p.VoltageUv >= lo && p.VoltageUv <= hi).ToList();
        if (window.Count == 0) throw new ArgumentException($"no curve point between {lo / 1000.0} and {hi / 1000.0} mV");

        int delta = (int)Math.Round(deltaMhz * 1000);
        var next = look.Curve.Offsets();
        foreach (var p in window)
        {
            next[p.Index] += delta;
            int above = next[p.Index] - daily.ElementAtOrDefault(p.Index);
            if (above > MaxAboveDailyMhz * 1000)
                throw new ArgumentException($"point {p.Index} ({p.VoltageUv / 1000.0} mV) would be {above / 1000.0} MHz above the daily profile; the cap is {MaxAboveDailyMhz} MHz");
        }
        // A point raised past the one above it would cap the whole curve there: lift each point above the window that
        // would be overtaken just enough to match (onto the 7.5 MHz grid). That runs a clock already being tested at a
        // higher voltage, so it adds no risk; the cap still holds.
        int top = window[^1].Index, lifted = 0;
        for (int k = top + 1; delta > 0 && k < pts.Count; k++)
        {
            long need = (long)pts[k - 1].BaseKhz + next[k - 1] - pts[k].BaseKhz;
            if (need <= next[k]) break;
            next[k] = (int)(Math.Ceiling(need / 7500.0) * 7500);
            int above = next[k] - daily.ElementAtOrDefault(k);
            if (above > MaxAboveDailyMhz * 1000)
                throw new ArgumentException($"point {k} ({pts[k].VoltageUv / 1000.0} mV) would have to be lifted to {above / 1000.0} MHz above the daily profile to stay under point {k - 1}; the cap is {MaxAboveDailyMhz} MHz");
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

    /// <summary>flatten_curve refuses to write above this GPU utilization: a curve written under load began 2 of 3 hangs.</summary>
    public const uint FlattenMaxUtilPct = 15;

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
        "under 850 mV are never touched. Refused while the GPU is busy (over 15 % utilization): write it at idle. back_to_daily " +
        "undoes it; keep_as_daily keeps it.")]
    public string FlattenCurve(
        [Description("Voltage the flat top starts at, mV.")] double fromMv,
        [Description("MHz under that point's clock to flatten to (default 0).")] double marginMhz = 0,
        [Description("Or flatten to this clock, MHz, instead of the point's.")] double? ceilingMhz = null,
        [Description("Why (where the chip stopped scaling).")] string? reason = null) => Guard(() =>
    {
        if (fromMv < MinMv) throw new ArgumentException($"fromMv is at least {MinMv} mV (CLOCKTAIL_MCP_MIN_MV)");
        if (marginMhz < 0) throw new ArgumentException("marginMhz lowers the top: give it as a positive number");
        var look = card.Read();
        if (look.Telemetry?.GpuUtilPercent is { } u && u > FlattenMaxUtilPct)
            throw new InvalidOperationException($"the GPU is {u} % busy: a curve written under load began 2 of 3 hangs on the reference card. Flatten at idle");
        var pts = look.Curve.Points;
        uint fromUv = (uint)Math.Round(fromMv * 1000);
        var at = pts.Where(p => p.VoltageUv >= fromUv).MinBy(p => p.VoltageUv) ?? throw new ArgumentException($"no curve point at or above {fromMv} mV");
        long target = ceilingMhz is { } c ? (long)Math.Round(c * 1000) : at.EffectiveKhz - (long)Math.Round(marginMhz * 1000);
        var before = look.Curve.Offsets();
        var next = FlattenPlan(pts, before, fromUv, target, (uint)MinMv * 1000);
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
            warning = unread,
        });
    });

    [McpServerTool(Name = "set_power_limit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the board power limit for the session, as a new trial with the curve left as it is. A lower wall makes the card " +
        "run lower down the curve, so adjust_curve can then sample points it never reaches at the full wall (and find where " +
        "the curve's slope bends). Later adjust_curve trials run under this wall until back_to_daily, end_session or a crash " +
        "puts the daily profile's wall back. This wall is never kept (keep_as_daily puts the daily wall back on); " +
        "set_daily_power_limit changes the daily one. Allowed: 400-600 W (the driver refuses less on this card). For points lower down " +
        "than 400 W reaches, cap the frame rate in the game instead: a lighter load runs lower on the curve.")]
    public string SetPowerLimit(
        [Description("The board power limit, W.")] double watts,
        [Description("Why this wall (which part of the curve it is meant to reach).")] string? reason = null) => Guard(() =>
    {
        if (watts > MaxPowerW) throw new ArgumentException($"{watts} W is above the cap ({MaxPowerW} W)");
        if (watts < MinPowerW) throw new ArgumentException($"{watts} W is below {MinPowerW} W (CLOCKTAIL_MCP_MIN_POWER_W)");
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
        "pick it from. Never above the VBIOS default (NVVDD 600 A, MSVDD 180 A); 'default' as amps puts the default back. " +
        "Never kept: back_to_daily, end_session, keep_as_daily, a crash and the agent disconnecting all put the default back.")]
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
        int floor = r == Clocktail.Domain.Rails.Rail.Nvvdd ? MinNvvddA : MinMsvddA;
        if (ma < floor * 1000) throw new ArgumentException($"{ma / 1000.0} A is below {floor} A for {rail} (CLOCKTAIL_MCP_MIN_{rail.ToUpperInvariant()}_A)");
        if (ma > limit.DefaultMa) throw new ArgumentException($"{ma / 1000.0} A is above the {rail} default of {limit.DefaultMa / 1000.0} A: a current limit is only ever lowered");
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

    [McpServerTool(Name = "adjust_msvdd", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Raise or lower the MSVDD rail (L2, crossbar, SYS and Video) through its REL and VMIN offsets, as a new trial with the " +
        "curve left as it is. MSVDD runs at what the core's frequency asks for minus its REL offset (measured 1:1: REL -50 → " +
        "-75 mV moved MSVDD 975 → 950 mV), down to its VMIN floor (825 mV stock). Raising REL lets XBAR run faster where MSVDD " +
        "held it: stock REL is -50 mV on a 5090, and REL -50 → 0 (NVIDIA's own reliability limit) made XBAR ~90 MHz faster and " +
        "Blender ~2 % faster for ~2 W, the core giving 15-23 MHz back under the wall; REL may go to +20 mV (past NVIDIA's limit; " +
        "MAX 1115 mV), which bought ~1 % more. Lowering REL is a straight undervolt: ~10 W saved at -175 mV, which the wall gives " +
        "to the core, but XBAR ~300 MHz slower. VMIN raises or lowers the floor (+50 mV at most above stock; raising it does not " +
        "cure the XBAR cliff, the voltage demand does: adjust_xbar). Per call each offset moves at most 10 mV down or 25 mV up; " +
        "REL at most 250 mV and VMIN 100 mV below stock. Too low shows as artefacts, a crash or a driver reset, under load or " +
        "at idle (VMIN). gpu_status shows MSVDD's volts, amps and watts. back_to_daily, end_session and a crash put the daily " +
        "profile's offsets back (stock when it keeps none); keep_as_daily keeps the offsets on the card.")]
    public string AdjustMsvdd(
        [Description("mV to add to MSVDD's REL offset (negative lowers MSVDD).")] double relDeltaMv,
        [Description("mV to add to MSVDD's VMIN offset (negative lowers the floor); default 0.")] double vminDeltaMv = 0,
        [Description("Why this step (what you saw, what you expect).")] string? reason = null) => Guard(() =>
    {
        if (relDeltaMv == 0 && vminDeltaMv == 0) throw new ArgumentException("both deltas are 0: nothing to try");
        if (-relDeltaMv > MsvddStepMv || -vminDeltaMv > MsvddStepMv)
            throw new ArgumentException($"a step down is at most {MsvddStepMv} mV per offset (CLOCKTAIL_MCP_MSVDD_STEP_MV); split it into several trials");
        if (relDeltaMv > MsvddRaiseStepMv || vminDeltaMv > MsvddRaiseStepMv)
            throw new ArgumentException($"a step up is at most {MsvddRaiseStepMv} mV per offset (CLOCKTAIL_MCP_MSVDD_RAISE_STEP_MV); split it into several trials");
        var look = card.Read();
        var rail = look.Msvdd ?? throw new InvalidOperationException("the MSVDD rail is unreadable");
        var stock = StockMsvdd(rail.Offsets);
        var daily = DailyMsvdd();
        int relTop = MsvddRelMaxMv * 1000, vminTop = stock.VminUv + MsvddVminAboveMv * 1000;
        int rel = rail.Offsets.RelUv + (int)Math.Round(relDeltaMv * 1000), vmin = rail.Offsets.VminUv + (int)Math.Round(vminDeltaMv * 1000);
        if (rel > relTop && relDeltaMv > 0) throw new ArgumentException($"MSVDD's REL offset goes to +{MsvddRelMaxMv} mV at most (CLOCKTAIL_MCP_MSVDD_REL_MAX_MV; 0 is NVIDIA's own limit)");
        if (vmin > vminTop && vminDeltaMv > 0) throw new ArgumentException($"MSVDD's VMIN offset goes {MsvddVminAboveMv} mV above stock at most (CLOCKTAIL_MCP_MSVDD_VMIN_ABOVE_MV)");
        if (rel < stock.RelUv - MsvddMaxBelowMv * 1000) throw new ArgumentException($"REL {rel / 1000.0} mV would be more than {MsvddMaxBelowMv} mV below stock (CLOCKTAIL_MCP_MSVDD_MAX_BELOW_MV)");
        if (vmin < stock.VminUv - MsvddVminBelowMv * 1000) throw new ArgumentException($"VMIN {vmin / 1000.0} mV would be more than {MsvddVminBelowMv} mV below stock (CLOCKTAIL_MCP_MSVDD_VMIN_BELOW_MV)");
        var t = look.Telemetry;
        string change = string.Create(CultureInfo.InvariantCulture, $"MSVDD REL offset {rail.Offsets.RelUv / 1000.0} → {rel / 1000.0} mV")
            + (vmin != rail.Offsets.VminUv ? string.Create(CultureInfo.InvariantCulture, $", VMIN offset {rail.Offsets.VminUv / 1000.0} → {vmin / 1000.0} mV") : "");
        var context = new List<string>
        {
            Readings(look),
            string.Create(CultureInfo.InvariantCulture, $"MSVDD before the write: REL {rail.Limits.RelUv / 1000.0} mV, MAX {rail.Limits.MaxUv / 1000.0} mV, VMIN {rail.Limits.VminUv / 1000.0} mV; stock offsets REL {stock.RelUv / 1000.0}, VMIN {stock.VminUv / 1000.0} mV")
                + (daily is { } dm ? string.Create(CultureInfo.InvariantCulture, $"; daily offsets REL {dm.RelUv / 1000.0}, VMIN {dm.VminUv / 1000.0} mV") : ""),
        };
        if (reason != null) context.Add($"Purpose: {reason}");
        var args = new Dictionary<string, object?>
        {
            ["curve"] = VfOffsets.Encode(look.Curve.Offsets()), ["change"] = change, ["at_uv"] = look.NvvddUv, ["at_mhz"] = t?.GraphicsClockMhz,
            ["keep_curve"] = true, ["context"] = context,
        };
        if (rel != rail.Offsets.RelUv) args["msvdd_rel_uv"] = rel;
        if (vmin != rail.Offsets.VminUv) args["msvdd_vmin_uv"] = vmin;
        var res = card.Broker(BrokerRequest.OpExperiment, "try", JsonSerializer.Serialize(args));
        if (!res.Ok) return Reply(res);
        var after = ReadAfter(out var unread);
        return Out(new { success = true, change, written = res.Written, rails = after == null ? null : Rails(after), warning = unread });
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
        "daily profile goes back on. The result goes into the tuning log. Blocks for the run's length.")]
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
        var before = card.Read();
        var samples = new List<Card.Sample>();
        var started = DateTime.UtcNow;
        // A sample the driver does not answer in time is a missing one (a hang is what this run is looking for).
        residency.StressUntil(DateTimeOffset.Now.AddSeconds(seconds + 30));   // light load counts as load for this run
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
        try { code = Nvgwls.Run(exe, load, seconds, Sample, out tooHot); }
        finally { residency.StressUntil(DateTimeOffset.MinValue); }
        int? resets = tooHot ? Nvgwls.DriverResets(started, 0) : Nvgwls.DriverResets(started, code);
        double ranS = Math.Round((DateTime.UtcNow - started).TotalSeconds);
        string verdict = tooHot && resets is not > 0
            ? string.Create(CultureInfo.InvariantCulture, $"thermal stop at {stopC:0} °C after {ranS:0} s (maxTempC {maxC}): stopped by stress_test, not a pass or a crash")
            : Nvgwls.Verdict(code, resets);
        bool failed = tooHot ? resets > 0 : Nvgwls.Failed(code, resets);

        // The first 2 s are the ramp; the rest is what the settings were tested at.
        var held = samples.Count > 4 ? samples.Skip(4).ToList() : samples;
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

        bool trial = TrialInForce(card.Broker(BrokerRequest.OpExperiment, "show")) == true;
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Stress nvgwls -l {load}, {seconds} s: {verdict} (exit code {(code?.ToString(CultureInfo.InvariantCulture) ?? "none")}). P50 {p50Mhz} MHz @ NVVDD {p50Mv} mV (curve point {pointIndex}), XBAR {P(held.Select(s => s.Mhz(AllClocksLayout.Xbar)), 0.5)} / SYS {P(held.Select(s => s.Mhz(AllClocksLayout.Sys)), 0.5)} MHz, board {p50W} W, NVVDD {p50NvW} W, MSVDD {p50MsMv} mV / {p50MsW} W; max {held.Max(s => s.Telemetry?.TemperatureC)} °C; limit reasons [{string.Join(", ", reasons)}]; {held.Count} samples; per sample {csv}")
            + (note != null ? $". Purpose: {note}" : "");
        card.Broker(BrokerRequest.OpExperiment, "note", summary);
        bool crashRecorded = false;
        if (failed && trial)
        {
            // A driver reset is the service's to record: it gives the cells back and records the trial as the crash, with
            // the flight recorder's voltage, seconds after the event. Recording it here too wrote a second entry without the
            // voltage, and the service's then blamed the daily profile. Only when it has not within the wait, or the run
            // failed with no reset (a data error), is it recorded here.
            if (resets > 0 && TrialClosedWithin(() => TrialInForce(card.Broker(BrokerRequest.OpExperiment, "show")), ServiceRecordsWithin, Thread.Sleep))
                crashRecorded = true;
            else
                crashRecorded = card.Broker(BrokerRequest.OpExperiment, "crash", $"nvgwls -l {load} {seconds} s: {verdict}").Ok;
        }
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

    [McpServerTool(Name = "stock", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Put the card back to stock, as NVIDIA shipped it, and keep it there: every setting Clocktail ever wrote goes back to " +
        "the value it found, and the daily profile is suspended (kept, not deleted), so nothing puts it back at the next start " +
        "or driver reset until load_daily. The trial in force (if any) ends as survived. Tell the person before calling this.")]
    public string Stock()
    {
        var off = card.Broker(BrokerRequest.OpProfile, "off");
        return off.Ok ? Applied(card.Broker(BrokerRequest.OpRecover), null) : Applied(off, null);
    }

    [McpServerTool(Name = "load_daily", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "End a suspension (after stock) and put the kept daily profile on the card now; it goes on at every start and after " +
        "every driver reset again. Fails when no daily profile is kept (a fresh install: offer a preset instead).")]
    public string LoadDaily() => Applied(card.Broker(BrokerRequest.OpProfile, "load"), null);

    [McpServerTool(Name = "preview_daily", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "What the daily profile would become, before asking the person: with no name, what keep_as_daily would keep (every " +
        "cell on the card this trial and the ones before it touched, not only the last step), and what it would not keep and " +
        "why; with a preset's or saved profile's name, what load_preset would load. Each change is daily now → becomes, with " +
        "the card's value now. Writes nothing. Show it, then ask.")]
    public string PreviewDaily([Description("A preset's or saved profile's name; omit for keep_as_daily.")] string? name = null) =>
        Reply(card.Broker(BrokerRequest.OpProfile, "preview", name));

    [McpServerTool(Name = "keep_as_daily", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Make what the trials set on the card the new daily profile, permanently: it goes on at every start and after " +
        "every driver reset from then on. Show preview_daily first: it lists exactly what would be kept. Kept from the card: the V/F curve, the fan curve, the memory offset, MSVDD's offsets, " +
        "XBAR's offset and voltage demand. Not kept: the power limit (it stays the daily profile's; set_daily_power_limit " +
        "changes it), current limits and fixed fan duties; a trial's lowered wall, limit or fixed fans go back at once. " +
        "Everything a trial set is kept, not just the last change: call back_to_daily first if only the curve should be. " +
        "The curve kept is the one on the card: if the trial ran under a lowered wall or limit, the curve is what matters, " +
        "not the wall it was found under. The trial in force (if any) " +
        "is recorded as stable. The old and the new profile go into the tuning log. Ask the person before calling this.")]
    public string KeepAsDaily([Description("Why this becomes the daily profile (games and minutes it survived).")] string note)
    {
        var r = card.Broker(BrokerRequest.OpProfile, "keep", note);
        if (!r.Ok || r.Result is not { } res) return Reply(r);
        var writes = res.GetProperty("profile").GetProperty("Writes").EnumerateArray()
            .Select(w => $"{w.GetProperty("Kind").GetString()}/{w.GetProperty("Key").GetString()} = {w.GetProperty("Value").GetString()}");
        return Out(new { success = true, daily = writes });
    }

    [McpServerTool(Name = "list_presets", ReadOnly = true, OpenWorld = false), Description(
        "The daily profiles that ship with Clocktail, each with the card model it was tuned on and whether this card is that " +
        "model, and the profiles the person saved under a name (save_profile). A fresh install runs the card at stock; a " +
        "preset is a starting point the person may choose instead. load_preset loads either kind by name.")]
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
        "Delete a profile saved with save_profile. The daily profile on the card and the presets are not affected. Ask the person first.")]
    public string DeleteProfile([Description("The saved profile's name, from list_presets.")] string name) =>
        Guard(() => Reply(card.Broker(BrokerRequest.OpProfile, "forget", name)));

    [McpServerTool(Name = "load_preset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Make a preset, or a profile saved with save_profile, the daily profile and put it on the card now: it replaces the " +
        "daily profile (the old one goes into the tuning log; save_profile it first if the person may want it back) and goes on " +
        "at every start from then on. A preset only loads on the card model it was tuned on, and another card of that model " +
        "is still other silicon: after loading one, run stress_test before trusting it. Show preview_daily with the name " +
        "first, then ask the person before calling this.")]
    public string LoadPreset([Description("The preset's or saved profile's name, from list_presets.")] string name) =>
        Applied(card.Broker(BrokerRequest.OpProfile, "preset", name), null);

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
            var sb = new System.Text.StringBuilder("at,clock_mhz,xbar_mhz,sys_mhz,memory_mhz,video_mhz,nvvdd_mv,msvdd_mv,board_w,limit_w,nvvdd_w,nvvdd_a,msvdd_w,msvdd_a,temp_c,util_pct,reasons,instant_w,vram_used_gib,temp_margin_c,gpc_mean_mv,gpc_min_mv\n");
            foreach (var s in samples)
            {
                var t = s.Telemetry;
                sb.Append(string.Join(",", new object?[]
                {
                    s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), t?.GraphicsClockMhz, s.Mhz(AllClocksLayout.Xbar), s.Mhz(AllClocksLayout.Sys),
                    s.Mhz(AllClocksLayout.Memory), s.Mhz(AllClocksLayout.Video), s.NvvddUv / 1000.0, s.MsvddUv / 1000.0, t?.PowerWatts, t?.PowerEnforcedWatts,
                    s.NvvddOut?.Mw / 1000.0, s.NvvddOut?.Ma / 1000.0, s.MsvddOut?.Mw / 1000.0, s.MsvddOut?.Ma / 1000.0, t?.TemperatureC, t?.GpuUtilPercent,
                    string.Join("|", t?.ThrottleReasons ?? []), t?.PowerInstantWatts, Gib(t?.VramUsedBytes), t?.TempMarginC,
                    s.Gpc is { } g ? Math.Round(g.MeanMv, 1) : null, s.Gpc?.MinMv,
                }.Select(v => v switch { null => "", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() })));
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"(not written: {e.Message})"; }
    }

    /// <summary>MSVDD's stock offsets: the journal's original while the broker holds the rail (a trial, or a daily profile
    /// that keeps offsets), else what it holds now.</summary>
    private RailOffsets StockMsvdd(RailOffsets now)
    {
        var r = card.Broker(BrokerRequest.OpOwnership);
        if (r.Ok && r.Result is { } res && res.TryGetProperty("items", out var items))
            foreach (var i in items.EnumerateArray())
                if (Str(i, "Kind") == RailWrites.Kind && Str(i, "Key") == RailWrites.Key(Clocktail.Domain.Rails.Rail.Msvdd) && Str(i, "Original") is { } o)
                    return RailWrites.Decode(o);
        return now;
    }

    /// <summary>The MSVDD offsets the daily profile keeps; null when it keeps none, is suspended or cannot be read.</summary>
    private RailOffsets? DailyMsvdd()
    {
        var r = card.Broker(BrokerRequest.OpProfile, "show");
        if (!r.Ok || r.Result is not { } res || res.GetProperty("profile") is not { ValueKind: JsonValueKind.Object } p || p.GetProperty("Suspended").GetBoolean()) return null;
        foreach (var w in p.GetProperty("Writes").EnumerateArray())
            if (w.GetProperty("Kind").GetString() == RailWrites.Kind && w.GetProperty("Key").GetString() == RailWrites.Key(Clocktail.Domain.Rails.Rail.Msvdd))
                return RailWrites.Decode(w.GetProperty("Value").GetString()!);
        return null;
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
    private static string Applied(BrokerAnswer r, JsonElement? point)
    {
        var lines = new List<string>();
        if (r.Result is { } res)
            foreach (var name in new[] { "applied", "items" })
                if (res.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var w in arr.EnumerateArray())
                        lines.Add($"{Str(w, "kind") ?? Str(w, "Kind")}/{Str(w, "key") ?? Str(w, "Key")}: {Str(w, "outcome") ?? Str(w, "action")}");
        // "end" and "crash" carry the profile's answer in "back": what that wrote is what the card went through.
        string? written = WriteOutcome.Of(r.Ok, r.Code, r.Result) ?? r.Written;
        return Out(new { success = r.Ok, error = r.Error, code = r.Ok ? null : r.Code, written, next = written == WriteOutcome.Unknown ? WriteOutcome.ReadFirst : null, data_point = point, writes = lines });
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Reply(BrokerAnswer r) =>
        Out(new { success = r.Ok, error = r.Error, code = r.Ok ? null : r.Code, written = r.Written, next = r.Written == WriteOutcome.Unknown ? WriteOutcome.ReadFirst : null, result = r.Result });

    private static string Out(object o) => JsonSerializer.Serialize(o, Json);

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
