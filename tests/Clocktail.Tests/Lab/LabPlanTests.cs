using Clocktail.Domain.Lab;
using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

public class LabPlanTests
{
    [Theory]
    [InlineData("core", 150, "burn,solo,leap")]
    [InlineData("memory", 90, "memory,latency")]
    [InlineData("uncore", 60, "l2,memory")]
    [InlineData("all", 270, "burn,solo,leap,memory,latency,l2")]
    public void Goals_keep_their_phase_budgets_through_the_worker_protocol(string profile, int seconds, string phases)
    {
        var plan = LabTestPlan.Create(profile);
        Assert.Equal(seconds, plan.Seconds);
        Assert.Equal(phases, string.Join(",", plan.Stages.Select(s => s.Phase)));
        var parsed = LabConfig.Parse(plan.PhaseArgument, seconds);
        Assert.Equal(plan.Stages.Select(s => s.Seconds), parsed.Select(p => p.Item2));
        Assert.Equal(plan.Stages.Select(s => s.Phase), parsed.Select(p => p.Item1.ToString().ToLowerInvariant()));
    }

    [Fact]
    public void The_default_core_run_gives_the_high_point_and_jump_a_minute_each()
    {
        var plan = LabTestPlan.Create();
        Assert.Equal("core", plan.Profile);
        Assert.Equal([30.0, 60, 60], plan.Stages.Select(s => s.Seconds));
        var longer = LabTestPlan.Create("core", seconds: 300);
        Assert.Equal([60.0, 120, 120], longer.Stages.Select(s => s.Seconds));
        Assert.Throws<ArgumentException>(() => LabTestPlan.Create("core", seconds: 60));
    }

    [Fact]
    public void An_explicit_legacy_list_keeps_its_equal_split_and_60_second_default()
    {
        var legacy = LabTestPlan.Create(phases: "burn,pulse,light,memory,latency");
        Assert.Equal("custom", legacy.Profile);
        Assert.All(legacy.Stages, s => Assert.Equal(12, s.Seconds));
        Assert.Equal([10.0], LabTestPlan.Create(phases: "light", seconds: 10).Stages.Select(s => s.Seconds));
    }

    [Theory]
    [InlineData("burn:30,solo", 90)]
    [InlineData("burn:NaN", 30)]
    [InlineData("burn:Infinity", 30)]
    [InlineData("burn:-1", 30)]
    [InlineData("burn:0", 30)]
    [InlineData("burn:30,solo:60", 60)]
    [InlineData("burn::30", 30)]
    [InlineData("8", 30)]
    public void Malformed_or_misbudgeted_schedules_never_reach_the_GPU(string phases, int seconds) =>
        Assert.Throws<ArgumentException>(() => LabConfig.Parse(phases, seconds));

    [Fact]
    public void A_schedule_without_seconds_runs_for_the_sum_of_its_durations()
    {
        var plan = LabTestPlan.Create(phases: "burn:30, solo : 60 ,leap:60");
        Assert.Equal(150, plan.Seconds);
        Assert.Equal(["burn", "solo", "leap"], plan.Stages.Select(s => s.Phase));
        Assert.Equal([30.0, 60, 60], plan.Stages.Select(s => s.Seconds));
        Assert.Throws<ArgumentException>(() => LabTestPlan.Create(phases: "burn:30.5,solo:60"));
        Assert.Equal(91, LabTestPlan.Create(phases: "burn:30.5,solo:60.5").Seconds);
    }

    [Theory]
    [InlineData("burn:0.5,solo:9.5", 10)]
    [InlineData("burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn,burn", 10)]
    public void No_phase_is_shorter_than_a_second(string phases, int seconds) =>
        Assert.Throws<ArgumentException>(() => LabTestPlan.Create(phases: phases, seconds: seconds));

    [Fact]
    public void A_profile_and_custom_phases_cannot_silently_override_each_other() =>
        Assert.Throws<ArgumentException>(() => LabTestPlan.Create("memory", "burn", 90));

    [Fact]
    public void Aria_is_an_explicit_c01_movement_and_the_existing_core_budget_is_unchanged()
    {
        var aria = LabTestPlan.Create("aria");
        Assert.Equal(180, aria.Seconds);
        Assert.Equal("c01", Assert.Single(aria.Stages).Phase);
        Assert.Equal(60, LabTestPlan.Create("aria", seconds: 60).Seconds);
        Assert.Equal("aria", LabTestPlan.Create("c01").Profile);
        Assert.Throws<ArgumentException>(() => LabTestPlan.Create("aria", seconds: 9));
        Assert.Equal(150, LabTestPlan.Create("core").Seconds);
        var score = LabTestPlan.Create("symphony");
        Assert.Equal(330, score.Seconds);
        Assert.Equal(["c01", "burn", "solo", "leap"], score.Stages.Select(s => s.Phase));
        Assert.Equal([180.0, 30, 60, 60], score.Stages.Select(s => s.Seconds));
        var scaled = LabTestPlan.Create("symphony", seconds: 331);
        Assert.Equal(331, scaled.Stages.Sum(s => s.Seconds), 8);
        Assert.Equal(Math.Truncate(scaled.Stages[0].Seconds), scaled.Stages[0].Seconds);
        Assert.Throws<ArgumentException>(() => LabTestPlan.Create("symphony", seconds: 329));
    }
}
