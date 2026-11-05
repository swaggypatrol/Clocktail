using System.Text.Json.Serialization;

namespace Bwt.Contracts;

/// <summary>Overall trial outcome (spec §14.3). Never just pass/fail.</summary>
[JsonConverter(typeof(UpperSnakeEnumConverter<Outcome>))]
public enum Outcome
{
    Pass,
    ComputeMismatch,
    DeviceLost,
    ApplyNoEffect,
    ApplyPartial,
    ResidencyInsufficient,
    Unreachable,
    Conflict,
    Flaky,
    EnvironmentLimited,
    Unsupported,
    NotRun,
    InfrastructureError,
    Cancelled,
    RecoveryFailed,
    Inconclusive,
}

/// <summary>Status of one stage of a trial (execution / validation / residency / restoration), kept separately.</summary>
public enum StageStatus { NotRun, Ok, Failed, Unknown, Insufficient, Unsupported }

public enum CheckScope { None, Sampled, Full, Mixed }

/// <summary>none: no usable residency; frequency_only: no voltage source (no V/F claim may be made);
/// vf_joint: clock and voltage paired under the manifest's skew/age/gap rules.</summary>
public enum EvidenceClass { None, FrequencyOnly, VfJoint }

public enum ClockDomain { Graphics, Sm, Memory, Video }

/// <summary>How a mutation landed (spec §6.3 step 5). Return-OK is not success: only readback decides.</summary>
public enum ApplyStatus { NotApplicable, Verified, Quantized, Clipped, Partial, NoEffect, Unknown, Failed }

public sealed record ProfileRef(
    string Id,
    string Name,
    int Version,
    string Seed,
    string? BinarySha256,
    string? ManifestSha256);

/// <summary>Layer 1 of 3 (spec §10): what was asked for.</summary>
public sealed record Requested(
    int? NodeId,
    long? VoltageUv,
    long? FrequencyKhz,
    long? OffsetKhz,
    long? PowerMw);

/// <summary>Layer 2 of 3: what the driver read back.</summary>
public sealed record Applied(
    ApplyStatus Status,
    bool Verified,
    string? SnapshotSha256,
    bool? Normalized,
    string? NativeStatus,
    long? FrequencyKhz,
    long? OffsetKhz);

public sealed record Execution(
    bool Executed,
    string? Backend,
    string? Algorithm,
    long LaunchCount,
    long CompletedEpochs,
    long WorkUnits,
    long? HostSubmitQpc,
    long? HostCompletionObservedQpc,
    long? GpuEventElapsedNs,
    long CompletedWorkIntervalNs,
    long? MaxLaunchNs,
    string? SkipReason)
{
    public static readonly Execution NotRun = new(false, null, null, 0, 0, 0, null, null, null, 0, null, "not_run");
}

public sealed record Validation(
    string? OracleVersion,
    long CheckedElements,
    CheckScope CheckScope,
    double? FullyCheckedFraction,
    long? MismatchCount,
    long? NonFiniteCount,
    long? StaleOrMissingCount,
    double? MaxAbsoluteError,
    double? MaxRelativeError,
    string? FirstFailureArtifact)
{
    public static readonly Validation None = new(null, 0, CheckScope.None, null, null, null, null, null, null, null);
}

/// <summary>Layer 3 of 3: what was observed. <see cref="QualifiedObservedDurationNs"/> is an observation interval under
/// the manifest's rules, not an electrical residency measurement (spec §14.2).</summary>
public sealed record Residency(
    EvidenceClass EvidenceClass,
    int ValidSampleCount,
    double? InTargetFraction,
    double? CoverageFraction,
    long? MaxGapNs,
    long QualifiedObservedDurationNs,
    string? VoltageSource,
    ClockDomain? FrequencyDomain,
    bool SensorAgeKnown,
    long? ObservedMedianFrequencyKhz,
    long? ObservedMedianVoltageUv)
{
    /// <summary>Dynamic observation verifies checked work under the candidate controls, not residency on individual nodes.</summary>
    public string? ObservationScope { get; init; }
    public const string DynamicBoostControlVerifiedScope = "dynamic_boost_control_verified";
    public string? FrequencyReferenceSource { get; init; }
    public string? ReferenceControlSha256 { get; init; }
    public long WarmupExcludedNs { get; init; }
    public int AmbiguousPointSamples { get; init; }
    public int InvalidLiveReferenceSamples { get; init; }
    public static readonly Residency None = new(EvidenceClass.None, 0, null, null, null, 0, null, null, false, null, null);
}

public sealed record Restoration(bool Required, string? TargetSnapshotSha256, bool Verified, string? Detail)
{
    public static readonly Restoration NotRequired = new(false, null, false, null);
}

/// <summary>
/// One trial (spec §14.2), written to trials.jsonl. A trial = one candidate × one workload × one condition. The overall
/// <see cref="Outcome"/> is derived from the four stage statuses, which are kept as well.
/// </summary>
public sealed record TrialResult(
    int SchemaVersion,
    string ScanId,
    string TrialId,
    GpuIdentity? GpuIdentity,
    string? DriverGeneration,
    ProfileRef Profile,
    Requested Requested,
    Applied Applied,
    Execution Execution,
    Validation Validation,
    Residency Residency,
    Restoration Restoration,
    StageStatus ExecutionStatus,
    StageStatus ValidationStatus,
    StageStatus ResidencyStatus,
    StageStatus RestorationStatus,
    Outcome Outcome,
    string? Detail)
{
    public const int CurrentSchema = 1;
    /// <summary>Optional sidecar metadata for per-batch evidence, absent in historical trials.</summary>
    public BatchDiagnostics? BatchDiagnostics { get; init; }
}
