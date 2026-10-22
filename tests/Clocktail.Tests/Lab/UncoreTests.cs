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
        Assert.Contains("a slower core costs", core.Why);
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

    /// <summary>XBAR's clock as the card set it: the lower of its floor (the ratio x a 3050 MHz core) and its ceiling (the
    /// clock MSVDD's reliability limit allows, 2750 MHz + 3 MHz per mV of REL). Neither the ratio nor MSVDD alone moves it
    /// by the 0.5 % kept; the two together do.</summary>
    [Fact]
    public void The_ratio_and_msvdd_go_up_together_when_neither_pays_alone()
    {
        double Speed(UncoreSetting s) => Math.Min(s.Ratio * 3050, 2750 + 3 * s.MsvddRelMv);
        var (search, tried) = Run(Speed);
        Assert.Equal(new UncoreSetting(250, 0, 58976 + 2 * 1638, 20), search.Best);
        Assert.Contains(search.Log, c => c.Stage == "window" && c.Outcome == Comparison.Better);
        Assert.DoesNotContain(search.Log, c => c.Stage is "ratio" or "msvdd" && c.Outcome == Comparison.Better);
    }

    [Theory]
    [InlineData(13.0, 2.0, 948UL, false)]       // the idle desktop: 13 % at idle clocks before the run, 2 % after it
    [InlineData(22.0, 24.0, 815UL, true)]       // Mystify drawing, read with the clocks up
    [InlineData(60.0, 2.0, 948UL, true)]        // a game before the run
    [InlineData(3.0, 2.0, 30000UL, true)]       // a local model holding the memory
    public void A_run_is_shared_when_other_work_kept_the_gpu_busy_or_holds_the_memory(double before, double after, ulong othersMib, bool shared) =>
        Assert.Equal(shared, new LabResult("", "", 0, 0, default, default, [], LabResult.Pass, [], before, othersMib, after).Shared);

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
    public void A_list_compares_each_candidate_with_the_start_and_keeps_the_fastest_better_one()
    {
        var start = new UncoreSetting(250, 0, 62252, 20);
        var spec = Spec with { Start = start, MsvddRelMaxMv = 50, MsvddAltGapMv = 20 };
        var against = new List<UncoreSetting>();
        var gains = new Dictionary<int, double> { [30] = 0.8, [40] = 1.6, [50] = 1.1 };
        var search = new UncoreSearch(spec, (stage, a, b) =>
        {
            against.Add(a);
            double g = gains.GetValueOrDefault(b.MsvddRelMv);
            return new(stage, a, b, g >= 0.5 ? Comparison.Better : Comparison.NotBetter, "", g, 0, 0, []);
        }, () => false);
        search.RunList([start with { MsvddRelMv = 30 }, start with { MsvddRelMv = 40 }, start with { XbarMhz = 400, DemandMv = 20 },
            start with { MsvddRelMv = 50 }, start with { MsvddRelMv = 60 }]);
        Assert.Equal(start with { MsvddRelMv = 40 }, search.Best);
        Assert.All(against, a => Assert.Equal(start, a));
        Assert.Equal(3, against.Count);
        // +400 MHz with +20 mV is the cliff, REL +60 past the limit: logged, not run.
        Assert.Contains(search.Log, c => c.B.XbarMhz == 400 && c.Why.Contains("needs +50 mV", StringComparison.Ordinal));
        Assert.Contains(search.Log, c => c.B.MsvddRelMv == 60 && c.Why.Contains("MSVDD REL past +50", StringComparison.Ordinal));
    }

    /// <summary>MSVDD's top is the lowest of REL and ALT; on the reference card ALT sits 20 mV above REL, so REL past +20 takes
    /// ALT with it, and a setting under the gap puts ALT back to 0.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 0)]
    [InlineData(30, 10)]
    [InlineData(50, 30)]
    public void Alt_follows_rel_past_the_gap(int relMv, int altMv)
    {
        Assert.Equal(altMv, (Spec with { MsvddAltGapMv = 20 }).AltMv(Spec.Start with { MsvddRelMv = relMv }));
        Assert.Null(Spec.AltMv(Spec.Start with { MsvddRelMv = relMv }));
    }

    [Fact]
    public void Lab_stop_ends_the_search()
    {
        var (search, tried) = Run(s => 4780 + s.XbarMhz, stopAfter: n => n >= 2);
        Assert.Equal("stopped (lab_stop)", search.Ended);
        Assert.Equal(2, tried.Count);
    }
}
