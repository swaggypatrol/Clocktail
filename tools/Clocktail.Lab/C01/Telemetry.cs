namespace Bwt.Contracts;

/// <summary>Independent NVAPI point-status reference captured beside a sensor sample. Frequencies are never fitted
/// from NVML observations; ControlSha256 must be computed from the full NVAPI control state by the reader.</summary>
public sealed record VfStatusReference(long StartNs, long EndNs, string Source, string ControlSha256, IReadOnlyList<VfNode> Nodes)
{
    public const string SourceName = "clocktail:nvapi-vf-status-control-v1";
}

/// <summary>
/// One telemetry sweep (spec §10.1). Each field is what its API returned; a missing reading is null, never 0. Clock
/// fields are named by their real domain: <see cref="GraphicsClockKhz"/> is NVML_CLOCK_GRAPHICS, <see cref="SmClockKhz"/>
/// is NVML_CLOCK_SM. (Clocktail's LabSample.SmMhz actually read the graphics clock: that trap is not repeated here.)
/// <para>
/// Timing: <see cref="MonoNs"/> is when the NVML sweep started, <see cref="QueryNs"/> how long it took. Voltage comes from
/// a different API at a different moment, so it carries its own <see cref="VoltageMonoNs"/>; joint V/F samples are formed
/// later only when |VoltageMonoNs − MonoNs| is within the manifest's pairing skew. Neither NVML nor the NVAPI rail status
/// returns a sensor timestamp, so <see cref="SensorTimestampNs"/> is null and sample age is unknown (evidence downgraded).
/// </para>
/// </summary>
public sealed record TelemetrySample(
    long Seq,
    long MonoNs,
    DateTimeOffset Utc,
    long QueryNs,
    long? GraphicsClockKhz,
    long? SmClockKhz,
    long? MemoryClockKhz,
    long? VideoClockKhz,
    long? NvvddUv,
    long? VoltageMonoNs,
    string? VoltageSource,
    int? Pstate,
    int? GpuTempC,
    long? PowerMw,
    int? FanPct,
    int? GpuUtilPct,
    int? MemUtilPct,
    ulong? ClockEventReasons,
    long? SensorTimestampNs,
    string? Error)
{
    /// <summary>Null for legacy/simulation readers. Required live references are selected by the frozen manifest policy.</summary>
    public VfStatusReference? VfStatus { get; init; }
}

/// <summary>NVML clock-event ("throttle") reason bits, as nvmlClocksEventReasons.</summary>
[Flags]
public enum ClockEventReason : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationsClocksSetting = 0x2,
    SwPowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    SwThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrakeSlowdown = 0x80,
    DisplayClockSetting = 0x100,
}

/// <summary>
/// A worker's progress tick (spec §10.1): submitted vs completed epochs and validated work, with QPC time, so residency
/// can be restricted to intervals in which the GPU actually completed checked work.
/// </summary>
public sealed record WorkerProgress(
    long MonoNs,
    string ProfileId,
    long SubmittedEpochs,
    long CompletedEpochs,
    long LaunchCount,
    long WorkUnits,
    long CheckedElements,
    long Mismatches,
    long? LastBatchStartNs,
    long? LastBatchEndNs);
