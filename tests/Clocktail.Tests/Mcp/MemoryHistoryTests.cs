using System.Text.Json;
using System.Text.Json.Nodes;
using Clocktail.Lab;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>lab_test's memory history: which runs it keeps as a reference, which kept run a new one is compared with, and a
/// temperature stop the lab made itself.</summary>
public class MemoryHistoryTests
{
    private static JsonObject Run(int? offset = 1000, long buffer = 20000, string? pstate = "P0", double? xbar = 2800, uint? driver = 59621) => new()
    {
        ["at"] = DateTimeOffset.Now, ["offset_mhz"] = offset, ["buffer_mib"] = buffer, ["pstate"] = pstate,
        ["xbar_mhz"] = xbar, ["driver"] = driver, ["gbps"] = 1700.0,
    };

    /// <summary>A line as it comes back from memory.jsonl, not the object written.</summary>
    private static JsonObject Line(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    [Fact]
    public void The_same_setup_compares()
    {
        Assert.True(TuningTools.SameMemorySetup(Line(Run()), Run()));
        Assert.True(TuningTools.SameMemorySetup(Line(Run(xbar: 2805)), Run(xbar: 2800)));   // within 1 %: the P50's own spread
        Assert.True(TuningTools.SameMemorySetup(Line(Run(xbar: null)), Run(xbar: null)));   // a card whose crossbar clock is not read
    }

    /// <summary>The reference card's crossbar set its bandwidth (XBAR +250: 1455 → 1474 GB/s), so a run after adjust_xbar at
    /// the same memory offset is another setup, and a new driver is too.</summary>
    [Theory]
    [InlineData(3050.0, 59621u, 1000)]   // the crossbar raised
    [InlineData(2800.0, 59700u, 1000)]   // another driver
    [InlineData(2800.0, 59621u, 1100)]   // another memory offset
    public void Another_crossbar_clock_driver_or_offset_does_not(double xbar, uint driver, int offset)
    {
        Assert.False(TuningTools.SameMemorySetup(Line(Run(xbar: 2800, driver: 59621, offset: 1000)), Run(xbar: xbar, driver: driver, offset: offset)));
        Assert.False(TuningTools.SameMemorySetup(Line(Run(xbar: null)), Run()));   // one known, one not
    }

    /// <summary>A line kept by 0.8.1, keyed by offset, buffer and P-state only: it says nothing of the crossbar or driver it ran at.</summary>
    [Fact]
    public void A_line_from_before_the_driver_and_crossbar_were_kept_matches_nothing()
    {
        var old = (JsonObject)JsonNode.Parse("""{"at":"2026-10-30T12:00:00+08:00","offset_mhz":1000,"buffer_mib":20000,"pstate":"P0","gbps":1700.0}""")!;
        Assert.False(TuningTools.SameMemorySetup(old, Run()));
        Assert.False(TuningTools.SameMemorySetup(old, Run(xbar: null)));
    }

    [Theory]
    [InlineData("pass", false, false, false, false, true)]
    [InlineData("inconclusive", false, false, false, false, false)]   // cut short by the lab: the temperature limit, telemetry, a phase unfinished
    [InlineData("pass", false, false, true, false, false)]            // stopped hot by lab_test
    [InlineData("pass", false, false, false, true, false)]            // hung
    [InlineData("pass", false, true, false, false, false)]            // shared
    [InlineData("fail", true, false, false, false, false)]
    [InlineData("none", false, false, false, false, false)]
    public void Only_a_clean_pass_is_kept_as_the_reference(string verdict, bool failed, bool shared, bool hot, bool hung, bool kept) =>
        Assert.Equal(kept, TuningTools.KeepsMemoryRun(verdict, failed, shared, hot, hung));

    private static JsonElement Lab(string verdict, string? interrupted, params double[] tempMax)
    {
        var phases = tempMax.Select(t => new PhaseResult { Kind = LabPhase.Memory, TempMax = t }).ToList();
        var r = new LabResult("", "", 20000, 170, default, default, phases, verdict, interrupted != null ? ["the GPU reached 80 °C, the 80 °C limit"] : [],
            Interrupted: interrupted);
        return JsonDocument.Parse(JsonSerializer.Serialize(r, LabJson.Options)).RootElement.Clone();
    }

    /// <summary>The lab samples every 100 ms and lab_test every 500 ms, so at a maxTempC the lab usually stops first: that is
    /// a thermal stop, at the hottest its phases saw.</summary>
    [Fact]
    public void The_labs_own_temperature_stop_is_a_thermal_stop()
    {
        Assert.True(TuningTools.LabStoppedHot(Lab(LabResult.Inconclusive, LabResult.ByTemperature, 78, 80), out var at));
        Assert.Equal(80, at);
        Assert.Equal("at the maxTempC of 80 °C asked for", Nvgwls.HotWhy(at, 80));
    }

    [Fact]
    public void Another_interruption_or_none_is_not()
    {
        Assert.False(TuningTools.LabStoppedHot(Lab(LabResult.Inconclusive, LabResult.ByTelemetry, 70), out _));
        Assert.False(TuningTools.LabStoppedHot(Lab(LabResult.Pass, null, 70), out _));
        Assert.False(TuningTools.LabStoppedHot(null, out _));
    }
}
