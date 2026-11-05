using Bwt.Contracts;

namespace Bwt.Gpu;

/// <summary>
/// Deliberate defects (spec §8.2 / §18) that a workload must be able to inject into exactly one batch, so the oracle
/// path is proven to fire. Every one of them must turn a passing run into COMPUTE_MISMATCH (or, for
/// <see cref="ComparatorFixture"/>, a mismatch raised by a wrong reference). A workload lists the ones it supports.
/// </summary>
public enum FaultKind
{
    None,
    /// <summary>Flip one bit of the device output after the kernel completed, before readback.</summary>
    DeviceBitFlip,
    /// <summary>Do not launch the producing kernel: the poisoned/sentinel output must be detected as missing.</summary>
    SkipOutput,
    /// <summary>Present the previous batch's output as this batch's: stale epoch/nonce must be detected.</summary>
    ReplayPrevious,
    /// <summary>The kernel writes correct values but tags them with the previous epoch.</summary>
    StaleEpoch,
    /// <summary>Overwrite one output value with NaN (or Inf for integer-free paths): non-finite must be detected.</summary>
    NonFinite,
    /// <summary>Corrupt the CPU reference (not the device output): the comparator must report a mismatch.</summary>
    ComparatorFixture,
    /// <summary>Only part of the output is produced (e.g. half the grid): missing elements must be detected.</summary>
    ShortOutput,
}

public enum BatchStatus { Ok, Mismatch, DeviceLost, InfrastructureError, Unsupported, Cancelled }

public sealed record WorkloadParams(ulong Seed, double Intensity, IReadOnlyDictionary<string, string> Extra)
{
    public static WorkloadParams Default(ulong seed) => new(seed, 1.0, new Dictionary<string, string>());
}

/// <summary>
/// One batch request. <see cref="Epoch"/> increases by one per batch within a run and <see cref="Nonce"/> is fresh per
/// batch: outputs must carry/depend on them so stale or replayed results cannot pass (docs/KERNEL_CONTRACT.md §2).
/// </summary>
public sealed record BatchRequest(
    long TrialGeneration,
    long Epoch,
    ulong Nonce,
    int LaunchBudgetMs,
    FaultKind Fault,
    CancellationToken Cancel);

/// <summary>
/// One batch's facts. Counts are what actually completed and was checked; a workload never reports work it did not
/// verify as checked. <see cref="HostSubmitQpc"/>/<see cref="HostCompleteQpc"/> are QPC ticks (Mono.Qpc), the GPU
/// event time is separate and is not a pure kernel execution time.
/// </summary>
public sealed record BatchResult(
    BatchStatus Status,
    long Epoch,
    long Launches,
    long WorkUnits,
    long CheckedElements,
    long TotalElements,
    CheckScope Scope,
    long Mismatches,
    long NonFinite,
    long StaleOrMissing,
    double? MaxAbsErr,
    double? MaxRelErr,
    string? FirstFailure,
    long HostSubmitQpc,
    long HostCompleteQpc,
    long? GpuElapsedNs,
    long? MaxLaunchNs,
    string? Detail = null)
{
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }
    public bool Clean => Status == BatchStatus.Ok && Mismatches == 0 && NonFinite == 0 && StaleOrMissing == 0;
}

/// <summary>
/// A load generator with its own oracle (docs/KERNEL_CONTRACT.md). Lifecycle: <see cref="Probe"/> → <see cref="Prepare"/>
/// (no timing; modules, buffers, references) → <see cref="RunBatch"/>* → <see cref="IDisposable.Dispose"/>.
/// Implementations are single-threaded and own their device buffers. They never touch clocks/curves/power/fans.
/// </summary>
public interface IWorkload : IDisposable
{
    WorkloadInfo Info { get; }

    /// <summary>Can this device run it? Unsupported is reported, not failed (spec §7).</summary>
    WorkloadSupport Probe(GpuContext gpu);

    /// <summary>Allocate, load kernels, build CPU references. Returns details for the record (registers, spills, sizes,
    /// backend/algorithm).</summary>
    PrepareResult Prepare(GpuContext gpu, WorkloadParams p);

    /// <summary>Submit one batch of launches (each ≤ <see cref="BatchRequest.LaunchBudgetMs"/>), wait for completion,
    /// validate, and report. A context-fatal CUDA error returns <see cref="BatchStatus.DeviceLost"/>.</summary>
    BatchResult RunBatch(BatchRequest req);
}

/// <summary>Optional run-level coverage contract. A fresh tracker is created by BatchRunner for every invocation;
/// direct Prepare warmup batches and previous invocations cannot satisfy a formal run's requirements.</summary>
public interface IWorkloadRunCoverage
{
    IWorkloadCoverage? CreateRunCoverage();
}

public interface IWorkloadCoverage
{
    void Observe(BatchDiagnostic batch);
    (Outcome Outcome, string Detail) Finish();
}

public static class FaultNames
{
    public static string Of(FaultKind k) => k switch
    {
        FaultKind.None => "none",
        FaultKind.DeviceBitFlip => "device_bit_flip",
        FaultKind.SkipOutput => "skip_output",
        FaultKind.ReplayPrevious => "replay_previous",
        FaultKind.StaleEpoch => "stale_epoch",
        FaultKind.NonFinite => "non_finite",
        FaultKind.ComparatorFixture => "comparator_fixture",
        FaultKind.ShortOutput => "short_output",
        _ => k.ToString(),
    };

    public static FaultKind Parse(string? s) => (s ?? "none").Trim().ToLowerInvariant() switch
    {
        "" or "none" => FaultKind.None,
        "device_bit_flip" => FaultKind.DeviceBitFlip,
        "skip_output" => FaultKind.SkipOutput,
        "replay_previous" => FaultKind.ReplayPrevious,
        "stale_epoch" => FaultKind.StaleEpoch,
        "non_finite" => FaultKind.NonFinite,
        "comparator_fixture" => FaultKind.ComparatorFixture,
        "short_output" => FaultKind.ShortOutput,
        var x => throw new ArgumentException($"unknown fault '{x}'"),
    };
}
