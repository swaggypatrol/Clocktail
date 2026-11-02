using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Mcp.CurveEditor;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// The V/F curve editor window (the user, 2026-11-01): the person asks the agent for it, drags the curve by hand, and
/// their click on the window's save button is their own approval (the "dialog" kind of <see cref="Consent"/>, in a window
/// of its own): the curve goes on the card as one trial, is read back, and is kept as the daily profile; then the window
/// closes. Opening it changes nothing; closing it any other way writes nothing.
/// </summary>
public sealed partial class TuningTools
{
    /// <summary>How far back the running points go: the residency day files.</summary>
    public static readonly TimeSpan RunsSpan = TimeSpan.FromDays(60);
    /// <summary>The session the window opens for its save when none is open.</summary>
    public const string EditorGame = "V/F 曲线编辑器";
    /// <summary>Beside the GPU's records: the curve the window saved last and the band it was drawn with, so the same band
    /// is shown again while the card still holds that curve (a detection alone would reopen a widened band narrower).</summary>
    public const string BandFile = "curve-editor.json";
    /// <summary>One save at a time in this process (the window allows one, but the claim is cheap and the card is one).</summary>
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    [McpServerTool(Name = "curve_editor", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Open the V/F curve editor window on the person's desktop, when they ask for it. It shows this GPU's curve (dark, " +
        "points dragged by hand), every crash pinned to the curve point it happened at, the highest setting each point was " +
        "marked stable or survived at, and where the card actually ran (the residency samples under load). Under the top of " +
        "the curve's low-voltage cliff it reshapes the cliff as a smooth transition band (no point under the band's foot is " +
        "lifted; both ends are draggable lines); above it the person drags freely, one button smooths, and the clock never " +
        "falls (the driver's own rule). The person's own click on '确认并保存为 Daily' in the window is their approval: it " +
        "writes the curve as one trial (opening a session itself when none is open), checks the readback, keeps it as the " +
        "daily profile (with any other trial settings in force, which the window names) and closes. Returns at once; call " +
        "again to bring the window to the front or to read how the last one ended. Opening it writes nothing.")]
    public string CurveEditor() => Guard(() =>
    {
#if WINDOWS
        if (!EditorState.TryClaim())
        {
            EditorState.Front();
            return Out(new { success = true, opened = false, note = "the curve editor is already open: brought to the front", last = EditorState.Last });
        }
        EditorData data;
        try { data = EditorData(); }
        catch { EditorState.Release(); throw; }
        CurveEditorForm.Show(data, (request, progress) => SaveFromEditor(data, request, progress), outcome =>
        {
            EditorState.Closed(outcome);
            try { card.Broker(BrokerRequest.OpExperiment, "note", $"Curve editor window closed: {outcome}"); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
        });
        return Out(new
        {
            success = true, opened = true,
            note = "the window is on the person's desktop; nothing is written until they click 确认并保存为 Daily there, which keeps the curve as the daily profile and closes it. Call curve_editor again later to read how it ended.",
            points = data.Mv.Length, crashes_marked = data.Crashes.Count, evidence_points = data.Evidence.Count, running_bins = data.Runs.Count,
            band = data.Band is { } b ? string.Create(CultureInfo.InvariantCulture, $"{data.Mv[b.Foot]}-{data.Mv[b.Top]} mV, as saved last time") : null,
            also_kept = data.AlsoKept, leftover = Leftover(), last = EditorState.Last,
        });
#else
        return Out(new { success = false, error = "this build of the server has no window (it is built for net10.0-windows only)" });
#endif
    });

    /// <summary>Whether a trial's change was the curve's (adjust_curve, flatten_curve, set_curve, shift_curve, the editor): its
    /// crash is evidence about the curve. Under a trial that moved XBAR, the memory, the wall, a limit or the fans the curve was
    /// merely on the card. By the change's words: a curve trial's data point also carries the session's earlier writes and
    /// wall, so the other fields do not tell. A core P0 offset trial moves every point too, but its data point's curve does
    /// not carry the offset, so it is left out rather than pinned at the wrong clock.</summary>
    public static bool CurveTrialChange(string change) =>
        System.Text.RegularExpressions.Regex.IsMatch(change, @"MHz on points|flat top at|whole curve written|curve shifted|curve editor");

    /// <summary>A trial of the window's own session still on the card: a save the server ended under (the agent left, the
    /// driver went away), for the agent to clear.</summary>
    private string? Leftover()
    {
        var show = card.Broker(BrokerRequest.OpExperiment, "show");
        return show.Ok && show.Result is { } r && r.TryGetProperty("experiment", out var e) && e.ValueKind == JsonValueKind.Object
            && Str(e, "Game") == EditorGame && e.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object
            ? $"an earlier save from the window did not finish: its trial is still on the card (session '{EditorGame}'); back_to_daily or end_session puts the daily profile back"
            : null;
    }

    /// <summary>The curve, the firmware range, the crashes on their points, each point's evidence and the running samples, read once.</summary>
    private EditorData EditorData()
    {
        var look = card.Read();
        var pts = look.Curve.Points;
        int lowKhz = -1_000_000, highKhz = 1_000_000, global = 0;
        try
        {
            var limits = VfLimits.From(card.WithDriver(Clocktail.Domain.Clocks.Pstates20Reader.Read));
            (lowKhz, highKhz, global) = (limits.LowestKhz, limits.HighestKhz, limits.GlobalKhz);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or Clocktail.Hw.NvApiException) { }

        double[] mv = [.. pts.Select(p => p.VoltageUv / 1000.0)];
        // A point runs at base + the core P0 offset + its own offset (VfLimits): with set_core_offset in force every point sits higher.
        int[] baseKhz = [.. pts.Select(p => (int)p.BaseKhz + global)];
        int[] offsets = look.Curve.Offsets();

        var crashes = new List<CrashMark>();
        var evidence = new List<EvidenceMark>();
        var runs = new List<RunBin>();
        string? dir = null;
        (int, int)? band = null;
        var show = card.Broker(BrokerRequest.OpExperiment, "show");
        var ticks = residency.Between(DateTimeOffset.Now - RunsSpan, DateTimeOffset.Now).OrderBy(t => t.At).ToList();
        if (show.Ok && show.Result is { } res && res.TryGetProperty("data", out var dp) && dp.GetString() is { } dataPath)
        {
            dir = Path.GetDirectoryName(dataPath);
            band = ReadBand(dir, offsets);
            var all = Experiment.ReadLast(dataPath, int.MaxValue);
            var at = ticks.Select(t => t.At).ToList();
            foreach (var d in all.Where(d => d.Outcome == Experiment.Crash))
            {
                int[] then;
                try { then = VfOffsets.Decode(d.Curve); } catch (FormatException) { continue; }
                if (then.Length != pts.Count) continue;
                var start = d.At - TimeSpan.FromMinutes(d.Minutes);
                int lo = at.BinarySearch(start), hi = at.BinarySearch(d.At);
                lo = lo < 0 ? ~lo : lo; hi = hi < 0 ? ~hi : hi + 1;
                var during = ticks.GetRange(lo, Math.Max(0, hi - lo));
                IReadOnlyList<(int Index, uint? ClockMhz)> where = during.Count >= 10 ? CrashAt(during, d.CrashUv, look.Curve)
                    : d.CrashUv is { } uv && Nearest(look.Curve, uv) is { } p ? [(p.Index, null)] : [];
                var thenEff = CurveModel.Effective(baseKhz, then);
                foreach (var (index, clock) in where)
                {
                    if (index >= pts.Count) continue;
                    crashes.Add(new CrashMark(index, mv[index], thenEff[index] / 1000.0, clock, d.At, d.Game, d.Change, CurveTrialChange(d.Change)));
                }
            }
            foreach (var (index, rec) in PointRecords(all, look.Curve))
            {
                if (index >= pts.Count || (rec.StableKhz == null && rec.SurvivedKhz == null)) continue;
                evidence.Add(new EvidenceMark(index, mv[index],
                    rec.StableKhz is { } s ? CurveModel.Clock(baseKhz, index, s) / 1000.0 : null, rec.StableSeconds,
                    rec.SurvivedKhz is { } v ? CurveModel.Clock(baseKhz, index, v) / 1000.0 : null));
            }
        }
        foreach (var g in ticks.GroupBy(t => (Mv: Math.Round(t.Uv / 1000.0), Step: (int)Math.Round(t.Mhz / 7.5))))
            runs.Add(new RunBin(g.Key.Mv, g.Key.Step * 7.5, g.Count()));

        var (keys, also) = KeptBesideCurve();
        return new EditorData(card.Name, mv, baseKhz, offsets, Math.Min(pts.Count, VfRules.WritablePoints), lowKhz, highKhz,
            look.NvvddVminUv / 1000.0, look.NvvddMaxUv / 1000.0, crashes, evidence, runs, band, dir, keys, also);
    }

    /// <summary>What a keep would take beside the curve, from the broker's preview: the other settings' sorted "kind/key"
    /// names (what the save checks again), and the words for the person, with what the daily profile would lose.</summary>
    private (IReadOnlyList<string> Keys, string? Words) KeptBesideCurve()
    {
        var preview = card.Broker(BrokerRequest.OpProfile, "preview");
        if (!preview.Ok || preview.Result is not { } pr) return ([], null);
        var kept = new List<(string Key, string Name)>();
        if (pr.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var x in ch.EnumerateArray())
                if (Str(x, "kind") is { } kind && kind != VfWrites.Kind)
                    kept.Add(($"{kind}/{Str(x, "key")}", Setting(kind, Str(x, "key"))));
        var dropped = new List<string>();
        if (pr.TryGetProperty("dropped", out var dr) && dr.ValueKind == JsonValueKind.Array)
            foreach (var x in dr.EnumerateArray())
                if (x.GetString() is { } s && s.Split(" = ")[0].Split('/') is { Length: 2 } kk && kk[0] != VfWrites.Kind)
                    dropped.Add(Setting(kk[0], kk[1]));
        var parts = new List<string>();
        if (kept.Count > 0) parts.Add($"一并保留当前试验中的：{string.Join(", ", kept.Select(k => k.Name).Distinct())}");
        if (dropped.Count > 0) parts.Add($"不再保留 daily 里的：{string.Join(", ", dropped.Distinct())}");
        return ([.. kept.Select(k => k.Key).Order(StringComparer.Ordinal)], parts.Count > 0 ? string.Join("；", parts) : null);
    }

    private sealed record BandRecord(string Curve, int Foot, int Top, DateTimeOffset SavedAt);

    /// <summary>The band remembered for the curve on the card, when the file names exactly this curve.</summary>
    private static (int Foot, int Top)? ReadBand(string? dir, int[] offsets)
    {
        if (dir == null) return null;
        try
        {
            string path = Path.Combine(dir, BandFile);
            if (!File.Exists(path)) return null;
            var b = JsonSerializer.Deserialize<BandRecord>(File.ReadAllText(path));
            return b != null && b.Curve == VfOffsets.Encode(offsets) ? (b.Foot, b.Top) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteBand(string? dir, int[] offsets, int foot, int top)
    {
        if (dir == null) return;
        try { File.WriteAllText(Path.Combine(dir, BandFile), JsonSerializer.Serialize(new BandRecord(VfOffsets.Encode(offsets), foot, top, DateTimeOffset.Now))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The window's save, on its own thread, one at a time: the curve as one trial (a session opened for it when
    /// none is), its readback, then keep_as_daily; a session it opened is ended (the daily profile, now this curve, stays
    /// on). Every refusal says what the card holds afterwards.</summary>
    private SaveResult SaveFromEditor(EditorData data, SaveRequest request, Action<string> progress)
    {
        if (!SaveGate.Wait(0)) return new SaveResult(false, null, "另一次保存正在进行");
        try { using (EditorState.Saving()) return SaveNow(data, request, progress); }
        finally { SaveGate.Release(); }
    }

    private SaveResult SaveNow(EditorData data, SaveRequest request, Action<string> progress)
    {
        var target = request.Offsets;
        var look = card.Read();
        var now = look.Curve.Offsets();
        if (target.Length != now.Length) return new SaveResult(false, null, $"显卡曲线的点数变了（{now.Length}，窗口里是 {target.Length}）：没有写入；请关掉窗口重新打开");
        // The window drew on the curve as opened: a trial written since (by an agent) would be overwritten unseen.
        if (!now.SequenceEqual(data.Offsets)) return new SaveResult(false, null, "窗口打开后显卡上的曲线被改过（另一个试验或 agent）：没有写入；请关掉窗口重新打开");
        bool began = false, wrote = false;
        // The card may hold the window's curve: the daily profile back (the session the window opened ended with it).
        SaveResult Undo(string why)
        {
            var r = began ? card.Broker(BrokerRequest.OpExperiment, "end") : card.Broker(BrokerRequest.OpProfile, "apply", "curve editor: " + why);
            return new SaveResult(false, null, r.Ok ? why + "；已回到原 daily"
                : why + $"；回到 daily 没有完全成功（{r.Error}）：请让 agent 执行 back_to_daily，或先看 card_settings");
        }
        // Nothing of the window's on the card: a session it opened is ended; the card is not touched otherwise.
        SaveResult Refused(string why)
        {
            if (began) card.Broker(BrokerRequest.OpExperiment, "end");
            return new SaveResult(false, null, why);
        }
        if (!now.SequenceEqual(target))
        {
            var show = card.Broker(BrokerRequest.OpExperiment, "show");
            bool open = show.Ok && show.Result is { } sr && sr.TryGetProperty("experiment", out var e) && e.ValueKind == JsonValueKind.Object;
            if (!open)
            {
                progress("正在开启试验会话…");
                var b = card.Broker(BrokerRequest.OpExperiment, "begin", JsonSerializer.Serialize(new
                {
                    game = EditorGame, note = "the person edited the curve in the curve_editor window and clicked its save button", unattended = false,
                }));
                if (!b.Ok) return new SaveResult(false, null, $"无法开启会话：{b.Error}");
                began = true;
            }
            progress("正在写入曲线…");
            var bases = look.Curve.Points.Select(q => (int)q.BaseKhz).ToArray();
            var moved = look.Curve.Points.Where(p => target[p.Index] != now[p.Index]).ToList();
            var context = new List<string> { Readings(look), "Purpose: drawn by the person in the curve editor window" };
            foreach (var p in moved)
                context.Add(string.Create(CultureInfo.InvariantCulture,
                    $"point {p.Index} ({p.VoltageUv / 1000.0} mV): offset {p.OffsetKhz / 1000.0} → {target[p.Index] / 1000.0} MHz, lands at about {CurveModel.Clock(bases, p.Index, target[p.Index]) / 1000.0} MHz"));
            var t = card.Broker(BrokerRequest.OpExperiment, "try", JsonSerializer.Serialize(new
            {
                curve = VfOffsets.Encode(target), change = $"curve editor: points {moved[0].Index}-{moved[^1].Index}",
                at_uv = look.NvvddUv, at_mhz = look.Telemetry?.GraphicsClockMhz, context,
            }));
            if (!t.Ok)
            {
                // A hung GPU is written to by nobody; a refusal that wrote nothing needs no put-back (and must not take an
                // agent's own trial off the card); anything else may have landed in part.
                if (t.Code == BrokerCode.GpuHung)
                    return new SaveResult(false, null, $"显卡看起来已经挂起（{t.Error}）：没有写入，通常随后会有驱动重置" + (began ? "；窗口开的会话留着，由 agent 的 end_session 收尾" : ""));
                if (t.Written == WriteOutcome.None) return Refused($"写入被拒绝：{t.Error}");
                wrote = true;
                return Undo($"写入失败：{t.Error}");
            }
            wrote = true;
            progress("正在回读核对…");
            int[] after;
            try { after = card.Read().Curve.Offsets(); }
            catch (Exception x) when (x is InvalidOperationException or InvalidDataException or Clocktail.Hw.NvApiException or Clocktail.Hw.NvmlException)
            { return Undo($"写入后读不回曲线：{x.Message}"); }
            if (!after.SequenceEqual(target))
            {
                int bad = Enumerable.Range(0, target.Length).First(i => after[i] != target[i]);
                return Undo($"驱动回读不一致（第 {bad} 点：想要 {target[bad] / 1000.0} MHz，读到 {after[bad] / 1000.0} MHz）");
            }
        }
        // What a keep would take beside the curve, now: the window named what was in force when it opened.
        var (keysNow, wordsNow) = KeptBesideCurve();
        if (!keysNow.SequenceEqual(data.AlsoKeptKeys))
        {
            string why = $"保存会一并保留的其他设定变了（现在：{wordsNow ?? "只有曲线"}；窗口打开时：{data.AlsoKept ?? "只有曲线"}）：没有保存为 Daily；请关掉窗口重新打开";
            return wrote ? Undo(why) : Refused(why);
        }
        progress("正在保存为 Daily…");
        var k = card.Broker(BrokerRequest.OpProfile, "keep", "drawn in the curve editor window; the person clicked 确认并保存为 Daily");
        // The broker saves the profile and then switches the card to it; a wall or limit that did not go back is reported
        // as a failure with saved=true: the daily profile is this curve all the same.
        bool saved = k.Ok || (k.Result is { } kr && kr.TryGetProperty("saved", out var sv) && sv.ValueKind == JsonValueKind.True);
        if (!saved) return wrote ? Undo($"保存 Daily 失败：{k.Error}") : Refused($"保存 Daily 失败：{k.Error}");
        WriteBand(data.Dir, target, request.Foot, request.Top);
        if (began) card.Broker(BrokerRequest.OpExperiment, "end");   // no trial is left after a keep: the daily, this curve, stays on
        return new SaveResult(true, k.Ok ? null : $"显卡还没完全切换到新的 daily（{k.Error}）：让 agent 执行 back_to_daily 或 load_daily 再试一次", null);
    }
}
