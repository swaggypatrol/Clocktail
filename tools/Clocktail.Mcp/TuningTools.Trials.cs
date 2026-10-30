using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Domain.Boost;
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
    /// <summary>adjust_xbar / adjust_clock_domain: the offset's range is the driver's (±1000 MHz on a 5090), unless
    /// CLOCKTAIL_MCP_MAX_XBAR_MHZ caps XBAR lower. Measured 0..+400 on the 5090; an overclocker's +450 without more voltage
    /// gave a grey screen. No step per call (the user, 2026-10-27: the driver's limits, no others).</summary>
    public static readonly int MaxXbarMhz = Env("CLOCKTAIL_MCP_MAX_XBAR_MHZ", int.MaxValue);
    /// <summary>The voltage demand's range: the driver's standard one (<see cref="ClockVdemand"/>).</summary>
    public const int MinVdemandMv = ClockVdemand.MinUv / 1000, MaxVdemandMv = ClockVdemand.MaxUv / 1000;
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

    [McpServerTool(Name = "set_fan_start", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the temperature the stopped fans start at, and the one they stop under, on every editable fan policy, as a new " +
        "trial: the firmware's zero-RPM band, 43 / 50 °C stock on a 5090 FE, on the curve's own (filtered) temperature. " +
        "Watched on the reference card: stopped at idle, the fans started when it reached 50.0 °C and stopped when it fell " +
        "to 43.0 °C. Only an earlier start: startC no later than the curve's first point, stopC at least 3 °C under it (7 by " +
        "default, as stock), 20 °C at the lowest. A stopped card warms by itself at idle (36 → 52 °C in about 5 minutes), so " +
        "a lower start keeps it cooler and quieter to the ear only above it. keep_as_daily keeps it; back_to_daily, " +
        "end_session and a crash put the daily profile's back.")]
    public string SetFanStart(
        [Description("Temperature the fans start at, °C.")] double startC,
        [Description("Temperature they stop under, °C (default startC - 7).")] double? stopC = null,
        [Description("Why (what it is meant to hold).")] string? reason = null) => Guard(() =>
    {
        decimal start = (decimal)startC, stop = (decimal)(stopC ?? startC - 7);
        var snap = card.WithDriver(FanPolicyReader.Read);
        var editable = snap.Info.Editable.Select(p => p.Index).ToList();
        if (editable.Count == 0) throw new InvalidOperationException("this card has no editable fan policy");
        string value = string.Create(CultureInfo.InvariantCulture, $"{stop:0.##}:{start:0.##}");
        var want = FanStopWrites.Parse(value)!.Value;
        foreach (int i in editable)
            if (snap.ControlRaw is { } raw && FanStopWrites.Invalid(want, FanPolicyParser.PointsIn(raw, i)) is { } why) throw new ArgumentException($"policy {i}: {why}");
        string was = snap.ControlRaw is { } r0 ? FanStopWrites.Format(FanStopWrites.Read(r0, editable[0])) : "?";
        return Trial($"fans stop/start {was} → {value} °C", reason,
            new() { ["writes"] = editable.Select(i => TrialWrite(FanStopWrites.Kind, FanPolicyWrites.Key(i), value)).ToList() });
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
        "the V/F curve's offsets, so the curve on the card is written again right after it, in the same trial. Anywhere in " +
        "the driver's range (-1000..+3000 on a 5090). On " +
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
        if (khz < p0.MinKhz || khz > p0.MaxKhz)
            throw new ArgumentException($"the driver's range is {p0.MinKhz / 1000.0}..{p0.MaxKhz / 1000.0} MHz");
        return Trial(string.Create(CultureInfo.InvariantCulture, $"memory offset {now / 1000.0:+0.#;-0.#;0} → {khz / 1000.0:+0.#;-0.#;0} MHz"),
            reason, new() { ["memory_khz"] = khz }, coreDeltaKhz: 0);
    });

    [McpServerTool(Name = "set_core_offset", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the core's P0 clock offset as a new trial: MHz added to every point of the V/F curve at once (stock curve + N), " +
        "beside the point offsets adjust_curve moves. The two spend one firmware offset budget. P0 and V/F controls can " +
        "overlap: the broker preserves the existing shape, repairs native side effects and verifies the effective change. Anywhere in the " +
        "driver's range. On the reference card +15 gave +15/+22/+15 MHz at " +
        "the same voltage. The edge differs by voltage, so a band is adjust_curve's; this shifts the whole curve. " +
        "keep_as_daily keeps it; back_to_daily, end_session and a crash put the daily profile's back.")]
    public string SetCoreOffset(
        [Description("The core P0 offset to try, MHz (absolute, not a delta).")] double mhz,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        var p0 = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Core) ?? throw new InvalidOperationException("the core offset is unreadable");
        int khz = (int)Math.Round(mhz * 1000), now = p0.DeltaKhz;
        if (khz == now) throw new ArgumentException($"the core offset is {now / 1000.0} MHz already: nothing to try");
        if (khz < p0.MinKhz || khz > p0.MaxKhz)
            throw new ArgumentException($"the driver's range is {p0.MinKhz / 1000.0}..{p0.MaxKhz / 1000.0} MHz");
        return Trial(string.Create(CultureInfo.InvariantCulture, $"core offset {now / 1000.0:+0.#;-0.#;0} → {khz / 1000.0:+0.#;-0.#;0} MHz"),
            reason, new() { ["writes"] = new[] { TrialWrite(ClockOffsets.Kind, ClockOffsets.Key(ClockDomain.Core), khz) } }, coreDeltaKhz: (long)khz - now);
    });

    /// <summary>One item of a trial's "writes": any registry write, through its own path and rules.</summary>
    private static object TrialWrite(string kind, string key, object value) =>
        new { kind, key, value = Convert.ToString(value, CultureInfo.InvariantCulture) };

    [McpServerTool(Name = "adjust_xbar", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Move XBAR's clock offset and/or its voltage demand (on MSVDD) as a new trial. XBAR is the crossbar between the GPCs, " +
        "L2 and memory; under load it runs between about 0.9 x the core clock (set_clock_ratio moves that ratio) and the clock " +
        "MSVDD's reliability limit allows (MSVDD's REL, which only set_voltage_boost raises since driver 621.01: +5 mV per 25 %). " +
        "At ratio 0.9 and REL 0 about +250 MHz reaches that window and more offset only lowers the MSVDD voltage it needs; with the " +
        "ratio and REL raised the offset pays further (the reference card on 617.42: +400 / +50 mV at ratio 0.95, REL +50 made the " +
        "lab's crossbar test 5.6 % faster than +250 there; on 621.01 with MSVDD at stock, +400 / +50 mV at ratio 0.9 was about 10 % " +
        "over stock). On the reference card (MSVDD REL 0): +250 made Blender 6.3 % " +
        "faster than stock, +300 with +20 mV of demand 6-7 %. The cliff: above +250 with no demand, memory-bound work lets " +
        "MSVDD fall to its 825 mV floor, the core is pinned near 1610 MHz and bandwidth falls 14-16 %; +20 mV of demand keeps " +
        "it off up to +300, so above +250 the demand must be +20 mV or more, and above +300 +50 mV (at +400 the memory phase " +
        "still dropped to ~1830-1940 MHz with +20/+30 mV; raise the demand first). Offset anywhere in the driver's range " +
        "(±1000 MHz on a 5090), demand -25..+50 mV. Past +400 is unmeasured: an overclocker " +
        "saw grey-screen artefacts at +450 with no voltage added. If anything is unstable with XBAR raised, step it down 50 " +
        "MHz before touching the curve. keep_as_daily keeps it; back_to_daily, end_session and a crash put the daily profile's back.")]
    public string AdjustXbar(
        [Description("MHz to add to XBAR's clock offset (negative lowers); default 0.")] double offsetDeltaMhz = 0,
        [Description("mV to add to XBAR's voltage demand (negative lowers); default 0.")] double vdemandDeltaMv = 0,
        [Description("Why this step (what you saw, what you expect).")] string? reason = null) => Guard(() =>
    {
        if (offsetDeltaMhz == 0 && vdemandDeltaMv == 0) throw new ArgumentException("both deltas are 0: nothing to try");
        var (offNow, demandNow) = SettingsTools.Domain(card, ClockDomainId.Xbar);
        int off = offNow + (int)Math.Round(offsetDeltaMhz * 1000), demand = demandNow + (int)Math.Round(vdemandDeltaMv * 1000);
        var (min, max) = OffsetRange(ClockDomainId.Xbar);
        max = Math.Min(max, MaxXbarMhz);
        if (off > max * 1000 || off < min * 1000)
            throw new ArgumentException($"the XBAR offset stays within {min}..+{max} MHz (it would be {off / 1000.0})");
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

    [McpServerTool(Name = "adjust_clock_domain", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Move SYS's or Video's clock offset and/or voltage demand, or the core's voltage demand, as a new trial, as " +
        "adjust_xbar does for XBAR. The core's demand (domain 'core', on NVVDD) moves every V/F point's voltage by the same " +
        "mV: the same curve at more voltage (margin) or less (an undervolt of the whole curve); card_settings vf_curve " +
        "voltage_demand_mv reads it back, and the core's clock offset is set_core_offset's. SYS is the " +
        "system clock (it falls as XBAR's offset rises: XBAR 0/+200/+300/+400 gave SYS 2182/2017/1912/1830 MHz in the memory " +
        "phase, and SYS +150 back at XBAR +300 changed nothing in a Blender render); Video is the video engine (NVENC/NVDEC), " +
        "which games do not use. Neither was written on the reference card before: judge a step by its own clock in " +
        "gpu_status and by a benchmark that uses it. The demand goes to the rail the domain's topology names (MSVDD on a 5090). " +
        "Offset anywhere in the driver's range, demand -25..+50 mV. keep_as_daily keeps it; " +
        "back_to_daily, end_session and a crash put the daily profile's back.")]
    public string AdjustClockDomain(
        [Description("'sys', 'video', or 'core' (voltage demand only).")] string domain,
        [Description("MHz to add to the domain's clock offset (negative lowers); default 0.")] double offsetDeltaMhz = 0,
        [Description("mV to add to the domain's voltage demand (negative lowers); default 0.")] double vdemandDeltaMv = 0,
        [Description("Why this step (what you saw, what you expect).")] string? reason = null) => Guard(() =>
    {
        var d = domain.Trim().ToLowerInvariant() switch
        {
            "sys" => ClockDomainId.Sys,
            "video" => ClockDomainId.Video,
            "core" or "graphics" => ClockDomainId.Graphics,
            "xbar" => throw new ArgumentException("XBAR is adjust_xbar's: it carries the rules for the MSVDD-floor cliff"),
            _ => throw new ArgumentException("domain is 'sys', 'video' or 'core'"),
        };
        if (offsetDeltaMhz == 0 && vdemandDeltaMv == 0) throw new ArgumentException("both deltas are 0: nothing to try");
        if (d == ClockDomainId.Graphics && offsetDeltaMhz != 0) throw new ArgumentException("the core's clock offset is set_core_offset's (P0); here only its voltage demand moves");
        var (offNow, demandNow) = SettingsTools.Domain(card, d);
        int off = offNow + (int)Math.Round(offsetDeltaMhz * 1000), demand = demandNow + (int)Math.Round(vdemandDeltaMv * 1000);
        var (min, max) = OffsetRange(d);
        if (off != offNow && (off > max * 1000 || off < min * 1000))
            throw new ArgumentException($"the {d} offset stays within the driver's {min}..+{max} MHz (it would be {off / 1000.0})");
        if (demand > MaxVdemandMv * 1000 || demand < MinVdemandMv * 1000)
            throw new ArgumentException($"the voltage demand stays within {MinVdemandMv}..+{MaxVdemandMv} mV (it would be {demand / 1000.0})");
        string name = d switch { ClockDomainId.Sys => "SYS", ClockDomainId.Video => "Video", _ => "core" };
        var writes = new List<object>();
        var parts = new List<string>();
        if (demand != demandNow)
        {
            writes.Add(TrialWrite(ClockVdemand.Kind, ClockVdemand.Key(d), demand));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name} voltage demand {demandNow / 1000.0:+0.#;-0.#;0} → {demand / 1000.0:+0.#;-0.#;0} mV"));
        }
        if (off != offNow)
        {
            writes.Add(TrialWrite(ClockDomainOffsets.Kind, ClockDomainOffsets.Key(d), off));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name} offset {offNow / 1000.0:+0.#;-0.#;0} → {off / 1000.0:+0.#;-0.#;0} MHz"));
        }
        return Trial(string.Join(", ", parts), reason, new() { ["writes"] = writes });
    });

    /// <summary>A clock domain's offset range, MHz, as the driver's topology states it.</summary>
    private (int Min, int Max) OffsetRange(ClockDomainId d)
    {
        var t = ClockDomainsParser.Find(card.WithDriver(ClockDomainOffsets.ReadTopology), (uint)d);
        return (t.MinOffsetMhz, t.MaxOffsetMhz);
    }

    [McpServerTool(Name = "set_voltage_boost", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the voltage boost percentage (0..100) as a new trial, on the path the card takes (legacy on a 5090, whose NVVDD " +
        "is kind 3). The legacy byte and the independent interface are one driver variable. It raises every rail's REL limit " +
        "5 mV per 25 %: 100 % adds the 20 mV between REL and ALT/OP (the reference card on driver 621.01: NVVDD 1095 → 1115, " +
        "MSVDD 1045 → 1065 mV; 1 % and 10 % are under the first step and moved nothing). It still writes on 621.01, which drops " +
        "the rails' own offsets, so it is the one way to raise MSVDD's ceiling, XBAR's: at XBAR +400 / +50 mV, 100 % made the " +
        "lab's L2 test 1.0-1.3 % faster with the core unchanged. A curve cut flat under NVVDD's limit never reaches the higher " +
        "one. Every rail's MAX must stay within the 1.15 V standard ceiling after the write, or it is put back. " +
        "keep_as_daily keeps it; back_to_daily, end_session and a crash put the daily profile's back.")]
    public string SetVoltageBoost(
        [Description("The boost to try, percent 0..100 (absolute, not a delta).")] int percent,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        if (percent < 0 || percent > VoltBoost.MaxPercent) throw new ArgumentException($"the boost is 0..{VoltBoost.MaxPercent} %");
        var o = card.WithDriver((hw, s) => BoostReader.Observe(hw, s));
        var path = o.DefaultRule(railsWritable: true) ?? throw new InvalidOperationException("this card has no boost path");
        uint now = path == BoostPath.Independent ? o.IndependentPercent ?? 0 : o.LegacyPercent;
        if (percent == now) throw new ArgumentException($"the boost is {now} % already: nothing to try");
        string key = path == BoostPath.Independent ? "independent" : "legacy";
        return Trial($"voltage boost ({key}) {now} → {percent} %", reason, new() { ["writes"] = new[] { TrialWrite(VoltBoost.Kind, key, percent) } });
    });

    [McpServerTool(Name = "set_clock_lock", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Cap, hold or pin the core clock as a new trial, through the driver's client clock-limit record (the one nvidia-smi " +
        "-lgc, a V/F point lock and Boost Lock all use): maxMhz caps the clock, minMhz holds it at least there (both: the " +
        "range nvidia-smi -lgc sets), lockMv pins the core to the V/F point at that voltage, boostLock holds it at the " +
        "curve's top even at idle. 0 (or boostLock false) releases that one; one left out stays as it is. A point lock and a " +
        "clock range cannot be in force together. Uses: hold one point of the curve for a stress run (lockMv), or keep a test " +
        "in one band while the wall would move it (minMhz/maxMhz). card_settings clock_locks shows what is in force. On the " +
        "reference card -lgc 1200,2400 held the clock at most 2392 MHz under load. Only a cap can become daily: keep_as_daily " +
        "keeps maxMhz (release a test cap first, or it becomes the daily ceiling). A floor, a point lock and Boost Lock are " +
        "trial-only, as a current limit is: they would hold the card at a high clock or voltage at idle after every boot, so " +
        "keep_as_daily, back_to_daily, end_session, a crash and the agent disconnecting release them.")]
    public string SetClockLock(
        [Description("Maximum core clock, MHz; 0 releases.")] double? maxMhz = null,
        [Description("Minimum core clock, MHz; 0 releases.")] double? minMhz = null,
        [Description("Pin the core to the V/F point at this voltage, mV (the nearest point); 0 releases.")] double? lockMv = null,
        [Description("true holds the clock at the curve's top (Boost Lock); false releases it.")] bool? boostLock = null,
        [Description("Why (what the lock is for).")] string? reason = null) => Guard(() =>
    {
        if (maxMhz is null && minMhz is null && lockMv is null && boostLock is null) throw new ArgumentException("nothing to set: give maxMhz, minMhz, lockMv or boostLock");
        if (lockMv is > 0 && boostLock == true) throw new ArgumentException("a point lock and Boost Lock are the same entry: give one");
        if (new[] { maxMhz, minMhz, lockMv }.Any(v => v < 0)) throw new ArgumentException("a value is positive, or 0 to release");
        var now = card.WithDriver(ClientLimitsLayout.Read);
        uint Held(int entry) => now.FirstOrDefault(l => l.Domain == entry)?.Value ?? 0;
        uint max = maxMhz is { } a ? (uint)Math.Round(a * 1000) : Held(0), min = minMhz is { } b ? (uint)Math.Round(b * 1000) : Held(1);
        uint voltage = Held(6);
        string? point = null;
        if (boostLock is { } bl) voltage = bl ? ClientLimitsLayout.BoostLockUv : voltage == ClientLimitsLayout.BoostLockUv ? 0 : voltage;
        if (lockMv is { } mv)
        {
            if (mv == 0) voltage = 0;
            else
            {
                var p = card.Read().Curve.Points.MinBy(x => Math.Abs((double)x.VoltageUv - mv * 1000)) ?? throw new InvalidOperationException("the V/F curve is unreadable");
                voltage = p.VoltageUv;
                point = string.Create(CultureInfo.InvariantCulture, $"point {p.Index}, {p.VoltageUv / 1000.0} mV, {p.EffectiveKhz / 1000.0} MHz");
            }
        }
        if (voltage is > 0 and not ClientLimitsLayout.BoostLockUv && (max > 0 || min > 0))
            throw new ArgumentException("a point lock and a clock range cannot be in force together: release the range (maxMhz 0, minMhz 0) or give no lockMv");
        if (max > 0 && min > max) throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"the minimum ({min / 1000.0} MHz) is above the maximum ({max / 1000.0} MHz)"));
        // Releases first, then the side that moves away from the other: raising both, the maximum first.
        var items = new List<(string Key, uint Value, uint Was)> { (ClockLimitWrites.MaxKey, max, Held(0)), (ClockLimitWrites.MinKey, min, Held(1)), (ClockLimitWrites.VoltageKey, voltage, Held(6)) }
            .Where(i => i.Value != i.Was)
            .OrderBy(i => i.Value == 0 ? 0 : 1).ThenBy(i => i.Key == ClockLimitWrites.MaxKey ? (max > Held(0) ? 0 : 1) : i.Key == ClockLimitWrites.MinKey ? (max > Held(0) ? 1 : 0) : 2)
            .ToList();
        if (items.Count == 0) throw new ArgumentException("that is what is in force already: nothing to try");
        string Words(string key, uint v) => v == 0 ? $"{key} off" : key == ClockLimitWrites.VoltageKey
            ? (v == ClientLimitsLayout.BoostLockUv ? "Boost Lock" : $"voltage locked at {v / 1000.0} mV ({point})")
            : string.Create(CultureInfo.InvariantCulture, $"{key} clock {v / 1000.0} MHz");
        return Trial("clock lock: " + string.Join(", ", items.Select(i => Words(i.Key, i.Value))), reason,
            new() { ["writes"] = items.Select(i => TrialWrite(ClockLimitWrites.Kind, i.Key, i.Value)).ToList() });
    });

    [McpServerTool(Name = "set_clock_ratio", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set XBAR's clock ratio to the core (the driver's NVVDD/MSVDD clock propagation ratio, the original's --xbar-ratio) " +
        "as a new trial. 0.8999 on a 5090 (58976/65536): under load XBAR is held to about 0.9 x the core clock, which is " +
        "why adjust_xbar stops paying past +250 MHz unless the ratio and MSVDD's limit (set_voltage_boost) go up with it. A higher ratio lets XBAR follow the core closer (about +29 MHz of XBAR " +
        "per +0.01 at a 2900 MHz core) and asks MSVDD for the voltage that clock needs, so adjust_xbar's demand rules apply " +
        "to the clock it reaches. On the reference card 0.92 ran XBAR 2595 MHz against a 2850 MHz core at the 600 W wall " +
        "(2535 / 2865 at stock), MSVDD 980 → 1005 mV. Alone it can pull the core down where MSVDD holds XBAR (0.95 at REL 0: " +
        "the core -3.5 %; on 621.01 with MSVDD at stock, 0.925 and 0.95 cost it 2.0 and 2.4 %); with MSVDD REL +50 and XBAR " +
        "+400 / +50 mV (617.42), 0.95 made the lab's crossbar test 5-6 % faster and the memory's bandwidth 7.8 % higher " +
        "(tune_uncore finds the mix). Its gain in frames is unmeasured: judge by a benchmark that uses " +
        "memory. 0.5..1.0. keep_as_daily keeps it; back_to_daily, " +
        "end_session and a crash put the daily profile's back.")]
    public string SetClockRatio(
        [Description("The ratio to try, 0.5..1.0 (absolute).")] double ratio,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        if (ratio < 0.5 || ratio > 1.0) throw new ArgumentException("the ratio is 0.5..1.0");
        var now = card.WithDriver(ClockRatio.Read);
        uint raw = (uint)Math.Round(ratio * 65536);
        if (raw == now.Raw) throw new ArgumentException($"the ratio is {ClockRatio.Words(now.Raw)} already: nothing to try");
        return Trial($"XBAR/core clock ratio {ClockRatio.Words(now.Raw)} → {ClockRatio.Words(raw)}", reason,
            new() { ["writes"] = new[] { TrialWrite(ClockRatio.Kind, ClockRatio.Key, raw) } });
    });

    [McpServerTool(Name = "set_power_cap", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Set the additional board power request (F8, the CLI's --power-cap) as a new trial, beside the ordinary one " +
        "set_power_limit moves (FE). On a 5090 FE it does not exist until the first call creates it, and the driver offers " +
        "no way to delete one: from then on it can only go back to the board's maximum (600 W), which is where back_to_daily, " +
        "end_session, a crash and the agent disconnecting put it, until the next boot. The lower of FE and F8 holds (on the " +
        "reference card F8 500 W under FE 600 W held the board at 500 W), so at the board's maximum it limits nothing; " +
        "gpu_status's power limit shows a change a few seconds late. The board's range as the driver reports it (400-600 W on a " +
        "5090). Never kept. " + AsksItself)]
    public async Task<string> SetPowerCap(
        McpServer server,
        [Description("The F8 request, W.")] double watts,
        [Description("Why (what it is meant to show).")] string? reason = null,
        [Description(ConfirmCode)] string? confirm = null,
        CancellationToken cancellationToken = default)
    {
        if (OutsideBoard(watts) is { } outside) return Out(new { success = false, error = $"the request is {outside}" });
        return await Asked(server, string.Create(CultureInfo.InvariantCulture,
            $"Set the second board power request (F8) to {watts:0.#} W for a test: ending the test puts it back to the board's maximum, but once made the request itself cannot be deleted until the next restart."),
            confirm, cancellationToken, () =>
        {
            uint mw = (uint)Math.Round(watts * 1000);
            var f8 = card.WithDriver(PowerRequestsReader.Read).Additional;
            string from = f8.Present ? string.Create(CultureInfo.InvariantCulture, $"{f8.Mw / 1000.0:0.#} W") : "absent";
            return Trial(string.Create(CultureInfo.InvariantCulture, $"F8 power request {from} → {mw / 1000.0:0.#} W"), reason,
                new() { ["writes"] = new[] { new { kind = PowerCap.Kind, key = PowerCap.Key(PowerRequestKind.Additional), value = mw.ToString(CultureInfo.InvariantCulture), create = true } } });
        });
    }

    [McpServerTool(Name = "set_daily_power_limit", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Make this the daily power limit: it goes on now and at every start and after every driver reset. A trial's wall " +
        "(set_power_limit) is never kept, so this is how the daily one changes. The board's range as the driver reports it " +
        "(400-600 W on an RTX 5090, 575 W stock). Set it " +
        "before tuning the curve: the wall decides where on the curve heavy load runs. On the reference card the last 100 W " +
        "bought about 10 % frames (GTA V 78 → 86 fps from 500 to 600 W); 475-500 W is the efficient zone. While a trial runs " +
        "under its own wall, the daily one goes on when the trial ends. " + AsksItself)]
    public async Task<string> SetDailyPowerLimit(McpServer server, [Description("The daily power limit, W.")] double watts,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default)
    {
        if (OutsideBoard(watts) is { } outside) return Out(new { success = false, error = $"the daily power limit is {outside}" });
        return await Asked(server, string.Create(CultureInfo.InvariantCulture, $"Set your graphics card's daily power limit to {watts:0.#} W, now and after every start."),
            confirm, cancellationToken, () =>
        {
            uint mw = (uint)Math.Round(watts * 1000);
            var r = card.Broker(BrokerRequest.OpProfile, "wall", mw.ToString(CultureInfo.InvariantCulture));
            if (!r.Ok) return Reply(r);
            var after = ReadAfter(out var unread);
            return Out(new { success = true, daily_power_limit_w = mw / 1000.0, written = r.Written, power_limit_w = after?.Telemetry?.PowerEnforcedWatts, warning = unread });
        });
    }

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
            efficiency = Efficiency(loaded, fps?.AvgFps),
            curve_shift = CurveShift(curve, ReadCurveOrNull(), point?.index, loaded),
            fps = FrameFeed.Show(fps) ?? (object)new
            {
                unavailable = noFrames ?? (process != null ? $"no frames from {process}: is that the game's process name?" : "no application drew frames: was the game running?"),
            },
            fps_csv = frames?.Path,
            note = loaded.Count == 0 ? "no sample was under load: was the game running in the foreground?" : null,
        });
    });

    private sealed record WatchStat(double? clock_mhz, double? nvvdd_mv, double? msvdd_mv, double? xbar_mhz, double? board_w, double? temp_c, double? vram_used_gib, double? memory_c);

    private static WatchStat WatchStats(IReadOnlyList<Card.Sample> s, double q) => new(
        Pct(s.Select(x => (double?)x.Telemetry?.GraphicsClockMhz), q), Pct(s.Select(x => x.NvvddUv / 1000.0), q),
        Pct(s.Select(x => x.MsvddUv / 1000.0), q), Pct(s.Select(x => x.Mhz(AllClocksLayout.Xbar)), q),
        Pct(s.Select(x => x.Telemetry?.PowerWatts), q), Pct(s.Select(x => x.Telemetry?.TemperatureC), q),
        Pct(s.Select(x => Gib(x.Telemetry?.VramUsedBytes)), q), Pct(s.Select(x => x.MemoryC), q));

    private static double? Pct(IEnumerable<double?> xs, double q)
    {
        var v = xs.Where(x => x != null).Select(x => x!.Value).Order().ToList();
        return v.Count == 0 ? null : Math.Round(v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)], 1);
    }

    /// <summary>One trial with the V/F curve left as it is: the broker writes <paramref name="items"/> beside the curve
    /// on the card (written again when the memory offset is among them) and records the trial.</summary>
    private string Trial(string change, string? reason, Dictionary<string, object?> items, long? coreDeltaKhz = null)
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
        var after = ReadAfter(out var unread);
        bool? intact = after == null ? null : after.Curve.Offsets().SequenceEqual(before);
        if (after != null && coreDeltaKhz is { } delta)
        {
            try { intact = VfEffectiveCurve.Mismatch(look.Curve, after.Curve, delta) == null; }
            catch (InvalidDataException e) { intact = false; unread = e.Message; }
        }
        return Out(new
        {
            success = coreDeltaKhz == null || intact == true,
            change,
            written = r.Written,
            curve_intact = intact,
            warning = unread ?? (intact == false ? "the effective V/F curve did not verify after this write: call back_to_daily before going on" : null),
        });
    }
}
