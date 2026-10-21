using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Hw;
using Clocktail.Mcp;
using Clocktail.Tests.Domain;

namespace Clocktail.Tests.Mcp;

/// <summary>A02: diagnostic_report's reads are GET only and say how the driver answered each one.</summary>
[Collection("fake-driver")]
public class DiagnosticReportTests
{
    [Fact]
    public void The_reads_call_no_setter_and_say_how_each_answered()
    {
        RailsFixture.SeedDriver();
        FakeDriver.Seed(Slot.ThermChannelGetStatus);
        // The temperature channels answer only for the channels asked for (+8): on the card, without it, -121.
        uint thermWord = Slots.Info(Slot.ThermChannelGetStatus).Shape!.Value.Word;
        uint? asked = null;
        FakeDriver.QueryResponder = q => { if (BitConverter.ToUInt32(q) == thermWord) asked = BitConverter.ToUInt32(q, 8); return null; };
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.ThermChannelGetStatus], [Slot.VoltVoltRailsSetControl]);
        var r = SettingsTools.Reads(Diagnostics.GetSweep(new NvClient(api), new GpuSession(1, "fake")));
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(4, r.Answered);
        Assert.Equal(0xffffu, asked);
        Assert.True(r.Items.Count > r.Answered);
        string json = JsonSerializer.Serialize(r.Items);
        Assert.Contains("\"slot\":\"VoltVoltRailsGetInfo\"", json);
        Assert.Contains("\"outcome\":\"Missing\"", json);
        Assert.DoesNotContain(@"\\Users\\", json);
    }
}
