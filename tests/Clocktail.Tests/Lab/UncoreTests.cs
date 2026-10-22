using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>The uncore tuner without a card: the A B B A verdict, and the search on a fake card whose speed is a known
/// function of the settings.</summary>
public class UncoreTests
{
    private static UncoreRun R(double l2, double dram = 1570, double core = 2970, string verdict = LabResult.Pass, bool shared = false) =>
        new(verdict, l2, dram, core, 60, shared, verdict == LabResult.Pass ? null : "3 bad words");

    [Fact]
    public void A_gain_in_both_pairs_is_better()
    {
        var j = UncoreSearch.Judge(R(4780), R(4830), R(4825), R(4782));
        Assert.Equal(Comparison.Better, j.Outcome);
        Assert.InRange(j.L2!.Value, 0.9, 1.1);
    }

    [Theory]
    [InlineData(4830, 4760, "no clear gain")]           // one pair up, one down: drift, not the setting
    [InlineData(4795, 4795, "no clear gain")]           // +0.3 %: under the 0.5 % kept
    public void A_gain_that_is_not_clear_is_not_better(double b1, double b2, string why)
    {
        var j = UncoreSearch.Judge(R(4780), R(b1), R(b2), R(4780));
        Assert.Equal(Comparison.NotBetter, j.Outcome);
        Assert.Contains(why, j.Why);
    }

    [Fact]
    public void The_cliff_and_a_dram_loss_are_not_better_whatever_L2_says()
    {
        var cliff = UncoreSearch.Judge(R(4780), R(4900, core: 1610), R(4900, core: 1620), R(4780));
        Assert.Equal(Comparison.NotBetter, cliff.Outcome);
        Assert.Contains("the cliff", cliff.Why);
        var dram = UncoreSearch.Judge(R(4780), R(4900, dram: 1540), R(4900, dram: 1545), R(4780));
        Assert.Contains("DRAM bandwidth fell", dram.Why);
        // Ratio 0.925 with XBAR held under MSVDD's limit, as measured: the core 1.9 % down is no cliff, and still not better.
        var core = UncoreSearch.Judge(R(4780), R(4900, core: 2915), R(4900, core: 2912), R(4780));
        Assert.Equal(Comparison.NotBetter, core.Outcome);
        Assert.Contains("XBAR could not follow the ratio", core.Why);
        // One V/F bin (15 MHz, 0.5 %) is not a loss.
        Assert.Equal(Comparison.Better, UncoreSearch.Judge(R(4780), R(4830, core: 2955), R(4825, core: 2955), R(4782)).Outcome);
    }

    [Fact]
    public void A_failing_candidate_fails_and_a_failing_incumbent_or_a_shared_gpu_leaves_it_unjudged()
    {
        Assert.Equal(Comparison.Failed, UncoreSearch.Judge(R(4780), R(4900, verdict: LabResult.Fail), R(4900), R(4780)).Outcome);
        Assert.Equal(Comparison.Unjudged, UncoreSearch.Judge(R(4780, verdict: LabResult.Fail), R(4900), R(4900), R(4780)).Outcome);
        Assert.Equal(Comparison.Unjudged, UncoreSearch.Judge(R(4780), R(4900, shared: true), R(4900), R(4780)).Outcome);
    }

    private static readonly UncoreSpec Spec = new("gpu", "0000:01:00.0", "0*127", new UncoreSetting(250, 0, 58976, 0));

    /// <summary>A fake card: L2 speed by setting, a crash rule; the comparison is exact (no noise).</summary>
    private static (UncoreSearch Search, List<UncoreSetting> Tried) Run(Func<UncoreSetting, double> speed, Func<UncoreSetting, bool>? crashes = null,
        UncoreSpec? spec = null, Func<int, bool>? stopAfter = null)
    {
        var tried = new List<UncoreSetting>();
        UncoreSearch? search = null;
        search = new UncoreSearch(spec ?? Spec, (stage, a, b) =>
        {
            tried.Add(b);
            if (crashes?.Invoke(b) == true) return new(stage, a, b, Comparison.Failed, "a bad word", null, null, null, []);
            bool better = speed(b) >= speed(a) * 1.005;
            return new(stage, a, b, better ? Comparison.Better : Comparison.NotBetter, "", null, null, null, []);
        }, () => stopAfter?.Invoke(tried.Count) == true);
        search.Run();
        return (search, tried);
    }

    [Fact]
    public void Xbar_climbs_the_demand_ladder_then_the_ratio_then_xbar_again_then_msvdd()
    {
        // XBAR pays to +400, then flat; the ratio pays up to 0.95 and lets XBAR pay to +450; MSVDD REL buys nothing.
        double Speed(UncoreSetting s) => 4780 * (1 + 0.01 * Math.Min(s.XbarMhz, s.Ratio > 0.92 ? 450 : 400) / 50.0) * (1 + 0.5 * (s.Ratio - 0.8999));
        var (search, tried) = Run(Speed);
        Assert.Null(search.Ended);
        Assert.Equal(new UncoreSetting(450, 50, 58976 + 2 * 1638, 0), search.Best);
        // +300 needs +20 mV (taken at the +20 level); +350 and up need +50 (the +30 and +40 levels are skipped).
        Assert.Equal(new UncoreSetting(300, 20, 58976, 0), tried[0]);
        Assert.Equal(new UncoreSetting(350, 50, 58976, 0), tried[1]);
        Assert.DoesNotContain(tried, s => s.XbarMhz > 300 && s.DemandMv < 50);
        Assert.Contains(tried, s => s.MsvddRelMv == 10);
        Assert.True(tried.Count < 20, $"{tried.Count} comparisons");
    }

    /// <summary>The reference card at its daily +250 MHz: XBAR sits under the clock MSVDD's reliability limit allows, so its
    /// offset buys nothing until MSVDD's REL is raised; then the next round climbs XBAR again.</summary>
    [Fact]
    public void An_axis_runs_again_when_another_one_moved_the_best()
    {
        double Speed(UncoreSetting s) => 4780 * (s.MsvddRelMv >= 10 ? 1.01 * (1 + 0.01 * Math.Min(s.XbarMhz - 250, 100) / 50.0) : 1);
        var (search, tried) = Run(Speed);
        Assert.Equal(new UncoreSetting(350, 50, 58976, 10), search.Best);
        Assert.Equal([300, 350], tried.Where(s => s.MsvddRelMv == 0 && s.RatioRaw == 58976 && s.XbarMhz > 250).Select(s => s.XbarMhz));
        Assert.Contains(tried, s => s.MsvddRelMv == 10 && s.XbarMhz == 350);
        Assert.True(tried.Count < 16, $"{tried.Count} comparisons");
    }

    [Fact]
    public void A_failure_ends_that_axis_and_the_best_stays_the_last_good_one()
    {
        var (search, tried) = Run(s => 4780 + s.XbarMhz, crashes: s => s.XbarMhz >= 350);
        Assert.Equal(300, search.Best.XbarMhz);
        Assert.Single(tried, s => s.XbarMhz == 350 && s.RatioRaw == 58976);   // never retried at the same ratio
    }

    [Fact]
    public void Two_misses_in_a_row_end_an_axis_and_a_miss_is_stepped_past()
    {
        // +300 is no faster than +250, but +350 is: a single miss is stepped past.
        var (search, _) = Run(s => s.XbarMhz switch { 300 => 4780, >= 350 => 4900, _ => 4780 });
        Assert.Equal(350, search.Best.XbarMhz);
        // Nothing pays at all: XBAR, the ratio and MSVDD each stop after two misses.
        var (flat, tried) = Run(_ => 4780);
        Assert.Equal(Spec.Start, flat.Best);
        Assert.True(tried.Count <= 8, $"{tried.Count} comparisons");
    }

    /// <summary>+400 at the rule's +50 mV hits the cliff on this fake card; the step is not given up, the demand is raised (not
    /// past +50, the policy's top). With a 70 mV top it would go on to +60 and keep +400 there.</summary>
    [Fact]
    public void A_step_that_hits_the_cliff_is_tried_again_with_more_demand()
    {
        var tried = new List<UncoreSetting>();
        var spec = Spec with { DemandMaxMv = 70 };
        var search = new UncoreSearch(spec, (stage, a, b) =>
        {
            tried.Add(b);
            if (b.XbarMhz == 400 && b.DemandMv < 60) return new(stage, a, b, Comparison.NotBetter, "the memory phase's core clock fell -33.0 %: the cliff", null, null, null, []);
            bool better = b.XbarMhz > a.XbarMhz && b.XbarMhz <= 400;
            return new(stage, a, b, better ? Comparison.Better : Comparison.NotBetter, "", null, null, null, []);
        }, () => false);
        search.Run();
        Assert.Equal(new UncoreSetting(400, 60, 58976, 0), search.Best);
        Assert.Equal([50, 60], tried.Where(s => s.XbarMhz == 400).Take(2).Select(s => s.DemandMv));
    }

    [Fact]
    public void Lab_stop_ends_the_search()
    {
        var (search, tried) = Run(s => 4780 + s.XbarMhz, stopAfter: n => n >= 2);
        Assert.Equal("stopped (lab_stop)", search.Ended);
        Assert.Equal(2, tried.Count);
    }
}
