using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Clocktail.Core.Gpu;
using Clocktail.Domain;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using ModelContextProtocol.Server;
using Rail = Clocktail.Domain.Rails.Rail;
using RailOffsets = Clocktail.Domain.Rails.RailOffsets;

namespace Clocktail.Mcp;

/// <summary>
/// The card as an agent with no other window on it needs to see it: every setting Clocktail can write, as it is on the
/// card now, beside stock and the daily profile; and what holds the clock back right now (the tray's main readout).
/// Read-only: the driver is only read, in the card's lane, and the broker is only asked to show.
/// </summary>
[McpServerToolType]
public sealed partial class SettingsTools(Card card)
{
    /// <summary>The limiter's samples: readings jitter (a game's load moves by the frame), so it takes the majority.</summary>
    public const int LimiterSamples = 5;
    public static readonly TimeSpan LimiterGap = TimeSpan.FromMilliseconds(200);

    /// <summary>Compact, and an unknown or absent value is left out rather than spelled null: these answers are long.</summary>
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly ClockDomainId[] Domains = [ClockDomainId.Xbar, ClockDomainId.Sys, ClockDomainId.Video];

    [McpServerTool(Name = "card_settings", ReadOnly = true, OpenWorld = false), Description(
        "Every setting Clocktail can write, as it is on the card now: the power limit (the FE request) and the board's " +
        "range, the P0 core/memory offsets, the Core V/F curve (offsets in kHz as runs offset*count, how many points are " +
        "offset, its top below NVVDD MAX and its knee), NVVDD/MSVDD offsets per slot and the limits they give, XBAR/SYS/" +
        "Video clock offsets and voltage demands with each domain's live clock, voltage boost, fans (each channel's mode, " +
        "duty and rpm; the firmware fan curves as degC:duty%:rpm), the NVVDD/MSVDD current limits, the daily profile and the " +
        "experiment session. stock_* is what the card held before Clocktail first wrote the setting (the ownership " +
        "journal), or the value now where Clocktail holds nothing; daily_* is the daily profile's value, absent where it " +
        "keeps none. journal_holds names the settings Clocktail has written and gives back on recovery; daily_profile is " +
        "absent when none is kept, session when none is open. daily_differs_on_card lists what the daily profile keeps that " +
        "the card does not hold now ([] = the card is on it; absent when none is kept, it is suspended or a trial is in " +
        "force). power.firmware_w is the board limit the firmware enforces (its power-policy record), which must equal " +
        "limit_w, the request. current_limits.policies is the driver's whole power-policy table: what each record limits (named only " +
        "where measured), the channel it watches, its range, the limit in force and the reading now with use_pct; one near " +
        "100 % under load is one that can hold the clock. controls is the capability map: each control, the MCP tool that sets it (null: none does), whether it can " +
        "be set on this card and driver, and why not. A section that cannot be read is {\"error\"} and the rest still answer. Read-only.")]
    public string CardSettings() => Guard(() =>
    {
        // What every section compares against comes first, and fails soft like a section: without it only stock_* and
        // daily_* go missing.
        IReadOnlyDictionary<string, string> writes = new Dictionary<string, string>();
        IReadOnlyDictionary<string, string>? originals = null;
        object? daily = null, held = null, lastCrash = null;
        try { writes = Daily(out daily); } catch (Exception e) when (Soft(e)) { daily = new { error = e.Message }; }
        try { originals = Held(out held, out lastCrash); } catch (Exception e) when (Soft(e)) { held = new { error = e.Message }; }
        var c = new Context(writes, originals);
        Card.Sample? sample = null;
        try { sample = card.ReadSample(); } catch (Exception e) when (Soft(e)) { }
        var t = sample?.Telemetry;
        RailsSnapshot? rails = null;
        string? railsError = null;
        try { rails = card.WithDriver(RailsReader.Read); } catch (Exception e) when (Soft(e)) { railsError = e.Message; }
        var session = card.Broker(BrokerRequest.OpExperiment, "show");

        string json = Out(new
        {
            gpu = card.Name,
            driver = VerifiedOn.Driver(card.DriverVersion),
            vbios = card.Vbios,
            tested_on = VerifiedOn.Describe(card.DriverVersion, card.Name, card.Vbios),
            other_gpus = card.OtherGpus.Count == 0 ? null : card.OtherGpus,
            at = DateTimeOffset.Now,
            // Locks in the driver's client clock-limit record: NVML's locked clocks, a V/F point lock, Boost Lock. Clocktail
            // sets none, so any one here was set by another tool and outlives it.
            clock_locks = Section(() => card.WithDriver(ClientLimitsLayout.Read).Select(h => new { entry = h.Domain, mode = h.Mode, value = h.Value, what = h.Describe() }).ToList()),
            tdr = Section(() => (OperatingSystem.IsWindows() ? Clocktail.Domain.Platform.TdrSettings.Read() : null) is { } tdr
                ? new { delay_s = tdr.Delay, level = tdr.LevelOrDefault, set_delay = tdr.DelayS != null, what = tdr.Describe(), safe_to_map = !tdr.Unsafe }
                : throw new InvalidOperationException("the GraphicsDrivers key could not be read")),
            perf_decrease = Section(() => card.PerfDecrease() is { } bits
                ? new { bits = $"0x{bits:x}", reasons = Card.PerfDecreaseReasons(bits) }
                : throw new InvalidOperationException("GetPerfDecreaseInfo did not answer")),
            temperature = Section(() =>
            {
                var (max, slow, shut) = card.TemperatureLimits();
                return new { now_c = t?.TemperatureC, margin_c = t?.TempMarginC, max_operating_c = max, slowdown_c = slow, shutdown_c = shut };
            }),
            power = Section(() => Power(c, t)),
            clock_offsets = Section(() => P0Offsets(c)),
            vf_curve = Section(() => Curve(c, rails)),
            rails = Section(() => Rails(c, rails ?? throw new InvalidOperationException(railsError))),
            clock_domains = Section(() => ClockDomains(c, rails, sample)),
            voltage_boost = Section(() => Boost(c)),
            perf_limits = Section(() => PerfLimitsView(card.WithDriver(PerfLimits.Read))),
            clock_ratio = Section(() =>
            {
                var cr = card.WithDriver(ClockRatio.Read);
                return new { ratio = Math.Round(cr.Ratio, 4), raw = cr.Raw, stock_raw = c.Stock(ClockRatio.Kind, ClockRatio.Key, Str((int)cr.Raw)), daily_raw = c.Daily(ClockRatio.Kind, ClockRatio.Key) };
            }),
            controls = Section(() => Controls.Map(card.WithDriver(Capabilities.Probe), WritePolicySet.Default)
                .Select(x => new { control = x.Name, tool = x.Tool, can = x.Can, why = x.Why }).ToList()),
            fans = new
            {
                nvml_fan_pct = t?.FanPercent,
                channels = Section(() => FanChannels(c)),
                curves = Section(() => FanCurves(c)),
            },
            current_limits = Section(() => Limits(c)),
            daily_profile = daily,
            journal_holds = held,
            last_crash = lastCrash,
            session = Section(() => Session(session)),
            trial_in_force = Trial(session),
        });
        using var doc = JsonDocument.Parse(json);
        if (DailyOffCard(doc.RootElement) is not { } off) return json;
        var node = JsonNode.Parse(json)!.AsObject();
        node["daily_differs_on_card"] = new JsonArray([.. off.Select(x => (JsonNode?)x)]);
        return node.ToJsonString(Json);
    });

    [McpServerTool(Name = "limiter", ReadOnly = true, OpenWorld = false), Description(
        "What holds the core clock back right now, judged on 5 samples 200 ms apart (the verdict most of them agree on), the " +
        "instantaneous power watched every 20 ms between them: power_wall (board power within 10 W of the limit in force, or " +
        "sw_power_cap with instantaneous peaks within 15 W of it: the wall answers short peaks, so a game's average can sit " +
        "30-60 W under it), current_limit (sw_power_cap with NVVDD or MSVDD drawing within 3 % of its current limit), " +
        "power_policy (sw_power_cap with none of those: a sub-policy), voltage_limit (NVVDD within 12.5 mV of its MAX; the driver's " +
        "reliability_voltage reason only when NVVDD is unread, since a 5090 sets it at idle), curve_cap (the clock within 15 MHz of the highest frequency the V/F curve reaches " +
        "below MAX: the curve's own top), thermal and hardware_slowdown (NVML's own reasons), idle (under 120 W, or gpu_idle " +
        "at under 10 % utilization) or no_limit (loaded, nothing reached: the load asks for less). Several can hold at once " +
        "(limiters, the wall first). readings are the medians of the samples behind the verdict, with the margins that " +
        "decided it. perf_limits is the driver's own arbitration for GPC, XBAR and memory: the ceiling it allows and the " +
        "floor it holds, each with the limit that set it (a clock lock, the reliability voltage limit at the curve's top, a " +
        "thermal policy…); the power wall acts below these, so a card at its wall runs under its ceiling. Run it with the " +
        "game or stress test loaded: at idle the answer is idle.")]
    public string WhatLimits() => Guard(() =>
    {
        // The ceilings do not move in a second: the curve and MAX once, then the light sample five times.
        var look = card.Read();
        uint? max = look.NvvddMaxUv;
        var top = Limiter.CurveTop(look.Curve.Points, max);
        var samples = new List<(Card.Sample S, LimiterReading R, IReadOnlyList<ClockLimit> Held)>();
        for (int i = 0; i < LimiterSamples; i++)
        {
            double? peak = card.PeakInstantW(LimiterGap);   // the gap is watched: the wall answers peaks the average hides
            var s = card.ReadSample();
            if (s.Reading(peak) is not { } r) continue;
            samples.Add((s, r, Limiter.Holding(r, max, top?.EffectiveKhz)));
        }
        var verdict = Limiter.Majority(samples.Select(x => x.Held).ToList())
            ?? throw new InvalidOperationException("NVML is unreadable: no sample to judge (board power, the clock and the limit reasons all come from it)");
        string key = Limiter.Key(verdict.Held);
        var behind = samples.Where(x => Limiter.Key(x.Held) == key).ToList();

        double? board = Median(behind.Select(x => x.R.BoardW)), limit = Median(behind.Select(x => x.R.LimitW));
        double? nvvdd = Median(behind.Select(x => x.R.NvvddUv / 1000.0)), clock = Median(behind.Select(x => (double?)x.R.ClockMhz));
        double? maxMv = max / 1000.0, topMhz = top?.EffectiveKhz / 1000.0;
        IReadOnlyList<DomainLimits>? arbitration = null;
        try { arbitration = card.WithDriver(PerfLimits.Read); } catch (Exception e) when (Soft(e)) { }
        string? lockNote = arbitration?.FirstOrDefault()?.Ceiling is { } gc && gc.By.StartsWith("CLIENT_", StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"A clock lock caps the core at {gc.Mhz:0} MHz ({gc.By}).") : null;
        return Out(new
        {
            limiter = Limiter.Id(verdict.Held[0]),
            limiters = verdict.Held.Select(Limiter.Id),
            explanation = string.Join(" ", [.. verdict.Held.Select(Limiter.Explain), .. lockNote is null ? [] : new[] { lockNote }]),
            perf_limits = arbitration is null ? null : PerfLimitsView(arbitration),
            votes = $"{verdict.Votes}/{samples.Count}",
            readings = new
            {
                board_w = board,
                power_limit_w = limit,
                power_headroom_w = R(limit - board),
                peak_w = R(behind.Max(x => x.R.PeakW)),
                topology_w = Topology(look.Telemetry?.PowerDefaultWatts),
                peak_headroom_w = R(limit - behind.Max(x => x.R.PeakW)),
                nvvdd_a = Median(behind.Select(x => x.R.NvvddA)),
                nvvdd_limit_a = Median(behind.Select(x => x.R.NvvddLimitA)),
                msvdd_a = Median(behind.Select(x => x.R.MsvddA)),
                msvdd_limit_a = Median(behind.Select(x => x.R.MsvddLimitA)),
                nvvdd_mv = nvvdd,
                nvvdd_max_mv = maxMv,
                nvvdd_below_max_mv = R(maxMv - nvvdd),
                clock_mhz = clock,
                curve_top_mhz = topMhz,
                curve_top_mv = top?.VoltageUv / 1000.0,
                clock_below_curve_top_mhz = R(topMhz - clock),
                util_pct = Median(behind.Select(x => (double?)x.R.UtilPct)),
                temp_c = Median(behind.Select(x => x.S.Telemetry?.TemperatureC)),
                temp_margin_c = Median(behind.Select(x => (double?)x.S.Telemetry?.TempMarginC)),
                reasons = behind.SelectMany(x => x.R.Reasons).Distinct(),
            },
            samples = samples.Select(x => new
            {
                verdict = Limiter.Key(x.Held),
                board_w = R(x.R.BoardW),
                peak_w = R(x.R.PeakW),
                nvvdd_mv = x.R.NvvddUv / 1000.0,
                clock_mhz = x.R.ClockMhz,
                util_pct = x.R.UtilPct,
                reasons = x.R.Reasons.Count == 0 ? null : x.R.Reasons,
            }),
        });
    });

    /// <summary>The driver's power topology now, in W (both entries are board power; <see cref="PowerTopologyLayout"/>):
    /// the power controller's own view beside NVML's. Null where it cannot be read.</summary>
    private object? Topology(double? defaultW)
    {
        if (defaultW is not { } d) return null;
        try
        {
            var e = card.WithDriver(PowerSplitReader.ReadTopology);
            return e.ToDictionary(x => x.Id switch { 0 => "steady", 1 => "wider_spread", var id => $"entry_{id}" }, x => Math.Round(PowerTopologyLayout.Watts(x.Raw, d), 1));
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>What every section compares against: the daily profile's writes and the journal's originals, both by
    /// "kind/key" in the write path's own encodings. Originals are null when the journal could not be read.</summary>
    private sealed class Context(IReadOnlyDictionary<string, string> daily, IReadOnlyDictionary<string, string>? originals)
    {
        public string? Daily(string kind, string key) => daily.GetValueOrDefault($"{kind}/{key}");

        /// <summary>Before Clocktail first wrote the cell: the journal's original, or <paramref name="now"/> when it holds
        /// nothing there (Clocktail never wrote it). Unknown when the journal could not be read.</summary>
        public string? Stock(string kind, string key, string now) => originals == null ? null : originals.GetValueOrDefault($"{kind}/{key}") ?? now;
    }

    private object Power(Context c, TelemetrySnapshot? t)
    {
        var p = card.WithDriver(PowerRequestsReader.Read);
        string fe = PowerCap.Key(PowerRequestKind.Ordinary);
        uint? firmware = null;
        try { firmware = card.WithDriver((hw, s) => BoardWall.Enforced(hw, s, p.Board)); }
        catch (Exception e) when (Soft(e)) { }
        return new
        {
            limit_w = p.Ordinary.Mw / 1000.0,
            enforced_w = t?.PowerEnforcedWatts,
            firmware_w = firmware / 1000.0,
            f8_cap_w = p.Additional.Mw / 1000.0,
            board_min_w = p.Board.MinMw / 1000.0,
            board_max_w = p.Board.MaxMw / 1000.0,
            stock_w = p.Board.DefaultMw / 1000.0,
            daily_w = Scaled(c.Daily(PowerCap.Kind, fe), 1000),
        };
    }

    private object P0Offsets(Context c)
    {
        var p = card.WithDriver(Pstates20Reader.Read);
        object? Entry(ClockDomain d)
        {
            if (p.P0(d) is not { } e) return null;
            string key = Clocktail.Domain.Clocks.ClockOffsets.Key(d);
            return new
            {
                offset_mhz = e.DeltaKhz / 1000.0,
                min_mhz = e.MinKhz / 1000.0,
                max_mhz = e.MaxKhz / 1000.0,
                editable = e.Editable && p.Editable,
                stock_mhz = Scaled(c.Stock(Clocktail.Domain.Clocks.ClockOffsets.Kind, key, Str(e.DeltaKhz)), 1000),
                daily_mhz = Scaled(c.Daily(Clocktail.Domain.Clocks.ClockOffsets.Kind, key), 1000),
            };
        }
        return new { core = Entry(ClockDomain.Core), memory = Entry(ClockDomain.Memory) };
    }

    private object Curve(Context c, RailsSnapshot? rails)
    {
        var s = card.WithDriver(VfReader.Read);
        var offsets = s.Offsets();
        uint? max = rails?.Get(Rail.Nvvdd)?.Limits.MaxUv;
        var top = Limiter.CurveTop(s.Points, max);
        bool flat = top != null && s.Points.Any(p => p.Index > top.Index && (max == null || p.VoltageUv <= max));
        int knee = VfShapes.Knee(s, max);
        string now = VfOffsets.Encode(offsets), stock = c.Stock(VfWrites.Kind, VfWrites.CoreKey, now) ?? "", daily = c.Daily(VfWrites.Kind, VfWrites.CoreKey) ?? "";
        int[]? dailyOffsets = null;
        try { if (daily.Length > 0) dailyOffsets = VfOffsets.Decode(daily); } catch (FormatException) { }
        return new
        {
            offsets_khz = now,
            points = s.EditableCount,
            offset_points = offsets.Count(o => o != 0),
            min_offset_mhz = offsets.Min() / 1000.0,
            max_offset_mhz = offsets.Max() / 1000.0,
            voltage_demand_mv = s.DemandShiftUv == 0 ? (double?)null : s.DemandShiftUv / 1000.0,
            // The highest clock the curve reaches up to MAX, at the lowest voltage that reaches it; flat when points above
            // it (still under MAX) give no more: the curve is cut flat there.
            top = top == null ? null : new
            {
                index = top.Index,
                mv = top.VoltageUv / 1000.0,
                mhz = top.EffectiveKhz / 1000.0,
                flat,
            },
            nvvdd_max_mv = max / 1000.0,
            knee = new { index = knee, mv = s.Points[knee].VoltageUv / 1000.0 },
            stock_offsets_khz = stock.Length == 0 ? null : stock,
            equals_stock = stock.Length == 0 ? (bool?)null : stock == now,
            daily_offsets_khz = daily.Length == 0 ? null : daily,
            points_off_daily = dailyOffsets == null ? (int?)null : offsets.Where((o, i) => o != dailyOffsets.ElementAtOrDefault(i)).Count(),
        };
    }

    private static object Rails(Context c, RailsSnapshot s)
    {
        object One(RailRecord r)
        {
            string key = RailWrites.Key(r.Rail);
            // MAX = min(REL, ALT/OP when reported, OV): the slots equal to it are the ones in force.
            var binding = new[] { (RailSlot.Rel, r.Limits.RelUv), (RailSlot.Alt, r.Limits.AltOpUv), (RailSlot.Ov, r.Limits.OvUv) }
                .Where(x => x.Item2 != 0 && x.Item2 == r.Limits.MaxUv).Select(x => x.Item1.ToString().ToLowerInvariant()).ToList();
            return new
            {
                offsets_mv = Offsets(r.Offsets),
                limits_mv = new { rel = Mv(r.Limits.RelUv), alt_op = Mv(r.Limits.AltOpUv), ov = Mv(r.Limits.OvUv), max = Mv(r.Limits.MaxUv), vmin = Mv(r.Limits.VminUv) },
                max_set_by = binding,
                stock_offsets_mv = Offsets(c.Stock(RailWrites.Kind, key, RailWrites.Encode(r.Offsets))),
                daily_offsets_mv = Offsets(c.Daily(RailWrites.Kind, key)),
                not_writable = r.Writable ? null : r.NotWritableReason,
            };
        }
        return s.Rails.ToDictionary(r => RailWrites.Key(r.Rail), One);
    }

    private object ClockDomains(Context c, RailsSnapshot? rails, Card.Sample? sample)
    {
        var topo = card.WithDriver(ClockDomainOffsets.ReadTopology);
        var live = new Dictionary<ClockDomainId, int> { [ClockDomainId.Xbar] = AllClocksLayout.Xbar, [ClockDomainId.Sys] = AllClocksLayout.Sys, [ClockDomainId.Video] = AllClocksLayout.Video };
        return Domains.ToDictionary(d => ClockDomainOffsets.Key(d), d => Section(() =>
        {
            var t = ClockDomainsParser.Find(topo, (uint)d);
            var e = card.WithDriver((hw, s) => ReadControl(hw, s, t));
            string key = ClockDomainOffsets.Key(d);
            // The demand goes to the one rail the domain's topology names; a domain may offer none.
            int? slot = null;
            string? noDemand = null;
            if (rails == null) noDemand = "the rails are unreadable, so the rail this domain demands from is unknown";
            else try { slot = ClockVdemand.Slot(t, ClockVdemand.RailInfo(rails)); } catch (InvalidDataException x) { noDemand = x.Message; }
            int? demand = slot is { } k ? (k == 0 ? e.VdemandNvvddUv : e.VdemandMsvddUv) : null;
            return new
            {
                offset_mhz = e.OffsetKhz / 1000.0,
                min_mhz = t.MinOffsetMhz,
                max_mhz = t.MaxOffsetMhz,
                clock_mhz = sample?.Mhz(live[d]),
                vdemand_mv = demand / 1000.0,
                vdemand_rail = slot switch { 0 => "nvvdd", 1 => "msvdd", _ => null },
                vdemand_unavailable = noDemand,
                stock_offset_mhz = Scaled(c.Stock(ClockDomainOffsets.Kind, key, Str(e.OffsetKhz)), 1000),
                stock_vdemand_mv = demand is { } dv ? Scaled(c.Stock(ClockVdemand.Kind, key, Str(dv)), 1000) : null,
                daily_offset_mhz = Scaled(c.Daily(ClockDomainOffsets.Kind, key), 1000),
                daily_vdemand_mv = Scaled(c.Daily(ClockVdemand.Kind, key), 1000),
            };
        }));
    }

    private object Boost(Context c)
    {
        var o = card.WithDriver((hw, s) => BoostReader.Observe(hw, s));
        return new
        {
            independent_pct = o.IndependentPercent,
            legacy_pct = (uint)o.LegacyPercent,
            stock_independent_pct = o.IndependentPercent is { } i ? Scaled(c.Stock(VoltBoost.Kind, "independent", Str(i)), 1) : null,
            stock_legacy_pct = Scaled(c.Stock(VoltBoost.Kind, "legacy", Str(o.LegacyPercent)), 1),
            daily_independent_pct = Scaled(c.Daily(VoltBoost.Kind, "independent"), 1),
            daily_legacy_pct = Scaled(c.Daily(VoltBoost.Kind, "legacy"), 1),
        };
    }

    /// <summary>Each fan channel: Status's level is the duty in % (0 with the fan stopped, 30 the least it spins at on the
    /// 5090) and its reading the rpm (stopped: 0 % and 0 rpm, NVML 0 %; spinning at idle: 30 % and 1101 rpm).</summary>
    private object FanChannels(Context c)
    {
        var snap = card.WithDriver(FanReader.Snapshot);
        return snap.Channels.Select(ch =>
        {
            string key = FanControl.Key(ch.Id);
            return new
            {
                id = ch.Id,
                // Auto: the firmware's curves drive it; manual: the fixed duty Clocktail (or another tool) set.
                mode = ch.Control.State.Manual ? "manual" : "auto",
                manual_duty_pct = ch.Control.State.Manual ? ch.Control.State.Level : (uint?)null,
                duty_pct = ch.Status.Level,
                rpm = ch.Status.Reading,
                min_duty_pct = ch.Status.MinLevel,
                max_duty_pct = ch.Status.MaxLevel,
                stock = c.Stock(FanControl.Kind, key, ch.Control.State.ToString()),
                daily = c.Daily(FanControl.Kind, key),
            };
        }).ToList();
    }

    private object FanCurves(Context c)
    {
        var snap = card.WithDriver(FanPolicyReader.Read);
        return snap.Info.Policies.Select(p =>
        {
            var ctl = snap.Policies.FirstOrDefault(x => x.Index == p.Index);
            var st = snap.Status.FirstOrDefault(x => x.Index == p.Index);
            string key = FanPolicyWrites.Key(p.Index);
            return new
            {
                index = p.Index,
                editable = p.Editable,
                curve = ctl?.Curve,
                // The temperature the curve follows (filtered) and the rpm it asks for now, 0 while the fans are stopped.
                curve_temp_c = st == null ? (double?)null : Math.Round(st.CurveTemperatureC, 1),
                target_rpm = st?.TargetRpm,
                stock_curve = ctl == null ? null : c.Stock(FanPolicyWrites.Kind, key, ctl.Curve),
                daily_curve = c.Daily(FanPolicyWrites.Kind, key),
            };
        }).ToList();
    }

    /// <summary>The driver's arbitration per domain, in MHz, with the limit that set each bound and what it is.</summary>
    public static object PerfLimitsView(IReadOnlyList<DomainLimits> domains) => domains.Select(d => new
    {
        domain = d.Domain,
        ceiling_mhz = d.Ceiling?.Mhz, ceiling_by = d.Ceiling?.By, ceiling_means = d.Ceiling is { } c ? PerfLimits.Means(c.By) : null,
        floor_mhz = d.Floor?.Mhz, floor_by = d.Floor?.By,
    }).ToList();

    private object Limits(Context c)
    {
        var (limits, readings, control) = card.WithDriver((hw, s) =>
        {
            var l = CurrentLimitsReader.Read(hw, s);
            byte[]? all = null;
            try { all = PolicyTable.Control(hw, s, l.Records); } catch (InvalidOperationException) { }
            return (l, CurrentLimitsReader.Monitor(hw, s, l.Channels), all);
        });
        object One(Rail r)
        {
            if (limits.Get(r) is not { } l) return new { error = limits.Missing.GetValueOrDefault(r) ?? "no current-limit record" };
            var o = limits.Output(r, readings);
            return new
            {
                limit_a = l.LimitMa / 1000.0,
                stock_a = l.DefaultMa / 1000.0,
                driver_min_a = l.MinMa / 1000.0,
                driver_max_a = l.MaxMa / 1000.0,
                now_a = o == null ? (double?)null : Math.Round(o.Ma / 1000.0, 1),
                now_w = o == null ? (double?)null : Math.Round(o.Mw / 1000.0, 1),
                daily_a = Scaled(c.Daily(CurrentLimitWrites.Kind, CurrentLimitWrites.Key(r)), 1000),
            };
        }
        var input = BoardInput.From(limits, readings);
        return new
        {
            nvvdd = One(Rail.Nvvdd), msvdd = One(Rail.Msvdd),
            input = input == null ? null : new
            {
                connector_w = Round1(input.ConnectorW), pcie_slot_w = Round1(input.PcieSlotW), input_a = Round1(input.InputA),
                input_limit_a = input.InputLimitA, concern = input.Concern(),
            },
            policies = control == null ? null : Policies(PolicyTable.Rows(limits, control, readings)),
        };
    }

    /// <summary>The driver's whole power-policy table, in W or A: what each record limits, the channel it watches, its
    /// bounds, the limit in force and the reading now.</summary>
    public static object Policies(IReadOnlyList<PolicyRow> rows) => rows.Select(p =>
    {
        double? U(long? v) => v is { } x ? Math.Round(x / 1000.0, 1) : null;
        return new
        {
            record = p.Record.Index, what = p.What, watches = p.Watches, unit = p.Milliamps ? "A" : "W",
            min = U(p.Record.Min), stock = U(p.Record.Default), max = U(p.Record.Max), limit = U(p.Limit), now = U(p.Now), use_pct = p.UsePct,
        };
    }).ToList();

    private static double? Round1(double? v) => v is { } x ? Math.Round(x, 1) : null;

    /// <summary>The daily profile's writes by "kind/key", and (in <paramref name="section"/>) the profile in short: when it
    /// was kept, whether it is suspended, what it keeps; null when none is kept, an error when the broker cannot say.</summary>
    private IReadOnlyDictionary<string, string> Daily(out object? section)
    {
        var writes = new Dictionary<string, string>();
        var r = card.Broker(BrokerRequest.OpProfile, "show");
        if (!r.Ok || r.Result is not { } res) { section = new { error = r.Error ?? "the daily profile could not be read" }; return writes; }
        if (!res.TryGetProperty("profile", out var p) || p.ValueKind != JsonValueKind.Object) { section = null; return writes; }
        foreach (var w in p.GetProperty("Writes").EnumerateArray())
            if (Text(w, "Kind") is { } kind && Text(w, "Key") is { } key && Text(w, "Value") is { } value)
                writes[$"{kind}/{key}"] = value;
        string? last = p.TryGetProperty("History", out var h) && h.ValueKind == JsonValueKind.Array && h.GetArrayLength() > 0 ? h[h.GetArrayLength() - 1].GetString() : null;
        // What the card ran when the profile was kept, against what it runs now (null where either is unknown).
        string? keptVbios = Text(p, "Vbios"), keptDriver = Text(p, "Driver");
        string? vbiosNow = res.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "vbios") : null;
        string? driverNow = c.ValueKind == JsonValueKind.Object ? Text(c, "driver") : null;
        static string? Other(string? kept, string? now) =>
            kept != null && now != null && !string.Equals(kept, now, StringComparison.OrdinalIgnoreCase) ? now : null;
        section = new
        {
            kept_at = p.GetProperty("KeptAt").GetDateTimeOffset(),
            suspended = p.GetProperty("Suspended").GetBoolean(),
            keeps = writes.Keys,
            last_change = last,
            kept_on_vbios = keptVbios,
            kept_on_driver = keptDriver,
            vbios_now = Other(keptVbios, vbiosNow),
            driver_now = Other(keptDriver, driverNow),
        };
        return writes;
    }

    /// <summary>The journal's originals by "kind/key"; null when the broker cannot say. <paramref name="held"/> lists the
    /// cells Clocktail holds now (written, with the original kept to give back).</summary>
    private IReadOnlyDictionary<string, string>? Held(out object? held, out object? lastCrash)
    {
        lastCrash = null;
        var r = card.Broker(BrokerRequest.OpOwnership);
        if (!r.Ok || r.Result is not { } res || !res.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            held = new { error = r.Error ?? "the ownership journal could not be read" };
            return null;
        }
        var originals = new Dictionary<string, string>();
        foreach (var i in items.EnumerateArray())
            if (Text(i, "Kind") is { } kind && Text(i, "Key") is { } key && Text(i, "Original") is { } original)
                originals[$"{kind}/{key}"] = original;
        held = originals.Keys.ToList();
        // The last crash on record (a driver reset, or a reboot with a write window open or cells held), when it was found
        // in the last 24 hours: what card_report says happened, and when.
        if (res.TryGetProperty("crashes", out var crashes) && crashes.ValueKind == JsonValueKind.Array && crashes.GetArrayLength() > 0
            && crashes[crashes.GetArrayLength() - 1] is var last && last.TryGetProperty("found_at", out var at) && at.TryGetDateTimeOffset(out var found)
            && DateTimeOffset.Now - found < TimeSpan.FromHours(24))
            lastCrash = new { found_at = found, what = Text(last, "sentinel"), held = last.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array ? its.GetArrayLength() : 0 };
        return originals;
    }

    /// <summary>The experiment session in short: the game, and the trial in force with what it set; null without one.</summary>
    private static object? Session((bool Ok, JsonElement? Result, string? Error) r)
    {
        if (!r.Ok || r.Result is not { } res) throw new InvalidOperationException(r.Error ?? "the experiment session could not be read");
        if (!res.TryGetProperty("experiment", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        object? trial = null;
        if (e.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object)
            trial = new
            {
                at = t.GetProperty("At").GetDateTimeOffset(),
                change = Text(t, "Change"),
                marked_stable = t.TryGetProperty("Marked", out var m) && m.ValueKind == JsonValueKind.True,
                power_limit_w = Number(t, "PowerMw", 1000),
                nvvdd_limit_a = Number(t, "NvvddMa", 1000),
                msvdd_limit_a = Number(t, "MsvddMa", 1000),
                msvdd_rel_offset_mv = Number(t, "MsvddRelUv", 1000),
                msvdd_vmin_offset_mv = Number(t, "MsvddVminUv", 1000),
            };
        return new { game = Text(e, "Game"), note = Text(e, "Note"), started_at = e.GetProperty("StartedAt").GetDateTimeOffset(), trial };
    }

    private static bool Trial((bool Ok, JsonElement? Result, string? Error) r) =>
        r.Ok && r.Result is { } res && res.TryGetProperty("experiment", out var e) && e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object;

    /// <summary>A clock domain's offset (kHz) and voltage demand (µV, on the rail its topology names) on the card now.</summary>
    internal static (int OffsetKhz, int VdemandUv) Domain(Card card, ClockDomainId d)
    {
        var t = ClockDomainsParser.Find(card.WithDriver(ClockDomainOffsets.ReadTopology), (uint)d);
        var e = card.WithDriver((hw, s) => ReadControl(hw, s, t));
        int slot = ClockVdemand.Slot(t, ClockVdemand.RailInfo(card.WithDriver(RailsReader.Read)));
        return (e.OffsetKhz, slot == 0 ? e.VdemandNvvddUv : e.VdemandMsvddUv);
    }

    private static ClockControlEntry ReadControl(NvClient hw, GpuSession gpu, ClockTopologyEntry topo)
    {
        var b = ClockDomainOffsets.Spec(topo.Index).ReadBuffer();
        var r = hw.Call(gpu, Slot.ClockClkDomainsGetControl, b);
        if (!r.IsOk) throw new InvalidOperationException($"clock-domain control read failed: {r}");
        return ClockDomainsParser.ParseControl(b, topo.Index);
    }

    private static object Offsets(RailOffsets o) => new { rel = o.RelUv / 1000.0, alt = o.AltUv / 1000.0, ov = o.OvUv / 1000.0, vmin = o.VminUv / 1000.0 };

    /// <summary>A rail's offsets in the write path's encoding, as mV; null when absent or not that encoding.</summary>
    private static object? Offsets(string? encoded)
    {
        if (encoded == null) return null;
        try { return Offsets(RailWrites.Decode(encoded)); }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    private static double? Mv(uint uv) => uv == 0 ? null : uv / 1000.0;

    /// <summary>An integer in a write's encoding (kHz, µV, mW, mA, %) divided into the unit shown; null for absent or not a number ("absent").</summary>
    private static double? Scaled(string? value, double div) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v / div : null;

    private static double? Number(JsonElement e, string name, double div) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() / div : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Str(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static double? R(double? v) => v is { } x ? Math.Round(x, 1) : null;

    private static double? Median(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x != null).Select(x => x!.Value).Order().ToList();
        if (v.Count == 0) return null;
        return Math.Round(v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2, 1);
    }

    /// <summary>A section is a report, not a decision: whatever one read throws becomes that section's error, and the
    /// other sections still answer.</summary>
    private static object? Section(Func<object?> read)
    {
        try { return read(); }
        catch (Exception e) when (Soft(e)) { return new { error = e.Message }; }
    }

    private static bool Soft(Exception e) => e is not OutOfMemoryException;

    private static string Out(object o) => JsonSerializer.Serialize(o, Json);

    /// <summary>An unreadable card as the tool's answer, with the reason: the SDK would otherwise hide the message.</summary>
    private static string Guard(Func<string> tool)
    {
        try { return tool(); }
        catch (Exception e) when (Soft(e)) { return Out(new { success = false, error = e.Message }); }
    }
}
