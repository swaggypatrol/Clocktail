using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Power;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// The trials beside the curve's, so an agent can do everything the reference card was tuned with by hand: the fan curve
/// (it sets the temperature the curve is tested at) and a fixed fan duty, the memory offset, XBAR's clock offset and
/// voltage demand; the daily wall, set on its own; and watching a stretch of play. Each trial goes through the broker
/// as adjust_curve's does, so every ceiling, the ownership journal and the crash handling hold, and each ends as a data point.
/// </summary>
public sealed partial class TuningTools
{
    /// <summary>set_memory_offset: the largest change per call (CLOCKTAIL_MCP_MEMORY_STEP_MHZ).</summary>
    public static readonly int MemoryStepMhz = Env("CLOCKTAIL_MCP_MEMORY_STEP_MHZ", 250);
    /// <summary>adjust_xbar: the largest offset change per call and the offset's range (CLOCKTAIL_MCP_XBAR_STEP_MHZ,
    /// CLOCKTAIL_MCP_MAX_XBAR_MHZ, CLOCKTAIL_MCP_MIN_XBAR_MHZ). Measured 0..+400 on the 5090; an overclocker's +450
    /// without more voltage gave a grey screen.</summary>
    public static readonly int XbarStepMhz = Env("CLOCKTAIL_MCP_XBAR_STEP_MHZ", 50);
    public static readonly int MaxXbarMhz = Env("CLOCKTAIL_MCP_MAX_XBAR_MHZ", 400);
    public static readonly int MinXbarMhz = Env("CLOCKTAIL_MCP_MIN_XBAR_MHZ", -100);
    /// <summary>adjust_xbar: the largest voltage demand change per call; the demand's own range is -25..+50 mV.</summary>
    public static readonly int VdemandStepMv = Env("CLOCKTAIL_MCP_VDEMAND_STEP_MV", 10);
    public const int MinVdemandMv = -25, MaxVdemandMv = 50;
    /// <summary>The MSVDD-floor cliff (2026-10-20): above +250 MHz with no demand, memory-bound work drops MSVDD to its
    /// 825 mV floor, the core is pinned near 1610 MHz and bandwidth falls 14-16 %; +20 mV of demand kept MSVDD off it.
    /// Above this offset the demand must be at least that.</summary>
    public const int XbarCliffMhz = 250, XbarCliffDemandMv = 20;
    /// <summary>Past +300 MHz +20 mV is not enough (2026-10-20, re/tuning-results §28): at +400 the memory phase's P10 core
    /// clock fell to 1942 MHz with +20 mV and 1830 with +30, and stayed clean (2392) only with +50.</summary>
    public const int XbarHighMhz = 300, XbarHighDemandMv = 50;

    /// <summary>The voltage demand an XBAR offset needs to stay off the MSVDD-floor cliff, mV; null below the cliff.</summary>
    public static int? XbarDemandNeededMv(double offsetMhz) =>
        offsetMhz > XbarHighMhz ? XbarHighDemandMv : offsetMhz > XbarCliffMhz ? XbarCliffDemandMv : null;
    /// <summary>set_fan_curve: full speed no later than the stock curve's (100 % at 88 °C), no duty under its 20 %.</summary>
    public const int FanFullByC = 88, FanMinCurvePct = 20;
    /// <summary>set_fan_speed: the firmware's floor while the fans spin.</summary>
    public const int FanMinPct = 30;
    /// <summary>watch: the longest stretch one call samples (CLOCKTAIL_MCP_MAX_WATCH_S); it blocks for it.</summary>
    public static readonly int MaxWatchSeconds = Env("CLOCKTAIL_MCP_MAX_WATCH_S", 600);
    /// <summary>watch: a sample counts as under load from this utilization and board power (menus and loading screens do not).</summary>
    public const uint LoadedUtilPct = 50;
    public const double LoadedW = 120;

    [McpServerTool(Name = "set_fan_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the firmware fan curve (every editable curve policy) as a new trial, the V/F curve left as it is: three points " +
        "degC:duty%, e.g. 50:20,79:60,86:100; rpm follows the firmware's own duty-to-rpm pairing (a point may name its rpm, " +
        "degC:duty%:rpm, only at or above that pairing: the firmware drives by rpm). The fan curve decides the " +
        "temperature the card runs at, and the V/F curve's clocks move with temperature (the driver's compensation steps near " +
        "57/62/72/82 C; ~2 MHz/C at 600 W, ~9 MHz/C near the knee), so set it before tuning the curve and test the curve at the " +
        "temperature it gives. Temperatures and duties must rise; the last point must be 100 % at 88 C or lower (full speed " +
        "never later than stock); no duty under 20 %. keep_as_daily keeps it; back_to_daily, end_session and a crash put the " +
        "daily profile's back. stress_test load 100 for 3-5 minutes shows the temperature it holds at the wall.")]
    public string SetFanCurve(
        [Description("Three points degC:duty%, rising, the last at 100 %: '50:20,79:60,86:100'; or 'stock' for the card's own.")] string curve,
        [Description("Why this curve (the temperature it is meant to hold at the wall).")] string? reason = null) => Guard(() =>
    {
        string text = FanCurveText.Stock;
        if (!FanCurveText.IsStock(curve))
        {
            var p = FanCurveText.Parse(curve);
            if (p[^1].Duty != FanCurvePoint.DutyOf(100) || p[^1].Temp > FanCurvePoint.TempOf(FanFullByC))
                throw new ArgumentException($"the last point must be 100 % at {FanFullByC} °C or lower: full speed never later than the stock curve's");
            if (p.Any(x => x.Duty < FanCurvePoint.DutyOf(FanMinCurvePct)))
                throw new ArgumentException($"no point under {FanMinCurvePct} % (the stock curve's lowest)");
            text = FanCurveText.Format(p);
        }
        return Trial($"fan curve → {text}", reason, new() { ["fan_curve"] = text });
    });

    [McpServerTool(Name = "set_fan_speed", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Hold every fan at a fixed duty for the session, as a new trial (or 'auto' to give them back to the fan curve): to " +
        "hold a temperature while testing, e.g. a fan sweep at the wall to see what each degree costs. On the reference card at " +
        "600 W: 100 % held 67 C, 75 % 73 C, 60 % 79 C; 45 % passed 85 C within 40 s. Watch temp_c and go back to 'auto' above " +
        "85 C. Never kept: back_to_daily, end_session, keep_as_daily, a crash and the agent disconnecting give the fans back to " +
        "the curve. 30..100 % " +
        "(the firmware's floor while spinning).")]
    public string SetFanSpeed(
        [Description("Duty in percent, 30..100, or 'auto'.")] string percent,
        [Description("Why (the temperature or the test it is for).")] string? reason = null) => Guard(() =>
    {
        string p = percent.Trim().TrimEnd('%').Trim();
        uint pct = 0;
        if (!p.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && (!uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out pct) || pct < FanMinPct || pct > 100))
            throw new ArgumentException($"percent is {FanMinPct}..100 (the firmware's floor while spinning) or 'auto'");
        return Trial(pct == 0 ? "fans back to the fan curve" : $"fans held at {pct} %", reason, new() { ["fan_pct"] = pct });
    });

    [McpServerTool(Name = "set_memory_offset", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the memory (GDDR7) offset as a new trial: the P0 offset in MHz, which NVML shows doubled. A memory write zeroes " +
        "the V/F curve's offsets, so the curve on the card is written again right after it, in the same trial. At most 250 MHz " +
        "from where it is per call (CLOCKTAIL_MCP_MEMORY_STEP_MHZ), within the driver's range (-1000..+3000 on a 5090). On " +
        "the reference card +358 lowered latency 2.4 % and left bandwidth flat; nothing higher was measured. Memory past its " +
        "edge shows as artefacts, a data error in stress_test (exit code 6), a crash, or performance that stops rising and falls " +
        "(the memory retries errors instead of failing): judge each step by a repeatable benchmark as well as stress_test. " +
        "keep_as_daily keeps it; back_to_daily, end_session and a crash put the daily profile's back.")]
    public string SetMemoryOffset(
        [Description("The memory offset to try, MHz (absolute, not a delta).")] double mhz,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        var p0 = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Memory) ?? throw new InvalidOperationException("the memory offset is unreadable");
        int khz = (int)Math.Round(mhz * 1000), now = p0.DeltaKhz;
        if (khz == now) throw new ArgumentException($"the memory offset is {now / 1000.0} MHz already: nothing to try");
        if (Math.Abs(khz - now) > MemoryStepMhz * 1000)
            throw new ArgumentException($"a step is at most {MemoryStepMhz} MHz (CLOCKTAIL_MCP_MEMORY_STEP_MHZ); it is {now / 1000.0} MHz now");
        if (khz < p0.MinKhz || khz > p0.MaxKhz)
            throw new ArgumentException($"the driver's range is {p0.MinKhz / 1000.0}..{p0.MaxKhz / 1000.0} MHz");
        return Trial(string.Create(CultureInfo.InvariantCulture, $"memory offset {now / 1000.0:+0.#;-0.#;0} → {khz / 1000.0:+0.#;-0.#;0} MHz"),
            reason, new() { ["memory_khz"] = khz });
    });

    [McpServerTool(Name = "adjust_xbar", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Move XBAR's clock offset and/or its voltage demand (on MSVDD) as a new trial. XBAR is the crossbar between the GPCs, " +
        "L2 and memory; under load it is held to about 0.9 x the core clock, and about +250 MHz of offset reaches that, after " +
        "which more offset only lowers the MSVDD voltage it needs. On the reference card (MSVDD REL 0): +250 made Blender 6.3 % " +
        "faster than stock, +300 with +20 mV of demand 6-7 %. The cliff: above +250 with no demand, memory-bound work lets " +
        "MSVDD fall to its 825 mV floor, the core is pinned near 1610 MHz and bandwidth falls 14-16 %; +20 mV of demand keeps " +
        "it off up to +300, so above +250 the demand must be +20 mV or more, and above +300 +50 mV (at +400 the memory phase " +
        "still dropped to ~1830-1940 MHz with +20/+30 mV; raise the demand first). At most 50 MHz and 10 mV per call; " +
        "offset -100..+400 MHz, demand -25..+50 mV. If anything is unstable with XBAR raised, step it down 50 MHz before " +
        "touching the curve. keep_as_daily keeps it; back_to_daily, end_session and a crash put the daily profile's back.")]
    public string AdjustXbar(
        [Description("MHz to add to XBAR's clock offset (negative lowers); default 0.")] double offsetDeltaMhz = 0,
        [Description("mV to add to XBAR's voltage demand (negative lowers); default 0.")] double vdemandDeltaMv = 0,
        [Description("Why this step (what you saw, what you expect).")] string? reason = null) => Guard(() =>
    {
        if (offsetDeltaMhz == 0 && vdemandDeltaMv == 0) throw new ArgumentException("both deltas are 0: nothing to try");
        if (Math.Abs(offsetDeltaMhz) > XbarStepMhz) throw new ArgumentException($"an offset step is at most {XbarStepMhz} MHz (CLOCKTAIL_MCP_XBAR_STEP_MHZ)");
        if (Math.Abs(vdemandDeltaMv) > VdemandStepMv) throw new ArgumentException($"a demand step is at most {VdemandStepMv} mV (CLOCKTAIL_MCP_VDEMAND_STEP_MV)");
        var (offNow, demandNow) = SettingsTools.Domain(card, ClockDomainId.Xbar);
        int off = offNow + (int)Math.Round(offsetDeltaMhz * 1000), demand = demandNow + (int)Math.Round(vdemandDeltaMv * 1000);
        if (off > MaxXbarMhz * 1000 || off < MinXbarMhz * 1000)
            throw new ArgumentException($"the XBAR offset stays within {MinXbarMhz}..+{MaxXbarMhz} MHz (it would be {off / 1000.0})");
        if (demand > MaxVdemandMv * 1000 || demand < MinVdemandMv * 1000)
            throw new ArgumentException($"the voltage demand stays within {MinVdemandMv}..+{MaxVdemandMv} mV (it would be {demand / 1000.0})");
        if (XbarDemandNeededMv(off / 1000.0) is { } need && demand < need * 1000)
            throw new ArgumentException($"XBAR at {off / 1000.0:+0.#} MHz needs at least +{need} mV of voltage demand (above +{XbarCliffMhz}: +{XbarCliffDemandMv}; " +
                $"above +{XbarHighMhz}: +{XbarHighDemandMv}): without it memory-bound work drops MSVDD to its floor and pins the core near 1610 MHz. Raise the demand first");
        var items = new Dictionary<string, object?>();
        var parts = new List<string>();
        if (off != offNow) { items["xbar_khz"] = off; parts.Add(string.Create(CultureInfo.InvariantCulture, $"XBAR offset {offNow / 1000.0:+0.#;-0.#;0} → {off / 1000.0:+0.#;-0.#;0} MHz")); }
        if (demand != demandNow) { items["xbar_vdemand_uv"] = demand; parts.Add(string.Create(CultureInfo.InvariantCulture, $"XBAR voltage demand {demandNow / 1000.0:+0.#;-0.#;0} → {demand / 1000.0:+0.#;-0.#;0} mV")); }
        return Trial(string.Join(", ", parts), reason, items);
    });

    [McpServerTool(Name = "set_daily_power_limit", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Make this the daily power limit: it goes on now and at every start and after every driver reset. A trial's wall " +
        "(set_power_limit) is never kept, so this is how the daily one changes. 400-600 W on an RTX 5090 (575 W stock). Set it " +
        "before tuning the curve: the wall decides where on the curve heavy load runs. On the reference card the last 100 W " +
        "bought about 10 % frames (GTA V 78 → 86 fps from 500 to 600 W); 475-500 W is the efficient zone. While a trial runs " +
        "under its own wall, the daily one goes on when the trial ends. Ask the person before calling this.")]
    public string SetDailyPowerLimit([Description("The daily power limit, W.")] double watts) => Guard(() =>
    {
        if (watts > MaxPowerW || watts < MinPowerW) throw new ArgumentException($"the daily power limit is {MinPowerW}..{MaxPowerW} W");
        uint mw = (uint)Math.Round(watts * 1000);
        var r = card.Broker(BrokerRequest.OpProfile, "wall", mw.ToString(CultureInfo.InvariantCulture));
        if (!r.Ok) return Reply(r);
        var after = ReadAfter(out var unread);
        return Out(new { success = true, daily_power_limit_w = mw / 1000.0, written = r.Written, power_limit_w = after?.Telemetry?.PowerEnforcedWatts, warning = unread });
    });

    [McpServerTool(Name = "watch", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Sample the card for a stretch while the person plays (twice a second) and summarize it: p10/p50/p90 of the core clock, " +
        "NVVDD, MSVDD, XBAR, board power and temperature over the samples under load (50 %+ utilization and 120 W+: menus and " +
        "loading screens are left out), how much of the time it sat on the wall, the curve point it ran on, and NVML's limit " +
        "reasons; and the game's frame rate over the same stretch (average, 1 % and 0.1 % lows, frame times; through the " +
        "service's PresentMon capture, nothing injected into the game), so a change is judged on frames, not only clocks. " +
        "This is how a game run is judged: its p90 clock at its voltage against what stress_test passed there (games " +
        "need 30-60 MHz more margin). The summary goes into the tuning log. Blocks for the stretch; 60-300 s is typical.")]
    public string Watch(
        [Description("How long to sample, seconds (10..600; default 120).")] int seconds = 120,
        [Description("What is being played (game, scene, settings), for the log.")] string? note = null,
        [Description("The game's process name for the frame rate (e.g. \"Cyberpunk2077\"); omit for the application that drew the most frames.")] string? process = null) => Guard(() =>
    {
        if (seconds < 10 || seconds > MaxWatchSeconds) throw new ArgumentException($"seconds is 10..{MaxWatchSeconds} (CLOCKTAIL_MCP_MAX_WATCH_S)");
        var curve = card.Read().Curve;
        // A live watch's capture is followed, not replaced: a second capture would end it, and every later live line would
        // say "no frames".
        string? noFrames = null;
        var shared = live.FramePath;
        var frames = shared != null ? FrameFeed.Following(shared, process) : FrameFeed.Start(card, seconds, process, out noFrames);
        var samples = new List<Card.Sample>();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            try { samples.Add(card.ReadSample()); } catch (InvalidOperationException) { }
            Thread.Sleep(500);
        }
        var loaded = samples.Where(s => s.Telemetry is { GpuUtilPercent: >= LoadedUtilPct, PowerWatts: >= LoadedW }).ToList();
        double? atWall = loaded.Count == 0 ? null : Math.Round(100.0 * loaded.Count(s =>
            s.Telemetry is { PowerWatts: { } w, PowerEnforcedWatts: { } l } && w >= l - Limiter.WallBandW) / loaded.Count);
        double? p50Mv = Pct(loaded.Select(s => s.NvvddUv / 1000.0), 0.5);
        var point = p50Mv is { } mv && Nearest(curve, (uint)(mv * 1000)) is { } pt
            ? new { index = pt.Index, mv = pt.VoltageUv / 1000.0, curve_mhz = pt.EffectiveKhz / 1000.0 } : null;
        var reasons = loaded.SelectMany(s => s.Telemetry?.ThrottleReasons ?? []).Distinct().ToList();
        if (shared == null) frames?.Settle(TimeSpan.FromSeconds(3));   // its own capture ends by itself: let it finish writing
        var fps = frames?.Total();
        var p50 = WatchStats(loaded, 0.5);
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Watched {seconds} s: {loaded.Count} of {samples.Count} samples under load. P50 {p50.clock_mhz} MHz @ NVVDD {p50.nvvdd_mv} mV (curve point {point?.index}), " +
            $"P90 {WatchStats(loaded, 0.9).clock_mhz} MHz, P10 {WatchStats(loaded, 0.1).clock_mhz} MHz; MSVDD {p50.msvdd_mv} mV, XBAR {p50.xbar_mhz} MHz, board {p50.board_w} W, " +
            $"{atWall} % of the time on the wall, max {loaded.Max(s => s.Telemetry?.TemperatureC)} °C, VRAM {p50.vram_used_gib} GiB; limit reasons [{string.Join(", ", reasons)}]")
            + (frames != null ? $"; {FrameFeed.Words(fps)}" : "")
            + (note != null ? $". Playing: {note}" : "");
        card.Broker(BrokerRequest.OpExperiment, "note", summary);
        return Out(new
        {
            seconds,
            samples = samples.Count,
            under_load = loaded.Count,
            p10 = WatchStats(loaded, 0.1),
            p50,
            p90 = WatchStats(loaded, 0.9),
            on_the_wall_pct = atWall,
            curve_point = point,
            limit_reasons = reasons,
            pstates = PStates(loaded),
            fps = FrameFeed.Show(fps) ?? (object)new
            {
                unavailable = noFrames ?? (process != null ? $"no frames from {process}: is that the game's process name?" : "no application drew frames: was the game running?"),
            },
            fps_csv = frames?.Path,
            note = loaded.Count == 0 ? "no sample was under load: was the game running in the foreground?" : null,
        });
    });

    private sealed record WatchStat(double? clock_mhz, double? nvvdd_mv, double? msvdd_mv, double? xbar_mhz, double? board_w, double? temp_c, double? vram_used_gib);

    private static WatchStat WatchStats(IReadOnlyList<Card.Sample> s, double q) => new(
        Pct(s.Select(x => (double?)x.Telemetry?.GraphicsClockMhz), q), Pct(s.Select(x => x.NvvddUv / 1000.0), q),
        Pct(s.Select(x => x.MsvddUv / 1000.0), q), Pct(s.Select(x => x.Mhz(AllClocksLayout.Xbar)), q),
        Pct(s.Select(x => x.Telemetry?.PowerWatts), q), Pct(s.Select(x => x.Telemetry?.TemperatureC), q),
        Pct(s.Select(x => Gib(x.Telemetry?.VramUsedBytes)), q));

    private static double? Pct(IEnumerable<double?> xs, double q)
    {
        var v = xs.Where(x => x != null).Select(x => x!.Value).Order().ToList();
        return v.Count == 0 ? null : Math.Round(v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)], 1);
    }

    /// <summary>One trial with the V/F curve left as it is: the broker writes <paramref name="items"/> beside the curve
    /// on the card (written again when the memory offset is among them) and records the trial.</summary>
    private string Trial(string change, string? reason, Dictionary<string, object?> items)
    {
        var look = card.Read();
        var t = look.Telemetry;
        var context = new List<string> { Readings(look) };
        if (reason != null) context.Add($"Purpose: {reason}");
        var before = look.Curve.Offsets();
        var args = new Dictionary<string, object?>
        {
            ["curve"] = VfOffsets.Encode(before), ["change"] = change, ["at_uv"] = look.NvvddUv, ["at_mhz"] = t?.GraphicsClockMhz,
            ["keep_curve"] = true, ["context"] = context,
        };
        foreach (var (k, v) in items) args[k] = v;
        var r = card.Broker(BrokerRequest.OpExperiment, "try", JsonSerializer.Serialize(args));
        if (!r.Ok) return Reply(r);
        bool? intact = ReadAfter(out var unread)?.Curve.Offsets().SequenceEqual(before);
        return Out(new
        {
            success = true,
            change,
            written = r.Written,
            curve_intact = intact,
            warning = unread ?? (intact == false ? "the V/F curve is not what it was before this write: call back_to_daily before going on" : null),
        });
    }
}
