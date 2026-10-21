using System.Text.Json;
using Clocktail.Domain.Power;
using Clocktail.Hw;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>T13: the driver's whole power-policy table, read in one CONTROL GET, with what each record limits, the limit
/// in force and its channel's reading.</summary>
[Collection("fake-driver")]
public class PolicyTableTests
{
    [Fact]
    public void Every_record_shows_its_limit_in_force_and_its_channels_reading()
    {
        FakeDriver.Reset();
        CurrentLimitsFixture.Install();
        var hw = new NvClient(FakeDriver.ApiFor([Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo], []));
        var gpu = new GpuSession(1, "fake");
        var limits = CurrentLimitsReader.Read(hw, gpu);
        int sets = FakeDriver.Sets;
        var control = PolicyTable.Control(hw, gpu, limits.Records);
        Assert.Equal(sets, FakeDriver.Sets);   // a GET
        // Full load at the 600 W wall on the reference card: total board 599.6 W, input current 49.6 A, NVVDD 470 A.
        MonitorReading[] readings = [new(0, 0xf5, 599_600, -1, 0), new(9, 0xa9, 0, 49_600, 0), new(13, 1, 430_000, 470_000, 915_000)];
        var rows = PolicyTable.Rows(limits, control, readings);

        Assert.Equal(16, rows.Count);   // record 16 is empty
        var wall = rows.Single(r => r.Record.Index == 8);
        Assert.Equal(("power limit", "channel 0, total board", 575_000u), (wall.What, wall.Watches, wall.Limit!.Value));
        Assert.Equal(599_600, wall.Now);
        var input = rows.Single(r => r.Record.Index == 9);
        Assert.Equal(("input current limit", true, 60_000u, 82.7), (input.What, input.Milliamps, input.Limit!.Value, input.UsePct!.Value));
        Assert.Equal("current limit", rows.Single(r => r.Record.Index == 13).What);
        Assert.Equal("class 0x17, not identified", rows.Single(r => r.Record.Index == 7).What);
        Assert.Equal("class 0x13, not identified", rows.Single(r => r.Record.Index == 5).What);   // the current-limit class in mW
        Assert.Null(rows.Single(r => r.Record.Index == 3).Now);   // no reading for its channel

        string json = JsonSerializer.Serialize(SettingsTools.Policies(rows));
        Assert.Contains("\"record\":9,\"what\":\"input current limit\",\"watches\":\"channel 9, board input current\",\"unit\":\"A\"", json);
        Assert.Contains("\"limit\":60,\"now\":49.6,\"use_pct\":82.7", json);
    }

    /// <summary>The board power policy is the one class 0x18 record in mW with the request's own range (record 8 on the
    /// 5090), not the 50 kW one beside it.</summary>
    [Fact]
    public void The_board_wall_is_the_power_limit_record_with_the_requests_range()
    {
        FakeDriver.Reset();
        CurrentLimitsFixture.Install();
        var hw = new NvClient(FakeDriver.ApiFor([Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo], []));
        var gpu = new GpuSession(1, "fake");
        var board = new BoardPowerInfo(0, 1, 400_000, 575_000, 600_000);
        Assert.Equal(8, BoardWall.Record(CurrentLimitsReader.Read(hw, gpu).Records, board)!.Index);
        Assert.Equal(575_000u, BoardWall.Enforced(hw, gpu, board));
        Assert.Null(BoardWall.Record(CurrentLimitsReader.Read(hw, gpu).Records, board with { MinMw = 1 }));
    }

    [Fact]
    public void A_record_the_driver_did_not_answer_for_has_no_limit()
    {
        FakeDriver.Reset();
        CurrentLimitsFixture.Install();
        var hw = new NvClient(FakeDriver.ApiFor([Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo], []));
        var limits = CurrentLimitsReader.Read(hw, new GpuSession(1, "fake"));
        var control = NvClient.NewBuffer(Slot.PowerPolicyGetControl);   // nothing answered
        Assert.All(PolicyTable.Rows(limits, control, []), r => Assert.Null(r.Limit));
    }
}
