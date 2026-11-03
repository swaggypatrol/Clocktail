using System.Text.Json;
using Clocktail.Domain.Profiles;
using Clocktail.Mcp.CurveEditor;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

public sealed class TestHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-history-" + Guid.NewGuid().ToString("N"));
    private string PathToData => Path.Combine(_dir, Experiment.DataFileName);
    private static DataPoint Point(string outcome = Experiment.Stable) =>
        new(DateTimeOffset.UtcNow, "Game", outcome, 2, "curve editor: points 65-70", 900000, 2800, "0*127");

    [Fact]
    public void Delete_persists_across_reads_without_rewriting_the_journal()
    {
        var p = Point();
        var other = p with { Game = "Another test at the same time" };
        Experiment.Append(PathToData, p);
        Experiment.Append(PathToData, other);
        byte[] journal = File.ReadAllBytes(PathToData);
        Experiment.DeletePoint(PathToData, p);

        Assert.Equal(other, Assert.Single(Experiment.ReadLast(PathToData, 100)));
        Assert.Equal(journal, File.ReadAllBytes(PathToData));
        Assert.Equal(2, Experiment.ReadLast(PathToData, 100, includeDeleted: true).Count);
        Assert.Single(Directory.GetFiles(Experiment.DeletedDirectory(PathToData), "*.json"));
        // A new instance, deserialized just as a newly opened window would see it, has the same identity.
        var reloaded = JsonSerializer.Deserialize<DataPoint>(JsonSerializer.Serialize(p))!;
        Experiment.DeletePoint(PathToData, reloaded);
        Assert.Single(Directory.GetFiles(Experiment.DeletedDirectory(PathToData), "*.json"));
    }

    [Fact]
    public void Last_count_is_applied_after_deletions_and_malformed_lines_are_preserved()
    {
        var a = Point();
        var b = a with { At = a.At.AddSeconds(1) };
        Experiment.Append(PathToData, a);
        File.AppendAllText(PathToData, "not json\n\n");
        Experiment.Append(PathToData, b);
        Experiment.DeletePoint(PathToData, b);
        Assert.Equal(a, Assert.Single(Experiment.ReadLast(PathToData, 1)));
        Assert.Contains("not json", File.ReadAllText(PathToData));
    }

    [Fact]
    public void Removing_a_crash_does_not_replenish_a_sessions_budget_or_erase_recovery_evidence()
    {
        var p = Point(Experiment.Crash) with { TrialAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
        Experiment.Append(PathToData, p);
        var session = new Experiment(p.Game, null, p.At.AddMinutes(-5), CrashBudget: 1);
        Assert.Equal(1, session.CrashesSince(PathToData));
        Experiment.DeletePoint(PathToData, p);
        Assert.Empty(Experiment.ReadLast(PathToData, 100));
        Assert.Equal(1, session.CrashesSince(PathToData));
        Assert.Equal(p.TrialAt, Assert.Single(Experiment.ReadLast(PathToData, 100, includeDeleted: true)).TrialAt);
    }

    [Fact]
    public void Unknown_records_and_unwritable_deletion_paths_fail_without_changing_history()
    {
        var p = Point();
        Experiment.Append(PathToData, p);
        Assert.Throws<InvalidOperationException>(() => Experiment.DeletePoint(PathToData, p with { Note = "not recorded" }));
        Assert.False(Directory.Exists(Experiment.DeletedDirectory(PathToData)));
        File.WriteAllText(Experiment.DeletedDirectory(PathToData), "a file, not a directory");
        Assert.ThrowsAny<IOException>(() => Experiment.DeletePoint(PathToData, p));
        Assert.Equal(p, Assert.Single(Experiment.ReadLast(PathToData, 10)));
    }

    [Fact]
    public void Concurrent_deletes_and_broker_appends_do_not_lose_records()
    {
        var p = Point();
        Experiment.Append(PathToData, p);
        Parallel.Invoke(
            () => Parallel.For(0, 12, _ => Experiment.DeletePoint(PathToData, p)),
            () => { for (int i = 1; i <= 50; i++) Experiment.Append(PathToData, p with { At = p.At.AddSeconds(i) }); });
        Assert.Equal(50, Experiment.ReadLast(PathToData, 100).Count);
        Assert.Equal(51, Experiment.ReadLast(PathToData, 100, includeDeleted: true).Count);
        Assert.Single(Directory.GetFiles(Experiment.DeletedDirectory(PathToData), "*.json"));
    }

    [Fact]
    public void Overlapping_marks_and_lower_stable_trials_are_individually_selectable()
    {
        var a = Point();
        var b = a with { Outcome = Experiment.Survived };
        var c = a with { Outcome = Experiment.Crash };
        var elsewhere = a with { Game = "Elsewhere" };
        TestMark[] tests = [new(a, [65, 66]), new(b, [65]), new(c, [70, 71]), new(elsewhere, [90])];
        var selected = HistoryData.Select(tests, [65, 66], [Experiment.PointId(c)]);
        Assert.Equal(3, selected.Count);
        Assert.Contains(a, selected);
        Assert.Contains(b, selected);
        Assert.Contains(c, selected);
        Assert.DoesNotContain(elsewhere, selected);
        Assert.Empty(HistoryData.Select(tests, [1], []));
    }

    [Fact]
    public void Deletion_reprojects_the_real_evidence_and_removes_every_mark_of_a_test()
    {
        VfPoint[] points = [
            new(0, 900000, 2500000, 2500000, 0, 900000),
            new(1, 910000, 2600000, 2600000, 0, 910000),
        ];
        var curve = new VfCurveSnapshot(VfMode.Regional, VfLayoutId.Regional132, [], points, 0, [], [], []);
        var end = DateTimeOffset.UtcNow.AddMinutes(-1);
        var lower = new DataPoint(end.AddMinutes(-2), "Game", Experiment.Stable, 1, "curve editor: lower", 900000, 2500, "0*2");
        var highest = lower with { At = end, Curve = "150000*2", Change = "curve editor: higher" };
        var crash = highest with { At = end.AddSeconds(10), Outcome = Experiment.Crash, CrashUv = 900000 };
        var samples = new List<Residency.Tick>();
        foreach (var trial in new[] { lower, highest })
            for (int i = 0; i < 30; i++)
                samples.Add(new(trial.At.AddSeconds(-30 + i), i < 15 ? 900000u : 910000u, i < 15 ? 2500u : 2600u, 100, 400));
        foreach (var p in new[] { lower, highest, crash }) Experiment.Append(PathToData, p);

        HistoryData Project() => TuningTools.ProjectEditorHistory(Experiment.ReadLast(PathToData, 100),
            [900, 910], [2500000, 2600000], curve, samples);
        var before = Project();
        Assert.Equal(2, before.Evidence.Count);
        Assert.True(before.Evidence[0].StableMhz > 2500);
        Assert.Equal(new[] { 0, 1 }, before.Tests.Single(t => t.Record.Change == highest.Change && t.Record.Outcome == Experiment.Stable).Indices);
        Assert.Single(before.Crashes);

        Experiment.DeletePoint(PathToData, highest);
        var after = Project();
        Assert.Equal(2500, after.Evidence.Single(e => e.Index == 0).StableMhz);
        Assert.Equal(2600, after.Evidence.Single(e => e.Index == 1).StableMhz);
        Assert.DoesNotContain(after.Tests, t => Experiment.PointId(t.Record) == Experiment.PointId(highest));
        Assert.Single(after.Crashes);
        Experiment.DeletePoint(PathToData, crash);
        Assert.Empty(Project().Crashes);
        Experiment.DeletePoint(PathToData, lower);
        Assert.Empty(Project().Evidence);
        Assert.Empty(Project().Tests);
    }

    private static readonly VfCurveSnapshot TwoPoints = new(VfMode.Regional, VfLayoutId.Regional132, [], [
        new(0, 900000, 2500000, 2500000, 0, 900000),
        new(1, 910000, 2600000, 2600000, 0, 910000),
    ], 0, [], [], []);
    private static readonly DateTimeOffset T = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One loaded sample a second from <paramref name="from"/> (plus <paramref name="ms"/>), on point 0 by default.</summary>
    private static List<Residency.Tick> Run(DateTimeOffset from, int seconds, uint uv = 900000, uint mhz = 2500, int ms = 500) =>
        [.. Enumerable.Range(0, seconds).Select(i => new Residency.Tick(from.AddSeconds(i).AddMilliseconds(ms), uv, mhz, 100, 400))];

    private static DataPoint Stable(DateTimeOffset trialAt, int seconds, string curve = "0*2") =>
        new(trialAt.AddSeconds(seconds), "Game", Experiment.Stable, Math.Round(seconds / 60.0, 1), "curve editor: points 0-1",
            900000, 2500, curve, TrialAt: trialAt);

    private static HistoryData Project(IReadOnlyList<DataPoint> all, IReadOnlyList<Residency.Tick> ticks) =>
        TuningTools.ProjectEditorHistory(all, [900, 910], [2500000, 2600000], TwoPoints, ticks);

    [Fact]
    public void Repeated_stable_marks_of_one_trial_count_its_time_once()
    {
        var first = Stable(T, 60);
        var second = Stable(T, 120);
        var h = Project([first, second], Run(T, 120));
        // Not 60 + 120. Observed time inside the trial's window [T, T+120 s) (VfTimeline.Within): the samples sit at
        // +0.5 s, so the first half second is unobserved and the last sample's second is cut at the window's end. This
        // was 120 (a whole second per sample, even past the end) before trial windows were clipped.
        Assert.Equal(119.5, h.Evidence.Single(e => e.Index == 0).StableSeconds, 9);
        Assert.Equal(2, h.Tests.Count);                                          // each mark still listed, deletable
    }

    [Fact]
    public void Two_servers_samplers_do_not_double_the_stable_time()
    {
        var ticks = Run(T, 60, ms: 100).Concat(Run(T, 60, ms: 600)).ToList();
        Assert.Equal(60, Residency.OnePerSecond(ticks).Count);
        // Not 120; the window [T, T+60 s) starts 0.1 s before the first sample (clipped windows; was 60 before).
        Assert.Equal(59.9, Project([Stable(T, 60)], ticks).Evidence.Single(e => e.Index == 0).StableSeconds, 9);
    }

    [Fact]
    public void Separate_trials_at_the_same_offset_add_up_and_a_higher_offset_starts_again()
    {
        var a = Stable(T, 30);
        var b = Stable(T.AddSeconds(100), 30);
        var ticks = Run(T, 30).Concat(Run(T.AddSeconds(100), 30)).ToList();
        // 29.5 s observed inside each 30 s window (samples at +0.5 s, cut at the window's end; was 30 + 30 before clipping).
        Assert.Equal(59, Project([a, b], ticks).Evidence.Single().StableSeconds, 9);
        var higher = Stable(T.AddSeconds(200), 30, "15000*2");
        Assert.Equal(29.5, Project([a, b, higher], [.. ticks, .. Run(T.AddSeconds(200), 30)]).Evidence.Single().StableSeconds, 9);
    }

    [Fact]
    public void A_trials_window_starts_when_it_went_on_not_at_its_rounded_minutes()
    {
        // 30 s ran, recorded as 0.6 min: the 6 s before it (the trial before, on point 1) are not this trial's.
        var d = Stable(T, 30) with { Minutes = 0.6 };
        var ticks = Run(T.AddSeconds(-6), 6, uv: 910000, mhz: 2600).Concat(Run(T, 30)).ToList();
        Assert.Equal(0, Assert.Single(Project([d], ticks).Evidence).Index);
    }

    [Fact]
    public void A_stable_mark_carries_the_clock_the_card_ran()
    {
        // +490 MHz set, but the card was held at 2500 (a clock lock, the power limit): the setting is drawn, the run is told.
        var ev = Project([Stable(T, 30, "490000*2")], Run(T, 30)).Evidence.Single();
        Assert.InRange(ev.StableMhz!.Value, 2980, 3000);   // where the driver lands +490 on a 2500 MHz point
        Assert.Equal(2500, ev.StableRanMhz);
        Assert.Equal(ev.StableMhz - 2500, ev.HeldBelowMhz);
        var free = Project([Stable(T, 30, "0*2")], Run(T, 30)).Evidence.Single();
        Assert.Equal(2500, free.StableRanMhz);
        Assert.Null(free.HeldBelowMhz);
    }

    [Fact]
    public void The_ran_clock_is_the_highest_stable_offsets_not_any_stable_trials()
    {
        var lower = Stable(T, 30);
        var higher = Stable(T.AddSeconds(100), 30, "15000*2");
        var ticks = Run(T, 30, mhz: 2600).Concat(Run(T.AddSeconds(100), 30, mhz: 2500)).ToList();
        var records = TuningTools.PointRecordsFrom([lower, higher], TwoPoints, ticks);
        Assert.Equal(2600u, records[0].StableMhz);      // data_points' stable_clock_mhz, unchanged
        Assert.Equal(2500u, records[0].StableRanMhz);   // paired with the offset that set stable_up_to
        Assert.Equal(15000, records[0].StableKhz);
    }

    [Theory]
    [InlineData("memory offset +0 → +1000 MHz", 0, 1)]
    [InlineData("XBAR offset +560 → +400 MHz", 0, 1)]
    [InlineData("curve editor: points 0-1", 1, 0)]
    public void By_point_counts_a_crash_against_the_curve_only_under_a_curve_trial(string change, int curve, int other)
    {
        var crash = new DataPoint(T.AddSeconds(30), "Game", Experiment.Crash, 0.5, change, 900000, 2500, "15000*2",
            CrashUv: 900000, TrialAt: T);
        var rec = TuningTools.PointRecordsFrom([crash], TwoPoints, Run(T, 30))[0];
        Assert.Equal(curve, rec.Crashes);
        Assert.Equal(other, rec.OtherCrashes);
        Assert.Equal(curve == 1 ? (int?)15000 : null, rec.CrashKhz);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
