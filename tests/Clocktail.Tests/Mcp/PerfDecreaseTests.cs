using System.Text.Json;
using Clocktail.Domain.Power;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class PerfDecreaseTests
{
    [Fact]
    public void The_public_bits_by_name()
    {
        Assert.Empty(Card.PerfDecreaseReasons(0));   // idle and the 600 W wall on the reference card
        Assert.Equal(["api_triggered"], Card.PerfDecreaseReasons(0x8));   // a CUDA program has the GPU
        Assert.Equal(["insufficient_power"], Card.PerfDecreaseReasons(0x10));
        Assert.Equal(["thermal_protection", "power_control"], Card.PerfDecreaseReasons(0x3));
    }

    [Fact]
    public void Power_topology_entries_are_thousandths_of_a_percent_of_the_default_limit()
    {
        // nvgwls at three loads under a 575 W default limit, 2026-10-20.
        Assert.InRange(PowerTopologyLayout.Watts(104_300, 575), 599, 601);
        Assert.InRange(PowerTopologyLayout.Watts(85_700, 575), 492, 494);
        Assert.InRange(PowerTopologyLayout.Watts(45_000, 575), 258, 260);
    }

    /// <summary>card_report's "Driver holding it down" row. On the reference card (617.42 and 621.01) api_triggered came
    /// with a CUDA program on the GPU every time, at idle too, the memory's ceiling then CUDA_MAX: information while nothing
    /// holds the clock (the limiter finds it idle or under no limit, no clock lock), a look under a limit.</summary>
    private static (string Name, string Mark, string Note)? Row(string reasons, string? limiter, string locks = "[]", string memoryBy = "CUDA_MAX") =>
        SettingsTools.ReportRows(JsonDocument.Parse($$"""
            {"perf_decrease":{"reasons":{{reasons}}},"clock_locks":{{locks}},
             "perf_limits":[{"domain":"gpc","ceiling_mhz":3082,"ceiling_by":"RELIABILITY_ALT_NVVDD_0"},{"domain":"memory","ceiling_mhz":15559,"ceiling_by":"{{memoryBy}}"}]}
            """).RootElement, JsonDocument.Parse(limiter == null ? "{}" : $$"""{"limiter":"{{limiter}}"}""").RootElement)
        .Where(r => r.Name == "Driver holding it down").Select(r => ((string, string, string)?)r).SingleOrDefault();

    [Theory]
    [InlineData("idle")]
    [InlineData("no_limit")]
    public void Api_triggered_with_nothing_holding_the_clock_is_information(string limiter)
    {
        var r = Row("""["api_triggered"]""", limiter)!.Value;
        Assert.Equal(SettingsTools.Info, r.Mark);
        Assert.Contains("a CUDA program has the GPU", r.Note);
        Assert.Contains("caps the memory at 15559 MHz for it (CUDA_MAX)", r.Note);
        Assert.Contains("nothing holds the clock now", r.Note);
        Assert.DoesNotContain("reducing performance", r.Note);
    }

    [Fact]
    public void Api_triggered_under_a_limit_or_a_lock_is_a_look()
    {
        var wall = Row("""["api_triggered"]""", "power_wall")!.Value;
        Assert.Equal(SettingsTools.Look, wall.Mark);
        Assert.Contains("a CUDA program has the GPU", wall.Note);
        Assert.DoesNotContain("nothing holds the clock", wall.Note);
        Assert.Equal(SettingsTools.Look, Row("""["api_triggered"]""", "idle", locks: """[{"what":"maximum clock 2400 MHz"}]""")!.Value.Mark);
        Assert.Equal(SettingsTools.Look, Row("""["api_triggered"]""", "idle", locks: """{"error":"x"}""")!.Value.Mark);   // locks unread
        Assert.Equal(SettingsTools.Look, Row("""["api_triggered"]""", null)!.Value.Mark);   // the limiter did not answer
    }

    [Fact]
    public void Without_the_cuda_cap_the_driver_is_quoted_and_other_reasons_stay_marked()
    {
        var plain = Row("""["api_triggered"]""", "idle", memoryBy: "PERF_CF_CONTROLLER_DRAM_MIN")!.Value;
        Assert.Equal(SettingsTools.Info, plain.Mark);
        Assert.Equal("the driver reports it is reducing performance; nothing holds the clock now", plain.Note);
        var both = Row("""["power_control","api_triggered"]""", "idle")!.Value;
        Assert.Equal(SettingsTools.Look, both.Mark);
        Assert.StartsWith("the driver reports it is reducing performance; a CUDA program has the GPU", both.Note);
        Assert.Equal(SettingsTools.Act, Row("""["insufficient_power"]""", "idle")!.Value.Mark);
        Assert.Equal(SettingsTools.Act, Row("""["thermal_protection","api_triggered"]""", "idle")!.Value.Mark);
        Assert.Null(Row("[]", "idle"));
    }
}
