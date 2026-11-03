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

    [Fact]
    public void The_fast_haze_matches_the_general_timeline_to_the_last_digit()
    {
        // Looks at about 4 a second over a midnight, with gaps and jitter, and residency seconds over part of it.
        var rnd = new Random(7);
        var looks = new List<VfObservation>();
        var at = T.AddHours(-13);
        for (int i = 0; i < 200_000; i++)
        {
            at = at.AddMilliseconds(rnd.Next(10) == 0 ? rnd.Next(300, 5_000) : 250 + rnd.Next(-20, 21));
            looks.Add(new VfObservation(at, 826_000 + (uint)rnd.Next(0, 200_000), 1500 + (uint)rnd.Next(0, 1500), VfSource.Lightweight));
        }
        VfHistoryStore.Append(VfDir, looks);
        var ticks = new List<Residency.Tick>();
        for (var t = T.AddHours(-12.5); t < T.AddHours(-11.5); t = t.AddMilliseconds(rnd.Next(900, 1600)))
            ticks.Add(new Residency.Tick(t, 900_000 + (uint)rnd.Next(0, 50_000), 2500 + (uint)rnd.Next(0, 200), 100, 400));
        var from = T.AddHours(-12.75);
        var to = at.AddMinutes(-10);
        var general = TuningTools.ObservedRuns(VfHistoryStore.Read(VfDir, from, to), ticks);
        var stats = new VfReadStats();
        var fast = TuningTools.ObservedRuns(VfDir, from, to, ticks, stats);
        Assert.Equal(general.LightweightSeconds, fast.LightweightSeconds);
        Assert.Equal(general.ResidencySeconds, fast.ResidencySeconds);
        Assert.Equal(general.Bins.OrderBy(b => b.Mv).ThenBy(b => b.Mhz), fast.Bins.OrderBy(b => b.Mv).ThenBy(b => b.Mhz));
        Assert.True(fast.ResidencySeconds > 0);
        Assert.Equal(0, stats.UnreadableFiles);
        Assert.True(stats.Files >= 2);
    }

    [Fact]
    public void A_history_file_that_cannot_be_read_is_reported_not_taken_for_no_record()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        VfHistoryStore.Append(VfDir, Light(T, 60));
        var path = Path.Combine(VfDir, VfHistoryFile.DayFileName(VfHistoryFile.UtcDay(T)));
        var stats = new VfReadStats();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Empty(TuningTools.ObservedRuns(VfDir, T, T.AddMinutes(2), [], stats).Bins);
        Assert.Equal(1, stats.UnreadableFiles);
        var report = System.Text.Json.JsonSerializer.SerializeToElement(TuningTools.ReadReport(stats));
        Assert.Equal(1, report.GetProperty("unreadable_files").GetInt32());
        Assert.Contains("missing", report.GetProperty("note").GetString());
    }

    [Fact]
    public void On_one_day_a_bin_needs_a_second_and_looks_that_passed_through_are_not_drawn()
    {
        List<RunBin> bins = [new(950, 2700, 600), new(900, 2400, 1.0), .. Enumerable.Range(0, 300).Select(i => new RunBin(830 + i % 60, 300 + i * 7.5, 0.25))];
        var s = TuningTools.Significant(bins, activeDays: 1);
        Assert.Equal([new RunBin(950, 2700, 600), new RunBin(900, 2400, 1.0)], s.Bins);
        Assert.Equal(300, s.HiddenBins);
        Assert.Equal(75, s.HiddenSeconds, 9);
        Assert.Equal(1, s.MinSeconds);
    }

    /// <summary>One day of real use as the 0.8.9 review saw it: a loaded point, a light-load cluster at 830 mV spread
    /// over many clocks, and single looks passing through almost every clock.</summary>
    private static List<RunBin> OneDay() =>
    [
        new(950, 2700, 900), new(955, 2707.5, 300),
        .. Enumerable.Range(0, 30).Select(i => new RunBin(830, 682.5 + i * 7.5, 7)),
        .. Enumerable.Range(0, 400).Select(i => new RunBin(800 + i % 90, 300 + i * 7.5, 0.25)),
    ];

    [Fact]
    public void With_the_same_use_what_is_drawn_does_not_change_as_the_history_grows()
    {
        var day = TuningTools.Significant(OneDay(), activeDays: 1);
        var sixty = TuningTools.Significant([.. OneDay().Select(b => b with { Seconds = b.Seconds * 60 })], activeDays: 60);
        Assert.Equal(day.Bins.Select(b => (b.Mv, b.Mhz)), sixty.Bins.Select(b => (b.Mv, b.Mhz)));
        Assert.Equal(day.HiddenBins, sixty.HiddenBins);
        Assert.Equal(32, day.Bins.Count);   // the loaded point and the whole 830 mV cluster, none of the passing looks
    }

    [Fact]
    public void Looks_that_pass_through_a_bin_now_and_then_never_pile_up_past_the_floor()
    {
        // A quarter second a day in each of 400 bins for 60 days: 15 s each, under the 60 s floor of 60 days.
        var s = TuningTools.Significant([new(950, 2700, 36_000), .. Enumerable.Range(0, 400).Select(i => new RunBin(800 + i % 90, 300 + i * 7.5, 15))], activeDays: 60);
        Assert.Single(s.Bins);
        Assert.Equal(60, s.MinSeconds);
    }

    [Fact]
    public void A_ten_minute_region_spread_by_jitter_is_drawn_after_months_of_heavy_use()
    {
        // The 0.8.9 review: 10 h a day for 60 days put 0.05 % of all time at 18 min and hid a real 10 min region, more so
        // when jitter spread it over neighbouring clocks. Per day of use the floor is 60 s, whatever the hours.
        List<RunBin> bins = [new(950, 2700, 60 * 36_000), .. Enumerable.Range(0, 5).Select(i => new RunBin(1000, 2850 + i * 7.5, 120))];
        var s = TuningTools.Significant(bins, activeDays: 60);
        Assert.Equal(5, s.Bins.Count(b => b.Mv == 1000));
        Assert.True(0.0005 * bins.Sum(b => b.Seconds) > 120);   // what 0.8.9 would have required of each of them
    }

    [Fact]
    public void Nothing_observed_draws_nothing_and_hides_nothing()
    {
        var s = TuningTools.Significant([], activeDays: 0);
        Assert.Empty(s.Bins);
        Assert.Equal(0, s.HiddenBins);
        Assert.Equal(0, s.HiddenSeconds);
        Assert.Null(TuningTools.RunsSummary(s, []));
    }

    [Fact]
    public void Active_days_are_the_UTC_days_that_hold_observed_time_in_both_reads()
    {
        VfHistoryStore.Append(VfDir, [.. Light(T, 10), .. Light(T.AddDays(1), 10), .. Light(T.AddDays(3), 10)]);
        var fast = TuningTools.ObservedRuns(VfDir, T.AddDays(-1), T.AddDays(5), []);
        var general = TuningTools.ObservedRuns(VfHistoryStore.Read(VfDir, T.AddDays(-1), T.AddDays(5)), []);
        Assert.Equal(3, fast.ActiveDays);
        Assert.Equal(3, general.ActiveDays);
        Assert.Equal(3, TuningTools.ObservedRuns(VfDir, T.AddDays(-1), T.AddDays(5), Ticks(T, 30)).ActiveDays);   // residency on a day already counted
        Assert.Equal(4, TuningTools.ObservedRuns(VfDir, T.AddDays(-1), T.AddDays(5), Ticks(T.AddDays(2), 30)).ActiveDays);   // and on a day of its own
    }

    [Fact]
    public void The_windows_line_says_what_is_drawn_and_what_it_holds()
    {
        var all = OneDay();
        var line = TuningTools.RunsSummary(TuningTools.Significant(all, 1), all);
        Assert.StartsWith($"Observed V/F: 32 of {all.Count} bins drawn, holding ", line);
        Assert.Contains("1 s per day of use", line);
        Assert.Contains("Ran for", line);
    }

    [Fact]
    public void A_points_ran_for_counts_every_bin_drawn_or_not()
    {
        var all = OneDay();
        var drawn = TuningTools.Significant(all, 1).Bins;
        var data = new EditorData("GPU", [830], [700_000], [0], 1, 0, 0, null, null, [], [], drawn, null, null, [], null, [], TwoPoints, all);
        Assert.Equal(all.Where(b => b.Mv == 830).Sum(b => b.Seconds), data.RunsForTotals.Where(b => b.Mv == 830).Sum(b => b.Seconds));
        Assert.Same(drawn, new EditorData("GPU", [830], [700_000], [0], 1, 0, 0, null, null, [], [], drawn, null, null, [], null, [], TwoPoints).RunsForTotals);
    }

    [Fact]
    public void A_trial_history_file_that_cannot_be_read_is_reported_whatever_its_age()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        var old = T.AddDays(-90);
        VfHistoryStore.Append(VfDir, Light(old, 60));
        var path = Path.Combine(VfDir, VfHistoryFile.DayFileName(VfHistoryFile.UtcDay(old)));
        var read = new VfReadStats();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            TuningTools.HistoryTimeline([Trial(old, 60)], [], VfDir, read);
        Assert.Equal(1, read.UnreadableFiles);
        Assert.NotNull(System.Text.Json.JsonSerializer.SerializeToElement(TuningTools.ReadReport(read)).GetProperty("note").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
