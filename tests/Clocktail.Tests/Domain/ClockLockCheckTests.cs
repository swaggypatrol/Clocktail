using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>A client clock limit is a request the driver still derates under: what ran is read from the sampled clock,
/// not from the setter's success or the record's readback (LACT issue #1177's lesson).</summary>
public class ClockLockCheckTests
{
    private static readonly ClientLimit Cap = new(0, 2, 2_400_000), Floor = new(1, 2, 2_700_000);

    [Fact]
    public void A_cap_one_grid_step_under_is_held()
    {
        // nvidia-smi -lgc 1200,2400 ran at most 2392 MHz on the reference card.
        var o = Assert.Single(ClockLockCheck.Check([Cap], [2392, 2385, 2392, 2377], _ => null));
        Assert.Equal("max", o.Kind);
        Assert.Equal(0, o.OffPct);
        Assert.StartsWith("held", o.Words);
    }

    [Fact]
    public void A_clock_over_the_cap_is_reported()
    {
        var o = Assert.Single(ClockLockCheck.Check([Cap], [2400, 2500, 2512, 2400], _ => null));
        Assert.Equal(50, o.OffPct);
        Assert.Contains("over the 2400 MHz cap", o.Words);
    }

    [Fact]
    public void A_floor_the_driver_derated_under_says_the_run_did_not_test_it()
    {
        var o = Assert.Single(ClockLockCheck.Check([Floor], [2700, 2692, 2550, 2700, 2400, 2700, 2700, 2700, 2700, 2700], _ => null));
        Assert.Equal("min", o.Kind);
        Assert.Equal(20, o.OffPct);   // 2550 and 2400 are more than the slack under 2700; 2692 is not
        Assert.Contains("derated under the lock", o.Words);
        Assert.Contains("did not test 2700 MHz throughout", o.Words);
    }

    [Fact]
    public void A_point_lock_is_set_against_its_curve_points_clock()
    {
        VfPoint[] points = [new(0, 900_000, 2_500_000, 2_500_000, 0, 900_000), new(1, 950_000, 2_800_000, 2_800_000, 0, 950_000),
            new(2, 1_000_000, 2_950_000, 2_950_000, 0, 1_000_000)];
        var curve = new VfCurveSnapshot(VfMode.Regional, VfLayoutId.Regional132, [], points, 0, [], [], []);

        var point = Assert.Single(TuningTools.LocksMet([new ClientLimit(6, 3, 950_000)], [2800, 2797, 2650], curve, null)!);
        Assert.Equal("pinned", point.Kind);
        Assert.Equal(2800, point.TargetMhz);
        Assert.Equal(33.3, point.OffPct);
        Assert.Equal(1, point.OffSamples);

        // Boost Lock is the curve's top under NVVDD MAX, not the sentinel's 1.5 V.
        var boost = Assert.Single(TuningTools.LocksMet([new ClientLimit(6, 3, ClientLimitsLayout.BoostLockUv)], [2800], curve, 960_000)!);
        Assert.Equal(2800, boost.TargetMhz);
        Assert.Equal(0, boost.OffPct);
    }

    /// <summary>One sample in a thousand rounds to 0 %: the count decides, not the rounded share.</summary>
    [Fact]
    public void A_single_sample_over_the_cap_in_a_long_run_is_not_held()
    {
        var o = Assert.Single(ClockLockCheck.Check([Cap], [.. Enumerable.Repeat(2390.0, 999), 2500], _ => null));
        Assert.Equal(1, o.OffSamples);
        Assert.Equal(0.1, o.OffPct);
        Assert.False(o.Words.StartsWith("held", StringComparison.Ordinal));
        Assert.Contains("1 of 1000 samples (0.1 %) ran over the 2400 MHz cap", o.Words);
    }

    [Fact]
    public void A_single_sample_under_the_floor_in_a_long_run_is_not_held()
    {
        var o = Assert.Single(ClockLockCheck.Check([Floor], [.. Enumerable.Repeat(2700.0, 999), 2600], _ => null));
        Assert.Equal(1, o.OffSamples);
        Assert.Contains("1 of 1000 samples (0.1 %) ran under the floor's 2700 MHz", o.Words);
    }

    [Fact]
    public void A_pinned_point_one_sample_under_in_a_long_run_is_not_held()
    {
        VfPoint[] points = [new(0, 900_000, 2_500_000, 2_500_000, 0, 900_000), new(1, 950_000, 2_800_000, 2_800_000, 0, 950_000),
            new(2, 1_000_000, 2_950_000, 2_950_000, 0, 1_000_000)];
        var curve = new VfCurveSnapshot(VfMode.Regional, VfLayoutId.Regional132, [], points, 0, [], [], []);
        var o = Assert.Single(TuningTools.LocksMet([new ClientLimit(6, 3, 950_000)], [.. Enumerable.Repeat((double?)2800, 999), 2650], curve, null)!);
        Assert.Equal(1, o.OffSamples);
        Assert.Contains("1 of 1000 samples (0.1 %) ran under the locked point's 2800 MHz", o.Words);
    }

    /// <summary>Exactly 0.5 % rounded to an even whole percent is 0: it must still not read as held.</summary>
    [Fact]
    public void Half_a_percent_off_is_not_held()
    {
        var o = Assert.Single(ClockLockCheck.Check([Cap], [.. Enumerable.Repeat(2390.0, 398), 2500, 2500], _ => null));
        Assert.Equal(2, o.OffSamples);
        Assert.Equal(0.5, o.OffPct);
        Assert.StartsWith("2 of 400 samples (0.5 %)", o.Words);
    }

    [Fact]
    public void Nothing_in_force_is_nothing_to_report() => Assert.Null(TuningTools.LocksMet([], [2800], null, null));

    [Fact]
    public void No_samples_under_load_or_an_unknown_entry_compare_nothing()
    {
        var none = Assert.Single(ClockLockCheck.Check([Cap], [], _ => null));
        Assert.Null(none.OffPct);
        Assert.Null(none.OffSamples);
        var unknown = Assert.Single(ClockLockCheck.Check([new ClientLimit(3, 1, 5)], [2400], _ => null));
        Assert.Equal("unknown", unknown.Kind);
        Assert.Null(unknown.OffPct);
        Assert.Equal("", TuningTools.LocksWords(null));
        Assert.StartsWith("; clock lock in force: maximum clock 2400 MHz: held", TuningTools.LocksWords(ClockLockCheck.Check([Cap], [2390], _ => null)));
    }
}
