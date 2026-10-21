using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// "How's my card doing": every setting as original (stock) → now with the change and a safety mark, the live readings
/// with theirs, and one verdict, as rows and as a Markdown table an agent shows as it is. The marks come from fixed rules,
/// not from the agent: ✅ within NVIDIA's own limits for the card (or the range measured on the reference card), ⚠️
/// outside them and worth a look, ⛔ act now. Safety, not stability: a ✅ setting can still crash a game, and stress_test
/// and play decide that. Built from card_settings and limiter, so the numbers are theirs.
/// </summary>
public sealed partial class SettingsTools
{
    public const string Safe = "✅", Look = "⚠️", Act = "⛔", Info = "ℹ️";
    /// <summary>Hotter than this is worth a look (the stock fan curve holds 83 °C); this hot, act.</summary>
    public const double WarmC = 85, HotC = 90;
    /// <summary>What was measured on the reference card: memory +358 MHz, MSVDD REL up to +20 mV, VMIN +50 mV, XBAR up
    /// to +400 MHz (above +250 only with +20 mV of voltage demand).</summary>
    public const double MeasuredMemoryMhz = 358, MeasuredMsvddRelMv = 20, MeasuredVminAboveMv = 50, MeasuredXbarMhz = 400, XbarCliffMhz = 250, XbarCliffDemandMv = 20;
    /// <summary>The ceiling no rail's MAX may pass.</summary>
    public const double CeilingMv = 1150;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The note on a setting whose stock value only the service knows (its journal of originals).</summary>
    private const string StockUnknown = "original unknown: the service keeps it";

    private sealed record ReportRow(string Icon, string Name, string Original, string Now, string Change, string Mark, string Note);

    [McpServerTool(Name = "card_report", ReadOnly = true, OpenWorld = true), Description(
        "How's my card doing: THE call for 'how is my card', 'is this safe', 'what did we change', 'status'. Every setting " +
        "as original (stock) → now with the change and a safety mark, then live readings (temperature, power, clock and " +
        "voltage, what holds the clock), then one verdict. The marks come from fixed rules: ✅ within NVIDIA's own limits for " +
        "this card (or the range measured on the reference card), so nothing strains the hardware; ⚠️ outside them, worth a " +
        "look; ⛔ act now; ℹ️ information. Safety, not stability: whether it holds in every game is what stress_test and play " +
        "decide. Show 'table' to the person exactly as it is (Markdown with emoji), then add one or two sentences of your own. " +
        "Between the two tables is the V/F curve drawn in Braille dots like btop (a code block, monochrome): the curve now " +
        "solid, stock dotted, ● where the card runs now; show it as it is too. " +
        "Run it with a game or stress test loaded for meaningful live rows; idle is fine for the settings. Its last row is " +
        "Clocktail's own version: when a newer one is out (update_available), tell the person and how to update.")]
    public string CardReport() => Guard(() =>
    {
        using var settingsDoc = JsonDocument.Parse(CardSettings());
        using var limiterDoc = JsonDocument.Parse(WhatLimits());
        var s = settingsDoc.RootElement;
        var l = limiterDoc.RootElement;
        var rows = SettingRows(s);
        var live = LiveRows(s, l);
        // The driver's own word that it is holding the card down: shown only when it says something.
        var why = Arr(s, "perf_decrease", "reasons").Select(x => x.GetString()).ToList();
        if (why.Count > 0)
            live.Add(new("🔌", "Driver holding it down", "", string.Join(", ", why), "", why.Contains("insufficient_power") || why.Contains("thermal_protection") ? Act : Look,
                why.Contains("insufficient_power") ? "the driver says the card is not getting enough power: check the 16-pin cable is fully seated and the PSU, now"
                : why.Contains("thermal_protection") ? "the driver is protecting the GPU from heat: check the fans and the case airflow now"
                : "the driver reports it is reducing performance"));
        var others = Arr(s, "other_gpus").Select(g => g.GetString()).ToList();
        if (others.Count > 0)
            live.Add(new("🖥️", "Other GPUs", "", string.Join(", ", others), "", Look,
                $"Clocktail reads and tunes the {Str(s, "gpu")} only; stress_test's load (nvgwls) picks its own GPU, so check the clock moves when it runs"));
        if (Str(s, "driver") is { } driver && Str(s, "tested_on") is { } tested)
            live.Add(new("🧪", "Tested on", "", $"driver {driver}, VBIOS {Str(s, "vbios")}", "", Info, tested));
        var (version, newer, versionNote) = UpdateCheck.Describe(UpdateCheck.Latest());
        live.Add(new(newer ? "🆕" : "🏷️", "Clocktail", "", version, "", Info, versionNote));
        var chart = CurveChart(s, l);
        var all = rows.Concat(live).ToList();
        var act = all.Where(r => r.Mark == Act).ToList();
        var look = all.Where(r => r.Mark == Look).ToList();
        string verdict = act.Count > 0
            ? $"{Act} Act now: {string.Join(", ", act.Select(r => r.Name.ToLowerInvariant()))}. {string.Join(" ", act.Select(r => r.Note))}"
            : look.Count > 0
                ? $"{Look} {look.Count} thing{(look.Count == 1 ? "" : "s")} to look at: {string.Join("; ", look.Select(r => $"{r.Name.ToLowerInvariant()} ({r.Note})"))}. Everything else is within NVIDIA's own limits."
                : $"{Safe} All good: every setting is within NVIDIA's own limits for this card, and it is running healthy.";
        return Out(new
        {
            gpu = Str(s, "gpu"),
            verdict,
            settings = rows.Select(Plain),
            live = live.Select(Plain),
            update_available = newer,
            vf_chart = chart.Count == 0 ? null : string.Join('\n', chart),
            table = Table(Str(s, "gpu") ?? "GPU", rows, chart, live, verdict),
        });
    });

    /// <summary>The temperature's mark: ⛔ at NVIDIA's own slowdown point (no margin left) or 90 °C, ⚠️ within 5 °C of it
    /// or from 85 °C, ✅ else. The margin is the driver's T.Limit, where it gives one.</summary>
    public static string TemperatureMark(double tempC, double? marginC) =>
        tempC >= HotC || marginC <= 0 ? Act : tempC >= WarmC || marginC <= 5 ? Look : Safe;

    /// <summary>card_report's rows (settings, then live) as name, mark and note, from card_settings and limiter answers.</summary>
    public static IReadOnlyList<(string Name, string Mark, string Note)> ReportRows(JsonElement settings, JsonElement limiter) =>
        [.. SettingRows(settings).Concat(LiveRows(settings, limiter)).Select(r => (r.Name, r.Mark, r.Note))];

    private static object Plain(ReportRow r) => new { setting = r.Name, original = r.Original, now = r.Now, change = r.Change, safe = r.Mark, note = r.Note };

    private static List<ReportRow> SettingRows(JsonElement s)
    {
        var rows = new List<ReportRow>();

        // Power limit: the board's own range is NVIDIA's limit.
        var pw = Obj(s, "power");
        double? limitW = Num(pw, "limit_w"), stockW = Num(pw, "stock_w"), maxW = Num(pw, "board_max_w"), minW = Num(pw, "board_min_w"), dailyW = Num(pw, "daily_w");
        bool powerOk = limitW is { } lw && (maxW is not { } mx || lw <= mx + 0.5) && (minW is not { } mn || lw >= mn - 0.5);
        if (Unread(pw) is { } pwErr)
            rows.Add(new("⚡", "Power limit", "?", "?", "?", Look, pwErr));
        else
            rows.Add(new("⚡", "Power limit", V(stockW, "W"), V(limitW, "W"), Change(limitW, stockW, "W"), powerOk ? Safe : Look,
                Join(maxW is { } bm ? $"board max {F(bm)} W" : null,
                     dailyW is { } dw && limitW is { } lwNow && Math.Abs(dw - lwNow) > 0.5 ? $"daily {F(dw)} W; a trial set this one" : null,
                     powerOk ? null : "outside the board's own range")));

        // The V/F curve: offsets move clocks along the curve, never the voltage past MAX (the rails row has MAX).
        var vf = Obj(s, "vf_curve");
        double? maxOff = Num(vf, "max_offset_mhz"), minOff = Num(vf, "min_offset_mhz"), points = Num(vf, "offset_points");
        double? topMhz = Num(vf, "top", "mhz"), topMv = Num(vf, "top", "mv");
        bool flat = Bool(vf, "top", "flat") == true;
        string curveNow = points is null or 0 ? "stock"
            : (minOff < 0 && maxOff > 0 ? $"{Sg(minOff)} to {Sg(maxOff)} MHz" : $"up to {Sg(maxOff > 0 ? maxOff : minOff)} MHz") + $" on {F(points)} points";
        string curveChange = maxOff > 0 ? $"⬆️ up to {Sg(maxOff)} MHz" : minOff < 0 ? $"⬇️ {Sg(minOff)} MHz" : "—";
        string? vfErr = Unread(vf);
        if (vfErr != null) (curveNow, curveChange) = ("?", "?");
        rows.Add(new("📈", "V/F curve", "stock", curveNow, curveChange, vfErr != null ? Look : Safe,
            vfErr ?? Join(topMhz is { } tm && topMv is { } tv ? $"tops out at {F(tm)} MHz @ {F(tv)} mV{(flat ? " (flat top)" : "")}" : null,
                "offsets move clocks, never the voltage limit")));

        // The P0 offsets: the core's only when set (the curve is the tool), memory always.
        if (Unread(Obj(s, "clock_offsets")) is { } offErr)
            rows.Add(new("🧠", "Memory offset", "?", "?", "?", Look, offErr));
        else foreach (var (key, icon, name) in new[] { ("core", "🎯", "Core offset (P0)"), ("memory", "🧠", "Memory offset") })
        {
            var o = Obj(s, "clock_offsets", key);
            double? now = Num(o, "offset_mhz"), stock = Num(o, "stock_mhz") ?? 0, min = Num(o, "min_mhz"), max = Num(o, "max_mhz");
            if (key == "core" && (now ?? 0) == stock) continue;
            bool inRange = now is { } n && (min is not { } a || n >= a) && (max is not { } b || n <= b);
            string note = key == "memory"
                ? now > MeasuredMemoryMhz ? $"above the reference's +{F(MeasuredMemoryMhz)}: this card's own tests decide" : "NVML shows it doubled"
                : "the V/F curve is the better tool for the core";
            rows.Add(new(icon, name, $"{Sg(stock)} MHz", $"{Sg(now)} MHz", Change(now, stock, "MHz"), inRange ? Safe : Look, inRange ? note : "outside the driver's range"));
        }

        // The rails: REL/ALT above 0 is past NVIDIA's own reliability limit; no MAX past the 1150 mV ceiling.
        foreach (var (key, icon, name) in new[] { ("nvvdd", "🔌", "NVVDD rail (core)"), ("msvdd", "🔌", "MSVDD rail (L2, crossbar)") })
        {
            var r = Obj(s, "rails", key);
            string? err = r is { } rv ? Str(rv, "error") : "unreadable";
            if (err != null) { rows.Add(new(icon, name, "?", "?", "?", Look, err)); continue; }
            double? rel = Num(r, "offsets_mv", "rel"), vmin = Num(r, "offsets_mv", "vmin"), alt = Num(r, "offsets_mv", "alt");
            double? relS = Num(r, "stock_offsets_mv", "rel"), vminS = Num(r, "stock_offsets_mv", "vmin"), altS = Num(r, "stock_offsets_mv", "alt");
            double? max = Num(r, "limits_mv", "max");
            var notes = new List<string?>();
            string mark = Safe;
            if (max > CeilingMv) { mark = Look; notes.Add($"MAX {F(max)} mV is past the {F(CeilingMv)} mV ceiling"); }
            if (rel > 0 || alt > Math.Max(0, altS ?? 0))
            {
                mark = Look;
                notes.Add(key == "msvdd" && rel <= MeasuredMsvddRelMv
                    ? $"REL {Sg(rel)} mV is past NVIDIA's own limit (+{F(MeasuredMsvddRelMv)} measured on the reference)"
                    : "raised past NVIDIA's own reliability limit");
            }
            else if (key == "msvdd" && rel == 0 && relS < 0) notes.Add("REL 0 is NVIDIA's own reliability limit");
            else if (rel < relS) notes.Add("undervolted");
            if (vmin - (vminS ?? 0) > MeasuredVminAboveMv) { mark = Look; notes.Add($"VMIN more than +{F(MeasuredVminAboveMv)} mV over stock"); }
            notes.Add(max is { } m ? $"MAX {F(m)} mV" : null);
            bool stockKnown = relS != null && vminS != null;
            if (!stockKnown) notes.Insert(0, StockUnknown);
            string change = !stockKnown ? "?"
                : Join(rel != relS ? $"REL {Arrow(rel - relS)} {Sg(rel - relS)} mV" : null, vmin != vminS ? $"VMIN {Arrow(vmin - vminS)} {Sg(vmin - vminS)} mV" : null) is { Length: > 0 } c ? c : "—";
            rows.Add(new(icon, name, stockKnown ? $"REL {Sg(relS)}, VMIN {Sg(vminS)} mV" : "?", $"REL {Sg(rel)}, VMIN {Sg(vmin)} mV", change, mark, Join([.. notes])));
        }

        // XBAR/SYS/Video: XBAR measured to +400, above +250 only with +20 mV of demand (the cliff); SYS and Video unmeasured.
        foreach (var (key, name) in new[] { ("xbar", "XBAR (crossbar)"), ("sys", "SYS clock"), ("video", "Video clock") })
        {
            var d = Obj(s, "clock_domains", key);
            double? off = Num(d, "offset_mhz"), offS = Num(d, "stock_offset_mhz") ?? 0, dem = Num(d, "vdemand_mv") ?? 0, demS = Num(d, "stock_vdemand_mv") ?? 0, clk = Num(d, "clock_mhz");
            if (key == "xbar" && (Unread(Obj(s, "clock_domains")) ?? (off == null ? "unreadable" : null)) is { } domErr)
            {
                rows.Add(new("🔀", name, "?", "?", "?", Look, domErr));
                continue;
            }
            if (key != "xbar" && off == offS && dem == demS) continue;
            string mark = Safe, note;
            if (key != "xbar") { mark = Look; note = "not measured on the reference card"; }
            else if (off > MeasuredXbarMhz) { mark = Look; note = $"past the +{F(MeasuredXbarMhz)} MHz measured; +450 without voltage gave a grey screen elsewhere"; }
            else if (off is { } o && TuningTools.XbarDemandNeededMv(o) is { } need && dem < need) { mark = Look; note = $"the cliff: at {Sg(o)} MHz it needs +{need} mV of voltage demand (+{TuningTools.XbarCliffDemandMv} above +{TuningTools.XbarCliffMhz}, +{TuningTools.XbarHighDemandMv} above +{TuningTools.XbarHighMhz})"; }
            else note = off > XbarCliffMhz ? $"+{F(dem)} mV keeps MSVDD off its floor" : off > 0 ? "within the measured range" : "stock";
            string change = Join(off != offS ? $"{Arrow(off - offS)} {Sg(off - offS)} MHz" : null, dem != demS ? $"{Arrow(dem - demS)} {Sg(dem - demS)} mV" : null) is { Length: > 0 } c ? c : "—";
            rows.Add(new("🔀", name, $"{Sg(offS)} MHz, {Sg(demS)} mV", $"{Sg(off)} MHz, {Sg(dem)} mV", change, mark,
                Join(note, clk is { } k && k > 0 ? $"running {F(k)} MHz now" : null)));
        }

        // Voltage boost: no measurable effect on a 5090; shown only when set.
        var vb = Obj(s, "voltage_boost");
        if (Num(vb, "independent_pct") is { } ib && ib != (Num(vb, "stock_independent_pct") ?? 0))
            rows.Add(new("🔋", "Voltage boost", $"{F(Num(vb, "stock_independent_pct") ?? 0)} %", $"{F(ib)} %", Change(ib, Num(vb, "stock_independent_pct") ?? 0, "%"), Safe, "no measurable effect on a 5090"));

        // The fan curve: full speed no later than stock's.
        var curve = Arr(s, "fans", "curves").FirstOrDefault(c => Bool(c, "editable") == true);
        if (curve.ValueKind == JsonValueKind.Object)
        {
            string? now = Str(curve, "curve"), stock = Str(curve, "stock_curve");
            var (nt, nd, _) = Last(now);
            var (st, _, _) = Last(stock);
            string? why = FanCurveShort(now, stock);
            if (stock == null)
                rows.Add(new("🌀", "Fan curve", "?", Short(now), "?", nd is >= 99.5 ? Info : Look,
                    Join(StockUnknown, nd is >= 99.5 ? $"full speed at {F(nt)} °C" : "never reaches full speed")));
            else
                rows.Add(new("🌀", "Fan curve", Short(stock), Short(now),
                    now == stock ? "—" : nt is { } a && st is { } b ? $"full speed at {F(a)} °C (stock {F(b)} °C)" : "changed",
                    why == null ? Safe : Look, why ?? (nt < st ? "full speed sooner than stock" : now == stock ? "the card's own" : "full speed no later than stock")));
        }

        // The fans now: following the curve, or held by a trial.
        var channels = Arr(s, "fans", "channels").Where(c => c.ValueKind == JsonValueKind.Object).ToList();
        if (channels.Count > 0)
        {
            bool manual = channels.Any(c => Str(c, "mode") == "manual");
            double? duty = channels.Select(c => Num(c, "duty_pct")).Max(), rpm = channels.Select(c => Num(c, "rpm")).Max();
            // A fixed duty Clocktail did not set (Afterburner, NV App, a lost journal): back_to_daily cannot give it back.
            var holds = Arr(s, "journal_holds").Select(h => h.ValueKind == JsonValueKind.String ? h.GetString() : null).ToList();
            bool known = s.TryGetProperty("journal_holds", out var jh) && jh.ValueKind == JsonValueKind.Array;
            bool ours = holds.Any(h => h?.StartsWith("fan/", StringComparison.Ordinal) == true);
            string fanNote = !manual ? "" : !known ? "held at a fixed duty; whether Clocktail set it is unknown (the journal could not be read)"
                : ours ? "a trial holds them; back_to_daily gives them back" : "held at a fixed duty by something other than Clocktail: back_to_daily will not give them back; the tool that set it, or its 'auto', does";
            // The tachometers against the rpm the firmware's curve asks for (the highest editable policy's target).
            uint? target = Arr(s, "fans", "curves").Where(c => Bool(c, "editable") == true).Select(c => Num(c, "target_rpm")).Max() is { } tr ? (uint)tr : null;
            var health = Clocktail.Domain.Fan.FanHealth.Check(
                [.. channels.Select(c => ((uint)(Num(c, "id") ?? 0), (uint)(Num(c, "rpm") ?? 0), Str(c, "mode") == "manual"))], target);
            string mark = health is { Stopped: true } ? Act : health != null || manual && !ours ? Look : Safe;
            rows.Add(new("🌀", "Fans", "auto", manual ? $"held at {F(duty)} %" : "auto", manual ? "held" : "—", mark,
                Join(rpm is { } r ? (r == 0 ? "stopped (idle, normal)" : $"{F(rpm)} rpm, {F(duty)} %") : null, fanNote is { Length: > 0 } fn ? fn : null, health?.Note)));
        }

        // Current limits: above the VBIOS default is a look (allowed, but past NVIDIA's own setting).
        var cl = Obj(s, "current_limits");
        double? nv = Num(cl, "nvvdd", "limit_a"), nvS = Num(cl, "nvvdd", "stock_a"), ms = Num(cl, "msvdd", "limit_a"), msS = Num(cl, "msvdd", "stock_a");
        if (nv != null || ms != null)
        {
            bool above = nv > nvS + 0.5 || ms > msS + 0.5;
            bool lowered = nv < nvS - 0.5 || ms < msS - 0.5;
            rows.Add(new("🔋", "Current limits", $"NVVDD {F(nvS)} A, MSVDD {F(msS)} A", $"NVVDD {F(nv)} A, MSVDD {F(ms)} A",
                above ? "⬆️ raised" : lowered ? "⬇️ lowered" : "—", above ? Look : Safe,
                above ? "above the VBIOS default" : lowered ? "lowered for a trial" : Join(Num(cl, "nvvdd", "now_a") is { } a ? $"NVVDD drawing {F(a)} A now" : null)));
        }
        return rows;
    }

    private static List<ReportRow> LiveRows(JsonElement s, JsonElement l)
    {
        var rows = new List<ReportRow>();
        // The board's input: the 16-pin connector and the input current (named from the 2026-10-21 measurements).
        if (Obj(s, "current_limits", "input") is { } input && Num(input, "connector_w") is { } cw)
            rows.Add(new("🔌", "16-pin input", "", Num(input, "input_a") is { } ia ? $"{F(cw)} W, {F(ia)} A" : $"{F(cw)} W", "",
                Str(input, "concern") != null ? Look : Safe,
                Str(input, "concern") ?? Join(Num(input, "input_limit_a") is { } il ? $"input limit {F(il)} A" : null, "rated 600 W")));
        if (Obj(s, "tdr") is { } tdr && Str(tdr, "what") is { } what)
            rows.Add(new("⏱️", "Driver timeout (TDR)", "", Num(tdr, "delay_s") is { } d ? $"{F(d)} s, level {F(Num(tdr, "level"))}" : "?", "",
                Bool(tdr, "safe_to_map") == false ? Act : Info,
                Bool(tdr, "safe_to_map") == false ? $"{what}: do not map the curve's edge with stress_test until TdrLevel is 3 (the default)" : what));
        if (Obj(s, "last_crash") is { } crash)
            rows.Add(new("💥", "Last crash", "", Str(crash, "found_at") is { } fa && DateTimeOffset.TryParse(fa, CultureInfo.InvariantCulture, out var when) ? when.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "?", "", Look,
                Join(Str(crash, "what"), Num(crash, "held") is { } n && n > 0 ? $"{F(n)} setting(s) were held; they were given back" : null, "data_points and read_log have the trial it happened in")));
        var locks = Arr(s, "clock_locks").Select(x => Str(x, "what")).OfType<string>().ToList();
        if (locks.Count > 0)
            rows.Add(new("🔒", "Clock lock", "", string.Join("; ", locks), "", Look,
                "set by another tool, not Clocktail, and still in force: a stress run or a curve test measures the lock, not the curve. " +
                "`nvidia-smi -rgc` releases NVML's locked clocks; a point lock or Boost Lock, the tool that set it"));
        var rd = Obj(l, "readings");
        // No readings (NVML unreadable, the driver busy): say so, or the verdict could call an unread card healthy.
        if (rd == null)
            rows.Add(new("🌡️", "Live readings", "", "?", "", Look, Str(l, "error") is { } le ? $"unavailable: {le}" : "unavailable"));
        double? temp = Num(rd, "temp_c"), board = Num(rd, "board_w"), limit = Num(rd, "power_limit_w"), clock = Num(rd, "clock_mhz"),
            mv = Num(rd, "nvvdd_mv"), max = Num(rd, "nvvdd_max_mv"), util = Num(rd, "util_pct");
        // NVIDIA's own margin to the temperature where the GPU slows down, where the driver gives it; the fixed marks else.
        double? margin = Num(rd, "temp_margin_c") ?? Num(s, "temperature", "margin_c"), maxOp = Num(s, "temperature", "max_operating_c");
        if (temp is { } t)
        {
            string mark = TemperatureMark(t, margin);
            bool hot = mark == Act, warm = mark != Safe;
            string headroom = margin is { } m ? $"{F(m)} °C under the {(maxOp is { } mo ? $"{F(mo)} °C " : "")}temperature where it slows down" : "";
            rows.Add(new("🌡️", "Temperature", "", $"{F(t)} °C", "", hot ? Act : warm ? Look : Safe,
                Join(hot ? "too hot: check the case airflow and the fan curve now" : warm ? "warm: the stock fan curve holds 83 °C at the wall" : t < 50 ? "cool" : "healthy", headroom)));
        }
        if (board is { } b)
            rows.Add(new("⚡", "Power draw", "", limit is { } lim ? $"{F(b)} W of {F(lim)} W" : $"{F(b)} W", "", Safe,
                Join(util is { } u ? $"{F(u)} % busy" : null, limit is { } li && b >= li - 10 ? "on the power limit" : null)));
        if (clock is { } c)
            rows.Add(new("📈", "Core", "", mv is { } v ? $"{F(c)} MHz @ {F(v)} mV" : $"{F(c)} MHz", "", mv > max + 0.5 ? Look : Safe,
                max is { } m ? $"voltage limit {F(m)} mV" : ""));
        if (Str(l, "limiter") is { } id)
            rows.Add(new(id switch { "power_wall" => "⚡", "current_limit" => "🔋", "thermal" => "🌡️", "idle" => "💤", "curve_cap" => "📈", "voltage_limit" => "🔌", _ => "🧭" },
                "Holding the clock", "", id switch
                {
                    "power_wall" => "the power limit",
                    "current_limit" => "a rail's current limit",
                    "power_policy" => "a power sub-limit",
                    "voltage_limit" => "the voltage limit",
                    "curve_cap" => "the top of the V/F curve",
                    "thermal" => "temperature",
                    "hardware_slowdown" => "a hardware slowdown",
                    "idle" => "nothing: it's idle",
                    _ => "nothing: the load asks for less",
                }, "", id is "thermal" or "hardware_slowdown" ? Look : Info, Str(l, "votes") is { } v ? $"{v} samples agree" : ""));

        // What is on the card in the long run: the daily profile, and a trial not yet kept.
        var daily = Obj(s, "daily_profile");
        if (daily is { } dp && Str(dp, "error") != null)
        {
            rows.Add(new("💾", "Daily profile", "", "unknown", "", Look,
                "the Clocktail service does not answer, so nothing can be changed, kept or put back after a crash: install_service (one UAC prompt), or check the ClocktailBroker scheduled task"));
            return rows;
        }
        string profile = daily == null ? "none: the card runs stock" : Bool(daily, "suspended") == true ? "suspended: stock until load_daily"
            : Str(daily.Value, "kept_at") is { Length: >= 10 } kept ? $"kept {kept[..10]}" : "kept";
        var off = DailyOffCard(s);
        // A profile kept on another VBIOS stays off the card (the service leaves it at stock); a new driver only asks for a test.
        if (daily is { } bound && Bool(bound, "suspended") != true && Str(bound, "vbios_now") is { } vbios)
            rows.Add(new("💾", "Daily profile", "", $"{profile} on VBIOS {Str(bound, "kept_on_vbios")}, not on the card", "", Look,
                $"the card now runs VBIOS {vbios}: the offsets were measured on the old firmware's curve, so the service leaves the card at stock. stress_test the curve on this VBIOS, then load_daily (it binds the profile to it) or keep_as_daily a new one"));
        else if (off is { Count: > 0 })
            rows.Add(new("💾", "Daily profile", "", $"{profile}, not on the card", "", Look,
                $"the card differs from it: {string.Join(", ", off)}. The service puts it on after a start or a driver reset once the GPU has idled a few seconds; if this stays, back_to_daily puts it on now"));
        else
            rows.Add(new("💾", "Daily profile", "", off != null ? $"{profile}, on the card" : profile, "", Info,
                daily != null && Bool(daily, "suspended") != true ? "the service puts it back after every boot and driver reset" : ""));
        if (daily is { } dr && Str(dr, "driver_now") is { } driver && Str(dr, "vbios_now") == null)
            rows.Add(new("💾", "Kept on driver", "", $"{Str(dr, "kept_on_driver")}, now {driver}", "", Look,
                "the profile still goes on; a new driver can move where the card runs at the same settings, so re-run stress_test on the daily curve before trusting it in games"));
        if (Bool(s, "trial_in_force") == true)
            rows.Add(new("🧪", "Trial on the card", "", Str(s, "session", "trial", "change") ?? "a trial", "", Info, "not kept yet: keep_as_daily or back_to_daily"));
        return rows;
    }

    /// <summary>
    /// Where the card differs from the daily profile it should be running: each setting the profile keeps whose value on
    /// the card is another ("memory offset +0 MHz (daily +358)"), or that cannot be read. Empty when the card is on it;
    /// null when there is nothing to compare: no profile kept, it is suspended (stock is meant), or a trial is in force
    /// (it is meant to differ). This catches every way the profile can fail to come back: a service that never started,
    /// an apply that gave up or is still waiting for the GPU to idle, or anything else that wrote over it.
    /// </summary>
    public static List<string>? DailyOffCard(JsonElement s)
    {
        if (Obj(s, "daily_profile") is not { } dp || Str(dp, "error") != null || Bool(dp, "suspended") == true || Bool(s, "trial_in_force") == true)
            return null;
        var off = new List<string>();
        void Cmp(string name, double? now, double? daily, string unit, bool signed = true)
        {
            if (now is not { } n || daily is not { } d) off.Add($"{name} unreadable");
            else if (Math.Abs(n - d) > 0.5) off.Add(signed ? $"{name} {Sg(n)} {unit} (daily {Sg(d)})" : $"{name} {F(n)} {unit} (daily {F(d)})");
        }
        foreach (var kept in Arr(dp, "keeps"))
        {
            var parts = (kept.ValueKind == JsonValueKind.String ? kept.GetString() : null)?.Split('/', 2);
            if (parts is not [var kind, var key]) continue;
            string name = key.ToUpperInvariant();
            switch (kind)
            {
                case PowerCap.Kind when key == PowerCap.Key(PowerRequestKind.Ordinary):
                    Cmp("power limit", Num(s, "power", "limit_w"), Num(s, "power", "daily_w"), "W", signed: false);
                    break;
                case ClockOffsets.Kind:
                    Cmp($"{key} offset", Num(s, "clock_offsets", key, "offset_mhz"), Num(s, "clock_offsets", key, "daily_mhz"), "MHz");
                    break;
                case VfWrites.Kind:
                    if (Num(s, "vf_curve", "points_off_daily") is not { } n) off.Add("V/F curve unreadable");
                    else if (n > 0) off.Add($"V/F curve ({F(n)} point{(n == 1 ? "" : "s")} off it)");
                    break;
                case RailWrites.Kind:
                    foreach (var slot in new[] { "rel", "alt", "ov", "vmin" })
                        Cmp($"{name} {slot.ToUpperInvariant()} offset", Num(s, "rails", key, "offsets_mv", slot), Num(s, "rails", key, "daily_offsets_mv", slot), "mV");
                    break;
                case ClockDomainOffsets.Kind:
                    Cmp($"{name} offset", Num(s, "clock_domains", key, "offset_mhz"), Num(s, "clock_domains", key, "daily_offset_mhz"), "MHz");
                    break;
                case ClockVdemand.Kind:
                    Cmp($"{name} voltage demand", Num(s, "clock_domains", key, "vdemand_mv"), Num(s, "clock_domains", key, "daily_vdemand_mv"), "mV");
                    break;
                case VoltBoost.Kind:
                    Cmp($"voltage boost ({key})", Num(s, "voltage_boost", $"{key}_pct"), Num(s, "voltage_boost", $"daily_{key}_pct"), "%", signed: false);
                    break;
                case CurrentLimitWrites.Kind:
                    Cmp($"{name} current limit", Num(s, "current_limits", key, "limit_a"), Num(s, "current_limits", key, "daily_a"), "A", signed: false);
                    break;
                case FanPolicyWrites.Kind:
                    var c = Arr(s, "fans", "curves").FirstOrDefault(x => Num(x, "index") is { } i && i.ToString(Inv) == key);
                    string? now = c.ValueKind == JsonValueKind.Object ? Str(c, "curve") : null, want = c.ValueKind == JsonValueKind.Object ? Str(c, "daily_curve") : null;
                    if (now == null || want == null) off.Add($"fan curve {key} unreadable");
                    else if (now != want) off.Add($"fan curve {key} ({Short(now)}; daily {Short(want)})");
                    break;
            }
        }
        return off;
    }

    /// <summary>
    /// The V/F curve as text (<see cref="VfChart"/>): now solid, stock dotted, ● where the card runs, under a legend line.
    /// Empty when the curve cannot be read; the settings row says so then.
    /// </summary>
    private List<string> CurveChart(JsonElement s, JsonElement l)
    {
        VfCurveSnapshot curve;
        try { curve = card.WithDriver(VfReader.Read); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
        var now = curve.Points.Select(p => (p.VoltageUv / 1000.0, p.EffectiveKhz / 1000.0)).ToList();
        var stock = curve.Points.Select(p => (p.VoltageUv / 1000.0, p.BaseKhz / 1000.0)).ToList();
        double? max = Num(s, "vf_curve", "nvvdd_max_mv");
        double? clock = Num(l, "readings", "clock_mhz"), mv = Num(l, "readings", "nvvdd_mv");
        (double, double)? at = clock is { } c && mv is { } v ? (v, c) : null;
        var lines = VfChart.Draw(now, stock, at, max).ToList();
        if (lines.Count == 0) return lines;
        var f = VfChart.Bounds(now, stock, max)!;
        string where = at is not { } a ? "" : f.Contains(a)
            ? $"   {VfChart.Running} running {F(a.Item2)} MHz @ {F(a.Item1)} mV"
            : $"   {VfChart.Running} running {F(a.Item2)} MHz @ {F(a.Item1)} mV, off the chart{(a.Item2 < f.Y0 ? ": below the curve, as at idle or light load" : "")}";
        lines.Insert(0, $"V/F curve (MHz over mV)   ⣀⣀ now   ⠁⠈ stock{where}");
        lines.Insert(1, $"each line {F(f.MhzPerLine)} MHz, labelled at its top dot (a dot {F(f.MhzPerLine / 4)} MHz); each character {F(f.MvPerChar)} mV" +
                        (max is { } m ? $"; the right edge is the {F(m)} mV voltage limit" : ""));
        lines.Insert(2, "");
        return lines;
    }

    private static string Table(string gpu, List<ReportRow> rows, List<string> chart, List<ReportRow> live, string verdict)
    {
        var b = new StringBuilder();
        b.AppendLine($"### 🩺 Card report · {gpu}");
        b.AppendLine();
        b.AppendLine("| | Setting | Original | Now | Change | Safe | Note |");
        b.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows) b.AppendLine($"| {r.Icon} | {r.Name} | {r.Original} | {r.Now} | {r.Change} | {r.Mark} | {r.Note} |");
        b.AppendLine();
        if (chart.Count > 0)
        {
            b.AppendLine("```text");
            foreach (var line in chart) b.AppendLine(line);
            b.AppendLine("```");
            b.AppendLine();
        }
        b.AppendLine("| | Right now | | Safe | Note |");
        b.AppendLine("|---|---|---|---|---|");
        foreach (var r in live) b.AppendLine($"| {r.Icon} | {r.Name} | {r.Now} | {r.Mark} | {r.Note} |");
        b.AppendLine();
        b.AppendLine($"**{verdict}**");
        b.AppendLine();
        b.Append("_✅ = within NVIDIA's own limits for this card (or the range measured on the reference card): nothing here strains the hardware. " +
                 "Whether it holds in every game is a separate question: stress tests and play answer that._");
        return b.ToString();
    }

    /// <summary>Why a card_settings section cannot be judged: missing, or its read failed ({"error": ...}); null when it
    /// can. A failed section is a JSON object too, so without this its rows read as stock and ✅.</summary>
    private static string? Unread(JsonElement? section) =>
        section is not { } x ? "unreadable" : Str(x, "error") is { } e ? $"unreadable: {e}" : null;

    private static JsonElement? Obj(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind == JsonValueKind.Object ? cur : null;
    }

    private static double? Num(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind == JsonValueKind.Number ? cur.GetDouble() : null;
    }

    private static bool? Bool(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return [];
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : [];
    }

    private static string F(double? v) => v is { } x ? x.ToString("0.#", Inv) : "?";
    private static string Sg(double? v) => v is { } x ? x.ToString("+0.#;-0.#;0", Inv) : "?";
    private static string V(double? v, string unit) => v is { } ? $"{F(v)} {unit}" : "?";
    private static string Arrow(double? d) => d > 0 ? "⬆️" : d < 0 ? "⬇️" : "";
    private static string Change(double? now, double? stock, string unit) =>
        now is { } n && stock is { } st ? Math.Abs(n - st) < 0.05 ? "—" : $"{Arrow(n - st)} {Sg(n - st)} {unit}" : "?";
    private static string Join(params string?[] parts) => string.Join("; ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"50:20:1100,79:60:1995,86:100:2875" → "50 °C 20 % · 79 °C 60 % · 86 °C 100 %".</summary>
    private static string Short(string? curve) => curve == null ? "?" : string.Join(" · ", curve.Split(',').Select(p =>
    {
        var f = p.Split(':');
        return f.Length >= 2 ? $"{f[0]} °C {f[1]} %" : p;
    }));

    /// <summary>
    /// Why a fan curve falls short of the stock one, or null when it does not: it must reach 100 % no later than stock,
    /// and, because the firmware drives by the rpm column, its top rpm must be no lower than stock's.
    /// </summary>
    public static string? FanCurveShort(string? now, string? stock)
    {
        var (nt, nd, nr) = Last(now);
        var (st, _, sr) = Last(stock);
        if (nd is not >= 99.5 || st is { } stTop && !(nt <= stTop + 0.01)) return "full speed later than the stock curve";
        if (sr is { } srTop && nr is { } nrTop && nrTop < srTop) return $"full speed at {F(nrTop)} rpm, under stock's {F(srTop)} rpm (the firmware drives by rpm)";
        return null;
    }

    /// <summary>The last point's temperature, duty and rpm (null when the text names none).</summary>
    private static (double? Temp, double? Duty, double? Rpm) Last(string? curve)
    {
        var f = curve?.Split(',').LastOrDefault()?.Split(':');
        double? rpm = f is { Length: >= 3 } && double.TryParse(f[2], NumberStyles.Float, Inv, out var r) ? r : null;
        return f is { Length: >= 2 } && double.TryParse(f[0], NumberStyles.Float, Inv, out var t) && double.TryParse(f[1], NumberStyles.Float, Inv, out var d)
            ? (t, d, rpm) : (null, null, null);
    }
}
