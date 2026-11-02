using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Domain;

/// <summary>S-8: the card's own edge per curve point, from crash data points with the flight recorder's voltage.</summary>
public class CrashEdgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<VfPoint> Curve = [new(75, 900_000, 2_400_000, 2_850_000, 450_000, 900_000), new(76, 910_000, 2_420_000, 2_870_000, 450_000, 910_000)];

    private static DataPoint P(string outcome, string curve, uint? crashUv, string change = "+15 MHz on points 76-78 (910-930 mV)") =>
        new(T0, "KCD2", outcome, 5, change, 910_000, 2895, curve, null, crashUv);

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

    [Theory]
    [InlineData("memory offset +0 → +1000 MHz")]
    [InlineData("memory offset → +1500 MHz (tune_memory)")]
    [InlineData("XBAR offset +560 → +400 MHz")]
    [InlineData("power limit 575 → 450 W")]
    [InlineData("core offset +0 → +150 MHz")]
    public void A_crash_under_a_trial_that_left_the_curve_alone_is_not_the_curves_edge(string change)
    {
        var edge = CrashEdge.From([P(Experiment.Crash, "0*75,465000*2", 911_000, change)], Curve);
        Assert.Empty(edge);
        Assert.Null(CrashEdge.Describe(edge, 76, 465_000));
    }

    [Fact]
    public void A_memory_crash_cannot_lower_the_edge_a_curve_crash_set()
    {
        var edge = CrashEdge.From([
            P(Experiment.Crash, "0*75,480000*2", 908_000),
            P(Experiment.Crash, "0*75,465000*2", 911_000, "memory offset +0 → +1000 MHz"),
        ], Curve);
        Assert.Equal(480_000, Assert.Single(edge).Value);
    }

    [Theory]
    [InlineData("+15 MHz on points 65-69 (860-885 mV), points 70-80 lifted to stay above", true)]
    [InlineData("curve editor: points 60-64", true)]
    [InlineData("memory offset +0 → +1000 MHz", false)]
    [InlineData("memory offset → +1500 MHz (tune_memory)", false)]
    [InlineData("uncore: XBAR +400 MHz (tune_uncore)", false)]
    public void Only_a_curve_trials_words_make_it_a_curve_change(string change, bool curve) =>
        Assert.Equal(curve, Experiment.CurveChange(change));
}