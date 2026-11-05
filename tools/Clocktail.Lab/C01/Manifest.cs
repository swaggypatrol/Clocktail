using System.Text.Json;

namespace Bwt.Contracts;

/// <summary>One V/F node as this card reports it. Node ids and voltages come from the runtime; nothing hard-coded.</summary>
public sealed record VfNode(int NodeId, long VoltageUv, long StockFrequencyKhz, long? CurrentFrequencyKhz, bool Writable);

public enum SearchStage { Smoke, Screen, EdgeConfirm, CurveValidate, ExtendedSoak }

public enum ObjectiveKind { BalancedDaily, Efficiency, Performance }

/// <summary>How much of one workload a stage requires (spec §12 budgets are development defaults, calibrated on HW).</summary>
public sealed record WorkloadBudget(
    string ProfileId,
    SearchStage Stage,
    double QualifiedSeconds,
    int RequiredPasses,
    IReadOnlyList<string> Seeds,
    string? OracleVersion,
    bool Required)
{
    /// <summary>Optional per-profile node intensities on the Screen budget; null uses the frozen search default.</summary>
    public IReadOnlyList<double>? RequiredIntensities { get; init; }
    /// <summary>The worker-reported implementation version frozen when this experiment was planned.</summary>
    public int? ProfileVersion { get; init; }
}

/// <summary>Residency acceptance rules, frozen per scan (spec §10.1). Thresholds are engineering starting points.</summary>
public sealed record ResidencyRules(
    double MinInTargetFraction,
    double MinCoverageFraction,
    long MaxGapNs,
    long MaxPairingSkewNs,
    long VoltageWindowUv,
    long FrequencyWindowKhz,
    long MinValidatedWorkUnits,
    long MinQualifiedNs)
{
    public static readonly ResidencyRules Default = new(
        MinInTargetFraction: 0.95,
        MinCoverageFraction: 0.80,
        MaxGapNs: 500_000_000,
        MaxPairingSkewNs: 50_000_000,
        VoltageWindowUv: 6_250,
        FrequencyWindowKhz: 15_000,
        MinValidatedWorkUnits: 1,
        MinQualifiedNs: 5_000_000_000);
}

public sealed record ScanConditions(
    long PowerBudgetMw,
    long? DevicePowerMinMw,
    long? DevicePowerMaxMw,
    string FanPolicy,
    int? TempMinC,
    int? TempMaxC,
    string NonCoreBaseline,
    string ThermalState);

public sealed record ScanTargets(
    IReadOnlyList<VfNode> Nodes,
    IReadOnlyList<int> AnchorNodeIds,
    long FrequencyStepKhz,
    int CoarseStepMultiple,
    int MarginSteps);

public sealed record ScanLimits(int MaxCandidates, long MaxWallSeconds, bool StopOnFirstDeviceLost, int MaxFlakyRetries);

public sealed record Objective(ObjectiveKind Kind, double MaxPerfRegressionFraction);

/// <summary>
/// manifest.json (spec §11.1 / §15): everything a candidate's evidence depends on. Frozen when the scan starts; a change
/// to any field is a different experiment.
/// </summary>
public sealed record ScanManifest(
    int SchemaVersion,
    string ScanId,
    DateTimeOffset CreatedUtc,
    ControlMode ControlMode,
    GpuIdentity Gpu,
    SoftwareVersions Software,
    ScanConditions Conditions,
    ScanTargets Targets,
    IReadOnlyList<WorkloadBudget> Workloads,
    ResidencyRules Residency,
    ScanLimits Limits,
    Objective Objective)
{
    public const int CurrentSchema = 2;

    /// <summary>Complete effective scan options as frozen before execution. Null in legacy schema 1 records.</summary>
    public JsonElement? Policy { get; init; }
}
