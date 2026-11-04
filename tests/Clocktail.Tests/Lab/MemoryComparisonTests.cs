using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

public class MemoryComparisonTests
{
    internal static SweepStep S(int mhz, double? gb = 1500, double? lat = 500) =>
        new(mhz, LabResult.Pass, gb, lat, 13801 + mhz, 60, 62, 1, null, 0, 0, null, null, "P0");

    [Fact]
    public void Flat_performance_shortlists_the_lowest_offset_and_confirms_no_gain()
    {
        Assert.Equal([0], MemoryComparisonRules.Shortlist([S(0), S(50), S(100)]));
        var c = MemoryComparisonRules.Judge("confirm", 0, 100, [S(0), S(100), S(100), S(0)]);
        Assert.Equal(MemoryComparison.Equivalent, c.Outcome);
        Assert.Equal(0, MemoryComparisonRules.Pick(0, [c]));
    }

    [Fact]
    public void A_repeated_bandwidth_or_latency_gain_is_kept()
    {
        var bandwidth = MemoryComparisonRules.Judge("confirm", 0, 100, [S(0), S(100, 1530), S(100, 1530), S(0)]);
        var latency = MemoryComparisonRules.Judge("confirm", 0, 100, [S(0), S(100, lat: 490), S(100, lat: 490), S(0)]);
        Assert.Equal(MemoryComparison.Better, bandwidth.Outcome);
        Assert.Equal(MemoryComparison.Better, latency.Outcome);
        Assert.Equal(100, MemoryComparisonRules.Pick(0, [bandwidth]));
        Assert.Equal(100, MemoryComparisonRules.Pick(0, [latency]));
    }

    [Fact]
    public void Rechecking_an_isolated_high_reading_removes_a_false_scan_edge_but_keeps_the_raw_reading()
    {
        var raw = new[] { S(0), S(50, 1515), S(100, 1501), S(150, 1502) };
        var effective = raw.ToList();
        Assert.NotNull(MemorySweep.Edge(effective));
        var recheck = MemoryComparisonRules.Judge("recheck", 50, 150, [S(50), S(150), S(150), S(50)]);
        MemoryComparisonRules.Recheck(effective, recheck);
        Assert.Null(MemorySweep.Edge(effective));
        Assert.Equal(1515, raw[1].Gbps);
        Assert.Equal(1500, effective[1].Gbps);
    }

    [Fact]
    public void A_single_low_baseline_run_does_not_make_a_candidate_faster()
    {
        var c = MemoryComparisonRules.Judge("confirm", 0, 100, [S(0, 1485), S(100), S(100), S(0)]);
        Assert.NotEqual(MemoryComparison.Better, c.Outcome);
        Assert.Equal(0, MemoryComparisonRules.Pick(0, [c]));
    }

    [Fact]
    public void Steady_drift_is_cancelled_and_disturbed_comparisons_are_unjudged()
    {
        const double drift = 1.003, gain = 1.01;
        var c = MemoryComparisonRules.Judge("confirm", 0, 100,
            [S(0), S(100, 1500 * gain * drift), S(100, 1500 * gain * drift * drift), S(0, 1500 * drift * drift * drift)]);
        Assert.Equal(MemoryComparison.Better, c.Outcome);
        Assert.InRange(c.BandwidthPct!.Value, 0.999, 1.001);
        var disturbed = MemoryComparisonRules.Judge("confirm", 0, 100, [S(0, 1450), S(100), S(100), S(0)]);
        Assert.True(disturbed.Disturbed);
        Assert.Equal(MemoryComparison.Unjudged, disturbed.Outcome);
        Assert.Null(MemoryComparisonRules.Pick(0, [disturbed]));
    }

    [Theory]
    [InlineData(1490, 500)]
    [InlineData(1500, 510)]
    [InlineData(1530, 510)]
    public void A_material_regression_in_either_metric_vetoes_a_gain_in_the_other(double gb, double lat)
    {
        var c = MemoryComparisonRules.Judge("confirm", 0, 100, [S(0), S(100, gb, lat), S(100, gb, lat), S(0)]);
        Assert.Equal(MemoryComparison.Slower, c.Outcome);
        Assert.Equal(0, MemoryComparisonRules.Pick(0, [c]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Missing_or_invalid_clock_never_establishes_that_an_offset_ran(double? clock)
    {
        var step = S(100) with { MemMhz = clock };
        var edge = MemorySweep.Edge([step], (0, 13801));
        Assert.True(edge!.Unjudged);
        Assert.Equal(MemorySweep.StepEnd.Unjudged, MemorySweep.End(step, edge, false));
        Assert.Empty(MemoryComparisonRules.Shortlist([step]));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void The_clock_must_follow_offsets_in_both_directions(int from, int to) =>
        Assert.NotNull(MemorySweep.InForce(from, 13801 + from, S(to) with { MemMhz = 13801 + from }));

    [Fact]
    public void A_higher_offset_with_equivalent_confirmed_performance_does_not_displace_a_lower_one()
    {
        var lower = MemoryComparisonRules.Judge("candidate", 0, 100, [S(0), S(100, 1530), S(100, 1530), S(0)]);
        var higher = MemoryComparisonRules.Judge("candidate", 0, 200, [S(0), S(200, 1531), S(200, 1531), S(0)]);
        Assert.Equal(100, MemoryComparisonRules.Pick(0, [higher, lower]));
        Assert.Equal(100, MemoryComparisonRules.Pick(0, [lower, higher]));
    }

    [Fact]
    public void A_spare_slot_covers_the_nondominated_compromise_when_joint_plateau_is_empty()
    {
        var scan = new[] { S(0), S(50, 1530, 520), S(100, 1515, 500), S(150, 1500, 490) };
        var shortlist = MemoryComparisonRules.Shortlist(scan);
        Assert.Equal(3, shortlist.Count);
        Assert.Contains(100, shortlist);
        var comparisons = shortlist.Select(offset =>
        {
            var candidate = scan.Single(s => s.OffsetMhz == offset);
            return MemoryComparisonRules.Judge("candidate", 0, offset, [scan[0], candidate, candidate, scan[0]]);
        }).ToArray();
        Assert.Equal(100, MemoryComparisonRules.Pick(0, comparisons));
        Assert.Equal(shortlist, MemoryComparisonRules.Shortlist(scan.Reverse().ToArray()));
    }

    [Fact]
    public void The_compromise_favors_balance_over_a_point_almost_identical_to_an_extreme()
    {
        var shortlist = MemoryComparisonRules.Shortlist([S(0), S(50, 1530, 520), S(75, 1529, 519), S(100, 1515, 500), S(150, 1500, 490)]);
        Assert.Equal(3, shortlist.Count);
        Assert.Contains(100, shortlist);
        Assert.DoesNotContain(75, shortlist);
    }

    [Fact]
    public void A_dominated_point_does_not_fill_a_spare_slot()
    {
        var shortlist = MemoryComparisonRules.Shortlist([S(0), S(50, 1530, 520), S(100, 1490, 510), S(150, 1500, 490)]);
        Assert.Equal(new[] { 50, 150 }, shortlist);
    }
}
