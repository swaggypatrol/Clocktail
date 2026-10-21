namespace Clocktail.Hw;

/// <summary>
/// The static slot table: ID, access, structure shape and evidence for every interface.
/// Mirrored in the embedded nvapi_slots.csv; a test keeps the two in sync.
/// Sizes are recorded as (size, rev) pairs because the version word cannot be parsed back (see <see cref="Versioned"/>).
/// </summary>
public static class Slots
{
    private static readonly SlotInfo[] Table = Build();

    public static SlotInfo Info(Slot slot)
    {
        int i = (int)slot;
        if ((uint)i >= (uint)Table.Length || Table[i] is null) throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown slot.");
        return Table[i];
    }

    public static IReadOnlyList<SlotInfo> All => Table;
    public static IEnumerable<SlotInfo> Setters => Table.Where(s => s.IsWrite);

    private static SlotInfo[] Build()
    {
        var t = new SlotInfo[78];
        void R(Slot s, uint id, string dom, Versioned? shape, Evidence ev, string notes = "", Signature sig = Signature.GpuStruct, int? buffer = null)
            => t[(int)s] = new SlotInfo(s, id, dom, SlotAccess.Read, shape, sig, ev, notes, buffer);
        void W(Slot s, uint id, string dom, Versioned? shape, Evidence ev, string notes = "", Signature sig = Signature.GpuStruct, int? buffer = null)
            => t[(int)s] = new SlotInfo(s, id, dom, SlotAccess.Write, shape, sig, ev, notes, buffer);
        static Versioned V(int size, int rev) => new(size, rev);

        // Voltage rails. Entries: info by rail bit (0xc0 stride @0x4c), control/status by popcount order.
        R(Slot.VoltVoltRailsGetInfo, 0x2c73afdc, "rails", V(0x184c, 2), Evidence.High, "+4 rail mask; verified on RTX 5090");
        R(Slot.VoltVoltRailsGetStatus, 0x5d0634ee, "rails", V(0x1620, 2), Evidence.High, "+4 must echo the info mask");
        R(Slot.VoltVoltRailsGetControl, 0xa3070db0, "rails", V(0xac8, 2), Evidence.High);
        W(Slot.VoltVoltRailsSetControl, 0x87c55c8a, "rails", V(0xac8, 2), Evidence.High, "write kind 1");

        R(Slot.ClockAdcDevicesGetInfo, 0x68789e2a, "adc", V(0x9f0, 2), Evidence.Medium);
        R(Slot.ClockAdcDevicesGetStatus, 0x43d9b26a, "adc", V(0x9c8, 1), Evidence.High, "+4 copied from info +4");

        // P-state 2.0 clock offsets: only P0, domain 0 = core, 4 = memory, offsets in kHz.
        R(Slot.GetPstates20, 0x6ff81213, "pstates", V(0x1cf8, 2), Evidence.High);
        W(Slot.SetPstates20, 0x0f4dae6b, "pstates", V(0x1cf8, 2), Evidence.High, "write kinds 2 (core) and 3 (memory)");

        // Power limit as a percentage (PCM, 100% = 100000).
        R(Slot.ClientPowerPoliciesGetInfo, 0x34206d86, "power", V(0xb8, 1), Evidence.Medium);
        R(Slot.ClientPowerPoliciesGetStatus, 0x70916171, "power", V(0x48, 1), Evidence.Medium);
        W(Slot.ClientPowerPoliciesSetStatus, 0xad95f5ed, "power", V(0x48, 1), Evidence.High, "write kind 4");

        // Core V/F points.
        R(Slot.ClockClientClkVfPointsGetInfo, 0x507b4b59, "vf", V(0x182c, 1), Evidence.High);
        R(Slot.ClockClientClkVfPointsGetStatus, 0x21537ad4, "vf", V(0x15b0c, 2), Evidence.High, "size overflows the version word (0x35b0c)");
        R(Slot.ClockClientClkVfPointsGetControl, 0x23f1b133, "vf", V(0x2420, 1), Evidence.High);
        W(Slot.ClockClientClkVfPointsSetControl, 0x0733e009, "vf", V(0x2420, 1), Evidence.High, "write kind 7");

        R(Slot.PowerMonitorGetInfo, 0xc12eb19e, "power-monitor", V(0xca8, 3), Evidence.High, "live: rev3 accepts only 0xca8; +0x10 channel mask, per channel provider/type at 0x34/0x38 + i*0x3c");
        R(Slot.PowerMonitorGetStatus, 0xf40238ef, "power-monitor", V(0x59c, 1), Evidence.High);
        R(Slot.GetCurrentPstate, 0x927da4f6, "pstates", null, Evidence.High, "public API", Signature.GpuScalar);
        R(Slot.ClientPowerTopologyGetStatus, 0xedcf624e, "power", null, Evidence.Medium, "board power in W");
        R(Slot.GetPerfDecreaseInfo, 0x7f7f4600, "perf", null, Evidence.High, "public API", Signature.GpuScalar);
        R(Slot.ClockClientClkDomainsGetInfo, 0x64b43a6a, "clocks", null, Evidence.Medium, "offset ranges; size unknown");
        R(Slot.GetAllClocks, 0x1bd69f49, "clocks", null, Evidence.High, "public API");

        // Clock domain topology and XBAR/SYS/Video offsets (+vdemand).
        R(Slot.ClockClkDomainsGetInfo, 0x57b5a5df, "clocks", V(0x86ac, 4), Evidence.High);
        R(Slot.ClockClkDomainsGetControl, 0xf58938f5, "clocks", V(0x61a4, 2), Evidence.High, "+8 domain mask");
        W(Slot.ClockClkDomainsSetControl, 0xd14b69cf, "clocks", V(0x61a4, 2), Evidence.High, "write kinds 6/12/13/14, one domain per write");
        R(Slot.SingleDomainFrequencyRead, 0x527fc458, "clocks", V(0xc, 1), Evidence.Medium, "name unknown; domain ids 0x14,0x1f,6,3,0x16", Signature.GpuDomainSentinel);

        // Boost lock / point voltage lock / clock range share one 0x30c record.
        R(Slot.PerfClientLimitsGetStatus, 0xe440b867, "perf-limits", V(0x30c, 2), Evidence.High);
        W(Slot.PerfClientLimitsSetStatus, 0x39442cfb, "perf-limits", V(0x30c, 2), Evidence.High, "write kind 8; exact 7-domain layout required");

        // The original's call form [observed]: the version word says the structure (0x5798 r1, 0x75c r1) and the buffer is
        // larger (0x8000, 0x2000), the guard after it; the driver writes past 0x75c (live 617.42, a 0x75c buffer's guard
        // caught it). Control +4 = info +8 (the relation count), +0x68 the ratio, U16.16. Set: gateway kind 9 [observed].
        R(Slot.ClockClkPropTopRelsGetInfo, 0xe826e4f0, "clock-prop", V(0x5798, 1), Evidence.Medium, "+8 = relation count; 0x8000 buffer", buffer: 0x8000);
        R(Slot.ClockClkPropTopRelsGetControl, 0xcbff71d0, "clock-prop", V(0x75c, 1), Evidence.Medium, "+4 = info's relation count; +0x68 U16.16 ratio; 0x2000 buffer", buffer: 0x2000);
        W(Slot.ClockClkPropTopRelsSetControl, 0xef3d20ea, "clock-prop", V(0x75c, 1), Evidence.Medium, "kind 9; the control block as read, +0x68 changed; 0x2000 buffer", buffer: 0x2000);

        // Fans. Sizes from the live size probe on 617.42: 0x62c/0x6a8. 0x928/0x14ac are not the coolers' sizes but the fan
        // policies' Info and Control (slots 58/60, below).
        R(Slot.ClientFanCoolersGetInfo, 0xfb85b01e, "fan", V(0x62c, 1), Evidence.High, "live size probe: rev1 accepts only 0x62c");
        R(Slot.ClientFanCoolersGetStatus, 0x35aed5e8, "fan", V(0x6a8, 1), Evidence.High, "live size probe: rev1 accepts only 0x6a8, no rev2");
        R(Slot.ClientFanCoolersGetControl, 0x814b209f, "fan", V(0x5ac, 1), Evidence.Medium, "0x2c header + 32 x 0x2c entries (the setter checks ver 0x105ac); same pattern as Info 0x2c + 32 x 0x30 and Status 0x28 + 32 x 0x34, both live-confirmed");
        W(Slot.ClientFanCoolersSetControl, 0xa58971a5, "fan", V(0x5ac, 1), Evidence.Medium, "write kind 16 checks ver 0x105ac");

        // Raw power policy table (current limits, board input current) in its layout 1: INFO 0xa70 r4 and CONTROL 0xa4c
        // r1, both read live on 617.42 (2026-10-19) and decoded (Domain/Power/CurrentLimits.cs). The F8/FE watt
        // requests live in slots 46-48, not here.
        R(Slot.PowerPolicyGetInfo, 0x67f31384, "power-policy", V(0xa70, 4), Evidence.High, "layout 1; live: rev1 also accepts 0x628, 0x828, 0xa3c (not decoded)");
        R(Slot.PowerPolicyGetControl, 0x8b3e7343, "power-policy", V(0xa4c, 1), Evidence.High, "layout 1; a query: the mask at +4 picks the records");
        W(Slot.PowerPolicySetControl, 0xaffc2279, "power-policy", V(0xa4c, 1), Evidence.High, "kind 0x13 (current limit); kind 0x1b (board power record) not written");

        R(Slot.ThermChannelGetStatus, 0x65fe3aad, "thermal", V(0x34c8, 3), Evidence.High, "+8 = 0xffff; +0xd4 core, +0x160 memory, 1/256 degC (probed 2026-10-21)");
        t[(int)Slot.RegisterOp] = new SlotInfo(Slot.RegisterOp, 0x2eb3c140, "registers", SlotAccess.Forbidden, null, Signature.GpuStruct, Evidence.Low,
            "raw register access. Never implemented here.");
        R(Slot.GetMemoryInfoEx, 0xc0599498, "memory", null, Evidence.Low);
        R(Slot.ClockPmumonClkDomainsGetSamples, 0x7c955ac0, "clocks", null, Evidence.Low);
        // Live 617.42 (2026-10-21): the info's 242 limit names at 0x290 + i x 0x90; the status's arbitration as {kHz, limit}
        // pairs per clock domain (PerfLimits). The status is 2 MB, nearly all zero.
        R(Slot.PerfPerfLimitsGetInfo, 0x4d2c0a9c, "perf-limits", V(0x12308, 2), Evidence.Medium, "+8 limit mask; names at 0x290 + i*0x90");
        R(Slot.PerfPerfLimitsGetStatus, 0x0b62b9e2, "perf-limits", V(0x207d90, 2), Evidence.Medium, "ceilings at 0x2010, floors at 0x1ae8, {kHz, limit} per domain");
        R(Slot.GetAllClockFrequencies, 0xdcb616c3, "clocks", null, Evidence.Medium, "public API");
        R(Slot.PowerPolicyGetStatus, 0x31b7a4cd, "power-policy", null, Evidence.Low, "0xca8 r3 is a candidate only");
        R(Slot.ClientPwrPoliciesGetInfo, 0x54924bf5, "power-watts", V(0x728, 1), Evidence.High, "live size probe: rev1 accepts only 0x728; 0x28 header + 32 x 0x38 board entries {flag, 0, min, default, max mW}; the F8/FE watt family is here");
        R(Slot.ClientPwrPoliciesGetControl, 0x33ab0353, "power-watts", V(0x528, 1), Evidence.High, "0x528 r1; +4 one board bit, command 0xfe/0xf8 at 0x28+board*0x28+4; live: only r1 0x528 accepted, echo verified");
        W(Slot.ClientPwrPoliciesSetControl, 0x17695269, "power-watts", V(0x528, 1), Evidence.High, "kinds 0x14 (F8) / 0x15 (FE), both through slot 48; same 0x528 r1 shape as GetControl; on since its live drill (2026-10-17)");

        // Thermal input override: dangerous (freezes a temperature the firmware trusts).
        R(Slot.ThermChannelGetControl, 0xa933ce98, "thermal-input", V(0x28cc, 3), Evidence.High, "+8 = 0xffff; channels at 0x4c stride 0x6c");
        W(Slot.ThermChannelSetControl, 0x8df19fa2, "thermal-input", V(0x28cc, 3), Evidence.High, "write kind 0x16; danger mode");

        // Independent voltage boost percentage.
        R(Slot.ClientVoltRailsGetControl, 0x9df23ca1, "boost", V(0x28, 1), Evidence.High, "+4 = percent");
        W(Slot.ClientVoltRailsSetControl, 0xb9306d9b, "boost", V(0x28, 1), Evidence.High, "write kind 0x17");

        // Per-domain V/F curves (large structures; sizes overflow the version word).
        R(Slot.ClockClkVfPointsGetInfo, 0x8895b510, "domain-vf", V(0x68604, 1), Evidence.Medium, "word 0x78604");
        R(Slot.ClockClkVfPointsGetControl, 0xda025c3e, "domain-vf", V(0x424604, 5), Evidence.Medium, "word 0x474604");
        R(Slot.ClockClkVfPointsGetStatus, 0x7fee9032, "domain-vf", null, Evidence.Medium, "size 0x1e8604, rev unconfirmed");
        R(Slot.ClockClkVfRelsGetInfo, 0x5a769461, "domain-vf", null, Evidence.Medium, "size 0x12ee28, rev unconfirmed");
        W(Slot.ClockClkVfPointsSetControl, 0xfec00d04, "domain-vf", V(0x424604, 5), Evidence.High, "write kind 0");

        // Firmware fan policies: the firmware's own fan curves (Domain/Fan/FanPolicy.cs). Sizes from
        // the live size probe on 617.42 (2026-10-17): Info 0x28 + 32 x 0x48, Status 0x28 + 32 x 0x44, Control 0x2c + 32 x 0xa4
        // (r1 0xeac also answers: the same entries without their last 0x30 bytes). Status and Control are queries.
        R(Slot.FanPolicyGetInfo, 0x76a38d54, "fan-policy", V(0x928, 1), Evidence.High, "live: +4 policy mask; entries 0x28 + i*0x48: +0 type, +8 link, +0x2f editable");
        R(Slot.FanPolicyGetStatus, 0x15b85505, "fan-policy", V(0x8a8, 1), Evidence.High, "live; a query: +4 names the policies filled in (a zero mask reads zeros)");
        R(Slot.FanPolicyGetControl, 0x0fe87b7f, "fan-policy", V(0x14ac, 2), Evidence.High, "live; a query like Status (one bit reads one policy); 3 points {Q8.8 degC, 16.16 duty, rpm} at entry +0x3c");
        W(Slot.FanPolicySetControl, 0x2b2a2a45, "fan-policy", V(0x14ac, 2), Evidence.High, "write kind 0x18; the mask names the policies written");

        R(Slot.ThermalPolicyGetInfo, 0x2f69f8e5, "thermal-policy", V(0x3d74, 4), Evidence.Medium);
        R(Slot.ThermalPolicyGetControl, 0xc4554575, "thermal-policy", V(0x3fc, 3), Evidence.Medium);
        W(Slot.ThermalPolicySetControl, 0xe097144f, "thermal-policy", V(0x3fc, 3), Evidence.Medium, "write kind 0x19; off by default");

        R(Slot.GetThermalSettings, 0xe3640a56, "thermal", null, Evidence.High, "public API (gpu, sensor, buf)", Signature.GpuIndexStruct);
        R(Slot.GetDynamicPstatesInfoEx, 0x60ded2ed, "utilization", null, Evidence.Medium, "public API");
        R(Slot.PerfVfeVarGetInfo, 0xb9da41d6, "vfe", null, Evidence.Low);
        R(Slot.PerfVfeVarGetControl, 0x5d387298, "vfe", null, Evidence.Low);
        R(Slot.GetVbiosImage, 0xfc13ee11, "vbios", null, Evidence.High);
        R(Slot.ManufacturingInfo, 0xa4218928, "manufacturing", null, Evidence.Low);

        // Legacy cooler interface, used only when the modern one is missing.
        R(Slot.GetCoolerSettings, 0xda141340, "fan-legacy", null, Evidence.High, "(gpu, cooler index, buf)", Signature.GpuIndexStruct);
        W(Slot.SetCoolerLevels, 0x891fa0ae, "fan-legacy", V(0x1c, 1), Evidence.High, "3 x {level, policy}", Signature.GpuIndexStruct);
        R(Slot.GetTachReading, 0x5f608315, "fan", null, Evidence.High, "rpm", Signature.GpuScalar);
        R(Slot.ClockClkProgsGetInfo, 0xfaceb39b, "clocks", null, Evidence.Low);
        R(Slot.GetRamType, 0x57f7caac, "memory", null, Evidence.Low, "call shape differs from the public GetRamType; purpose unconfirmed");

        // Plain-ID structured calls.
        R(Slot.GetArchInfo, 0xd8265d24, "identity", V(0x10, 2), Evidence.High, "arch / implementation / revision");
        R(Slot.VoltVoltDevicesGetInfo, 0xa38acf9d, "rails", V(0xf48, 1), Evidence.High, "32 x 0x78 voltage domains: +8 min, +0xc max, +0x10 step (uV)");

        return t;
    }
}
