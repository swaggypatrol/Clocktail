namespace Bwt.Contracts;

public sealed record WorkloadInfo(
    string Id,
    string Name,
    int Version,
    string Phase,
    string OracleVersion,
    string Backend,
    string Description,
    IReadOnlyList<string> SupportedFaults);

public sealed record WorkloadSupport(string Id, bool Supported, string? Reason);

/// <summary>Setup and warmup evidence reported before the measured C01 batches.</summary>
public sealed record PrepareResult(
    bool Ok,
    string ProfileId,
    string? Error,
    string? Backend,
    string? Algorithm,
    IReadOnlyDictionary<string, string> Details);

/// <summary>
/// Run batches until <see cref="DurationMs"/> elapses or <see cref="MaxBatches"/> complete (whichever first; 0 = no
/// limit on that axis). <see cref="LaunchBudgetMs"/> bounds each individual kernel / launch (spec §6.4: 10–50 ms).
/// <see cref="Fault"/> injects a deliberate defect so the oracle path is proven to fire (spec §8.2).
/// </summary>
public sealed record StartRequest(
    long TrialGeneration,
    long DurationMs,
    long MaxBatches,
    int LaunchBudgetMs,
    long EpochBase,
    string Fault,
    bool StopOnFirstMismatch);

/// <summary>Structured C01 execution and validation evidence written to the CLI report.</summary>
public sealed record RunResult(
    string ProfileId,
    Outcome Outcome,
    Execution Execution,
    Validation Validation,
    long Batches,
    string? Fault,
    string? Detail,
    IReadOnlyList<string> FailureArtifacts)
{
    // Retain the nullable JSON field for existing CLI report readers.
    public object? BatchDiagnostics => null;
}
