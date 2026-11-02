using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>Report judgments use the current GPU's ranges, not one development board's measured offsets.</summary>
public class PerGpuSettingsReportTests
{
    [Theory]
    [InlineData(650, 800, SettingsTools.Safe)]
    [InlineData(650, 600, SettingsTools.Look)]
    [InlineData(100, 200, SettingsTools.Safe)]
    public void Clock_domain_marks_follow_its_own_reported_range(int offset, int maximum, string expected)
    {
        using var settings = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            gpu = "NVIDIA GeForce RTX 5070",
            clock_domains = new { xbar = new { offset_mhz = offset, min_mhz = -200, max_mhz = maximum, vdemand_mv = 0 } },
        }));
        using var limiter = JsonDocument.Parse("{}");
        var rows = SettingsTools.ReportRows(settings.RootElement, limiter.RootElement);
        var row = Assert.Single(rows, r => r.Name == "XBAR (crossbar)");
        Assert.Equal(expected, row.Mark);
        Assert.DoesNotContain("reference", row.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cliff", row.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("needs", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_memory_offset_is_not_compared_with_the_developers_personal_offset()
    {
        using var settings = JsonDocument.Parse("""
            {"clock_offsets":{"memory":{"offset_mhz":1500,"stock_mhz":0,"min_mhz":-500,"max_mhz":2000}}}
            """);
        using var limiter = JsonDocument.Parse("{}");
        var row = Assert.Single(SettingsTools.ReportRows(settings.RootElement, limiter.RootElement), r => r.Name == "Memory offset");
        Assert.Equal(SettingsTools.Safe, row.Mark);
        Assert.DoesNotContain("358", row.Note);
        Assert.DoesNotContain("reference", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A rail is judged against this card's own stock offsets: raised past them is a look, lowered or as shipped is
    /// fine, whatever the voltage; no fixed ceiling.</summary>
    [Theory]
    [InlineData(-50, 0, -50, 0, SettingsTools.Safe)]       // as shipped
    [InlineData(-75, 0, -50, 0, SettingsTools.Safe)]       // undervolted
    [InlineData(-25, 0, -50, 0, SettingsTools.Look)]       // REL raised past stock
    [InlineData(-50, 25, -50, 0, SettingsTools.Look)]      // VMIN raised
    public void A_rail_is_marked_against_its_own_stock(int rel, int vmin, int relStock, int vminStock, string expected)
    {
        using var settings = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            rails = new
            {
                msvdd = new
                {
                    offsets_mv = new { rel, alt = 0, ov = 0, vmin },
                    stock_offsets_mv = new { rel = relStock, alt = 0, ov = 0, vmin = vminStock },
                    limits_mv = new { max = 1250 },   // past the old 1150 mV ceiling: not a mark by itself
                },
            },
        }));
        using var limiter = JsonDocument.Parse("{}");
        var row = Assert.Single(SettingsTools.ReportRows(settings.RootElement, limiter.RootElement), r => r.Name == "MSVDD rail (L2, crossbar)");
        Assert.Equal(expected, row.Mark);
        Assert.DoesNotContain("ceiling", row.Note);
    }
}
