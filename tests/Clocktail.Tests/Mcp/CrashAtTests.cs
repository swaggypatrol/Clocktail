using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>by_point's crash attribution on the shape measured on the reference card (MingVolt re/tuning-results §2,
/// §17): a V/F hang at ~910 mV, then ~30 s of frozen readings at the curve's top (3105 MHz at 1030 mV, util 100 %,
/// ~140 W) until the driver logs the reset. The crash is the point the card ran at, not the frozen top.</summary>
public class CrashAtTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 59, 0, TimeSpan.Zero);

    private static VfCurveSnapshot Curve()
    {
        var points = Enumerable.Range(0, 25).Select(i =>
        {
            uint uv = 850_000 + (uint)i * 10_000;   // 850 … 1090 mV
            uint khz = 2_700_000 + (uint)i * 20_000;
            return new VfPoint(70 + i, uv, khz, khz, 0, uv);
        }).ToList();
        return new VfCurveSnapshot(default, default, new byte[32], points, 0, [], [], []);
    }

    private static List<Residency.Tick> Hang(int liveSeconds = 60, int frozenSeconds = 30)
    {
        var ticks = new List<Residency.Tick>();
        for (int s = 0; s < liveSeconds; s++) ticks.Add(new(T0.AddSeconds(s), 910_000, 2895, 100, 560));
        for (int s = 0; s < frozenSeconds; s++) ticks.Add(new(T0.AddSeconds(liveSeconds + s), 1_030_000, 3105, 100, 140));
        return ticks;
    }

    [Fact]
    public void A_frozen_tail_is_cut_before_the_last_30_seconds_are_counted()
    {
        var at = Assert.Single(TuningTools.CrashAt(Hang(), null, Curve()));
        Assert.Equal(76, at.Index);         // 910 mV, where it ran: not 88 (1030 mV, the frozen top)
        Assert.Equal(2895u, at.ClockMhz);
    }

    [Fact]
    public void The_flight_recorders_crash_voltage_picks_the_point_alone()
    {
        var at = Assert.Single(TuningTools.CrashAt(Hang(), 908_000, Curve()));
        Assert.Equal(76, at.Index);
        Assert.Equal(2895u, at.ClockMhz);
        // The voltage names the point even when every sample is frozen: no clock to give then.
        Assert.Equal((75, (uint?)null), Assert.Single(TuningTools.CrashAt(Hang(liveSeconds: 0), 900_000, Curve())));
    }

    [Fact]
    public void A_steady_load_that_did_not_freeze_is_counted_as_it_ran()
    {
        var ticks = Enumerable.Range(0, 60).Select(s => new Residency.Tick(T0.AddSeconds(s), 1_030_000, 3105, 100, 380)).ToList();
        Assert.Equal(88, Assert.Single(TuningTools.CrashAt(ticks, null, Curve())).Index);   // pinned at the top, power never fell
    }
}
