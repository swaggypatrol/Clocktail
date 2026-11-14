using Clocktail.Domain.Power;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class PerfDecreaseTests
{
    [Fact]
    public void The_public_bits_by_name()
    {
        Assert.Empty(Card.PerfDecreaseReasons(0));   // idle and the 600 W wall on the reference card
        Assert.Equal(["insufficient_power"], Card.PerfDecreaseReasons(0x10));
        Assert.Equal(["thermal_protection", "power_control"], Card.PerfDecreaseReasons(0x3));
    }

    [Fact]
    public void Power_topology_entries_are_thousandths_of_a_percent_of_the_default_limit()
    {
        // nvgwls at three loads under a 575 W default limit, 2026-10-20.
        Assert.InRange(PowerTopologyLayout.Watts(104_300, 575), 599, 601);
        Assert.InRange(PowerTopologyLayout.Watts(85_700, 575), 492, 494);
        Assert.InRange(PowerTopologyLayout.Watts(45_000, 575), 258, 260);
    }

    [Fact]
    public void IsRtx50_and_sample_quantiles_match_expected_values()
    {
        Assert.True(Card.IsRtx50("NVIDIA GeForce RTX 5090"));
        Assert.True(Card.IsRtx50("RTX5080"));
        Assert.False(Card.IsRtx50("NVIDIA GeForce RTX 4090"));

        var items = new double?[] { null, 50.0, 10.0, 90.0, 30.0, 70.0, null };
        var buf = new List<double>();
        var (p10, p50, p90) = SampleQuantiles.P10P50P90(items, x => x, buf);
        Assert.Equal(10.0, p10);
        Assert.Equal(50.0, p50);
        Assert.Equal(90.0, p90);
        Assert.Equal(50.0, SampleQuantiles.Avg(items, x => x));

        var empty = SampleQuantiles.P10P50P90(Array.Empty<double?>(), x => x, buf);
        Assert.Equal((null, null, null), empty);
        Assert.Null(SampleQuantiles.Avg(Array.Empty<double?>(), x => x));
    }
}
