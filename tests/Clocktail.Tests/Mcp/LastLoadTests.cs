using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>adjust_curve between stress runs: the card is idle, so its window centres on where the last run held.</summary>
public class LastLoadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_last_10_seconds_of_a_load_that_ended_within_2_minutes_count()
    {
        var ticks = new List<Residency.Tick>();
        for (int s = 0; s < 60; s++) ticks.Add(new(T0.AddSeconds(s), s < 50 ? 905_000u : 920_000u, 2880, 100, 590));
        var now = T0.AddSeconds(90);   // 30 s after the run ended
        Assert.Equal(920_000u, Residency.LastLoadMedian(ticks, now, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10)));
        Assert.Null(Residency.LastLoadMedian(ticks, T0.AddMinutes(5), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10)));   // too long ago
        Assert.Null(Residency.LastLoadMedian([], now, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10)));
    }
}

/// <summary>A light-load stress run (nvgwls -l 30/40, read as 30-57 % utilization) leaves samples for its trial.</summary>
public class StressResidencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Light_load_counts_only_while_a_stress_run_is_on()
    {
        Assert.False(Residency.Counts(35, T0, DateTimeOffset.MinValue));          // a game at 35 %: a menu, not load
        Assert.True(Residency.Counts(35, T0, T0.AddSeconds(90)));                  // stress_test load 40
        Assert.False(Residency.Counts(10, T0, T0.AddSeconds(90)));                 // idle even during the run
        Assert.True(Residency.Counts(80, T0, DateTimeOffset.MinValue));
    }
}
