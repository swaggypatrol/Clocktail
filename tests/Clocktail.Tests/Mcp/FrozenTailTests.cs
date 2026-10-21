using Clocktail.Core.Gpu;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>A stress run that hung: its statistics are taken before the frozen readings, not over them.</summary>
public class FrozenTailTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 59, 0, TimeSpan.Zero);

    private static Card.Sample S(double sec, uint mhz, uint uv, double w) => new(T0.AddSeconds(sec),
        new TelemetrySnapshot(70, w, 600, 575, 600, 400, 600, mhz, 15000, 3200, 15000, 100, 40, null, null, [], []), uv, null, null, null);

    [Fact]
    public void The_frozen_tail_of_a_hang_is_cut()
    {
        var run = new List<Card.Sample>();
        for (int i = 0; i < 80; i++) run.Add(S(i * 0.5, 2895, 910_000, 598));
        for (int i = 0; i < 60; i++) run.Add(S(40 + i * 0.5, 3105, 1_030_000, 140));
        var held = TuningTools.WithoutFrozenTail(run);
        Assert.Equal(80, held.Count);
        Assert.All(held, s => Assert.Equal(910_000u, s.NvvddUv));
    }

    [Fact]
    public void A_run_with_no_freeze_or_one_frozen_throughout_is_kept_whole()
    {
        var steady = Enumerable.Range(0, 40).Select(i => S(i * 0.5, 3045, 985_000, 380)).ToList();
        Assert.Equal(40, TuningTools.WithoutFrozenTail(steady).Count);   // pinned at the top: not a hang
        var varied = Enumerable.Range(0, 40).Select(i => S(i * 0.5, 2895u + (uint)(i % 3) * 7, 910_000, 590)).ToList();
        Assert.Equal(40, TuningTools.WithoutFrozenTail(varied).Count);
    }
}
