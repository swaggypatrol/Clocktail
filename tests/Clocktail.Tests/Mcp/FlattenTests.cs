using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>flatten_curve's plan: from a voltage up, every point at most the target clock; lower only, on the grid.</summary>
public class FlattenTests
{
    // 900 … 1000 mV every 10 mV; effective = 2 850 + 20 MHz per point, offsets 15 MHz on each.
    private static List<VfPoint> Points() => [.. Enumerable.Range(0, 11).Select(i =>
        new VfPoint(80 + i, 900_000u + (uint)i * 10_000, 2_835_000u + (uint)i * 20_000, 2_850_000u + (uint)i * 20_000, 15_000, 900_000u + (uint)i * 10_000))];

    private static int[] Offsets() => [.. Enumerable.Range(0, 91).Select(i => i >= 80 ? 15_000 : 0)];

    [Fact]
    public void Everything_from_the_voltage_up_runs_at_most_the_target()
    {
        var pts = Points();
        var next = TuningTools.FlattenPlan(pts, Offsets(), 950_000, 2_950_000, 850_000);
        foreach (var p in pts)
        {
            long now = p.EffectiveKhz - p.OffsetKhz + next[p.Index];
            if (p.VoltageUv >= 950_000) Assert.True(now <= 2_950_000, $"point {p.Index} at {now}");
            Assert.True(next[p.Index] <= 15_000, "never raised");
            Assert.Equal(0, next[p.Index] % 7_500);
        }
        Assert.Equal(15_000, next[80]);                       // under the voltage and under the target: untouched
        Assert.Equal(15_000, next[85]);                       // the point at 950 mV already runs 2 950
        Assert.Equal(-7_500, next[86]);                       // 2 970 → at most 2 950: 2 955 bare, -5 rounds down to -7.5
    }

    [Fact]
    public void A_point_under_the_voltage_above_the_target_comes_down_too_so_the_curve_never_falls()
    {
        var next = TuningTools.FlattenPlan(Points(), Offsets(), 950_000, 2_900_000, 850_000);
        Assert.True(Points()[3].EffectiveKhz - 15_000 + next[83] <= 2_900_000);   // 930 mV ran 2 910: lowered
        Assert.Equal(15_000, next[81]);                                            // 910 mV ran 2 870: untouched
    }

    [Fact]
    public void Points_under_the_floor_are_never_touched()
    {
        var next = TuningTools.FlattenPlan(Points(), Offsets(), 900_000, 2_700_000, 950_000);
        Assert.All(Enumerable.Range(80, 5), i => Assert.Equal(15_000, next[i]));   // under 950 mV
    }

    [Fact]
    public void A_point_lifted_by_a_left_neighbor_spike_uses_its_own_base_when_flattened()
    {
        // Point 80 has a +200 MHz spike (2800 -> 3000 MHz); point 81 has Base=2820, Offset=0, Effective=3000 because driver clamps monotonically.
        var pts = new List<VfPoint>
        {
            new(80, 900_000, 2_800_000, 3_000_000, 200_000, 900_000),
            new(81, 910_000, 2_820_000, 3_000_000, 0, 910_000),
        };
        var offsets = new int[82];
        offsets[80] = 200_000;
        offsets[81] = 0;

        var next = TuningTools.FlattenPlan(pts, offsets, 900_000, 2_900_000, 850_000);
        Assert.Equal(97_500, next[80]);   // 2800 + 97.5 <= 2900
        Assert.Equal(0, next[81]);        // 2820 + 0 <= 2900 already; must NOT get pushed down to -100 MHz!
    }
}
