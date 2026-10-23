using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>tune_uncore's candidates: "XBAR MHz/demand mV/ratio/boost %" separated by ';', as the lab reads them.</summary>
public class UncoreCandidatesTests
{
    [Fact]
    public void A_list_becomes_the_labs_settings_with_the_ratio_raw()
    {
        var list = TuningTools.Candidates(" 400/50/0.95/100 ; 500/50/0.9499/50;")!;
        var json = JsonSerializer.Serialize(list);
        Assert.Equal("""[{"xbar_mhz":400,"demand_mv":50,"ratio_raw":62259,"boost_pct":100},{"xbar_mhz":500,"demand_mv":50,"ratio_raw":62253,"boost_pct":50}]""", json);
        Assert.Null(TuningTools.Candidates("  "));
    }

    [Theory]
    [InlineData("400/50/0.95")]
    [InlineData("400/50/fast/50")]
    [InlineData("400/50/1.2/50")]
    [InlineData("400/50/0.95/125")]
    public void A_setting_that_does_not_parse_is_refused(string text) =>
        Assert.Throws<ArgumentException>(() => TuningTools.Candidates(text));
}
