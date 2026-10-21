using Clocktail.Domain;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class DiagnosticsTests
{
    [Fact]
    public void The_get_sweep_never_touches_setters_and_reports_each_read()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus], [Slot.VoltVoltRailsSetControl]);
        var probes = Diagnostics.GetSweep(new NvClient(api), new GpuSession(1, "fake"));
        Assert.DoesNotContain(probes, p => Slots.Info(p.Slot).IsWrite);
        Assert.DoesNotContain(probes, p => p.Slot == Slot.RegisterOp);
        Assert.Equal(0, FakeDriver.Sets);
        var control = probes.Single(p => p.Slot == Slot.VoltVoltRailsGetControl);
        Assert.Equal(NvOutcome.Ok, control.Outcome);
        Assert.True(control.VersionEchoed);
        Assert.True(control.NonZeroDwords >= 2);   // mask and the MSVDD offset
        Assert.Equal(NvOutcome.Missing, probes.Single(p => p.Slot == Slot.ClockClkDomainsGetInfo).Outcome);
        Assert.Equal(NvOutcome.Missing, probes.Single(p => p.Slot == Slot.ClockClkPropTopRelsGetControl).Outcome);   // swept in the original's buffer
    }

    [Fact]
    public void Size_probing_finds_the_size_the_driver_accepts()
    {
        FakeDriver.Reset();
        FakeDriver.State[new Versioned(0x30, 1).Word] = new byte[0x30];
        FakeDriver.State[new Versioned(0x44, 2).Word] = new byte[0x44];
        var api = FakeDriver.ApiFor([Slot.ClientFanCoolersGetInfo]);
        var probes = Diagnostics.ProbeSize(new NvClient(api), new GpuSession(1, "fake"), Slot.ClientFanCoolersGetInfo, maxSize: 0x60, minRev: 1, maxRev: 2);
        Assert.Equal([0x30], probes.Single(p => p.Rev == 1).AcceptedSizes);
        Assert.Equal([0x44], probes.Single(p => p.Rev == 2).AcceptedSizes);
        Assert.All(probes, p => Assert.Null(p.GuardBroken));
    }

    [Fact]
    public void Size_probing_is_refused_for_setters()
    {
        var hw = new NvClient(FakeDriver.ApiFor([]));
        Assert.Throws<ArgumentException>(() => Diagnostics.ProbeSize(hw, new GpuSession(1, "fake"), Slot.VoltVoltRailsSetControl));
        Assert.Equal(NvOutcome.Rejected, hw.CallRaw(new GpuSession(1, "fake"), Slot.VoltVoltRailsSetControl, new byte[8]).Outcome);
    }
}
