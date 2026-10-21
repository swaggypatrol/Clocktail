using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class CapabilitiesTests
{
    [Fact]
    public void A_5090_like_driver_reports_volt_boost_and_pstates()
    {
        RailsFixture.SeedDriver();
        FakeDriver.Seed(Slot.ClientVoltRailsGetControl, b => b[4] = 25);
        FakeDriver.Seed(Slot.GetPstates20, b => { b[4] = 1; b[8] = 2; b[0xc] = 3; });
        var api = FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.ClientVoltRailsGetControl, Slot.GetPstates20],
            [Slot.VoltVoltRailsSetControl, Slot.ClientVoltRailsSetControl, Slot.SetPstates20]);
        var c = Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake"));
        Assert.True(c.VoltRead.Available, c.VoltRead.Reason);
        Assert.Contains("0x3", c.VoltRead.Reason);
        Assert.True(c.VoltWrite.Available);
        Assert.Equal(VoltBoostPath.Independent, c.Boost);
        Assert.True(c.Pstates.Available);
        Assert.Contains("2 P-states, 3 clocks", c.Pstates.Reason);
        Assert.True(c.PstatesWrite.Available);
        Assert.False(c.FanCoolers.Available);
        Assert.Contains("missing", c.FanCoolers.Reason);
        Assert.False(c.Presence.ContainsKey(Slot.RegisterOp));
    }

    [Fact]
    public void Without_the_independent_boost_the_legacy_byte_is_the_path()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus], [Slot.VoltVoltRailsSetControl]);
        var c = Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake"));
        Assert.Equal(VoltBoostPath.Legacy, c.Boost);
        Assert.False(c.Pstates.Available);
    }

    /// <summary>A01: the 5090's NVVDD is kind 3, so a boost write goes the legacy way even with the independent getter
    /// answering; the capability map said independent before (the write's own rule, VoltBoost, said legacy).</summary>
    [Fact]
    public void A_kind_3_nvvdd_takes_the_legacy_boost_path_as_the_write_does()
    {
        RailsFixture.SeedDriver();
        BinaryPrimitives.WriteUInt32LittleEndian(FakeDriver.Current(Slot.VoltVoltRailsGetInfo).AsSpan(Clocktail.Domain.Rails.RailsLayout.InfoBase), 3);
        FakeDriver.Seed(Slot.ClientVoltRailsGetControl, b => b[4] = 25);
        var api = FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.ClientVoltRailsGetControl],
            [Slot.VoltVoltRailsSetControl, Slot.ClientVoltRailsSetControl]);
        Assert.Equal(VoltBoostPath.Legacy, Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake")).Boost);
    }

    /// <summary>A01: the map joins what the driver has, what the write policy lets through and the tool that drives it.</summary>
    [Fact]
    public void The_control_map_says_which_tool_can_set_what_and_why_not()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.ClockClientClkVfPointsGetInfo,
             Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.PerfClientLimitsGetStatus],
            [Slot.VoltVoltRailsSetControl, Slot.ClockClientClkVfPointsSetControl, Slot.PerfClientLimitsSetStatus]);
        var map = Controls.Map(Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake")), WritePolicySet.Default);

        var curve = Assert.Single(map, c => c.Name == "V/F curve");
        Assert.True(curve.Can, curve.Why);
        Assert.Equal("adjust_curve, flatten_curve, set_curve, shift_curve", curve.Tool);
        Assert.Contains("for each write the tool makes", curve.Why);
        Assert.True(Assert.Single(map, c => c.Name == "MSVDD voltage").Can);

        var fans = Assert.Single(map, c => c.Name == "fixed fan speed");
        Assert.False(fans.Can);
        Assert.Contains("does not have it: missing", fans.Why);

        var clockLock = Assert.Single(map, c => c.Name == "clock lock");
        Assert.Equal("set_clock_lock", clockLock.Tool);
        Assert.True(clockLock.Can, clockLock.Why);
        Assert.Contains("for each write the tool makes", clockLock.Why);
        Assert.Equal("set_voltage_boost", Assert.Single(map, c => c.Name == "voltage boost").Tool);
        Assert.Equal("set_clock_ratio", Assert.Single(map, c => c.Name == "XBAR/core clock ratio").Tool);
        // adjust_xbar moves XBAR only: SYS and Video, NVVDD and F8 are the CLI's.
        Assert.Equal("adjust_xbar", Assert.Single(map, c => c.Name == "XBAR offset and voltage demand").Tool);
        Assert.Equal("set_core_offset", Assert.Single(map, c => c.Name == "core P0 offset").Tool);
        Assert.True(Assert.Single(map, c => c.Name == "NVVDD offsets") is { Tool: "adjust_nvvdd", Can: true });
        Assert.Equal("adjust_clock_domain", Assert.Single(map, c => c.Name == "SYS and Video offsets and voltage demands").Tool);
        Assert.Equal("set_power_cap", Assert.Single(map, c => c.Name == "additional power request (F8)").Tool);
    }

    [Fact]
    public void A_driver_without_nvvdd_has_no_voltage_path_at_all()
    {
        RailsFixture.SeedDriver();
        BinaryPrimitives.WriteUInt32LittleEndian(FakeDriver.Current(Slot.VoltVoltRailsGetInfo).AsSpan(4), 2);
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus], [Slot.VoltVoltRailsSetControl]);
        var c = Capabilities.Probe(new NvClient(api), new GpuSession(1, "fake"));
        Assert.False(c.VoltRead.Available);
        Assert.False(c.VoltWrite.Available);
        Assert.Equal(VoltBoostPath.None, c.Boost);
    }
}
