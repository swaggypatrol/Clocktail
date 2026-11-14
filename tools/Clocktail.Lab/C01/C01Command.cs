using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bwt.Contracts;
using Bwt.Gpu;
using Bwt.Workloads;

namespace Clocktail.Lab;

internal static class C01Command
{
    internal static Dictionary<string, string> AriaParameters() => new() { ["acc"] = "16", ["hash_interval"] = "8" };

    public static int Run(double seconds, double intensity, string seedText, double maxTemp, string? pci, string? output)
    {
        if (seconds is < 10 or > 600 || !double.IsFinite(seconds) || seconds != Math.Truncate(seconds))
            throw new ArgumentException("seconds must be a whole number in 10..600");
        if (intensity is < 0.01 or > 1 || !double.IsFinite(intensity))
            throw new ArgumentException("intensity must be in 0.01..1");
        if (maxTemp is < 40 or > 100 || !double.IsFinite(maxTemp))
            throw new ArgumentException("max-temp-c must be in 40..100");
        if (seedText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) seedText = seedText[2..];
        if (!ulong.TryParse(seedText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong seed))
            throw new ArgumentException("seed must be an unsigned 64-bit hexadecimal number");

        using var gpu = GpuContext.Open(pciBusId: pci);
        using var sampler = new NvmlSampler(gpu.Info.PciBusId);
        using var workload = new C01Fp32Fma();
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var watch = Stopwatch.StartNew();
        var before = sampler.Read(0, 0);
        var vram = sampler.Vram();
        using var safety = new SafetyMonitor(() => new NvmlSampler(gpu.Info.PciBusId), maxTemp, cancel);
        safety.WaitReady(TimeSpan.FromSeconds(12));
        string? interrupted = null;
        uint? peak = before.TempC;
        PrepareResult? prepare = null;
        RunResult? result = null;
        string? reason = null;
        string verdict = "inconclusive";
        DateTime? measurementStarted = null, measurementFinished = null;
        long nextSample = 0;
        int reasonSamples = 0, powerCapSamples = 0;
        const int launchBudgetMs = 50;

        void Sample(bool measuring = false)
        {
            if (watch.ElapsedMilliseconds < nextSample) return;
            nextSample = watch.ElapsedMilliseconds + 100;
            var sample = sampler.Read(watch.Elapsed.TotalSeconds, 0);
            if (measuring)
            {
                ulong flags = sampler.ClockEventReasons();
                reasonSamples++;
                if ((flags & 4) != 0) powerCapSamples++;
            }
            if (sample.TempC is not { } temp) return;
            peak = Math.Max(peak ?? temp, temp);
            if (temp < maxTemp) return;
            interrupted = "temperature";
            cancel.Cancel();
        }

        try
        {
            var support = workload.Probe(gpu);
            Sample();
            if (!support.Supported) reason = support.Reason;
            else if (!cancel.IsCancellationRequested)
            {
                prepare = workload.Prepare(gpu, new WorkloadParams(seed, intensity,
                    AriaParameters()), cancel.Token);
                if (!prepare.Ok) reason = prepare.Error;
                else
                {
                    Sample();
                    measurementStarted = DateTime.UtcNow;
                    result = BatchRunner.Run(workload, seed,
                        new StartRequest(1, (long)seconds * 1000, 0, launchBudgetMs, 1, "none", 1000, true),
                        cancel.Token, onBatch: batch =>
                        {
                            Sample(measuring: true);
                            if (batch.MaxLaunchNs is > launchBudgetMs * 1_000_000L) cancel.Cancel();
                        });
                    measurementFinished = DateTime.UtcNow;
                    reason = result.Validation.FirstFailureArtifact ?? result.Detail;
                    verdict = result.Outcome switch
                    {
                        Outcome.ComputeMismatch or Outcome.DeviceLost => "fail",
                        Outcome.Pass when result.Execution.MaxLaunchNs is > 0 and <= launchBudgetMs * 1_000_000L
                            && interrupted == null => "pass",
                        _ => "inconclusive",
                    };
                }
            }
        }
        catch (WorkloadPreparationException e)
        {
            reason = e.Message;
            verdict = e.Result.Status == BatchStatus.DeviceLost || e.Result.Mismatches + e.Result.NonFinite + e.Result.StaleOrMissing > 0 ? "fail" : "inconclusive";
        }
        catch (Bwt.Gpu.CudaException e)
        {
            if (e.ContextFatal) gpu.MarkPoisoned();
            reason = e.Message;
            verdict = e.ContextFatal ? "fail" : "inconclusive";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { verdict = "inconclusive"; }

        safety.Dispose();
        interrupted = safety.Interrupted ?? interrupted;
        if (interrupted != null && verdict != "fail") { verdict = "inconclusive"; reason ??= safety.Reason; }

        var report = new
        {
            verdict, interrupted, reasons = verdict == "pass" || reason == null ? [] : new[] { reason },
            busy_before_pct = before.UtilPct, others_vram_mib = vram?.Used >> 20,
            phases = new[] { new { kind = "c01", started_utc = measurementStarted, finished_utc = measurementFinished, temp_max = peak, intensity, seed = $"0x{seed:x16}",
                clock_reason_samples = reasonSamples, sw_power_cap_samples = powerCapSamples, prepare, result } },
        };
        string json = JsonSerializer.Serialize(report, BwtJson.Indented);
        if (output == null) Console.WriteLine(json);
        else File.WriteAllText(output, json);
        return verdict switch { "pass" => 0, "fail" => 4, _ => 5 };
    }
}
