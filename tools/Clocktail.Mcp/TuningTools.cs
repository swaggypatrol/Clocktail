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
    /// <summary>A run's maxTempC: null when none was asked for; refused when it is no temperature a GPU can have (a typo,
    /// not a tuning bound: 1..120 °C).</summary>
    public static int? MaxTemp(int? maxTempC) => maxTempC is not { } c ? null
        : c is >= 1 and <= 120 ? c : throw new ArgumentException($"maxTempC is a GPU temperature, 1..120 °C, not {c}");

    public static string? Busy(Card.Look look) => look.Telemetry?.GpuUtilPercent is { } u && u > BusyUtilPct
        ? $"written while the GPU was {u} % busy: concurrent workload and temperature changes can affect the result; verify readback and compare repeatable runs"
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
        "Read this GPU's operating clocks, voltages, V/F point, power, temperature/headroom, utilization, memory, input power and available sensors. Compare readings with this board's own limits. Daily offsets refer only to a user-kept profile; report when none exists.")]
    public string GpuStatus() => Guard(() =>
    {
        var look = card.Read();
        var daily = DailyOffsets(out var dailyNote);
        var t = look.Telemetry;
        object? point = null;
        if (look.NvvddUv is { } uv && Nearest(look.Curve, uv) is { } p)
            point = Row(p, daily);
        var session = card.Broker(BrokerRequest.OpExperiment, "show");
        var (gpc, clocks, locks, memoryC) = card.ReadStatusExtras();
        double? Domain(int id) => clocks != null && clocks.TryGetValue(id, out var k) ? Math.Round(k / 1000.0) : null;
        return Out(new
        {
            gpu = card.Name,
            at = DateTimeOffset.Now,
            nvvdd_mv = look.NvvddUv / 1000.0,
            msvdd_mv = look.MsvddUv / 1000.0,
            nvvdd_max_mv = look.NvvddMaxUv / 1000.0,
            clock_mhz = t?.GraphicsClockMhz,
            memory_mhz = t?.MemoryClockMhz,
            // The other clock domains as the driver's clock table has them: a clock held on the core says nothing about these,
            // and a lock or a P-state can leave the core high while they sit low.
            domain_clocks_mhz = clocks == null ? null : new { xbar = Domain(AllClocksLayout.Xbar), sys = Domain(AllClocksLayout.Sys), video = Domain(AllClocksLayout.Video) },
            clock_locks = locks is { Count: > 0 } ? locks.Select(l => l.Describe()).ToList() : null,
            power_w = t?.PowerWatts,
            power_limit_w = t?.PowerEnforcedWatts,
            temp_c = t?.TemperatureC,
            temp_margin_c = t?.TempMarginC,
            pstate = t?.PState is { } ps ? $"P{ps}" : null,
            util_pct = t?.GpuUtilPercent,
            memory_c = memoryC,
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

    [McpServerTool(Name = "set_power_limit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set this board's ordinary power request for a trial, within its driver-reported range. Stock power and useful power targets vary by GPU, board vendor and VBIOS. Trial power is not automatically made daily.")]
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
        "Set a rail's current limit for a trial, using the current GPU's reported range; 'default' requests that board's own default. Read back and compare actual current, power and performance. This control is temporary.")]
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
        "Run NVIDIA's nvgwls stress tester on the current settings, sampling the actual operating point. Check failures against the trial that ran and record confirmed failures. The test holds a measurement lease. It stops within 2 °C of NVIDIA's slowdown point, and at maxTempC when one is given. No pass or fixed MHz margin guarantees stability in every workload.")]
    public string StressTest(
        [Description("0..100; 100 = continuous full load (default).")] int load = 100,
        [Description("Run length in seconds, 10..300 (default 60).")] int seconds = 60,
        [Description("What this run is meant to show, for the log.")] string? note = null,
        [Description("Also stop the run if the GPU reaches this temperature, °C. Omitted: it stops only within 2 °C of NVIDIA's slowdown point. A stop is neither a pass nor a crash.")] int? maxTempC = null) => Guard(() =>
    {
        if (load is < 0 or > 100) throw new ArgumentException("load is 0..100");
        int? maxC = MaxTemp(maxTempC);
        if (seconds < 10 || seconds > MaxStressSeconds) throw new ArgumentException($"seconds is 10..{MaxStressSeconds} (CLOCKTAIL_MCP_MAX_STRESS_S)");
        string exe = Nvgwls.Find() ?? throw new InvalidOperationException("nvgwls.exe was not found beside the loaded driver");
        var samples = new List<Card.Sample>();
        int? code;
        bool tooHot = false, exited = true;
        int hotRun = 0;
        double? stopC = null;
        string? hotWhy = null;
        bool Sample()
        {
            try
            {
                var s = card.ReadSample();
                samples.Add(s);
                hotRun = Nvgwls.TooHot(s.Telemetry?.TemperatureC, s.Telemetry?.TempMarginC, maxC) ? hotRun + 1 : 0;
                if (hotRun >= 2) { stopC = s.Telemetry?.TemperatureC; hotWhy = Nvgwls.HotWhy(stopC, maxC); return true; }
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
        IReadOnlyList<Clocktail.Domain.Clocks.ClientLimit>? locks;
        var started = DateTime.UtcNow;
        try
        {
            before = card.Read();   // the card as the run finds it, once no write is landing
            locks = card.ClockLocks();   // a lock outlives what set it (nvidia-smi -lgc, Afterburner's L): the run is checked against it
            started = DateTime.UtcNow;
            // A sample the driver does not answer in time is a missing one (a hang is what this run is looking for).
            residency.StressUntil(DateTimeOffset.Now.AddSeconds(seconds + 30));   // light load counts as load for this run
            code = Nvgwls.Run(exe, load, seconds, Sample, out tooHot, out exited, grace);
        }
        finally { residency.StressUntil(DateTimeOffset.MinValue); hold.Dispose(); }
        int? resets = tooHot ? Nvgwls.DriverResets(started, 0) : Nvgwls.DriverResets(started, code);
        double ranS = Math.Round((DateTime.UtcNow - started).TotalSeconds);
        string verdict = tooHot && resets is not > 0
            ? string.Create(CultureInfo.InvariantCulture, $"thermal stop at {stopC:0} °C after {ranS:0} s ({hotWhy}): stopped by stress_test, not a pass or a crash")
            : Nvgwls.Verdict(code, resets);
        bool failed = tooHot ? resets > 0 : Nvgwls.Failed(code, resets);

        // The first 2 s are the ramp; the rest is what the settings were tested at. A run that hung ends in frozen readings
        // (the curve's top at ~125 W for the whole TdrDelay): they are not what it was tested at.
        IReadOnlyList<Card.Sample> held = samples.Count > 4 ? samples.GetRange(4, samples.Count - 4) : samples;
        if (failed) held = WithoutFrozenTail(held);
        var (p10, p50, p90) = StressQuantiles(held);
        string csv = WriteSamples(samples, load, seconds);
        double? p50Mv = p50.nvvdd_mv, p50Mhz = p50.clock_mhz;
        var reasons = held.SelectMany(s => s.Telemetry?.ThrottleReasons ?? []).Distinct().ToList();
        int? pointIndex = p50Mv is { } mv && Nearest(before.Curve, (uint)(mv * 1000)) is { } pt ? pt.Index : null;
        var shift = CurveShift(before.Curve, ReadCurveOrNull(), pointIndex, held);
        var locksMet = LocksMet(locks, held.Select(s => (double?)s.Telemetry?.GraphicsClockMhz), before.Curve, before.NvvddMaxUv);

        bool trial = hold.Trial != null;   // in force when the run began: the trial it measured
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Stress nvgwls -l {load}, {seconds} s: {verdict} (exit code {(code?.ToString(CultureInfo.InvariantCulture) ?? "none")}). P50 {p50Mhz} MHz @ NVVDD {p50Mv} mV (curve point {pointIndex}), XBAR {p50.xbar_mhz} / SYS {p50.sys_mhz} MHz, board {p50.board_w} W, NVVDD {p50.nvvdd_w} W, MSVDD {p50.msvdd_mv} mV / {p50.msvdd_w} W; max {held.Max(s => s.Telemetry?.TemperatureC)} °C; limit reasons [{string.Join(", ", reasons)}]; {held.Count} samples; per sample {csv}")
            + LocksWords(locksMet) + LocksUnreadWords(locks) + (exited ? "" : ". nvgwls did not exit after being stopped") + (note != null ? $". Purpose: {note}" : "");
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
            p50,
            p10,
            p90,
            limit_reasons = reasons,
            pstates = PStates(held),
            efficiency = Efficiency(held, null, p50Mhz),
            curve_point = pointIndex,
            curve_shift = shift,
            clock_locks = locksMet,
            clock_locks_unread = locks == null ? LocksUnread : null,
            samples_csv = csv,
            trial_in_force = trial,
            crash_recorded = crashRecorded,
            warning = exited ? null : $"nvgwls did not exit within {Clocktail.Domain.Platform.ChildProcess.ExitWait.TotalSeconds:0} s of being stopped: its load may still be on the card. Check for nvgwls.exe in Task Manager before the next step",
            thermal_stop = tooHot && !failed ? new { at_c = stopC, after_s = ranS, why = hotWhy, max_temp_c = maxC } : null,
            next = failed ? "the card failed: read data_points / read_log before the next step"
                : tooHot ? "too hot to test this way: a steeper fan curve (set_fan_curve), fans back to auto, or a lower wall, then run again" : null,
        });
    });

    private static (StressStat P10, StressStat P50, StressStat P90) StressQuantiles(IReadOnlyList<Card.Sample> held)
    {
        var buf = new List<double>(held.Count);
        var clk = SampleQuantiles.P10P50P90(held, s => (double?)s.Telemetry?.GraphicsClockMhz, buf);
        var nvMv = SampleQuantiles.P10P50P90(held, s => s.NvvddUv / 1000.0, buf);
        var msMv = SampleQuantiles.P10P50P90(held, s => s.MsvddUv / 1000.0, buf);
        var brdW = SampleQuantiles.P10P50P90(held, s => s.Telemetry?.PowerWatts, buf);
        var nvW = SampleQuantiles.P10P50P90(held, s => s.NvvddOut?.Mw / 1000.0, buf);
        var nvA = SampleQuantiles.P10P50P90(held, s => s.NvvddOut?.Ma / 1000.0, buf);
        var msW = SampleQuantiles.P10P50P90(held, s => s.MsvddOut?.Mw / 1000.0, buf);
        var msA = SampleQuantiles.P10P50P90(held, s => s.MsvddOut?.Ma / 1000.0, buf);
        var xbar = SampleQuantiles.P10P50P90(held, s => s.Mhz(AllClocksLayout.Xbar), buf);
        var sys = SampleQuantiles.P10P50P90(held, s => s.Mhz(AllClocksLayout.Sys), buf);
        var vid = SampleQuantiles.P10P50P90(held, s => s.Mhz(AllClocksLayout.Video), buf);
        var mem = SampleQuantiles.P10P50P90(held, s => s.Mhz(AllClocksLayout.Memory), buf);
        var tmp = SampleQuantiles.P10P50P90(held, s => (double?)s.Telemetry?.TemperatureC, buf);
        var memC = SampleQuantiles.P10P50P90(held, s => s.MemoryC, buf);
        var vram = SampleQuantiles.P10P50P90(held, s => Gib(s.Telemetry?.VramUsedBytes), buf);
        var gpcMean = SampleQuantiles.P10P50P90(held, s => s.Gpc?.MeanMv, buf);
        var gpcMin = SampleQuantiles.P10P50P90(held, s => s.Gpc?.MinMv, buf);
        return (
            new(clk.P10, nvMv.P10, msMv.P10, brdW.P10, nvW.P10, nvA.P10, msW.P10, msA.P10, xbar.P10, sys.P10, vid.P10, mem.P10, tmp.P10, memC.P10, vram.P10, gpcMean.P10, gpcMin.P10),
            new(clk.P50, nvMv.P50, msMv.P50, brdW.P50, nvW.P50, nvA.P50, msW.P50, msA.P50, xbar.P50, sys.P50, vid.P50, mem.P50, tmp.P50, memC.P50, vram.P50, gpcMean.P50, gpcMin.P50),
            new(clk.P90, nvMv.P90, msMv.P90, brdW.P90, nvW.P90, nvA.P90, msW.P90, msA.P90, xbar.P90, sys.P90, vid.P90, mem.P90, tmp.P90, memC.P90, vram.P90, gpcMean.P90, gpcMin.P90));
    }

    /// <summary>What a stretch of load cost: the average board power, the energy (Wh, the samples' watts over the time between
    /// them) and, with a frame rate, frames per second per 100 W, the figure a power-limit sweep compares (manual
    /// 'power-thermal'). Null without board power.
    /// </summary>
    public static object? Efficiency(IReadOnlyList<Card.Sample> samples, double? avgFps, double? p50Mhz = null, string? over = null)
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
            over,
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

    [McpServerTool(Name = "install_service", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Install or update the service that performs writes, owns recovery records and reapplies only the user's existing daily profile. Obtain approval; Windows may request elevation. Installing does not create a tuning profile or write tuning defaults.")]
    public string InstallService() => Guard(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("the Clocktail service requires Windows");
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
    private int[] DailyOffsets(out string note) => DailyOffsetsFromShow(card.Broker(BrokerRequest.OpProfile, "show"), out note);

    /// <summary>The daily profile's Core offsets and status note from a <c>profile show</c> broker answer.</summary>
    public static int[] DailyOffsetsFromShow(BrokerAnswer r, out string note)
    {
        if (!r.Ok || r.Result is not { } res) { note = r.Error ?? "the daily profile could not be read"; return []; }
        if (res.GetProperty("profile") is not { ValueKind: JsonValueKind.Object } p) { note = "no daily profile is kept: offsets compare against stock"; return []; }
        foreach (var w in p.GetProperty("Writes").EnumerateArray())
            if (w.GetProperty("Kind").GetString() == VfWrites.Kind)
            {
                bool suspended = p.GetProperty("Suspended").GetBoolean();
                bool unapproved = res.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.False;
                note = suspended ? "the daily profile is suspended (stock on the card)"
                    : unapproved ? "the daily profile is waiting for approval (load_daily)"
                    : "daily profile on";
                return VfOffsets.Decode(w.GetProperty("Value").GetString()!);
            }
        note = "the daily profile keeps no curve: offsets compare against stock";
        return [];
    }

    /// <summary>Bytes as GiB to 0.1 (what NVML's 32 GB card calls 31.8).</summary>
    internal static double? Gib(ulong? bytes) => bytes is { } b ? Math.Round(b / 1073741824.0, 1) : null;

    /// <summary>The client clock limits that were in force for a run (<paramref name="held"/>, read as it began) against the
    /// core clock it sampled under load; null when none was in force. A voltage lock is set against its curve point's clock,
    /// Boost Lock against the curve's top under NVVDD MAX.</summary>
    public static IReadOnlyList<Clocktail.Domain.Clocks.ClockLockOutcome>? LocksMet(IReadOnlyList<Clocktail.Domain.Clocks.ClientLimit>? held,
        IEnumerable<double?> clocksMhz, VfCurveSnapshot? curve, uint? maxUv)
    {
        if (held is not { Count: > 0 }) return null;
        return Clocktail.Domain.Clocks.ClockLockCheck.Check(held, clocksMhz.OfType<double>().ToList(), l => curve == null ? null
            : l.Value == Clocktail.Domain.Clocks.ClientLimitsLayout.BoostLockUv ? Clocktail.Domain.Limiter.CurveTop(curve.Points, maxUv)?.EffectiveKhz / 1000.0
            : Nearest(curve, l.Value)?.EffectiveKhz / 1000.0);
    }

    /// <summary>An unreadable clock-lock record (<see cref="Card.ClockLocks"/> null) is not "no lock": the run's clocks may
    /// be a lock's, not the curve's.</summary>
    public const string LocksUnread = "the clock-lock record could not be read: a lock may be in force";

    /// <summary><see cref="LocksUnread"/> as a clause for a summary line when <paramref name="locks"/> could not be read; empty
    /// otherwise.</summary>
    public static string LocksUnreadWords(IReadOnlyList<Clocktail.Domain.Clocks.ClientLimit>? locks) => locks == null ? "; " + LocksUnread : "";

    /// <summary>The lock outcomes in one clause for a summary line; empty when none was in force.</summary>
    public static string LocksWords(IReadOnlyList<Clocktail.Domain.Clocks.ClockLockOutcome>? met) =>
        met is not { Count: > 0 } ? "" : "; clock lock in force: " + string.Join("; ", met.Select(m => $"{m.What}: {m.Words}"));

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
        // Report omitted daily settings with their current readback for the person.
        var notApplied = r.Result is { } nr && nr.TryGetProperty("not_applied", out var na) && na.ValueKind == JsonValueKind.Array ? na : (JsonElement?)null;
        string? next = written == WriteOutcome.Unknown ? WriteOutcome.ReadFirst
            : notApplied != null ? "tell the person which settings were omitted, why, and what the card reads now (not_applied)" : null;
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
