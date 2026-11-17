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

public enum CheckScope { None, Sampled, Full, Mixed }

/// <summary>Completed work and timing from the C01 measurement window.</summary>
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
    string? SkipReason);

/// <summary>CPU oracle coverage and failures for completed batches.</summary>
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
    string? FirstFailureArtifact);
