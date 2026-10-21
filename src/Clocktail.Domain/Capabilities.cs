using System.Buffers.Binary;
using Clocktail.Domain.Boost;
using Clocktail.Hw;

namespace Clocktail.Domain;

public sealed record Capability(bool Available, string Reason)
{
    public static Capability Yes(string how) => new(true, how);
    public static Capability No(string why) => new(false, why);
}

public enum VoltBoostPath { None, Independent, Legacy }

/// <summary>
/// What this driver and GPU let the program do, as data (does the function resolve, does the structure echo its
/// version and mask), as one object the CLI and UI read.
/// </summary>
public sealed record Capabilities(
    Capability VoltRead,
    Capability VoltWrite,
    VoltBoostPath Boost,
    Capability Pstates,
    Capability PstatesWrite,
    Capability ClockDomains,
    Capability PropagationRatio,
    Capability BoostLock,
    Capability PowerPercent,
    Capability PowerPolicy,
    Capability PwrPolicies,
    Capability ThermalInput,
    Capability FanCoolers,
    Capability VfPoints,
    Capability DomainVf,
    IReadOnlyDictionary<Slot, bool> Presence)
{
    public static Capabilities Probe(NvClient hw, GpuSession gpu)
    {
        var p = hw.Presence();
        bool Has(params Slot[] s) => s.All(x => p.GetValueOrDefault(x));
        string Missing(params Slot[] s) => "missing " + string.Join(", ", s.Where(x => !p.GetValueOrDefault(x)));
        Capability Present(string what, params Slot[] s) => Has(s) ? Capability.Yes($"{what} interfaces resolve") : Capability.No(Missing(s));

        // Volt: the structures must echo version and mask with NVVDD present.
        Capability voltRead;
        if (!Has(Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltRailsGetControl))
            voltRead = Capability.No(Missing(Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltRailsGetControl));
        else
        {
            var info = NvClient.NewBuffer(Slot.VoltVoltRailsGetInfo);
            var r = hw.Call(gpu, Slot.VoltVoltRailsGetInfo, info);
            uint mask = r.IsOk ? BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4)) : 0;
            voltRead = !r.IsOk ? Capability.No($"info read failed: {r}")
                : (mask & 1) == 0 ? Capability.No("the driver did not expose NVVDD")
                : Capability.Yes($"rail mask 0x{mask:x}");
        }
        var voltWrite = !voltRead.Available ? Capability.No("no voltage read") : Present("set", Slot.VoltVoltRailsSetControl);

        // The path a boost write takes, by the rule the write itself uses (BoostObservation.DefaultRule): independent only
        // when its getter answers and NVVDD is not kind 3. The 5090's rails are kind 3, so it is legacy there.
        VoltBoostPath boost = VoltBoostPath.None;
        if (voltRead.Available)
            try
            {
                var rule = BoostReader.Observe(hw, gpu).DefaultRule(voltWrite.Available);
                boost = rule == BoostPath.Independent && Has(Slot.ClientVoltRailsSetControl) ? VoltBoostPath.Independent
                    : voltWrite.Available ? VoltBoostPath.Legacy : VoltBoostPath.None;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }

        Capability pstates;
        if (!Has(Slot.GetPstates20)) pstates = Capability.No(Missing(Slot.GetPstates20));
        else
        {
            var b = NvClient.NewBuffer(Slot.GetPstates20);
            var r = hw.Call(gpu, Slot.GetPstates20, b);
            pstates = !r.IsOk ? Capability.No($"read failed: {r}")
                : (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) & 1) == 0 ? Capability.No("table reported as not editable")
                : Capability.Yes($"{BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8))} P-states, {BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0xc))} clocks each");
        }

        return new Capabilities(
            voltRead, voltWrite, boost,
            pstates,
            pstates.Available ? Present("set", Slot.SetPstates20) : Capability.No("no P-state read"),
            Present("clock domain", Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl, Slot.ClockClkDomainsSetControl),
            Present("propagation", Slot.ClockClkPropTopRelsGetInfo, Slot.ClockClkPropTopRelsGetControl, Slot.ClockClkPropTopRelsSetControl),
            Present("perf limits", Slot.PerfClientLimitsGetStatus, Slot.PerfClientLimitsSetStatus),
            Present("power percent", Slot.ClientPowerPoliciesGetInfo, Slot.ClientPowerPoliciesGetStatus, Slot.ClientPowerPoliciesSetStatus),
            Present("power policy", Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerPolicySetControl),
            Present("power requests", Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.ClientPwrPoliciesSetControl),
            Present("thermal input", Slot.ThermChannelGetControl, Slot.ThermChannelSetControl),
            Present("fan cooler", Slot.ClientFanCoolersGetInfo, Slot.ClientFanCoolersGetStatus, Slot.ClientFanCoolersGetControl, Slot.ClientFanCoolersSetControl),
            Present("V/F point", Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.ClockClientClkVfPointsSetControl),
            Present("domain V/F", Slot.ClockClkVfPointsGetInfo, Slot.ClockClkVfPointsGetControl, Slot.ClockClkVfPointsGetStatus, Slot.ClockClkVfPointsSetControl),
            p);
    }
}
