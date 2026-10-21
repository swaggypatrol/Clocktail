namespace Clocktail.Hw;

/// <summary>
/// Every private NVAPI interface this program knows, numbered 0..75. Entries past 75 are structured calls resolved
/// by plain ID; they get the same treatment here.
/// Names come from public ID tables, except <see cref="SingleDomainFrequencyRead"/> whose name is unknown.
/// </summary>
public enum Slot
{
    VoltVoltRailsGetInfo = 0,
    VoltVoltRailsGetStatus = 1,
    VoltVoltRailsGetControl = 2,
    VoltVoltRailsSetControl = 3,
    ClockAdcDevicesGetInfo = 4,
    ClockAdcDevicesGetStatus = 5,
    GetPstates20 = 6,
    SetPstates20 = 7,
    ClientPowerPoliciesGetInfo = 8,
    ClientPowerPoliciesGetStatus = 9,
    ClientPowerPoliciesSetStatus = 10,
    ClockClientClkVfPointsGetInfo = 11,
    ClockClientClkVfPointsGetStatus = 12,
    ClockClientClkVfPointsGetControl = 13,
    ClockClientClkVfPointsSetControl = 14,
    PowerMonitorGetInfo = 15,
    PowerMonitorGetStatus = 16,
    GetCurrentPstate = 17,
    ClientPowerTopologyGetStatus = 18,
    GetPerfDecreaseInfo = 19,
    ClockClientClkDomainsGetInfo = 20,
    GetAllClocks = 21,
    ClockClkDomainsGetInfo = 22,
    ClockClkDomainsGetControl = 23,
    ClockClkDomainsSetControl = 24,
    SingleDomainFrequencyRead = 25,
    PerfClientLimitsGetStatus = 26,
    PerfClientLimitsSetStatus = 27,
    ClockClkPropTopRelsGetInfo = 28,
    ClockClkPropTopRelsGetControl = 29,
    ClockClkPropTopRelsSetControl = 30,
    ClientFanCoolersGetInfo = 31,
    ClientFanCoolersGetStatus = 32,
    ClientFanCoolersGetControl = 33,
    ClientFanCoolersSetControl = 34,
    PowerPolicyGetInfo = 35,
    PowerPolicyGetControl = 36,
    PowerPolicySetControl = 37,
    ThermChannelGetStatus = 38,
    RegisterOp = 39,
    GetMemoryInfoEx = 40,
    ClockPmumonClkDomainsGetSamples = 41,
    PerfPerfLimitsGetInfo = 42,
    PerfPerfLimitsGetStatus = 43,
    GetAllClockFrequencies = 44,
    PowerPolicyGetStatus = 45,
    ClientPwrPoliciesGetInfo = 46,
    ClientPwrPoliciesGetControl = 47,
    ClientPwrPoliciesSetControl = 48,
    ThermChannelGetControl = 49,
    ThermChannelSetControl = 50,
    ClientVoltRailsGetControl = 51,
    ClientVoltRailsSetControl = 52,
    ClockClkVfPointsGetInfo = 53,
    ClockClkVfPointsGetControl = 54,
    ClockClkVfPointsGetStatus = 55,
    ClockClkVfRelsGetInfo = 56,
    ClockClkVfPointsSetControl = 57,
    FanPolicyGetInfo = 58,
    FanPolicyGetStatus = 59,
    FanPolicyGetControl = 60,
    FanPolicySetControl = 61,
    ThermalPolicyGetInfo = 62,
    ThermalPolicyGetControl = 63,
    ThermalPolicySetControl = 64,
    GetThermalSettings = 65,
    GetDynamicPstatesInfoEx = 66,
    PerfVfeVarGetInfo = 67,
    PerfVfeVarGetControl = 68,
    GetVbiosImage = 69,
    ManufacturingInfo = 70,
    GetCoolerSettings = 71,
    SetCoolerLevels = 72,
    GetTachReading = 73,
    ClockClkProgsGetInfo = 74,
    GetRamType = 75,

    // Structured calls resolved by plain ID.
    GetArchInfo = 76,
    VoltVoltDevicesGetInfo = 77,
}

public enum SlotAccess
{
    Read,
    Write,
    /// <summary>Never called by this program. Raw register access has no safe use here.</summary>
    Forbidden,
}

/// <summary>How the function pointer is invoked.</summary>
public enum Signature
{
    /// <summary><c>fn(gpu, struct*)</c>, structure surrounded by 0xCD guard bytes.</summary>
    GpuStruct,
    /// <summary><c>fn(gpu, index, struct*)</c>, same guards. Legacy cooler and thermal-settings calls.</summary>
    GpuIndexStruct,
    /// <summary><c>fn(gpu, uint*)</c>, a single value with no version word.</summary>
    GpuScalar,
    /// <summary><c>fn(gpu, {ver, domain, out}*)</c> inside a 0xA5-filled 0x100c buffer; the tail must stay 0xA5.</summary>
    GpuDomainSentinel,
}

/// <summary>How well the evidence pins this entry down (nvapi_slots.csv's confidence column).</summary>
public enum Evidence { High, Medium, Low }

public sealed record SlotInfo(
    Slot Slot,
    uint Id,
    string Domain,
    SlotAccess Access,
    /// <summary>Structure size and revision when known. Null means the size must be probed (see P1) before use.</summary>
    Versioned? Shape,
    Signature Signature,
    Evidence Evidence,
    string Notes,
    /// <summary>The buffer a call hands the driver, when larger than <see cref="Shape"/>'s size: the version word says the
    /// structure's size and the driver writes past it (the clock propagation family; the original gave 0x2000 / 0x8000).
    /// Null: the buffer is the structure.</summary>
    int? Buffer = null)
{
    public string Name => Slot.ToString();
    public bool IsWrite => Access == SlotAccess.Write;
    /// <summary>True for entries resolved by plain ID rather than through the slot table.</summary>
    public bool PlainId => (int)Slot > 75;
}
