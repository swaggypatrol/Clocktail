using System.Text.Json;

namespace Bwt.Contracts;

/// <summary>
/// Host ⇄ worker protocol (spec §14.1): one JSON object per line on the worker's stdin (host→worker) and stdout
/// (worker→host). stderr is free-form log. Project-internal, versioned by <see cref="SchemaVersion"/>.
/// <para>Message types (<see cref="Type"/>):</para>
/// <list type="bullet">
/// <item>worker→host <c>hello</c> (<see cref="WorkerHello"/>), sent once at start.</item>
/// <item>host→worker <c>prepare</c> (<see cref="PrepareRequest"/>) → worker <c>prepared</c> (<see cref="PrepareResult"/>).</item>
/// <item>host→worker <c>start</c> (<see cref="StartRequest"/>) → worker <c>progress</c>* (<see cref="WorkerProgress"/>) then <c>result</c> (<see cref="RunResult"/>).</item>
/// <item>host→worker <c>cancel</c>: stop submitting, drain, answer <c>result</c> with outcome CANCELLED.</item>
/// <item>host→worker <c>shutdown</c>: release everything and exit 0.</item>
/// <item>worker→host <c>error</c> (<see cref="WorkerError"/>): protocol/infrastructure error.</item>
/// </list>
/// The worker never changes clocks, curves, power or fans. It only generates load and checks results.
/// </summary>
public sealed record WorkerEnvelope(
    int SchemaVersion,
    string Type,
    long Seq,
    string? RequestId,
    string? ScanId,
    string? TrialId,
    int WorkerPid,
    long MonoNs,
    JsonElement? Body)
{
    public const int CurrentSchema = 1;

    public static WorkerEnvelope Of<T>(string type, long seq, T body, string? requestId = null, string? scanId = null, string? trialId = null)
        => new(CurrentSchema, type, seq, requestId, scanId, trialId, Environment.ProcessId, Mono.NowNs(),
            JsonSerializer.SerializeToElement(body, BwtJson.Options));

    public T? BodyAs<T>() => Body is { } b ? b.Deserialize<T>(BwtJson.Options) : default;
}

public static class MessageType
{
    public const string Hello = "hello";
    public const string Prepare = "prepare";
    public const string Prepared = "prepared";
    public const string Start = "start";
    public const string Progress = "progress";
    public const string BatchDiagnostic = "batch_diagnostic";
    public const string Cancel = "cancel";
    public const string Result = "result";
    public const string Shutdown = "shutdown";
    public const string Error = "error";
}

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

public sealed record WorkerHello(
    string WorkerBuild,
    string? BinarySha256,
    GpuIdentity? Gpu,
    IReadOnlyList<WorkloadInfo> Workloads,
    IReadOnlyList<WorkloadSupport> Support);

public sealed record PrepareRequest(string ProfileId, string Seed, double Intensity, IReadOnlyDictionary<string, string>? Params);

/// <summary>prepare finished: modules loaded, buffers allocated, oracle ready. No timing is taken during prepare.</summary>
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
    int ProgressIntervalMs,
    bool StopOnFirstMismatch);

/// <summary>A run's structured result: the execution and validation layers of a <see cref="TrialResult"/>.</summary>
public sealed record RunResult(
    string ProfileId,
    Outcome Outcome,
    Execution Execution,
    Validation Validation,
    long Batches,
    string? Fault,
    string? Detail,
    IReadOnlyList<string> FailureArtifacts,
    BatchDiagnostics? BatchDiagnostics = null);

/// <summary>Host-side location and completeness of the streamed batch diagnostics. Null on older results.</summary>
public sealed record BatchDiagnostics(string? JsonlPath, long Received, bool Complete, string? Error, string? Sha256 = null);

/// <summary>Facts for a completed batch. Detail and FirstFailure are workload-provided text, not parsed fields.</summary>
public sealed record BatchDiagnostic(
    string ProfileId, long BatchIndex, long TrialGeneration, long RequestedEpoch, long ResultEpoch,
    string Seed, string Nonce, string Fault, string Status, long Launches, long WorkUnits,
    long CheckedElements, long TotalElements, CheckScope Scope, long Mismatches, long NonFinite,
    long StaleOrMissing, double? MaxAbsErr, double? MaxRelErr, string? Detail, string? FirstFailure,
    long HostSubmitQpc, long HostCompleteQpc, long? GpuElapsedNs, long? MaxLaunchNs,
    IReadOnlyDictionary<string, double>? Metrics = null);

public sealed record WorkerError(string Kind, string Message, int? NativeCode);
