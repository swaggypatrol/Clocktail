using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

public sealed partial class TuningTools
{
    [McpServerTool(Name = "begin_session", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Start an explicitly approved tuning session on the current RTX 50 GPU. Trials are recorded and recoverable. First inspect this card's current settings; no tuning preset or default daily profile is supplied. Optional unattended/crash-budget parameters are user-selected stop conditions." + " " + AsksItself)]
    public Task<string> BeginSession(
        McpServer server,
        [Description("The game being played.")] string game,
        [Description("Anything worth keeping with the data (settings, scene).")] string? note = null,
        [Description("The person is away: stop writing new trials after maxCrashes crashes.")] bool unattended = false,
        [Description("With unattended: the user-selected crash budget, a positive count (default 2).")] int maxCrashes = 2,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default) =>
        Asked(server, $"Allow overclocking trials for '{game}' in this session ({note ?? "no additional scope specified"}). " +
            $"NVIDIA decides which settings it accepts; Clocktail imposes no extra tuning envelope. Unattended: {unattended}, crash budget: {maxCrashes}. " +
            "Trials are recorded and can be undone; making them daily still requires separate approval.",
            confirm, cancellationToken, () => BeginApprovedSession(game, note, unattended, maxCrashes));

    private string BeginApprovedSession(string game, string? note, bool unattended, int maxCrashes)
    {
        if (maxCrashes < 1) return Out(new { success = false, error = "maxCrashes must be a positive count" });
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

    [McpServerTool(Name = "mark_stable", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Record the active trial's observed stable outcome and the workload that was actually tested. Do not infer stability from another GPU or from an interrupted or inconclusive run.")]
    public string MarkStable([Description("What was played and seen.")] string note) => Guard(() =>
        Reply(card.Broker(BrokerRequest.OpExperiment, "stable", note + TrialStays(out _))));

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

    /// <summary>The fields of a trial that say nothing about what the card runs: when and why it was written, its identity
    /// (<see cref="TrialId"/>), how it ended, the operating point at the write, and what a hand-back takes off it (a fixed
    /// fan duty, current limits).</summary>
    private static readonly HashSet<string> NotSettings = ["At", "Id", "JobId", "Change", "Marked", "Ending", "AtUv", "AtMhz", "FanPct", "NvvddMa", "MsvddMa", "Writes"];

    /// <summary>The identity of the trial in force, from a "show" or "quiet" answer (ExperimentTrial.Id, kept through a
    /// hand-back); null when no trial is in force, the answer cannot be read, or the service gave the trial none.</summary>
    public static string? TrialId(BrokerAnswer show) =>
        show.Ok && show.Result is { } r && r.TryGetProperty("experiment", out var e) && e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object
        && t.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>
    /// The settings the trial in force runs, from a "show" or "quiet" answer: every field but when and why it was written
    /// and what a hand-back of trial-only cells takes off it, and its writes but the trial-only ones (a clock floor or lock,
    /// the F8 request). A run's crash goes to the trial in force at its end only when these are the ones it measured: the
    /// service handing the fans back mid-run (its agent gone quiet) writes the trial again (a new "At") with the same settings. Null when no trial
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
        "Record a crash of the trial that ran, then restore the user's kept daily profile or the captured original baseline when none exists. Driver resets are also recorded by the service; avoid attributing a failure twice.")]
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
        "End the tuning session and restore the user's kept daily profile, or return Clocktail's changes to the captured baseline when no daily exists. Nothing is kept automatically.")]
    public string EndSession()
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "end");
        // "end" wraps the profile's answer (or a recovery's) in "back", beside the trial's data point.
        var back = r.Result is { } res && res.TryGetProperty("back", out var b) && b.ValueKind == JsonValueKind.Object ? b : (JsonElement?)null;
        var point = r.Result is { } res2 && res2.TryGetProperty("point", out var p) ? p : (JsonElement?)null;
        return Applied(r with { Result = back }, point);
    }

    [McpServerTool(Name = "data_points", ReadOnly = true, OpenWorld = false), Description(
        "Read recorded trials, tested curve points, workloads and outcomes for this GPU. Results are evidence about this GPU, not stable defaults for the RTX 50 series.")]
    public string DataPoints() => Guard(() =>
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        if (!r.Ok || r.Result is not { } res) return Reply(r);
        var read = new VfReadStats();
        var byPoint = ByPoint(res.GetProperty("data").GetString()!, read);
        return Out(new { data = res.GetProperty("data"), points = res.GetProperty("points"), by_point = byPoint, vf_history_read = ReadReport(read) });
    });

    /// <summary>One curve point's record across every data point the observations cover (the residency samples and the
    /// broker's always-on V/F history, merged by <see cref="VfTimeline"/>).</summary>
    public sealed class PointRecord
    {
        /// <summary>The highest offset a stable / survived trial ran the point at, and the lowest a crash under a curve
        /// trial found it at (kHz).</summary>
        public int? StableKhz, SurvivedKhz, CrashKhz;
        /// <summary>The highest clock (P90) any stable trial ran the point at, and the lowest a curve-trial crash ran it at.</summary>
        public uint? StableMhz, CrashMhz;
        /// <summary>The clock (P90) the card ran the point at in the stable trials at <see cref="StableKhz"/> (the highest
        /// of them): below the setting when a clock lock, the power limit, temperature or another core offset held it.</summary>
        public uint? StableRanMhz;
        /// <summary>The observed time the point ran at <see cref="StableKhz"/> in stable trials, by observation (its UTC
        /// ticks): the parts of its span (seconds after the look) that stable marks covered, as a union. Two marks of one
        /// trial that both cover a span count it once, and a mark that covers less of it does not shrink what another
        /// covered (<see cref="VfTimeline.Within"/>).</summary>
        public readonly Dictionary<long, List<(double Start, double End)>> StableCovered = [];
        public double StableSeconds => StableCovered.Values.Sum(spans => spans.Sum(s => s.End - s.Start));

        /// <summary>Adds a covered part of one observation's span to the union.</summary>
        public void Cover(WeightedObservation o)
        {
            if (o.Seconds <= 0) return;
            long key = o.At.UtcTicks;
            if (!StableCovered.TryGetValue(key, out var spans)) StableCovered[key] = spans = [];
            double start = o.SpanStart, end = o.SpanEnd;
            for (int i = spans.Count - 1; i >= 0; i--)
                if (spans[i].Start <= end && start <= spans[i].End)
                {
                    start = Math.Min(start, spans[i].Start);
                    end = Math.Max(end, spans[i].End);
                    spans.RemoveAt(i);
                }
            spans.Add((start, end));
        }
        /// <summary>Crashes under a curve trial; and crashes under any other trial (memory, XBAR, power, a limit, the fans,
        /// a core P0 offset), which ran here but are not the curve's evidence.</summary>
        public int Crashes, OtherCrashes;
    }

    private object ByPoint(string dataPath, VfReadStats? read = null)
    {
        var all = Experiment.ReadLast(dataPath, int.MaxValue);
        if (all.Count == 0) return Array.Empty<object>();
        var look = card.Read();
        var daily = DailyOffsets(out _);
        var records = PointRecords(all, look.Curve, dataPath, read: read);
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
                crashes_under_other_trials = v.OtherCrashes,
            };
        }).ToList();
    }

    /// <summary>Every curve point's record across the data points the residency samples cover: the highest offset a stable
    /// or survived trial ran it at, the lowest a crash under a curve trial found it at (the curve editor draws these beside
    /// its points).</summary>
    private SortedDictionary<int, PointRecord> PointRecords(IReadOnlyList<DataPoint> all, VfCurveSnapshot curve, string dataPath,
        Action<DataPoint, int>? visit = null, VfReadStats? read = null)
    {
        if (all.Count == 0) return [];
        var ticks = residency.Between(all.Min(Experiment.StartOf), DateTimeOffset.Now);
        return PointRecordsFrom(all, curve, HistoryTimeline(all, ticks, VfHistoryFile.DirBeside(dataPath), read), visit);
    }

    /// <summary>How far past a trial's window the V/F history is read, so the window's last look has its successor
    /// (<see cref="VfDwell"/>).</summary>
    private static readonly TimeSpan WindowMargin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The observations the trials' windows hold, every source in one timeline: the residency samples given, and the
    /// broker's always-on V/F history in <paramref name="vfDir"/> (only the trials' windows of it are read, not the months
    /// between). Each counts for the time it stands for, whichever source it came from (<see cref="VfDwell"/>).
    /// </summary>
    public static VfTimeline HistoryTimeline(IReadOnlyList<DataPoint> all, IReadOnlyList<Residency.Tick> residencyTicks, string? vfDir,
        VfReadStats? read = null)
    {
        var light = VfHistoryStore.ReadWindows(vfDir, all.Select(d => (Experiment.StartOf(d) - WindowMargin, d.At + WindowMargin)), read);
        return VfTimeline.Build(residencyTicks.Select(Observation).Concat(light));
    }

    /// <summary>A residency sample as an observation: its utilization and power with it.</summary>
    public static VfObservation Observation(Residency.Tick t) => new(t.At, t.Uv, t.Mhz, VfSource.Residency, t.Util, t.W);

    public static VfTimeline Timeline(IReadOnlyList<Residency.Tick> ticks) => VfTimeline.Build(ticks.Select(Observation));

    /// <summary><see cref="PointRecords"/> over residency samples alone, one a second (<see cref="Residency.OnePerSecond"/>).</summary>
    public static SortedDictionary<int, PointRecord> PointRecordsFrom(IReadOnlyList<DataPoint> all, VfCurveSnapshot curve,
        IReadOnlyList<Residency.Tick> samples, Action<DataPoint, int>? visit = null) =>
        PointRecordsFrom(all, curve, Timeline(samples), visit);

    /// <summary>
    /// Every curve point's record over the observations of every source. An observation says where the card ran; only a
    /// data point's own outcome says how that went: an observation outside every stable or survived trial is no stable
    /// evidence, however long the card ran there, and no observation makes one.
    /// </summary>
    public static SortedDictionary<int, PointRecord> PointRecordsFrom(IReadOnlyList<DataPoint> all, VfCurveSnapshot curve,
        VfTimeline timeline, Action<DataPoint, int>? visit = null)
    {
        var records = new SortedDictionary<int, PointRecord>();
        foreach (var d in all)
        {
            var start = Experiment.StartOf(d);
            // The trial's window [start, end): only observed time inside it counts (VfTimeline.Within).
            var during = timeline.Within(start, d.At);
            if (during.Sum(o => o.Seconds) < MinObservedSeconds) continue;   // before the samplers, or too short to say anything
            int[] offsets;
            try { offsets = VfOffsets.Decode(d.Curve); } catch (FormatException) { continue; }
            // A crash belongs to where the card was when it fell over, not to the whole trial; and to the curve only when
            // the trial changed the curve (under any other the curve was merely on the card).
            if (d.Outcome == Experiment.Crash)
            {
                bool curveTrial = Experiment.CurveChange(d.Change);
                foreach (var (index, clock) in CrashAt(during, d.CrashUv, curve))
                {
                    int off = offsets.ElementAtOrDefault(index);
                    if (!records.TryGetValue(index, out var rec)) records[index] = rec = new PointRecord();
                    if (!curveTrial) { rec.OtherCrashes++; continue; }
                    rec.Crashes++;
                    if (rec.CrashKhz is not { } kc || off < kc) rec.CrashKhz = off;
                    if (clock is { } c && (rec.CrashMhz is not { } cm || c < cm)) rec.CrashMhz = c;
                }
                continue;
            }
            int[]? near = null;
            foreach (var s in Stays(during, curve).Where(s => s.Share >= 0.1))
            {
                if (d.Outcome is Experiment.Stable or Experiment.Survived) visit?.Invoke(d, s.Index);
                int off = offsets.ElementAtOrDefault(s.Index);
                if (!records.TryGetValue(s.Index, out var rec)) records[s.Index] = rec = new PointRecord();
                switch (d.Outcome)
                {
                    case Experiment.Stable:
                        if (rec.StableMhz is not { } sm || s.ClockMhz > sm) rec.StableMhz = s.ClockMhz;
                        if (rec.StableKhz is not { } k || off >= k)
                        {
                            // A higher offset starts the count again; at the same one, the seconds this trial ran here join it.
                            if (rec.StableKhz != off) { rec.StableCovered.Clear(); rec.StableRanMhz = null; }
                            rec.StableKhz = off;
                            if (rec.StableRanMhz is not { } r || s.ClockMhz > r) rec.StableRanMhz = s.ClockMhz;
                            near ??= [.. during.Select(t => Nearest(curve, t.Uv)!.Index)];
                            for (int i = 0; i < during.Count; i++)
                                if (near[i] == s.Index) rec.Cover(during[i]);
                        }
                        break;
                    case Experiment.Survived when rec.SurvivedKhz is not { } ks || off > ks:
                        rec.SurvivedKhz = off;
                        break;
                }
            }
        }
        return records;
    }

    /// <summary>
    /// The curve points a crash data point fell over at, with the clock the card ran there (null when no sample sat on it).
    /// A hung GPU keeps answering with frozen readings until the reset (for the whole TdrDelay, 30 s on the reference
    /// machine: 3105 MHz at 1030 mV, util 100 %, ~140 W), so the trial's samples are cut at the frozen tail first, by the
    /// flight recorder's rule, and the last 30 s before it are what counts. When the flight recorder found the crash
    /// voltage, the crash is the point nearest it alone.
    /// </summary>
    public static IReadOnlyList<(int Index, uint? ClockMhz)> CrashAt(IReadOnlyList<Residency.Tick> during, uint? crashUv, VfCurveSnapshot curve) =>
        CrashAt(Timeline(during).Items, crashUv, curve);

    /// <summary>
    /// <see cref="CrashAt(IReadOnlyList{Residency.Tick}, uint?, VfCurveSnapshot)"/> over observations of every source. The
    /// frozen tail is found where it can be: in the residency samples, which carry utilization and power (a voltage and a
    /// clock alone cannot tell a hang from a steady load), and every observation from it on is cut. The broker's V/F
    /// history never wrote a hang's looks in the first place (<c>VfHistoryRecorder</c> withholds them on the flight
    /// recorder's own hang boundary), so its observations need no cut of their own.
    /// </summary>
    public static IReadOnlyList<(int Index, uint? ClockMhz)> CrashAt(IReadOnlyList<WeightedObservation> during, uint? crashUv, VfCurveSnapshot curve)
    {
        if (curve.Points.Count == 0 || during.Count == 0) return [];
        // A tail that is the whole trial is a load pinned at the curve's cap, not a hang: only one power fell into is cut.
        var rich = during.Where(o => o.Observation.UtilPercent != null).ToList();
        var frozen = rich.Count == 0 ? null : FlightRecorder.FrozenFrom([.. rich.Select(o => new FlightSample(o.At, 0, o.Mhz, o.Uv / 1000,
            o.Observation.Watts, null, o.Observation.UtilPercent, null))]);
        IReadOnlyList<WeightedObservation> live = frozen == null || frozen.At == rich[0].At ? during : [.. during.Where(t => t.At < frozen.At)];
        List<WeightedObservation> last = live.Count == 0 ? [] : [.. live.Where(t => t.At >= live[^1].At - TimeSpan.FromSeconds(30))];
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

    /// <summary>Observed time under which a trial says nothing about the points it ran (ten residency seconds before).</summary>
    public const double MinObservedSeconds = 10;

    /// <summary>A curve point a run of observations sat on, its share of their observed time, and the clock it ran there
    /// (time-weighted P90).</summary>
    private sealed record Stay(int Index, double Mv, double Share, double Seconds, uint ClockMhz);

    /// <summary>The curve points a run of observations sat on (nearest by voltage), most observed time first.</summary>
    private static List<Stay> Stays(IReadOnlyList<WeightedObservation> obs, VfCurveSnapshot curve)
    {
        var timed = obs.Where(o => o.Seconds > 0).ToList();
        double total = timed.Sum(o => o.Seconds);
        if (timed.Count == 0 || total <= 0 || curve.Points.Count == 0) return [];
        return timed.GroupBy(t => Nearest(curve, t.Uv)!)
            .Select(g =>
            {
                var group = g.ToList();
                double seconds = group.Sum(o => o.Seconds);
                return new Stay(g.Key.Index, g.Key.VoltageUv / 1000.0, Math.Round(seconds / total, 2), seconds, VfTimeline.P90Mhz(group));
            })
            .OrderByDescending(s => s.Seconds).ToList();
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
        var all = Timeline(ticks).Items;
        var end = all.Where(t => t.At >= all[^1].At - TimeSpan.FromSeconds(30)).ToList();
        last = $"\n\nLast 30 s: {Describe(Stays(end, curve))}";
        return $"\n\nUnder load ({ticks.Count} s): {Describe(Stays(all, curve))}";
    }
}
