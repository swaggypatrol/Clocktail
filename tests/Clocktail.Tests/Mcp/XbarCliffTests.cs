using System.Text.Json;
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

    /// <summary>card_report's XBAR row: measured to +560 / +50 mV on 621.01 (ratio 0.95, boost 100 %: lab_test and
    /// stress_test passed, the reference card's daily), so past it is a look, and so is an offset short of its demand.</summary>
    [Theory]
    [InlineData(250, 0, SettingsTools.Safe, "within the measured range")]
    [InlineData(450, 50, SettingsTools.Safe, "+50 mV keeps MSVDD off its floor")]
    [InlineData(560, 50, SettingsTools.Safe, "+50 mV keeps MSVDD off its floor")]
    [InlineData(590, 50, SettingsTools.Look, "past the +560 MHz measured on the reference card")]
    [InlineData(450, 0, SettingsTools.Look, "the cliff: at +450 MHz it needs +50 mV")]
    [InlineData(560, 20, SettingsTools.Look, "the cliff: at +560 MHz it needs +50 mV")]
    public void The_report_marks_past_the_measured_range_and_short_of_the_demand(int offsetMhz, int demandMv, string mark, string note)
    {
        var r = Xbar(offsetMhz, demandMv);
        Assert.Equal(mark, r.Mark);
        Assert.Contains(note, r.Note);
        Assert.DoesNotContain("grey screen", r.Note);
    }

    [Fact]
    public void Past_the_range_and_short_of_the_demand_says_both()
    {
        var r = Xbar(600, 0);
        Assert.Equal(SettingsTools.Look, r.Mark);
        Assert.Contains("past the +560 MHz measured", r.Note);
        Assert.Contains("the cliff: at +600 MHz it needs +50 mV", r.Note);
    }

    private static (string Name, string Mark, string Note) Xbar(int offsetMhz, int demandMv) =>
        Assert.Single(SettingsTools.ReportRows(
            JsonDocument.Parse($$"""{"clock_domains":{"xbar":{"offset_mhz":{{offsetMhz}},"vdemand_mv":{{demandMv}} } } }""").RootElement,
            JsonDocument.Parse("{}").RootElement), r => r.Name == "XBAR (crossbar)");
}
