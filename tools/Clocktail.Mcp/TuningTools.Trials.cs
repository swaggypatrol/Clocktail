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
/// Trials for controls exposed by the current GPU. Driver capabilities, readback, ownership and recovery apply
/// independently of board model; no card-specific tuning recipe is supplied.
/// </summary>
public sealed partial class TuningTools
{
    /// <summary>watch: the longest stretch one call samples (CLOCKTAIL_MCP_MAX_WATCH_S); it blocks for it.</summary>
    public static readonly int MaxWatchSeconds = Env("CLOCKTAIL_MCP_MAX_WATCH_S", 600);

    [McpServerTool(Name = "set_fan_curve", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set three encoded points on each editable fan policy as a trial. Use this board's own fan topology and RPM pairing; explicit RPM values are user choices. No stock-relative minimum duty or full-speed temperature is imposed.")]
    public string SetFanCurve(
        [Description("Three points degC:duty%: '50:20,79:60,86:100'; or 'stock' for the card's own.")] string curve,
        [Description("Why this curve (the temperature it is meant to hold at the wall).")] string? reason = null) => Guard(() =>
    {
        string text = FanCurveText.Stock;
        if (!FanCurveText.IsStock(curve))
        {
            var p = FanCurveText.Parse(curve);
            text = FanCurveText.Format(p);
        }
        return Trial($"fan curve → {text}", reason, new() { ["fan_curve"] = text });
    });

    [McpServerTool(Name = "set_fan_start", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set stopped fans' start and stop temperatures on editable policies as a trial. Read this board's existing zero-RPM behavior. There is no universal stock band, minimum temperature or mandatory band width; stop must be under start (or it is no band), both within 0..120 °C.")]
    public string SetFanStart(
        [Description("Temperature the fans start at, °C.")] double startC,
        [Description("Temperature they stop under, °C (default startC - 7).")] double? stopC = null,
        [Description("Why (what it is meant to hold).")] string? reason = null) => Guard(() =>
    {
        double stopValue = stopC ?? startC - 7;
        if (!double.IsFinite(startC) || !double.IsFinite(stopValue) || Math.Abs(startC) > 1000 || Math.Abs(stopValue) > 1000)
            throw new ArgumentException($"startC and stopC are temperatures in °C, {FanStopWrites.LowestC}..{FanStopWrites.HighestC}");
        decimal start = (decimal)startC, stop = (decimal)stopValue;
        var snap = card.WithDriver(FanPolicyReader.Read);
        var editable = snap.Info.Editable.Select(p => p.Index).ToList();
        if (editable.Count == 0) throw new InvalidOperationException("this card has no editable fan policy");
        string value = string.Create(CultureInfo.InvariantCulture, $"{stop:0.##}:{start:0.##}");
        var want = FanStopWrites.Parse(value)!.Value;
        if (FanStopWrites.Invalid(want) is { } why) throw new ArgumentException(why);
        string was = snap.ControlRaw is { } r0 ? FanStopWrites.Format(FanStopWrites.Read(r0, editable[0])) : "?";
        return Trial($"fans stop/start {was} → {value} °C", reason,
            new() { ["writes"] = editable.Select(i => TrialWrite(FanStopWrites.Kind, FanPolicyWrites.Key(i), value)).ToList() });
    });

    [McpServerTool(Name = "set_fan_speed", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Temporarily hold available fan channels at a user-selected duty, or use 'auto' to return them to the firmware curve. The broker uses the actual channel ranges. The service does not watch the temperature and take it back: the duty goes back with the trial (back_to_daily, end_session, a crash) and when the agent goes quiet. Never kept.")]
    public string SetFanSpeed(
        [Description("Duty in percent, 1..100, or 'auto'; subject to the driver's reported range.")] string percent,
        [Description("Why (the temperature or the test it is for).")] string? reason = null) => Guard(() =>
    {
        string p = percent.Trim().TrimEnd('%').Trim();
        uint pct = 0;
        if (!p.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && (!uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out pct) || pct == 0 || pct > 100))
            throw new ArgumentException("percent is 1..100 or 'auto'; the driver-reported fan range is checked by the broker");
        return Trial(pct == 0 ? "fans back to the fan curve" : $"fans held at {pct} %", reason, new() { ["fan_pct"] = pct });
    });

    [McpServerTool(Name = "set_memory_offset", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set this GPU's memory P0 offset as a trial, in MHz as the driver defines it. Use the reported range. The broker preserves and verifies the effective V/F curve across the memory write. Judge correctness, sustained performance and real workloads rather than copying a memory offset.")]
    public string SetMemoryOffset(
        [Description("The memory offset to try, MHz (absolute, not a delta).")] double mhz,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        var p0 = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Memory) ?? throw new InvalidOperationException("the memory offset is unreadable");
        int khz = ToInt32Units(mhz), now = p0.DeltaKhz;
        if (khz == now) throw new ArgumentException($"the memory offset is {now / 1000.0} MHz already: nothing to try");
        if (khz < p0.MinKhz || khz > p0.MaxKhz)
            throw new ArgumentException($"the driver's range is {p0.MinKhz / 1000.0}..{p0.MaxKhz / 1000.0} MHz");
        return Trial(string.Create(CultureInfo.InvariantCulture, $"memory offset {now / 1000.0:+0.#;-0.#;0} → {khz / 1000.0:+0.#;-0.#;0} MHz"),
            reason, new() { ["memory_khz"] = khz }, coreDeltaKhz: 0);
    });

    [McpServerTool(Name = "set_core_offset", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set this GPU's core P0 offset as a trial. It shares the reported offset budget with per-point V/F offsets; the broker preserves its shape and verifies the effective frequency change. No model-specific target or stable offset is assumed.")]
    public string SetCoreOffset(
        [Description("The core P0 offset to try, MHz (absolute, not a delta).")] double mhz,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        var p0 = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Core) ?? throw new InvalidOperationException("the core offset is unreadable");
        int khz = ToInt32Units(mhz), now = p0.DeltaKhz;
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
        "Independently adjust XBAR clock offset and/or voltage demand as a trial where this GPU exposes them. Use the driver's reported clock range; a demand past ±1000 mV is refused as a units slip, inside it the driver decides. No mandatory MHz/mV pairing is imposed. Measure effects on cache/memory performance, core clock and power.")]
    public string AdjustXbar(
        [Description("MHz to add to XBAR's clock offset (negative lowers); default 0.")] double offsetDeltaMhz = 0,
        [Description("mV to add to XBAR's voltage demand (negative lowers); default 0.")] double vdemandDeltaMv = 0,
        [Description("Why this step (what you saw, what you expect).")] string? reason = null) => Guard(() =>
    {
        if (offsetDeltaMhz == 0 && vdemandDeltaMv == 0) throw new ArgumentException("both deltas are 0: nothing to try");
        var (offNow, demandNow) = SettingsTools.Domain(card, ClockDomainId.Xbar);
        int off = checked(offNow + ToInt32Units(offsetDeltaMhz)), demand = checked(demandNow + ToInt32Units(vdemandDeltaMv));
        if (demand != demandNow && ClockVdemand.Implausible(demand) is { } slip) throw new ArgumentException(slip);
        var (min, max) = OffsetRange(ClockDomainId.Xbar);
        if (off > max * 1000L || off < min * 1000L)
            throw new ArgumentException($"the XBAR offset stays within {min}..+{max} MHz (it would be {off / 1000.0})");
        var items = new Dictionary<string, object?>();
        var parts = new List<string>();
        if (off != offNow) { items["xbar_khz"] = off; parts.Add(string.Create(CultureInfo.InvariantCulture, $"XBAR offset {offNow / 1000.0:+0.#;-0.#;0} → {off / 1000.0:+0.#;-0.#;0} MHz")); }
        if (demand != demandNow) { items["xbar_vdemand_uv"] = demand; parts.Add(string.Create(CultureInfo.InvariantCulture, $"XBAR voltage demand {demandNow / 1000.0:+0.#;-0.#;0} → {demand / 1000.0:+0.#;-0.#;0} mV")); }
        return Trial(string.Join(", ", parts), reason, items);
    });

    [McpServerTool(Name = "adjust_clock_domain", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Adjust an available XBAR, SYS or Video clock domain and/or its voltage demand, or core voltage demand only. The topology identifies its rail and capabilities. Core clock offsets use set_core_offset. No board-specific clock/voltage relationship is assumed.")]
    public string AdjustClockDomain(
        [Description("'xbar', 'sys', 'video', or 'core' (voltage demand only).")] string domain,
        [Description("MHz to add to the domain's clock offset (negative lowers); default 0.")] double offsetDeltaMhz = 0,
        [Description("mV to add to the domain's voltage demand (negative lowers); default 0.")] double vdemandDeltaMv = 0,
        [Description("Why this step (what you saw, what you expect).")] string? reason = null) => Guard(() =>
    {
        var d = domain.Trim().ToLowerInvariant() switch
        {
            "sys" => ClockDomainId.Sys,
            "video" => ClockDomainId.Video,
            "core" or "graphics" => ClockDomainId.Graphics,
            "xbar" => ClockDomainId.Xbar,
            _ => throw new ArgumentException("domain is 'xbar', 'sys', 'video' or 'core'"),
        };
        if (offsetDeltaMhz == 0 && vdemandDeltaMv == 0) throw new ArgumentException("both deltas are 0: nothing to try");
        if (d == ClockDomainId.Graphics && offsetDeltaMhz != 0) throw new ArgumentException("the core's clock offset is set_core_offset's (P0); here only its voltage demand moves");
        var (offNow, demandNow) = SettingsTools.Domain(card, d);
        int off = checked(offNow + ToInt32Units(offsetDeltaMhz)), demand = checked(demandNow + ToInt32Units(vdemandDeltaMv));
        if (demand != demandNow && ClockVdemand.Implausible(demand) is { } slip) throw new ArgumentException(slip);
        var (min, max) = OffsetRange(d);
        if (off != offNow && (off > max * 1000L || off < min * 1000L))
            throw new ArgumentException($"the {d} offset stays within the driver's {min}..+{max} MHz (it would be {off / 1000.0})");
        string name = d switch { ClockDomainId.Xbar => "XBAR", ClockDomainId.Sys => "SYS", ClockDomainId.Video => "Video", _ => "core" };
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

    internal static int ToInt32Units(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("the value must be finite");
        return checked((int)Math.Round(value * 1000));
    }

    private static uint ToUInt32Units(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("the value must be finite");
        return checked((uint)Math.Round(value * 1000));
    }

    [McpServerTool(Name = "set_voltage_boost", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Set the voltage-boost percentage through the path this GPU exposes. NVIDIA accepts or rejects it and readback verifies it. The actual voltage change per percentage depends on the GPU and driver; do not assume a fixed mV conversion.")]
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
        "Set a core clock cap, floor, V/F point lock or Boost Lock as a trial through the driver client-limit record. Use this GPU's actual curve; omitted entries stay unchanged and zero releases an entry. NVIDIA decides accepted combinations. The lock is a request, not a hard P-state lock: the driver still lowers the clock under it for power, temperature and voltage, and the other clock domains are not held with it; stress_test, watch and live_read report what actually ran against it (clock_locks). Only a cap is currently retained by keep_as_daily; preview exclusions before keeping.")]
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
        uint max = maxMhz is { } a ? ToUInt32Units(a) : Held(0), min = minMhz is { } b ? ToUInt32Units(b) : Held(1);
        uint voltage = Held(6);
        string? point = null;
        if (boostLock is { } bl) voltage = bl ? ClientLimitsLayout.BoostLockUv : voltage == ClientLimitsLayout.BoostLockUv ? 0 : voltage;
        if (lockMv is { } mv)
        {
            if (!double.IsFinite(mv)) throw new ArgumentException("lockMv must be finite");
            if (mv == 0) voltage = 0;
            else
            {
                var p = card.Read().Curve.Points.MinBy(x => Math.Abs((double)x.VoltageUv - mv * 1000)) ?? throw new InvalidOperationException("the V/F curve is unreadable");
                voltage = p.VoltageUv;
                point = string.Create(CultureInfo.InvariantCulture, $"point {p.Index}, {p.VoltageUv / 1000.0} mV, {p.EffectiveKhz / 1000.0} MHz");
            }
        }
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
        "Set the exposed clock propagation ratio as a trial, above 0 and representable in U16.16. Read the current value rather than assuming a universal stock ratio. NVIDIA decides acceptance; measure this GPU's resulting clocks and performance.")]
    public string SetClockRatio(
        [Description("The ratio to try (absolute, U16.16).")] double ratio,
        [Description("Why this step.")] string? reason = null) => Guard(() =>
    {
        if (!double.IsFinite(ratio) || Math.Round(ratio * 65536) is < ClockRatio.MinRaw or > uint.MaxValue)
            throw new ArgumentException("the ratio must be above 0 and fit the driver's U16.16 field");
        var now = card.WithDriver(ClockRatio.Read);
        uint raw = (uint)Math.Round(ratio * 65536);
        if (raw == now.Raw) throw new ArgumentException($"the ratio is {ClockRatio.Words(now.Raw)} already: nothing to try");
        return Trial($"XBAR/core clock ratio {ClockRatio.Words(now.Raw)} → {ClockRatio.Words(raw)}", reason,
            new() { ["writes"] = new[] { TrialWrite(ClockRatio.Kind, ClockRatio.Key, raw) } });
    });

    [McpServerTool(Name = "set_power_cap", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Set an additional board power request (F8) within this board's reported range after explicit approval. It may create a request that cannot be deleted until driver restart; trial cleanup returns it to this board's maximum, not a fixed wattage. It is not made daily." + " " + AsksItself)]
    public async Task<string> SetPowerCap(
        McpServer server,
        [Description("The F8 request, W.")] double watts,
        [Description("Why (what it is meant to show).")] string? reason = null,
        [Description(ConfirmCode)] string? confirm = null,
        CancellationToken cancellationToken = default)
    {
        if (OutsideBoard(watts) is { } outside) return Out(new { success = false, error = $"the request is {outside}" });
        uint mw = (uint)Math.Round(watts * 1000);
        return await Asked(server, PowerCapQuestion(mw), confirm, cancellationToken, () =>
        {
            var f8 = card.WithDriver(PowerRequestsReader.Read).Additional;
            string from = f8.Present ? string.Create(CultureInfo.InvariantCulture, $"{f8.Mw / 1000.0:0.#} W") : "absent";
            return Trial(string.Create(CultureInfo.InvariantCulture, $"F8 power request {from} → {mw / 1000.0:0.#} W"), reason,
                new() { ["writes"] = new[] { new { kind = PowerCap.Kind, key = PowerCap.Key(PowerRequestKind.Additional), value = mw.ToString(CultureInfo.InvariantCulture), create = true } } });
        });
    }

    /// <summary>set_power_cap's question, with the request to the milliwatt it writes: the confirm code is bound to the
    /// question's words, so a yes to one wattage is never a yes to another that rounds to the same tenth of a watt.</summary>
    public static string PowerCapQuestion(uint mw) => string.Create(CultureInfo.InvariantCulture,
        $"Set the second board power request (F8) to {mw / 1000.0:0.###} W for a test: ending the test puts it back to the board's maximum, but once made the request itself cannot be deleted until the next restart.");

    /// <summary>set_daily_power_limit's question, with the limit to the milliwatt it writes (see <see cref="PowerCapQuestion"/>).</summary>
    public static string DailyPowerLimitQuestion(uint mw) => string.Create(CultureInfo.InvariantCulture,
        $"Set your graphics card's daily power limit to {mw / 1000.0:0.###} W, now and after every start.");

    [McpServerTool(Name = "set_daily_power_limit", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "After explicit approval, create or update this user's daily ordinary power limit using this board's reported range. No stock wattage or default daily wall is supplied. The service reads back and reapplies only the user-selected value." + " " + AsksItself)]
    public async Task<string> SetDailyPowerLimit(McpServer server, [Description("The daily power limit, W.")] double watts,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default)
    {
        if (OutsideBoard(watts) is { } outside) return Out(new { success = false, error = $"the daily power limit is {outside}" });
        uint mw = (uint)Math.Round(watts * 1000);
        return await Asked(server, DailyPowerLimitQuestion(mw), confirm, cancellationToken, () =>
        {
            var r = card.Broker(BrokerRequest.OpProfile, "wall", mw.ToString(CultureInfo.InvariantCulture));
            if (!r.Ok) return Reply(r);
            var after = ReadAfter(out var unread);
            return Out(new { success = true, daily_power_limit_w = mw / 1000.0, written = r.Written, power_limit_w = after?.Telemetry?.PowerEnforcedWatts, warning = unread });
        });
    }

    [McpServerTool(Name = "watch", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Sample this GPU and the user's workload for a stretch, including operating points, limit reasons, power, temperature and available frame statistics. Record evidence for this GPU rather than comparing it with another board's clocks.")]
    public string Watch(
        [Description("How long to sample, seconds (10..600; default 120).")] int seconds = 120,
        [Description("What is being played (game, scene, settings), for the log.")] string? note = null,
        [Description("The game's process name for the frame rate (e.g. \"Cyberpunk2077\"); omit for the application that drew the most frames.")] string? process = null) => Guard(() =>
    {
        if (seconds < 10 || seconds > MaxWatchSeconds) throw new ArgumentException($"seconds is 10..{MaxWatchSeconds} (CLOCKTAIL_MCP_MAX_WATCH_S)");
        var look = card.Read();
        var curve = look.Curve;
        var locks = card.ClockLocks();
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
        var loaded = samples.Where(s => s.Telemetry is { } t && LiveWatch.IsLoaded(t)).ToList();   // the live watch's rule
        double? atWall = loaded.Count == 0 ? null : Math.Round(100.0 * loaded.Count(s =>
            s.Telemetry is { PowerWatts: { } w, PowerEnforcedWatts: { } l } && w >= l - Limiter.WallBandW) / loaded.Count);
        double? p50Mv = Pct(loaded.Select(s => s.NvvddUv / 1000.0), 0.5);
        var point = p50Mv is { } mv && Nearest(curve, (uint)(mv * 1000)) is { } pt
            ? new { index = pt.Index, mv = pt.VoltageUv / 1000.0, curve_mhz = pt.EffectiveKhz / 1000.0 } : null;
        var reasons = loaded.SelectMany(s => s.Telemetry?.ThrottleReasons ?? []).Distinct().ToList();
        var locksMet = LocksMet(locks, loaded.Select(s => (double?)s.Telemetry?.GraphicsClockMhz), curve, look.NvvddMaxUv);
        if (shared == null) frames?.Settle(TimeSpan.FromSeconds(3));   // its own capture ends by itself: let it finish writing
        var fps = frames?.Total();
        var p50 = WatchStats(loaded, 0.5);
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Watched {seconds} s: {loaded.Count} of {samples.Count} samples under load. P50 {p50.clock_mhz} MHz @ NVVDD {p50.nvvdd_mv} mV (curve point {point?.index}), " +
            $"P90 {WatchStats(loaded, 0.9).clock_mhz} MHz, P10 {WatchStats(loaded, 0.1).clock_mhz} MHz; MSVDD {p50.msvdd_mv} mV, XBAR {p50.xbar_mhz} / SYS {p50.sys_mhz} MHz, board {p50.board_w} W, " +
            $"{atWall} % of the time on the wall, max {loaded.Max(s => s.Telemetry?.TemperatureC)} °C, VRAM {p50.vram_used_gib} GiB; limit reasons [{string.Join(", ", reasons)}]")
            + (frames != null ? $"; {FrameFeed.Words(fps)}" : "") + LocksWords(locksMet) + LocksUnreadWords(locks)
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
            efficiency = WatchEfficiency(samples, loaded, fps?.AvgFps),
            curve_shift = CurveShift(curve, ReadCurveOrNull(), point?.index, loaded),
            clock_locks = locksMet,
            clock_locks_unread = locks == null ? LocksUnread : null,
            fps = FrameFeed.Show(fps) ?? (object)new
            {
                unavailable = noFrames ?? (process != null ? $"no frames from {process}: is that the game's process name?" : "no application drew frames: was the game running?"),
            },
            fps_csv = frames?.Path,
            note = loaded.Count == 0 ? "no sample was under load: was the game running in the foreground?" : null,
        });
    });

    /// <summary>
    /// watch's efficiency: its frame rate is the whole capture's (menus and loading screens included), so with one, the
    /// power it is set against is the whole watch's too; without one, the samples under load.
    /// </summary>
    public static object? WatchEfficiency(IReadOnlyList<Card.Sample> all, IReadOnlyList<Card.Sample> loaded, double? wholeFps) =>
        wholeFps == null ? Efficiency(loaded, null)
        : Efficiency(all, wholeFps, over: "the whole watch, idle samples too: its frame rate covers every frame of it, menus and loading screens included");

    private sealed record WatchStat(double? clock_mhz, double? nvvdd_mv, double? msvdd_mv, double? xbar_mhz, double? sys_mhz, double? video_mhz,
        double? board_w, double? temp_c, double? vram_used_gib, double? memory_c);

    private static WatchStat WatchStats(IReadOnlyList<Card.Sample> s, double q) => new(
        Pct(s.Select(x => (double?)x.Telemetry?.GraphicsClockMhz), q), Pct(s.Select(x => x.NvvddUv / 1000.0), q),
        Pct(s.Select(x => x.MsvddUv / 1000.0), q), Pct(s.Select(x => x.Mhz(AllClocksLayout.Xbar)), q),
        Pct(s.Select(x => x.Mhz(AllClocksLayout.Sys)), q), Pct(s.Select(x => x.Mhz(AllClocksLayout.Video)), q),
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
