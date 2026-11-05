using Bwt.Contracts;

namespace Bwt.Gpu;

/// <summary>
/// Runs one prepared workload for a <see cref="StartRequest"/> and folds its batches into a <see cref="RunResult"/>.
/// The fault (if any) is injected into batch index 1 (the second batch, so "replay previous" has a previous one); a run
/// with a fault therefore always runs at least two batches. Cancellation stops new submissions between batches.
/// </summary>
public static class BatchRunner
{
    public static ulong NonceFor(ulong seed, long trialGeneration, long epoch)
        => SplitMix64(seed ^ ((ulong)trialGeneration << 32) ^ (ulong)epoch * 0x9E3779B97F4A7C15UL);

    public static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public static RunResult Run(IWorkload w, ulong seed, StartRequest start, CancellationToken cancel, Action<WorkerProgress>? progress = null,
        Action<BatchResult>? onBatch = null, Action<BatchDiagnostic>? onDiagnostic = null)
    {
        var fault = FaultNames.Parse(start.Fault);
        var coverage = (w as IWorkloadRunCoverage)?.CreateRunCoverage();
        long t0 = Mono.NowNs();
        long deadline = start.DurationMs > 0 ? t0 + start.DurationMs * 1_000_000 : long.MaxValue;
        long maxBatches = start.MaxBatches > 0 ? start.MaxBatches : long.MaxValue;
        if (fault != FaultKind.None && maxBatches < 2) maxBatches = 2;
        long lastProgress = t0;

        long batches = 0, launches = 0, work = 0, checkedEl = 0, totalEl = 0, mism = 0, nonFinite = 0, stale = 0, completedEpochs = 0;
        double? maxAbs = null, maxRel = null;
        long? gpuNs = null, maxLaunchNs = null, firstSubmit = null, lastComplete = null;
        long intervalNs = 0;
        var scopes = new HashSet<CheckScope>();
        var artifacts = new List<string>();
        BatchStatus worst = BatchStatus.Ok;
        string? detail = null;
        string backend = w.Info.Backend;

        for (long i = 0; i < maxBatches; i++)
        {
            if (cancel.IsCancellationRequested) { worst = Max(worst, BatchStatus.Cancelled); break; }
            if (i > 0 && Mono.NowNs() >= deadline && !(fault != FaultKind.None && i < 2)) break;
            long epoch = start.EpochBase + i;
            var f = (fault != FaultKind.None && i == 1) ? fault : FaultKind.None;
            BatchResult r;
            try
            {
                r = w.RunBatch(new BatchRequest(start.TrialGeneration, epoch, NonceFor(seed, start.TrialGeneration, epoch), start.LaunchBudgetMs, f, cancel));
            }
            catch (CudaException e) when (e.ContextFatal)
            {
                worst = BatchStatus.DeviceLost; detail = e.Message; break;
            }
            catch (CudaException e)
            {
                worst = Max(worst, BatchStatus.InfrastructureError); detail = e.Message; break;
            }
            batches++;
            var diagnostic = new BatchDiagnostic(w.Info.Id, i, start.TrialGeneration, epoch, r.Epoch,
                $"0x{seed:x16}", $"0x{NonceFor(seed, start.TrialGeneration, epoch):x16}", FaultNames.Of(f),
                r.Status.ToString(), r.Launches, r.WorkUnits, r.CheckedElements, r.TotalElements, r.Scope,
                r.Mismatches, r.NonFinite, r.StaleOrMissing, r.MaxAbsErr, r.MaxRelErr, r.Detail, r.FirstFailure,
                r.HostSubmitQpc, r.HostCompleteQpc, r.GpuElapsedNs, r.MaxLaunchNs, r.Metrics);
            coverage?.Observe(diagnostic);
            onDiagnostic?.Invoke(diagnostic);
            onBatch?.Invoke(r);
            launches += r.Launches; work += r.WorkUnits; checkedEl += r.CheckedElements; totalEl += r.TotalElements;
            mism += r.Mismatches; nonFinite += r.NonFinite; stale += r.StaleOrMissing;
            if (r.Status == BatchStatus.Ok || r.Status == BatchStatus.Mismatch) completedEpochs++;
            maxAbs = MaxN(maxAbs, r.MaxAbsErr); maxRel = MaxN(maxRel, r.MaxRelErr);
            if (r.GpuElapsedNs is { } g) gpuNs = (gpuNs ?? 0) + g;
            if (r.MaxLaunchNs is { } ml) maxLaunchNs = Math.Max(maxLaunchNs ?? 0, ml);
            firstSubmit ??= r.HostSubmitQpc;
            lastComplete = r.HostCompleteQpc;
            intervalNs += Mono.TicksToNs(r.HostCompleteQpc - r.HostSubmitQpc);
            scopes.Add(r.Scope);
            if (r.FirstFailure != null && artifacts.Count < 16) artifacts.Add($"epoch {r.Epoch}: {r.FirstFailure}");
            var s = r.Clean ? BatchStatus.Ok : r.Status == BatchStatus.Ok ? BatchStatus.Mismatch : r.Status;
            worst = Max(worst, s);
            if (r.Detail != null) detail ??= r.Detail;
            long now = Mono.NowNs();
            if (progress != null && (now - lastProgress) >= start.ProgressIntervalMs * 1_000_000L)
            {
                lastProgress = now;
                progress(new WorkerProgress(now, w.Info.Id, i + 1, completedEpochs, launches, work, checkedEl, mism + nonFinite + stale,
                    Mono.TicksToNs(r.HostSubmitQpc), Mono.TicksToNs(r.HostCompleteQpc)));
            }
            if (s is BatchStatus.DeviceLost or BatchStatus.Unsupported or BatchStatus.InfrastructureError) break;
            if (s == BatchStatus.Mismatch && start.StopOnFirstMismatch) break;
        }

        var scope = scopes.Count switch { 0 => CheckScope.None, 1 => scopes.First(), _ => scopes.Contains(CheckScope.Sampled) || scopes.Contains(CheckScope.None) ? CheckScope.Mixed : CheckScope.Full };
        var exec = new Execution(
            Executed: launches > 0,
            Backend: backend,
            Algorithm: null,
            LaunchCount: launches,
            CompletedEpochs: completedEpochs,
            WorkUnits: work,
            HostSubmitQpc: firstSubmit,
            HostCompletionObservedQpc: lastComplete,
            GpuEventElapsedNs: gpuNs,
            CompletedWorkIntervalNs: intervalNs,
            MaxLaunchNs: maxLaunchNs,
            SkipReason: launches > 0 ? null : detail ?? "no launch completed");
        var val = new Validation(
            OracleVersion: w.Info.OracleVersion,
            CheckedElements: checkedEl,
            CheckScope: scope,
            FullyCheckedFraction: totalEl > 0 ? (double)checkedEl / totalEl : null,
            MismatchCount: mism,
            NonFiniteCount: nonFinite,
            StaleOrMissingCount: stale,
            MaxAbsoluteError: maxAbs,
            MaxRelativeError: maxRel,
            FirstFailureArtifact: artifacts.FirstOrDefault());
        var outcome = worst switch
        {
            BatchStatus.DeviceLost => Outcome.DeviceLost,
            BatchStatus.Mismatch => Outcome.ComputeMismatch,
            BatchStatus.Unsupported => Outcome.Unsupported,
            BatchStatus.InfrastructureError => Outcome.InfrastructureError,
            BatchStatus.Cancelled => Outcome.Cancelled,
            _ => launches > 0 && checkedEl > 0 && work > 0 ? Outcome.Pass : Outcome.Inconclusive,
        };
        if (coverage != null)
        {
            var check = coverage.Finish();
            if (outcome == Outcome.Pass) outcome = check.Outcome;
            detail = (detail == null ? "" : detail + " | ") + check.Detail;
        }
        return new RunResult(w.Info.Id, outcome, exec, val, batches, fault == FaultKind.None ? null : FaultNames.Of(fault), detail, artifacts);
    }

    // Severity order: Ok < Cancelled < Mismatch < Unsupported < InfrastructureError < DeviceLost.
    private static int Rank(BatchStatus s) => s switch
    {
        BatchStatus.Ok => 0, BatchStatus.Cancelled => 1, BatchStatus.Mismatch => 2, BatchStatus.Unsupported => 3,
        BatchStatus.InfrastructureError => 4, BatchStatus.DeviceLost => 5, _ => 4,
    };

    private static BatchStatus Max(BatchStatus a, BatchStatus b) => Rank(a) >= Rank(b) ? a : b;
    private static double? MaxN(double? a, double? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
}

/// <summary>
/// Sizes per-launch work to a time budget (spec §6.4): start small, measure with events, scale toward
/// <paramref name="targetMs"/>, never past <paramref name="maxMs"/>. Use during Prepare only (not formal timing).
/// </summary>
public static class LaunchCalibrator
{
    /// <param name="runOnceMs">Launches with the given work amount, waits, returns GPU elapsed ms.</param>
    public static long Calibrate(Func<long, double> runOnceMs, long initialWork, long maxWork, double targetMs = 20, double maxMs = 50)
    {
        long work = Math.Max(1, initialWork);
        double ms = runOnceMs(work);
        for (int i = 0; i < 24 && ms < targetMs * 0.5 && work < maxWork; i++)
        {
            double factor = ms <= 0.01 ? 8 : Math.Min(8, targetMs / ms);
            work = Math.Min(maxWork, Math.Max(work + 1, (long)(work * factor)));
            ms = runOnceMs(work);
        }
        while (ms > maxMs && work > 1)
        {
            work = Math.Max(1, (long)(work * (targetMs / ms)));
            ms = runOnceMs(work);
        }
        return work;
    }
}
