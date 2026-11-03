using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public sealed class VfHistoryReviewRegressionTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Stable_time_cannot_extend_beyond_the_trials_end()
    {
        var curve = new VfCurveSnapshot(VfMode.Regional, VfLayoutId.Regional132, [],
            [new(0, 900000, 2500000, 2500000, 0, 900000)], 0, [], [], []);
        var trial = new DataPoint(T.AddSeconds(10), "Game", Experiment.Stable, 10.0 / 60,
            "curve editor: point 0", 900000, 2500, "0", TrialAt: T);
        var observations = Enumerable.Range(0, 45)
            .Select(i => new VfObservation(T.AddMilliseconds(i * 250), 900000, 2500, VfSource.Lightweight));
        var records = TuningTools.PointRecordsFrom([trial], curve, VfTimeline.Build(observations));

        Assert.Equal(10, records[0].StableSeconds, 6);
    }

    [Fact]
    public void Retrying_a_batch_after_a_later_day_is_locked_does_not_duplicate_the_earlier_day()
    {
        string dir = Path.Combine(Path.GetTempPath(), "clocktail-vf-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var first = new VfObservation(T, 900000, 2500, VfSource.Lightweight);
        var second = new VfObservation(T.AddDays(1), 910000, 2600, VfSource.Lightweight);
        try
        {
            string lockedPath = Path.Combine(dir, VfHistoryFile.DayFileName(VfHistoryFile.UtcDay(second.At)));
            using (var held = new FileStream(lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => VfHistoryStore.Append(dir, [first, second]));
                Assert.Throws<IOException>(() => VfHistoryStore.Append(dir, [first, second]));
            }
            VfHistoryStore.Append(dir, [first, second]);

            string firstPath = Path.Combine(dir, VfHistoryFile.DayFileName(VfHistoryFile.UtcDay(first.At)));
            Assert.Equal(VfHistoryFile.HeaderSize + VfHistoryFile.RecordSize, new FileInfo(firstPath).Length);
            Assert.Single(VfHistoryStore.ReadDay(dir, VfHistoryFile.UtcDay(first.At)));
            Assert.Single(VfHistoryStore.ReadDay(dir, VfHistoryFile.UtcDay(second.At)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
