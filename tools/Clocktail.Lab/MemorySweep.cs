using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Platform;

namespace Clocktail.Lab;

/// <summary>What tune_memory asks for: the card, its curve (the trials write it back after each P0 write), the daily memory
/// offset, and the steps.</summary>
/// <summary>What tune_memory asks for. CUDA work alone ran in P1 on the reference card, also with Boost Lock held (2026-10-21),
/// and the memory offset is a P0 offset: each step checks the memory clock rose with the offset, so an offset that is not in
/// force while the lab runs stops the sweep instead of misleading it.</summary>
public sealed record SweepSpec(string StableId, string Pci, string Curve, int DailyMhz, int FromMhz, int ToMhz, int StepMhz,
    int Seconds = 60, int WarmupSeconds = 60, int GraceSeconds = 45);

/// <summary>One step of the sweep: the offset, how the lab judged it, and the numbers the stop rules read.</summary>
public sealed record SweepStep(int OffsetMhz, string Verdict, double? Gbps, double? LatencyNs, double? MemMhz, double? TempP50,
    double? TempMax, double? MaxCvPct, IReadOnlyList<PatternResult>? Patterns, long Errors, int? DriverResets, string? Signal, string? Detail,
    string? PState = null, double? BusyBeforePct = null, ulong? OthersVramMib = null, bool Shared = false);

public sealed record SweepStatus(string Job, string State, DateTime StartedUtc, DateTime UpdatedUtc, int? AtMhz, ulong? BufferMib,
    IReadOnlyList<SweepStep> Steps, int? BestMhz, string? Why, string? Next)
{
    public const string Running = "running", Done = "done", Stopped = "stopped", Failed = "failed";
}

/// <summary>
/// ST02, the memory sweep: raise the memory offset a step at a time, each as a trial through the service, and measure each
/// with the lab at the same buffer: bit errors and chain errors, bandwidth in five access orders, latency. GDDR7 hides its
/// errors by retrying them (EDR): past its edge the memory does not fail, it gets slower, first in latency, then in
/// bandwidth. So a step stops the sweep when it fails, or when it is slower than the best step so far
/// (<see cref="Signal"/>); the best offset is the step before that. It runs in a process of its own with no GPU library
/// loaded: each measurement is a child lab process, so a hang or a driver reset ends a measurement, not the sweep.
/// </summary>
public sealed class MemorySweep(SweepSpec spec, string jobDir, string labExe, Action<string>? log = null)
    : LabJob(spec.StableId, spec.Pci, spec.GraceSeconds, jobDir, labExe)
{
    /// <summary>Bandwidth under the best step so far by more than this (0.5 %): the memory is retrying. It need not rise: on the
    /// reference card +0/+179/+358 gave 1437/1440/1439 GB/s, the crossbar and not the DRAM setting the rate (XBAR +250 gave
    /// 1455 → 1474), so a rule that wanted bandwidth to follow the memory clock would stop a healthy sweep.</summary>
    public const double BandwidthFall = 0.005;
    /// <summary>Latency over the lowest step so far by more than this (1 %): a retried burst costs a round trip first. Latency
    /// is the signal that follows the DRAM: 531 → 518 ns (-2.4 %) for +0 → +358 on the reference card.</summary>
    public const double LatencyRise = 0.01;

    private readonly List<SweepStep> _steps = [];
    private readonly DateTime _started = DateTime.UtcNow;
    private ulong? _bufferMib;

    /// <summary>Why the last step stops the sweep, or null to go on: it failed (a bad bit, a wrong chain, a crash), it could not
    /// be judged (a shared GPU, an offset not in force), or it is slower than the best step so far (latency up, bandwidth down).</summary>
    public static string? Signal(IReadOnlyList<SweepStep> steps)
    {
        var last = steps[^1];
        if (last.Verdict != LabResult.Pass) return last.Detail ?? $"the step {last.Verdict}";
        if (last.Shared)
            return Inv($"other work shared the GPU before the step ({last.OthersVramMib} MiB of video memory, {last.BusyBeforePct:0} % busy): its speed is not the memory's alone; close it and run again");
        if (steps.Count < 2) return null;
        var prev = steps[^2];
        // NVML's memory clock follows the offset 1:1 (13801 → 14159 MHz at +358 on the reference card). One that did not move
        // means the offset is not in force while the lab runs (a P-state without it): nothing after can be judged.
        if (last.OffsetMhz > prev.OffsetMhz && last.MemMhz is { } mm && prev.MemMhz is { } pm && mm - pm < (last.OffsetMhz - prev.OffsetMhz) / 2.0)
            return Inv($"the memory clock did not follow the offset ({pm:0} → {mm:0} MHz for {last.OffsetMhz - prev.OffsetMhz:+0} MHz, {last.PState ?? "P?"}): the offset is not in force while the lab runs");
        var before = steps.Take(steps.Count - 1).ToList();
        if (last.Gbps is { } g && before.Max(s => s.Gbps) is { } bg && g < bg * (1 - BandwidthFall))
            return Inv($"bandwidth fell to {g:0.#} GB/s, {(g / bg - 1) * 100:+0.0;-0.0} % under the best step's {bg:0.#}: the memory is retrying");
        if (last.LatencyNs is { } l && before.Min(s => s.LatencyNs) is { } bl && l > bl * (1 + LatencyRise))
            return Inv($"latency rose to {l:0.#} ns, {(l / bl - 1) * 100:+0.0} % over the lowest step's {bl:0.#}: the memory is retrying");
        return null;
    }

    /// <summary>The highest offset to keep: the step before the one that stopped the sweep, or the last when none did.</summary>
    public static int? Best(IReadOnlyList<SweepStep> steps)
    {
        if (steps.Count == 0) return null;
        int stop = -1;
        for (int i = 0; i < steps.Count; i++)
            if (Signal(steps.Take(i + 1).ToList()) != null) { stop = i; break; }
        return stop < 0 ? steps[^1].OffsetMhz : stop > 0 ? steps[stop - 1].OffsetMhz : null;
    }

    public static SweepStep Step(int offsetMhz, LabResult r, int? resets)
    {
        var mem = r.Phases.FirstOrDefault(p => p.Kind == LabPhase.Memory);
        var lat = r.Phases.FirstOrDefault(p => p.Kind == LabPhase.Latency);
        long errors = r.Phases.Sum(p => p.MemoryErrors + p.ChainErrors + p.ComputeMismatches);
        bool failed = r.Verdict == LabResult.Fail || resets > 0;
        string? detail = resets > 0 ? $"a driver reset during the step ({resets})" : r.Reasons.Count > 0 ? string.Join("; ", r.Reasons) : null;
        return new(offsetMhz, failed ? LabResult.Fail : r.Verdict, mem?.Gbps, lat?.LatencyNs, mem?.MemMhzP50, mem?.TempP50,
            r.Phases.Max(p => p.TempMax), mem?.Patterns?.Max(p => p.CvPct), mem?.Patterns, errors, resets, null, detail, mem?.PState, r.BusyBeforePct, r.OthersVramMib, r.Shared);
    }

    public int Run()
    {
        using var heartbeat = Heartbeat();
        try
        {
            var show = Broker("show");
            if (show is not { Ok: true, Result: { } res } || !res.TryGetProperty("experiment", out var exp) || exp.ValueKind != JsonValueKind.Object)
                return Finish(SweepStatus.Failed, "no experiment session is open: begin_session first", null);
            if (exp.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object)
                return Finish(SweepStatus.Failed, "a trial is in force: back_to_daily (or keep_as_daily) first, so the sweep starts from the daily profile", null);

            Save(SweepStatus.Running, null, null, $"warming the memory for {spec.WarmupSeconds} s at the daily offset");
            var warm = Measure(spec.WarmupSeconds, "memory", null);
            if (warm.Verdict != LabResult.Pass)
                return Finish(SweepStatus.Failed, $"the warm-up at the daily offset ({spec.DailyMhz:+0;-0;0} MHz) did not pass: {string.Join("; ", warm.Reasons)}", null);
            _bufferMib = warm.BufferMib;

            for (int offset = spec.FromMhz; offset <= spec.ToMhz; offset += spec.StepMhz)
            {
                if (StopAsked) return Finish(SweepStatus.Stopped, "stopped (lab_stop)", null);
                bool trial = offset != spec.DailyMhz;
                if (trial)
                {
                    var w = Try(offset);
                    if (w is not { Ok: true }) return Finish(SweepStatus.Failed, $"the service did not write {offset:+0;-0;0} MHz: {w?.Error ?? "no answer"}", offset);
                    Thread.Sleep(3000);
                }
                Save(SweepStatus.Running, offset, null, $"measuring {offset:+0;-0;0} MHz for {spec.Seconds} s");
                var started = DateTime.UtcNow;
                var r = Measure(spec.Seconds, "memory,latency", _bufferMib);
                int? resets = OperatingSystem.IsWindows() ? TryCount(started) : null;
                var step = Step(offset, r, resets);
                _steps.Add(step);
                string? signal = Signal(_steps);
                _steps[^1] = step with { Signal = signal };
                File.AppendAllText(Path.Combine(JobDir, "steps.jsonl"), JsonSerializer.Serialize(_steps[^1], JsonLine) + "\n");
                string line = Line(_steps[^1]);
                log?.Invoke(line);
                Broker("note", $"tune_memory: {line}");
                if (step.Verdict == LabResult.Fail)
                {
                    // A driver reset is the service's to record; a bad bit with the GPU alive is recorded here.
                    if (trial && step.DriverResets is not > 0) Broker("crash", $"tune_memory {offset:+0;-0;0} MHz: {step.Detail}");
                    return Finish(SweepStatus.Done, $"{offset:+0;-0;0} MHz failed: {step.Detail}", offset);
                }
                if (trial) Broker("stable", $"tune_memory: {line}");
                // Shared GPU or an offset not in force: the step says nothing about the memory, so there is no result.
                bool unjudged = step.Shared || signal?.Contains("not in force", StringComparison.Ordinal) == true;
                if (signal != null) return Finish(unjudged ? SweepStatus.Failed : SweepStatus.Done, $"{offset:+0;-0;0} MHz: {signal}", offset);
            }
            return Finish(SweepStatus.Done, $"reached {spec.ToMhz:+0;-0;0} MHz with nothing slower", null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Finish(SweepStatus.Failed, $"the sweep stopped: {e.GetType().Name}: {e.Message}", null);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? TryCount(DateTime since)
    {
        try { return SystemLog.Count(SystemLog.DriverResetFilter, since.AddSeconds(-2)); } catch (InvalidOperationException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string Line(SweepStep s) => Inv($"{s.OffsetMhz:+0;-0;0} MHz: {s.Verdict}") +
        (s.Gbps is { } g ? Inv($", {g:0.#} GB/s linear") : "") + (s.LatencyNs is { } l ? Inv($", {l:0.#} ns") : "") +
        (s.MemMhz is { } m ? Inv($", memory {m:0} MHz") : "") + (s.TempP50 is { } t ? Inv($", {t:0} °C") : "") +
        (s.MaxCvPct is { } cv ? Inv($", spread {cv:0.##} %") : "") + (s.PState != null ? $", {s.PState}" : "") + (s.Errors > 0 ? $", {s.Errors} errors" : "") +
        (s.Signal != null ? $" — {s.Signal}" : "");

    private int Finish(string state, string why, int? at)
    {
        // The daily offset goes back on whatever happened (after a driver reset the service has done it already).
        if (_steps.Any(s => s.OffsetMhz != spec.DailyMhz)) Broker("apply", "tune_memory finished", BrokerRequest.OpProfile);
        int? best = Best(_steps);
        string? next = state == SweepStatus.Done && best is { } b
            ? Inv($"best {b:+0;-0;0} MHz: tell the person, then set_memory_offset to it in steps of at most 250 MHz, play their games, and keep_as_daily (ask); if a game fails, one step lower")
            : null;
        Save(state, at, why, next, best);
        log?.Invoke($"{state}: {why}");
        return state == SweepStatus.Failed ? 1 : 0;
    }

    private void Save(string state, int? at, string? why, string? next, int? best = null)
    {
        var s = new SweepStatus(Path.GetFileName(JobDir), state, _started, DateTime.UtcNow, at, _bufferMib, _steps, best ?? Best(_steps), why, next);
        SaveStatus(s);
    }

    private BrokerResponse? Try(int offsetMhz)
    {
        var args = new Dictionary<string, object?>
        {
            ["curve"] = spec.Curve, ["change"] = Inv($"memory offset → {offsetMhz:+0;-0;0} MHz (tune_memory)"), ["keep_curve"] = true,
            ["memory_khz"] = offsetMhz * 1000, ["context"] = new[] { "tune_memory: the lab measures each step" },
        };
        return Broker("try", JsonSerializer.Serialize(args));
    }

}

public static class LabJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
