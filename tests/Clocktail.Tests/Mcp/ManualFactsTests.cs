using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The measured facts the agent acts on are in the manual it is served (the embedded pages, as the manual tool
/// returns them): S-3, S-6, S-9 and S-10 from the 0.2.1 review's sweep of the reference card's measurements.</summary>
public class ManualFactsTests
{
    /// <summary>Every tool that writes a trial is in tuning.md's table of what it changes and what keep takes.</summary>
    [Fact]
    public void The_tuning_table_names_every_trial_tool()
    {
        string tuning = ManualTools.Manual("tuning");
        foreach (var tool in new[] { "set_curve", "shift_curve", "set_core_offset", "adjust_nvvdd", "adjust_clock_domain", "set_clock_ratio",
                                     "set_voltage_boost", "set_clock_lock", "set_power_cap" })
            Assert.Contains($"| `{tool}` |", tuning);
        Assert.Contains("the lower of FE and F8 holds", tuning);
    }

    [Theory]
    [InlineData("tuning", "loosen the wall or the current limit after each raise")]          // S-3
    [InlineData("tuning", "480 → 490 W, or 350 → 360 A")]
    [InlineData("stability", "were pinned with `nvidia-smi -lgc` clock locks (`set_clock_lock`")]                 // S-6
    [InlineData("stability", "55-64 965-985 mV at 430-550 W")]
    [InlineData("method", "To measure this card's own")]                                        // S-9
    [InlineData("xbar-msvdd", "SYS 2182 / 2017 / 1912 / 1830 MHz")]                              // S-10
    public void The_manual_carries_it(string topic, string fact) => Assert.Contains(fact, ManualTools.Manual(topic));
}
