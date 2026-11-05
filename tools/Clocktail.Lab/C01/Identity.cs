namespace Bwt.Contracts;

/// <summary>
/// One physical GPU across APIs (spec §4.3). Matching is by UUID / PCI bus id / DXGI LUID, never by API index:
/// <see cref="CudaOrdinal"/> and <see cref="NvmlIndex"/> are recorded only so a mismatch can be shown.
/// </summary>
public sealed record GpuIdentity(
    string Name,
    string? Uuid,
    string? PciBusId,
    string? Luid,
    string? VbiosVersion,
    string? DriverVersion,
    int? CudaOrdinal,
    int? NvmlIndex,
    int? ComputeCapabilityMajor,
    int? ComputeCapabilityMinor);

/// <summary>Every software component a result depends on (spec §13.2 manifest split). Unknown stays null.</summary>
public sealed record SoftwareVersions(
    string? DisplayDriverVersion,
    int? CudaDriverApiVersion,
    string? NvrtcVersion,
    string? CublasLtVersion,
    string? OptixAbiVersion,
    string? OptixRuntimeVersion,
    string ToolBuild,
    IReadOnlyDictionary<string, string> BinaryHashes);

/// <summary>Capability is a ladder, not a bool (spec §4.2).</summary>
public enum CapabilityLevel { Absent, Declared, Readable, Writable, ReadbackVerified, ObservedUnderLoad }

public enum CapabilityFailure { None, SymbolMissing, NotSupported, NoPermission, LayoutMismatch, ApplyNoEffect, DeviceLost, NotProbed }

public sealed record CapabilityRecord(
    string Name,
    CapabilityLevel Level,
    CapabilityFailure Failure,
    string? NativeStatus,
    string? Detail);

/// <summary>pass / fail / unsupported / not_run. not_run is never treated as pass (spec §13.5).</summary>
public enum CheckState { Pass, Fail, Unsupported, NotRun }

public sealed record CompatCheck(string Name, CheckState State, string Evidence);

/// <summary>compatibility.json (spec §13.5): written before any automatic search.</summary>
public sealed record CompatibilityReport(
    int SchemaVersion,
    DateTimeOffset Utc,
    GpuIdentity? Gpu,
    SoftwareVersions Software,
    ControlMode ControlMode,
    IReadOnlyList<CapabilityRecord> Capabilities,
    IReadOnlyList<CompatCheck> Checks);

/// <summary>External control modes (spec §4.2). This standalone build implements ObserveOnly, ManualProfile and
/// Simulated; PublicOffset / CurveExperimental are reserved for the Clocktail-broker integration.</summary>
public enum ControlMode { ObserveOnly, PublicOffset, CurveExperimental, ManualProfile, Simulated }
