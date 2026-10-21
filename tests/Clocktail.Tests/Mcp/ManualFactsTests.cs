using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The measured facts the agent acts on are in the manual it is served (the embedded pages, as the manual tool
/// returns them): S-3, S-6, S-9 and S-10 from the 0.2.1 review's sweep of the reference card's measurements.</summary>
public class ManualFactsTests
{
    [Theory]
    [InlineData("tuning", "loosen the wall or the current limit after each raise")]          // S-3
    [InlineData("tuning", "480 → 490 W, or 350 → 360 A")]
    [InlineData("stability", "were pinned with `nvidia-smi -lgc` clock locks")]                 // S-6
    [InlineData("stability", "55-64 965-985 mV at 430-550 W")]
    [InlineData("method", "To measure this card's own")]                                        // S-9
    [InlineData("xbar-msvdd", "SYS 2182 / 2017 / 1912 / 1830 MHz")]                              // S-10
    public void The_manual_carries_it(string topic, string fact) => Assert.Contains(fact, ManualTools.Manual(topic));
}
