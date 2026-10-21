using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>T08: what a stretch of load cost, and frames per 100 W for a power-limit sweep.</summary>
public class EfficiencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static Card.Sample S(double sec, double w) => new(T0.AddSeconds(sec),
        new TelemetrySnapshot(70, w, 600, 575, 600, 400, 600, 2900, 15000, 3200, 15000, 99, 40, null, null, [], []), 950_000, null, null, null);

    [Fact]
    public void Energy_power_and_frames_per_100_watts()
    {
        var samples = Enumerable.Range(0, 7201).Select(i => S(i * 0.5, 500)).ToList();   // one hour at 500 W
        var e = JsonSerializer.SerializeToElement(TuningTools.Efficiency(samples, 120, 2900));
        Assert.Equal(500, e.GetProperty("avg_board_w").GetDouble());
        Assert.Equal(500, e.GetProperty("energy_wh").GetDouble());
        Assert.Equal(24, e.GetProperty("fps_per_100w").GetDouble());
        Assert.Equal(5.8, e.GetProperty("mhz_per_w").GetDouble());
    }

    [Fact]
    public void A_gap_in_the_samples_is_not_energy_and_no_power_is_no_answer()
    {
        var e = JsonSerializer.SerializeToElement(TuningTools.Efficiency([S(0, 400), S(0.5, 400), S(600, 400)], null));
        Assert.Equal(0.06, e.GetProperty("energy_wh").GetDouble());   // only the half second: 400 W x 0.5 s
        Assert.Equal(JsonValueKind.Null, e.GetProperty("fps_per_100w").ValueKind);
        Assert.Null(TuningTools.Efficiency([], 100));
    }
}
