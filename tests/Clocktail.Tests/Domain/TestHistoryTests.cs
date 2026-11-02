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

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
