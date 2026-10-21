using System.Text.Json;
using Clocktail.Domain.Power;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>T09 as measured on the RTX 5090 FE (2026-10-21): channel type 0xda is the 16-pin input (board less the PCIe
/// slot, to 0.1 W), 0xa9 the input current (49.6 A at the 600 W wall), capped at 60 A by its policy record.</summary>
public class BoardInputTests
{
    private static CurrentLimits Limits() => new(
        [new PolicyRecord(9, 0x13, 9, PolicyTableLayout.UnitMilliamps, 0, 60_000, 60_000)],
        [new MonitorChannel(1, 0, 0xff), new MonitorChannel(2, 0, 0xda), new MonitorChannel(9, 0, 0xa9)],
        null, null, new Dictionary<Clocktail.Domain.Rails.Rail, string>());

    private static IReadOnlyList<MonitorReading> At600W() =>
        [new MonitorReading(1, 0xff, 10_800, 900, 12_000_000), new MonitorReading(2, 0xda, 561_400, 48_200, 11_950_000), new MonitorReading(9, 0xa9, 0, 49_600, 0)];

    [Fact]
    public void The_wall_reads_as_connector_watts_and_input_current_under_its_limit()
    {
        var input = BoardInput.From(Limits(), At600W())!;
        Assert.Equal((561.4, 10.8, 49.6, 60.0), (input.ConnectorW!.Value, input.PcieSlotW!.Value, input.InputA!.Value, input.InputLimitA!.Value));
        Assert.Null(input.Concern());   // 83 % of the limit
        Assert.Equal("16-pin input", PowerMonitorLayout.Name(0xda));
        Assert.Equal("board input current", PowerMonitorLayout.Name(0xa9));
    }

    [Fact]
    public void Over_the_rating_or_near_the_limit_is_a_concern()
    {
        Assert.Contains("over its 600 W rating", new BoardInput(612, 10, 52, 60).Concern());
        Assert.Contains("within 10 %", new BoardInput(590, 10, 54.5, 60).Concern());
        Assert.Null(BoardInput.From(Limits(), [new MonitorReading(1, 0xff, 10_000, 800, 12_000_000)]));   // no such channels
    }

    [Fact]
    public void Card_report_shows_the_16_pin_row()
    {
        var s = JsonDocument.Parse("""{"current_limits":{"input":{"connector_w":612,"input_a":52,"input_limit_a":60,"concern":"the 16-pin connector carries 612 W, over its 600 W rating"}}}""").RootElement;
        var row = Assert.Single(SettingsTools.ReportRows(s, JsonDocument.Parse("{}").RootElement), r => r.Name == "16-pin input");
        // The firmware holding another wall than the request is a look, with what to do.
        var wall = JsonDocument.Parse("""{"power":{"limit_w":600,"firmware_w":575,"board_min_w":400,"board_max_w":600,"stock_w":575,"daily_w":600}}""").RootElement;
        var power = Assert.Single(SettingsTools.ReportRows(wall, JsonDocument.Parse("{}").RootElement), r => r.Name == "Power limit");
        Assert.Equal(SettingsTools.Look, power.Mark);
        Assert.Contains("the card enforces 575 W, not the 600 W asked for", power.Note);
        Assert.Equal(SettingsTools.Look, row.Mark);
    }
}
