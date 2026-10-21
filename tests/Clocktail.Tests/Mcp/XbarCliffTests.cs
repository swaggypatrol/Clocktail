using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The voltage demand an XBAR offset needs, as measured on the reference card (re/tuning-results §28): none to
/// +250, +20 mV to +300, +50 mV past it (+400 with +20/+30 mV still fell to 1830-1942 MHz in the memory phase).</summary>
public class XbarCliffTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(250, null)]
    [InlineData(275, 20)]
    [InlineData(300, 20)]
    [InlineData(325, 50)]
    [InlineData(400, 50)]
    public void The_demand_needed_steps_up_past_300(double offsetMhz, int? needMv) =>
        Assert.Equal(needMv, TuningTools.XbarDemandNeededMv(offsetMhz));
}
