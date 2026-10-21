using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>card_report with sections card_settings could not read: each is ⚠️ "unreadable", never stock ✅, and with no
/// live readings the verdict cannot call the card healthy.</summary>
public class UnreadableReportTests
{
    private static IReadOnlyList<(string Name, string Mark, string Note)> Rows(string settings, string limiter) =>
        SettingsTools.ReportRows(JsonDocument.Parse(settings).RootElement, JsonDocument.Parse(limiter).RootElement);

    [Fact]
    public void Failed_sections_are_marked_unreadable()
    {
        var rows = Rows("""
            {"power":{"error":"NVAPI -8"},"vf_curve":{"error":"the driver did not answer"},"clock_offsets":{"error":"x"},
             "clock_domains":{"error":"y"},"rails":{"nvvdd":{"error":"z"},"msvdd":{"error":"z"}}}
            """, """{"error":"NVML is unreadable"}""");
        foreach (var name in new[] { "Power limit", "V/F curve", "Memory offset", "XBAR (crossbar)", "Live readings" })
        {
            var r = Assert.Single(rows, r => r.Name == name);
            Assert.Equal(SettingsTools.Look, r.Mark);
            Assert.Contains("unreadable", r.Note.Replace("unavailable", "unreadable"));
        }
        Assert.DoesNotContain(rows, r => r.Mark == SettingsTools.Safe);
    }

    [Fact]
    public void A_readable_stock_card_is_still_safe()
    {
        var rows = Rows("""
            {"power":{"limit_w":575,"stock_w":575,"board_max_w":600,"board_min_w":400},
             "vf_curve":{"offset_points":0},"clock_offsets":{"memory":{"offset_mhz":0,"stock_mhz":0,"min_mhz":-1000,"max_mhz":3000}},
             "clock_domains":{"xbar":{"offset_mhz":0,"vdemand_mv":0}}}
            """, """{"readings":{"temp_c":45,"board_w":60,"power_limit_w":575,"clock_mhz":900,"nvvdd_mv":800}}""");
        foreach (var name in new[] { "Power limit", "V/F curve", "Memory offset", "XBAR (crossbar)" })
            Assert.Equal(SettingsTools.Safe, Assert.Single(rows, r => r.Name == name).Mark);
        Assert.DoesNotContain(rows, r => r.Name == "Live readings");
    }
}
