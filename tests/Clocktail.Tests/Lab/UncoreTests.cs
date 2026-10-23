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

    /// <summary>The case made against 0.3.3: A 8500, B 10030 and 10030, A 10000 was "better, +8.79 %" (pairs +18 and +0.3 %),
    /// when B was 0.3 % faster and A's first run read 15 % low. Runs of one setting that far apart were disturbed: the
    /// comparison is unjudged (the job measures it again), never a gain, and never a loss either.</summary>
    [Fact]
    public void Runs_of_one_setting_far_apart_leave_the_comparison_unjudged()
    {
        var j = UncoreSearch.Judge(R(8500), R(10030), R(10030), R(10000));
        Assert.Equal(Comparison.Unjudged, j.Outcome);
        Assert.True(j.Disturbed);
        Assert.Contains("17.6 % apart", j.Why);
        // The other way round: a candidate's run read low (the 621.01 search turned +150 and +200 down on runs like this).
        var low = UncoreSearch.Judge(R(4780), R(4100), R(4900), R(4782));
        Assert.Equal(Comparison.Unjudged, low.Outcome);
        Assert.True(low.Disturbed);
    }

    /// <summary>A run reading off by less than that, with no real gain behind it, shows in one pair only: the other pair says
    /// none, so it is not better.</summary>
    [Fact]
    public void One_run_reading_off_is_no_gain_when_the_other_pair_says_none()
    {
        var j = UncoreSearch.Judge(R(4708), R(4780), R(4781), R(4780));
        Assert.Equal(Comparison.NotBetter, j.Outcome);
        Assert.False(j.Disturbed);
        Assert.False(j.Gain);
        Assert.Contains("no clear gain", j.Why);
    }

    /// <summary>A steady drift through the four runs (the card warming, 0.5 % a run here) is what A B B A cancels: a real 1 %
    /// gain is kept at its size.</summary>
    [Fact]
    public void A_steady_drift_is_cancelled_and_a_real_gain_kept()
    {
        double a = 4780, g = 1.01, d = 1.005;
        var j = UncoreSearch.Judge(R(a), R(a * g * d), R(a * g * d * d), R(a * d * d * d));
        Assert.Equal(Comparison.Better, j.Outcome);
        Assert.True(j.Gain);
        Assert.InRange(j.L2!.Value, 0.99, 1.01);
    }

    [Fact]
    public void The_cliff_and_a_dram_loss_are_not_better_whatever_L2_says()
    {
        var cliff = UncoreSearch.Judge(R(4780), R(4900, core: 1610), R(4900, core: 1620), R(4780));
        Assert.Equal(Comparison.NotBetter, cliff.Outcome);
        Assert.Equal(Comparison.Cliff, cliff.Veto);
        Assert.Contains("the cliff", cliff.Why);
        var dram = UncoreSearch.Judge(R(4780), R(4900, dram: 1540), R(4900, dram: 1545), R(4780));
        Assert.Contains("DRAM bandwidth fell", dram.Why);
        Assert.Equal((Comparison.DramCost, true), (dram.Veto, dram.Gain));   // the L2 gain held; its cost turned it down
        // Ratio 0.925 with XBAR held under MSVDD's limit, as measured: the core 1.9 % down is no cliff, and still not better.
        var core = UncoreSearch.Judge(R(4780), R(4900, core: 2915), R(4900, core: 2912), R(4780));
        Assert.Equal(Comparison.NotBetter, core.Outcome);
        Assert.Contains("a slower core costs", core.Why);
        Assert.Equal((Comparison.CoreCost, true), (core.Veto, core.Gain));
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
    public void Xbar_climbs_the_demand_ladder_then_the_ratio_then_xbar_again_then_the_boost()
    {
        // XBAR pays to +400, then flat; the ratio pays up to 0.95 and lets XBAR pay to +450; the boost buys nothing.
        double Speed(UncoreSetting s) => 4780 * (1 + 0.01 * Math.Min(s.XbarMhz, s.Ratio > 0.92 ? 450 : 400) / 50.0) * (1 + 0.5 * (s.Ratio - 0.8999));
        var (search, tried) = Run(Speed);
        Assert.Null(search.Ended);
        Assert.Equal(new UncoreSetting(450, 50, 58976 + 2 * 1638, 0), search.Best);
        // +300 needs +20 mV (taken at the +20 level); +350 and up need +50 (the +30 and +40 levels are skipped).
        Assert.Equal(new UncoreSetting(300, 20, 58976, 0), tried[0]);
        Assert.Equal(new UncoreSetting(350, 50, 58976, 0), tried[1]);
        Assert.DoesNotContain(tried, s => s.XbarMhz > 300 && s.DemandMv < 50);
        Assert.Contains(tried, s => s.BoostPct == 25);
        Assert.True(tried.Count < 20, $"{tried.Count} comparisons");
    }

    /// <summary>The reference card at its daily +250 MHz: XBAR sits under the clock MSVDD's reliability limit allows, so its
    /// offset buys nothing until the boost raises that limit; then the next round climbs XBAR again.</summary>
    [Fact]
    public void An_axis_runs_again_when_another_one_moved_the_best()
    {
        double Speed(UncoreSetting s) => 4780 * (s.BoostPct >= 25 ? 1.01 * (1 + 0.01 * Math.Min(s.XbarMhz - 250, 100) / 50.0) : 1);
        var (search, tried) = Run(Speed);
        Assert.Equal(new UncoreSetting(350, 50, 58976, 25), search.Best);
        Assert.Equal([300, 350], tried.Where(s => s.BoostPct == 0 && s.RatioRaw == 58976 && s.XbarMhz > 250).Select(s => s.XbarMhz));
        Assert.Contains(tried, s => s.BoostPct == 25 && s.XbarMhz == 350);
        Assert.True(tried.Count < 22, $"{tried.Count} comparisons");   // the boost axis takes four 25 % steps where MSVDD took two
    }

    /// <summary>XBAR's clock as the card set it: the lower of its floor (the ratio x a 3050 MHz core) and its ceiling (the
    /// clock MSVDD's reliability limit allows, 2750 MHz + 3 MHz per mV, the boost +5 mV per 25 %). Neither the ratio nor the
    /// boost alone moves it by the 0.5 % kept; the two together do (the boost capped at 50 % here, so it ends there).</summary>
    [Fact]
    public void The_ratio_and_the_boost_go_up_together_when_neither_pays_alone()
    {
        double Speed(UncoreSetting s) => Math.Min(s.Ratio * 3050, 2750 + 0.6 * s.BoostPct);
        var (search, tried) = Run(Speed, spec: Spec with { BoostMaxPct = 50 });
        Assert.Equal(new UncoreSetting(250, 0, 58976 + 2 * 1638, 50), search.Best);
        Assert.Contains(search.Log, c => c.Stage == "window" && c.Outcome == Comparison.Better);
        Assert.DoesNotContain(search.Log, c => c.Stage is "ratio" or "boost" && c.Outcome == Comparison.Better);
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
        // Nothing pays at all: XBAR, the ratio and the boost each stop after two misses.
        var (flat, tried) = Run(_ => 4780);
        Assert.Equal(Spec.Start, flat.Best);
        Assert.True(tried.Count <= 8, $"{tried.Count} comparisons");
    }

    /// <summary>+400 at the rule's +50 mV hits the cliff on this fake card; the step is not given up, the demand is raised (not
    /// past +50, the policy's top). With a 70 mV top it would go on to +60 and keep +400 there. The search reads the cliff
    /// from the comparison's value, not its words.</summary>
    [Fact]
    public void A_step_that_hits_the_cliff_is_tried_again_with_more_demand()
    {
        var tried = new List<UncoreSetting>();
        var spec = Spec with { DemandMaxMv = 70 };
        var search = new UncoreSearch(spec, (stage, a, b) =>
        {
            tried.Add(b);
            if (b.XbarMhz == 400 && b.DemandMv < 60) return new(stage, a, b, Comparison.NotBetter, "the core fell", null, null, null, [], Veto: Comparison.Cliff);
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
        var start = new UncoreSetting(250, 0, 62252, 0);
        var spec = Spec with { Start = start };
        var against = new List<UncoreSetting>();
        var gains = new Dictionary<int, double> { [25] = 0.8, [50] = 1.6, [75] = 1.1 };
        var search = new UncoreSearch(spec, (stage, a, b) =>
        {
            against.Add(a);
            double g = gains.GetValueOrDefault(b.BoostPct);
            return new(stage, a, b, g >= 0.5 ? Comparison.Better : Comparison.NotBetter, "", g, 0, 0, []);
        }, () => false);
        search.RunList([start with { BoostPct = 25 }, start with { BoostPct = 50 }, start with { XbarMhz = 400, DemandMv = 20 },
            start with { BoostPct = 75 }, start with { BoostPct = 125 }]);
        Assert.Equal(start with { BoostPct = 50 }, search.Best);
        Assert.All(against, a => Assert.Equal(start, a));
        Assert.Equal(3, against.Count);
        // +400 MHz with +20 mV is the cliff, a boost of 125 % past the driver's 100: logged, not run.
        Assert.Contains(search.Log, c => c.B.XbarMhz == 400 && c.Why.Contains("needs +50 mV", StringComparison.Ordinal));
        Assert.Contains(search.Log, c => c.B.BoostPct == 125 && c.Why.Contains("the boost outside 0..100 %", StringComparison.Ordinal));
    }

    /// <summary>Runs of one setting that disagree were disturbed; when only the candidate's did, its incumbent's agreeing, the
    /// job counts a second time as the candidate's miss rather than the end of the search (the review of 0.3.4: a setting
    /// erratic in itself, or heating the card, ended hours of search with nothing named).</summary>
    [Fact]
    public void Runs_that_disagree_are_disturbed_and_say_whether_only_the_candidates_did()
    {
        var erratic = UncoreSearch.Judge(R(4780), R(4950), R(4826), R(4782));
        Assert.Equal(Comparison.Unjudged, erratic.Outcome);
        Assert.True(erratic.Disturbed);
        Assert.True(erratic.Erratic);
        Assert.False(erratic.Gain);
        var outside = UncoreSearch.Judge(R(4780), R(4900), R(4902), R(4600));
        Assert.True(outside.Disturbed);
        Assert.False(outside.Erratic);
    }

    /// <summary>The confirmation names a best whose DRAM cost stays within <see cref="UncoreSearch.DramCap"/>; past it, nothing
    /// (the review of 0.3.4: a DRAM fall of 8 % still named the best).</summary>
    [Theory]
    [InlineData(-0.8, true)]
    [InlineData(-1.9, true)]
    [InlineData(-2.1, false)]
    [InlineData(-8.0, false)]
    public void A_confirmation_that_costs_dram_past_the_cap_names_nothing(double dramPct, bool holds) =>
        Assert.Equal(holds, UncoreSearch.Holds(new Comparison("confirm", Spec.Start, Spec.Start with { XbarMhz = 300 }, Comparison.NotBetter,
            "DRAM bandwidth fell", 2.5, dramPct, 0, [], Gain: true, Veto: Comparison.DramCost)));

    [Fact]
    public void Lab_stop_ends_the_search()
    {
        var (search, tried) = Run(s => 4780 + s.XbarMhz, stopAfter: n => n >= 2);
        Assert.Equal("stopped (lab_stop)", search.Ended);
        Assert.Equal(2, tried.Count);
    }

    /// <summary>A candidate's run that could not be made (too hot, the runner) says nothing about the candidate: unjudged,
    /// never its failure, so no crash is recorded against the setting.</summary>
    [Fact]
    public void A_candidate_run_that_could_not_be_made_is_unjudged_not_failed()
    {
        var j = UncoreSearch.Judge(R(4780), R(4900, verdict: LabResult.Inconclusive), R(4900), R(4780));
        Assert.Equal(Comparison.Unjudged, j.Outcome);
        Assert.Contains("could not be judged", j.Why);
        Assert.Equal(Comparison.Failed, UncoreSearch.Judge(R(4780), R(4900), R(4900, verdict: LabResult.Fail), R(4780)).Outcome);
    }

    /// <summary>The best is named only when it beats the start again in a comparison of its own: climbed to a step at a time,
    /// each step against the best before it, it was never measured against the start (and a list picks the fastest, where
    /// one fast reading wins). One that does not hold up leaves the start as the best.</summary>
    [Fact]
    public void The_best_is_kept_only_when_it_beats_the_start_again()
    {
        var (search, _) = Run(s => 4780 + s.XbarMhz);
        var found = search.Best;
        Assert.NotEqual(Spec.Start, found);
        var c = search.Confirm();
        Assert.Equal(("confirm", Spec.Start, found, Comparison.Better), (c!.Stage, c.A, c.B, c.Outcome));
        Assert.Equal(found, search.Best);
        Assert.Same(c, search.Log[^1]);

        // Kept on the way (the step to +300 read faster), not faster than the start when measured against it.
        Comparison Fooled(string stage, UncoreSetting a, UncoreSetting b) =>
            new(stage, a, b, stage != "confirm" && b.XbarMhz == 300 ? Comparison.Better : Comparison.NotBetter, "", null, null, null, []);
        var fooled = new UncoreSearch(Spec, Fooled, () => false);
        fooled.Run();
        Assert.Equal(300, fooled.Best.XbarMhz);
        Assert.Equal(Comparison.NotBetter, fooled.Confirm()!.Outcome);
        Assert.Equal(Spec.Start, fooled.Best);
        Assert.Null(fooled.Ended);

        // A confirmation that could not be judged ends the search, and names nothing.
        var busy = new UncoreSearch(Spec, (stage, a, b) => stage == "confirm" ? new(stage, a, b, Comparison.Unjudged, "disturbed twice", null, null, null, []) : Fooled(stage, a, b), () => false);
        busy.Run();
        busy.Confirm();
        Assert.Equal(Spec.Start, busy.Best);
        Assert.Contains("could not be measured again against the start: disturbed twice", busy.Ended);

        // Nothing beat the start: nothing to confirm.
        var (flat, _) = Run(_ => 4780);
        Assert.Null(flat.Confirm());
    }

    /// <summary>Each step may cost the core up to 1 %; the steps' sum against the start can cost more (the reference card's
    /// 617.42 run kept steps at -0.9 and -0.5 %, inside the core's spread there). That cost is told with the best, not a
    /// reason to name nothing, as long as the L2 gain itself holds; the cliff, or a gain that did not hold, still is.</summary>
    [Fact]
    public void A_cost_the_steps_add_up_to_is_told_with_the_best_not_a_reason_to_drop_it()
    {
        UncoreSearch With(Func<string, UncoreSetting, UncoreSetting, Comparison> confirming)
        {
            var s = new UncoreSearch(Spec, (stage, a, b) => stage == "confirm" ? confirming(stage, a, b)
                : new(stage, a, b, b.XbarMhz == 300 ? Comparison.Better : Comparison.NotBetter, "", null, null, null, []), () => false);
            s.Run();
            s.Confirm();
            return s;
        }
        var cost = With((stage, a, b) => new(stage, a, b, Comparison.NotBetter, "the memory phase's core clock fell -1.40 %", 2.5, 0.1, -1.4, [], Gain: true, Veto: Comparison.CoreCost));
        Assert.Equal(300, cost.Best.XbarMhz);
        Assert.True(UncoreSearch.Holds(cost.Log[^1]));
        var dram = With((stage, a, b) => new(stage, a, b, Comparison.NotBetter, "DRAM bandwidth fell -0.80 %", 2.5, -0.8, 0, [], Gain: true, Veto: Comparison.DramCost));
        Assert.Equal(300, dram.Best.XbarMhz);
        var cliff = With((stage, a, b) => new(stage, a, b, Comparison.NotBetter, "the cliff", 2.5, 0, -40, [], Gain: true, Veto: Comparison.Cliff));
        Assert.Equal(Spec.Start, cliff.Best);
        var gone = With((stage, a, b) => new(stage, a, b, Comparison.NotBetter, "no clear gain", 0.2, 0, -1.4, [], Gain: false, Veto: Comparison.CoreCost));
        Assert.Equal(Spec.Start, gone.Best);
    }
}
