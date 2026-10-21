using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Domain;

/// <summary>S-8: the card's own edge per curve point, from crash data points with the flight recorder's voltage.</summary>
public class CrashEdgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<VfPoint> Curve = [new(75, 900_000, 2_400_000, 2_850_000, 450_000, 900_000), new(76, 910_000, 2_420_000, 2_870_000, 450_000, 910_000)];

    private static DataPoint P(string outcome, string curve, uint? crashUv) =>
        new(T0, "KCD2", outcome, 5, "+15", 910_000, 2895, curve, null, crashUv);

    [Fact]
    public void The_lowest_crashed_offset_at_each_point_is_the_edge()
    {
        var edge = CrashEdge.From([
            P(Experiment.Crash, "0*75,480000*2", 908_000),   // point 76 at +480
            P(Experiment.Crash, "0*75,465000*2", 911_000),   // point 76 again at +465: the lower one counts
            P(Experiment.Stable, "0*75,500000*2", null),
            P(Experiment.Crash, "0*75,500000*2", null),      // no crash voltage: not placed
        ], Curve);
        Assert.Equal(465_000, Assert.Single(edge, e => e.Key == 76).Value);
        Assert.Equal("edge: 15 MHz under the offset that crashed at this point (+465)", CrashEdge.Describe(edge, 76, 450_000));
        Assert.Contains("at or over", CrashEdge.Describe(edge, 76, 465_000));
        Assert.Null(CrashEdge.Describe(edge, 75, 450_000));
    }
}
