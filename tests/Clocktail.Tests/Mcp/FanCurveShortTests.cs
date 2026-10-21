using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>card_report's fan curve row: full speed no later than stock, by temperature and by the rpm the firmware drives.</summary>
public class FanCurveShortTests
{
    private const string Stock = "50:20:1100,83:50:1775,88:100:2875";

    [Fact]
    public void Stock_and_the_founders_curve_hold()
    {
        Assert.Null(SettingsTools.FanCurveShort(Stock, Stock));
        Assert.Null(SettingsTools.FanCurveShort("50:20:1100,79:60:1995,86:100:2875", Stock));
    }

    [Fact]
    public void Full_duty_at_a_lower_top_rpm_is_short_of_stock()
    {
        var why = SettingsTools.FanCurveShort("50:20:1100,79:60:1300,86:100:1500", Stock);
        Assert.NotNull(why);
        Assert.Contains("1500 rpm", why);
    }

    [Fact]
    public void Full_speed_later_than_stock_or_never_is_short()
    {
        Assert.Equal("full speed later than the stock curve", SettingsTools.FanCurveShort("50:20:1100,85:50:1775,95:100:2875", Stock));
        Assert.Equal("full speed later than the stock curve", SettingsTools.FanCurveShort("50:20:1100,83:50:1775,88:90:2875", Stock));
    }
}
