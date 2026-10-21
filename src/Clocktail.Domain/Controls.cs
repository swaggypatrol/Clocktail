using Clocktail.Hw;

namespace Clocktail.Domain;

/// <summary>One thing that can be changed on the card, as the agent sees it: whether it can, which MCP tool does it, and why.</summary>
public sealed record Control(string Name, string? Tool, bool Can, string Why);

/// <summary>
/// The capability map the agent reads (card_settings "controls"). <see cref="Capabilities"/> says what this driver and
/// card expose, which only means the interface resolves or echoes; the write policy says whether Clocktail writes it at
/// all; the table says which MCP tool drives it. A control can be used only when all three agree.
/// </summary>
public static class Controls
{
    /// <param name="PerWrite">The setter is off by default and the tool's write path enables it for its own write.</param>
    /// <param name="NotOffered">Why no MCP tool sets it; null for the controls a tool drives.</param>
    private sealed record Entry(string Name, string? Tool, Func<Capabilities, Capability> Has, Slot? Setter, bool PerWrite = false, string? NotOffered = null);

    private static readonly Entry[] Table =
    [
        new("V/F curve", "adjust_curve, flatten_curve, set_curve, shift_curve", c => c.VfPoints, Slot.ClockClientClkVfPointsSetControl, PerWrite: true),
        new("power limit", "set_power_limit, set_daily_power_limit", c => c.PwrPolicies, Slot.ClientPwrPoliciesSetControl),
        new("additional power request (F8)", "set_power_cap", c => c.PwrPolicies, Slot.ClientPwrPoliciesSetControl),
        new("current limits", "set_current_limit", c => c.PowerPolicy, Slot.PowerPolicySetControl, PerWrite: true),
        new("memory offset", "set_memory_offset", c => c.PstatesWrite, Slot.SetPstates20),
        new("core P0 offset", "set_core_offset", c => c.PstatesWrite, Slot.SetPstates20),
        new("XBAR offset and voltage demand", "adjust_xbar", c => c.ClockDomains, Slot.ClockClkDomainsSetControl),
        new("SYS and Video offsets and voltage demands", "adjust_clock_domain", c => c.ClockDomains, Slot.ClockClkDomainsSetControl),
        new("core voltage demand", "adjust_clock_domain (core)", c => c.ClockDomains, Slot.ClockClkDomainsSetControl),
        new("MSVDD voltage", "adjust_msvdd", c => c.VoltWrite, Slot.VoltVoltRailsSetControl),
        new("NVVDD offsets", "adjust_nvvdd", c => c.VoltWrite, Slot.VoltVoltRailsSetControl),
        new("fan curve", "set_fan_curve", c => Present(c, "fan policy", Slot.FanPolicyGetInfo, Slot.FanPolicyGetControl, Slot.FanPolicySetControl), Slot.FanPolicySetControl),
        new("fixed fan speed", "set_fan_speed", c => c.FanCoolers, Slot.ClientFanCoolersSetControl),
        new("voltage boost", "set_voltage_boost", c => c.Boost == VoltBoostPath.None ? Capability.No("no boost path") : Capability.Yes($"the {c.Boost.ToString().ToLowerInvariant()} path"), null),
        new("clock lock", "set_clock_lock", c => c.BoostLock, Slot.PerfClientLimitsSetStatus, PerWrite: true),
        new("XBAR/core clock ratio", "set_clock_ratio", c => c.PropagationRatio, Slot.ClockClkPropTopRelsSetControl, PerWrite: true),
        new("temperature input", null, c => c.ThermalInput, Slot.ThermChannelSetControl, NotOffered: "it would freeze a temperature the firmware trusts"),
        new("domain V/F points", null, c => c.DomainVf, Slot.ClockClkVfPointsSetControl, NotOffered: "its rule is unknown"),
    ];

    public static IReadOnlyList<Control> Map(Capabilities caps, WritePolicySet policy) =>
        Table.Select(e => Describe(e, e.Has(caps), e.Setter is { } s ? policy.Get(s) : null)).ToList();

    private static Control Describe(Entry e, Capability has, WritePolicy? setter)
    {
        bool perWrite = e.PerWrite && setter is { State: PolicyState.Disabled, Complete: true };
        string? state = setter switch
        {
            null => null,
            { State: PolicyState.Enabled } => "Clocktail writes it",
            _ when perWrite => "Clocktail enables the setter for each write the tool makes",
            { State: PolicyState.Forbidden } => "the setter is forbidden",
            _ => $"the setter is off ({setter.Reason})",
        };
        string driver = has.Available ? $"the driver has it ({has.Reason})" : $"the driver does not ({has.Reason})";
        if (e.Tool == null) return new(e.Name, null, false, $"not offered: {e.NotOffered}; {driver}{(state != null ? $"; {state}" : "")}");
        if (!has.Available) return new(e.Name, e.Tool, false, $"this card or driver does not have it: {has.Reason}");
        bool can = setter is null or { State: PolicyState.Enabled } || perWrite;
        return new(e.Name, e.Tool, can, can ? $"{has.Reason}; {state}" : $"{has.Reason}, but {state}");
    }

    private static Capability Present(Capabilities c, string what, params Slot[] s) =>
        s.All(x => c.Presence.GetValueOrDefault(x)) ? Capability.Yes($"{what} interfaces resolve")
        : Capability.No("missing " + string.Join(", ", s.Where(x => !c.Presence.GetValueOrDefault(x))));
}
