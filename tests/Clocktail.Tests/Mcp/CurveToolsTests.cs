using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>set_curve and shift_curve stay under the daily-profile cap point by point.</summary>
public class CurveToolsTests
{
    [Fact]
    public void The_first_point_past_the_cap_is_named()
    {
        int[] daily = [0, 30_000, 30_000];
        Assert.Null(TuningTools.OverDaily([0, 230_000, 0], daily, 200));
        Assert.Equal(1, TuningTools.OverDaily([0, 230_001, 300_000], daily, 200));
        Assert.Equal(3, TuningTools.OverDaily([0, 0, 0, 200_001], daily, 200));   // a point the daily profile does not reach counts from 0
    }
}
