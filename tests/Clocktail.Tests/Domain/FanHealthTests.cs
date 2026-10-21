using System.Text.Json;
using Clocktail.Domain.Fan;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>S-11: under the firmware's curve the tach follows the target to ±5 rpm (re/fan.md §1); a big gap or a
/// stopped fan with the curve asking for speed is a fault.</summary>
public class FanHealthTests
{
    [Fact]
    public void A_fan_that_follows_or_ramps_or_is_held_is_fine()
    {
        Assert.Null(FanHealth.Check([(0, 1990, false), (1, 1984, false)], 1986));
        Assert.Null(FanHealth.Check([(0, 1500, false)], 1700));          // ramping: within 25 %
        Assert.Null(FanHealth.Check([(0, 0, false)], 0));                // stopped at idle, as asked
        Assert.Null(FanHealth.Check([(0, 900, true)], 1986));            // a fixed duty does not follow the curve
        Assert.Null(FanHealth.Check([(0, 1100, false)], 13107));         // a spin-up kick, not a curve target
    }

    [Fact]
    public void A_slow_fan_needs_a_look_and_a_stopped_one_needs_action()
    {
        Assert.False(FanHealth.Check([(0, 1990, false), (1, 1200, false)], 1986)!.Value.Stopped);
        Assert.True(FanHealth.Check([(0, 1990, false), (1, 0, false)], 1986)!.Value.Stopped);
        var s = JsonDocument.Parse("""{"fans":{"channels":[{"id":0,"mode":"auto","duty_pct":60,"rpm":1990},{"id":1,"mode":"auto","duty_pct":60,"rpm":0}],"curves":[{"editable":true,"target_rpm":1986}]}}""").RootElement;
        var row = Assert.Single(SettingsTools.ReportRows(s, JsonDocument.Parse("{}").RootElement), r => r.Name == "Fans");
        Assert.Equal(SettingsTools.Act, row.Mark);
        Assert.Contains("fan 1 reads 0 rpm", row.Note);
    }
}
