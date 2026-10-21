using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class NvgwlsTests
{
    [Fact]
    public void Exit_code_2_with_a_driver_reset_is_a_hang_the_card_failed()
    {
        Assert.StartsWith("hang: the driver reset", Nvgwls.Verdict(2, 1));
        Assert.True(Nvgwls.Failed(2, 1));
    }

    [Fact]
    public void Exit_code_2_without_a_reset_is_a_run_that_could_not_start()
    {
        Assert.Equal(Nvgwls.Verdict(2), Nvgwls.Verdict(2, 0));
        Assert.False(Nvgwls.Failed(2, 0));
    }

    [Fact]
    public void An_unreadable_log_leaves_exit_code_2_open_and_the_others_as_they_were()
    {
        Assert.Contains("could not be read", Nvgwls.Verdict(2, null));
        Assert.False(Nvgwls.Failed(2, null));
        Assert.Equal("pass", Nvgwls.Verdict(0, null));
        Assert.True(Nvgwls.Failed(6, null));
    }

    [Fact]
    public void A_reset_fails_even_a_run_that_exited_0()
    {
        Assert.True(Nvgwls.Failed(0, 2));
        Assert.Contains("2 reset events", Nvgwls.Verdict(0, 2));
    }
}

/// <summary>stress_test's temperature stop: the limit, or within 2 °C of the driver's slowdown point.</summary>
public class StressThermalStopTests
{
    [Fact]
    public void Too_hot_is_the_limit_or_the_last_two_degrees_of_margin()
    {
        Assert.False(Clocktail.Mcp.Nvgwls.TooHot(84, 6, 85));
        Assert.True(Clocktail.Mcp.Nvgwls.TooHot(85, 5, 85));
        Assert.True(Clocktail.Mcp.Nvgwls.TooHot(80, 2, 85));     // the driver says it is about to slow down
        Assert.False(Clocktail.Mcp.Nvgwls.TooHot(null, null, 85)); // nothing read: not a reason to stop
    }
}
