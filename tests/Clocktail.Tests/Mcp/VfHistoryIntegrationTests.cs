using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;
using Clocktail.Mcp.CurveEditor;

namespace Clocktail.Tests.Mcp;

/// <summary>The broker's V/F history in the curve history: read by the editor's haze and by by_point from the same day
/// files the broker writes, weighted by observed time like the residency samples, never a stable result by itself, and
/// leaving crash attribution as it was.</summary>
public sealed class VfHistoryIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clocktail-vfint-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_root, Experiment.DataFileName);
    private string VfDir => VfHistoryFile.DirBeside(DataPath);
    private static readonly DateTimeOffset T = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly VfCurveSnapshot TwoPoints = new(VfMode.Regional, VfLayoutId.Regional132, [], [
        new(0, 900000, 2500000, 2500000, 0, 900000),
        new(1, 910000, 2600000, 2600000, 0, 910000),
    ], 0, [], [], []);

    private static List<VfObservation> Light(DateTimeOffset from, double seconds, uint uv = 900_000, uint mhz = 2500) =>
        [.. Enumerable.Range(0, (int)(seconds * 4)).Select(i => new VfObservation(from.AddMilliseconds(i * 250), uv, mhz, VfSource.Lightweight))];

    private static List<Residency.Tick> Ticks(DateTimeOffset from, int seconds, uint uv = 900_000, uint mhz = 2500, int ms = 500) =>
        [.. Enumerable.Range(0, seconds).Select(i => new Residency.Tick(from.AddSeconds(i).AddMilliseconds(ms), uv, mhz, 100, 400))];

    private static DataPoint Trial(DateTimeOffset trialAt, int seconds, string outcome = Experiment.Stable, string curve = "0*2") =>
        new(trialAt.AddSeconds(seconds), "Game", outcome, Math.Round(seconds / 60.0, 1), "curve editor: points 0-1",
            900000, 2500, curve, TrialAt: trialAt);

    [Fact]
    public void The_editors_haze_reads_the_broker_day_files_by_observed_time()
    {
        VfHistoryStore.Append(VfDir, Light(T, 60));                       // a minute at a light load, four looks a second
        var runs = TuningTools.ObservedRuns(VfHistoryStore.Read(VfDir, T.AddDays(-60), T.AddHours(1)), []);
        var bin = Assert.Single(runs.Bins);
        Assert.Equal(900, bin.Mv);
        Assert.Equal(2497.5, bin.Mhz);                                    // the 7.5 MHz step 2500 rounds to
        Assert.Equal(60, bin.Seconds, 6);                                 // a minute, not 240
        Assert.Equal(60, runs.LightweightSeconds, 6);
        Assert.Equal(0, runs.ResidencySeconds);
    }

    [Fact]
    public void Residency_and_the_broker_history_over_the_same_minute_count_it_once()
    {
        VfHistoryStore.Append(VfDir, Light(T, 60));
        var runs = TuningTools.ObservedRuns(VfHistoryStore.Read(VfDir, T, T.AddHours(1)), Ticks(T, 60, ms: 600));
        Assert.Equal(60, runs.Bins.Sum(b => b.Seconds), 6);
        Assert.True(runs.ResidencySeconds > 0 && runs.LightweightSeconds > 0);
        // Residency alone, as before this history: one second each.
        var before = TuningTools.ObservedRuns([], Ticks(T, 60));
        Assert.Equal(60, before.Bins.Sum(b => b.Seconds), 6);
    }

    [Fact]
    public void Transitions_within_a_second_reach_the_haze_as_separate_points()
    {
        VfObservation[] second =
        [
            new(T, 870_000, 2400, VfSource.Lightweight),
            new(T.AddMilliseconds(250), 905_000, 2700, VfSource.Lightweight),
            new(T.AddMilliseconds(500), 950_000, 2900, VfSource.Lightweight),
        ];
        VfHistoryStore.Append(VfDir, second);
        var runs = TuningTools.ObservedRuns(VfHistoryStore.Read(VfDir, T, T.AddMinutes(1)), Ticks(T, 1, uv: 870_000, mhz: 2400, ms: 0));
        Assert.Equal(3, runs.Bins.Count);
        Assert.Equal(0.75, runs.Bins.Sum(b => b.Seconds), 6);
    }

    [Fact]
    public void A_stable_trial_ran_on_broker_looks_alone_counts_its_observed_seconds_like_residency()
    {
        var trial = Trial(T, 30);
        VfHistoryStore.Append(VfDir, Light(T, 30));
        var fromLight = TuningTools.PointRecordsFrom([trial], TwoPoints, TuningTools.HistoryTimeline([trial], [], VfDir));
        // Residency on the second boundaries, as the looks are: both cover the same window from its start.
        var fromResidency = TuningTools.PointRecordsFrom([trial], TwoPoints, Ticks(T, 30, ms: 0));
        Assert.Equal(fromResidency[0].StableSeconds, fromLight[0].StableSeconds, 6);
        Assert.Equal(30, fromLight[0].StableSeconds, 6);                 // not 120 looks
        Assert.Equal(fromResidency[0].StableKhz, fromLight[0].StableKhz);
        Assert.Equal(2500u, fromLight[0].StableRanMhz);
    }

    [Fact]
    public void Both_sources_inside_one_trial_do_not_double_its_stable_time()
    {
        var trial = Trial(T, 60);
        VfHistoryStore.Append(VfDir, Light(T, 60));
        var rec = TuningTools.PointRecordsFrom([trial], TwoPoints, TuningTools.HistoryTimeline([trial], Ticks(T, 60, ms: 600), VfDir));
        Assert.InRange(rec[0].StableSeconds, 59.5, 60.0001);
    }

    [Fact]
    public void Observed_operation_alone_is_never_a_stable_result()
    {
        // An hour on point 1 with no trial on record: where the card ran, not how it held.
        VfHistoryStore.Append(VfDir, Light(T, 3600, uv: 910_000, mhz: 2600));
        Assert.Empty(TuningTools.PointRecordsFrom([], TwoPoints, VfTimeline.Build(VfHistoryStore.Read(VfDir, T, T.AddHours(2)))));
        // Inside a crash trial or a survived one, it stays what the trial's outcome says.
        var crash = Trial(T.AddMinutes(10), 60, Experiment.Crash) with { Change = "memory offset +0 → +1000 MHz" };
        var survived = Trial(T.AddMinutes(20), 60, Experiment.Survived, "15000*2");
        var records = TuningTools.PointRecordsFrom([crash, survived], TwoPoints, TuningTools.HistoryTimeline([crash, survived], [], VfDir));
        var p1 = records[1];
        Assert.Null(p1.StableKhz);
        Assert.Equal(0, p1.StableSeconds);
        Assert.Equal(15000, p1.SurvivedKhz);
        Assert.Equal(1, p1.OtherCrashes);   // a crash under a memory trial: ran here, not the curve's evidence
        Assert.Equal(0, p1.Crashes);
    }

    [Fact]
    public void Broker_looks_in_a_hangs_frozen_tail_get_no_credit_and_the_crash_stays_where_the_card_ran()
    {
        var curve = new VfCurveSnapshot(default, default, new byte[32], [.. Enumerable.Range(0, 25).Select(i =>
        {
            uint uv = 850_000 + (uint)i * 10_000;
            uint khz = 2_700_000 + (uint)i * 20_000;
            return new VfPoint(70 + i, uv, khz, khz, 0, uv);
        })], 0, [], [], []);
        var ticks = new List<Residency.Tick>();
        for (int s = 0; s < 60; s++) ticks.Add(new(T.AddSeconds(s), 910_000, 2895, 100, 560));
        for (int s = 0; s < 30; s++) ticks.Add(new(T.AddSeconds(60 + s), 1_030_000, 3105, 100, 140));   // frozen, power fallen
        // Broker looks over the same 90 s, as if a hang's had reached the disk: residency's frozen tail cuts them too.
        var light = Light(T, 60, 910_000, 2895).Concat(Light(T.AddSeconds(60), 30, 1_030_000, 3105)).ToList();
        var timeline = VfTimeline.Build(ticks.Select(TuningTools.Observation).Concat(light));
        var at = Assert.Single(TuningTools.CrashAt(timeline.Items, null, curve));
        Assert.Equal(76, at.Index);
        Assert.Equal(2895u, at.ClockMhz);
        Assert.Equal(TuningTools.CrashAt(ticks, null, curve), TuningTools.CrashAt(timeline.Items, null, curve));
    }

    [Fact]
    public void The_editor_projection_shows_evidence_from_broker_looks_and_keeps_old_tests_selectable()
    {
        var stable = Trial(T, 30);
        var old = new DataPoint(T.AddDays(-1), "Game", Experiment.Crash, 1, "curve editor: points 0-1", 910000, 2600, "0*2", CrashUv: 910000);
        VfHistoryStore.Append(VfDir, Light(T, 30));
        var h = TuningTools.ProjectEditorHistory([stable, old], [900, 910], [2500000, 2600000], TwoPoints,
            TuningTools.HistoryTimeline([stable, old], [], VfDir));
        var ev = Assert.Single(h.Evidence);
        Assert.Equal(0, ev.Index);
        Assert.Equal(30, ev.StableSeconds, 6);
        var crash = Assert.Single(h.Crashes);                    // no samples: placed by its recorded crash voltage
        Assert.Equal(1, crash.Index);
        Assert.Equal(2, h.Tests.Count);
    }

    private static SortedDictionary<int, TuningTools.PointRecord> Records(IReadOnlyList<DataPoint> trials, IEnumerable<VfObservation> obs) =>
        TuningTools.PointRecordsFrom(trials, TwoPoints, VfTimeline.Build(obs));

    [Fact]
    public void A_trial_that_starts_inside_a_look_counts_from_its_start()
    {
        var late = Trial(T.AddMilliseconds(100), 20) with { At = T.AddSeconds(20) };   // [0.1 s, 20 s)
        Assert.Equal(19.9, Records([late], Light(T, 22))[0].StableSeconds, 9);
    }

    [Fact]
    public void The_minimum_observed_time_gate_is_the_trials_own_time()
    {
        // Exactly 10 s inside the window passes (looks go on after it); 9.9 s does not, whatever follows.
        Assert.True(Records([Trial(T, 10)], Light(T, 12)).ContainsKey(0));
        var short9 = Trial(T.AddMilliseconds(100), 10) with { At = T.AddSeconds(10) };
        Assert.Empty(Records([short9], Light(T.AddMilliseconds(100), 9.9).Concat(Light(T.AddSeconds(10), 2))));
    }

    [Fact]
    public void Overlapping_marks_of_one_trial_cover_their_union()
    {
        // Two marks of one trial at the same setting, [0, 10) and [5, 15): 15 s, not 20.
        var a = Trial(T, 10);
        var b = Trial(T.AddSeconds(5), 10);
        Assert.Equal(15, Records([a, b], Light(T, 20))[0].StableSeconds, 9);
        Assert.Equal(15, Records([b, a], Light(T, 20))[0].StableSeconds, 9);
    }

    [Fact]
    public void A_shorter_mark_does_not_shrink_what_a_longer_one_covered()
    {
        // [0, 10.1) and [0, 10.05): the look at 10.0 s is covered 0.1 s by one, 0.05 s by the other.
        var longer = Trial(T, 10) with { At = T.AddMilliseconds(10_100) };
        var shorter = Trial(T, 10) with { At = T.AddMilliseconds(10_050) };
        Assert.Equal(10.1, Records([longer, shorter], Light(T, 12))[0].StableSeconds, 9);
        Assert.Equal(10.1, Records([shorter, longer], Light(T, 12))[0].StableSeconds, 9);
    }

    [Fact]
    public void Shares_and_the_ran_clock_count_only_the_trials_window()
    {
        // 10 s on point 0 in the trial, then a minute on point 1 after it: none of that minute is the trial's.
        var trial = Trial(T, 10);
        var obs = Light(T, 10).Concat(Light(T.AddSeconds(10), 60, uv: 910_000, mhz: 2600));
        var rec = Records([trial], obs);
        Assert.Equal(10, rec[0].StableSeconds, 9);
        Assert.False(rec.ContainsKey(1));
        Assert.Equal(2500u, rec[0].StableRanMhz);
    }

    [Fact]
    public void Residency_only_history_reads_as_it_did_before()
    {
        var a = Trial(T, 30);
        var b = Trial(T.AddSeconds(100), 30);
        var ticks = Ticks(T, 30).Concat(Ticks(T.AddSeconds(100), 30)).ToList();
        var viaTicks = TuningTools.PointRecordsFrom([a, b], TwoPoints, ticks);
        var viaTimeline = TuningTools.PointRecordsFrom([a, b], TwoPoints, TuningTools.HistoryTimeline([a, b], ticks, VfDir));   // no files yet
        Assert.Equal(59, viaTicks[0].StableSeconds, 9);   // 2 × 29.5 s inside the windows (samples at +0.5 s)
        Assert.Equal(viaTicks[0].StableSeconds, viaTimeline[0].StableSeconds);
        Assert.Equal(viaTicks[0].StableRanMhz, viaTimeline[0].StableRanMhz);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
