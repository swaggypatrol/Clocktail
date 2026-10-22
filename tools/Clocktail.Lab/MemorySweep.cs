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
/// Trend: a slower step does not stop the sweep (only a failure, a shared GPU or an offset not in force does), so it shows
/// the whole curve of bandwidth and latency against the offset; the best is still the step before the first slower one.
public sealed record SweepSpec(string StableId, string Pci, string Curve, int DailyMhz, int FromMhz, int ToMhz, int StepMhz,
    int Seconds = 60, int WarmupSeconds = 60, int GraceSeconds = 45, bool Trend = false);

/// <summary>One step of the sweep: the offset, how the lab judged it, and the numbers the stop rules read.</summary>
public sealed record SweepStep(int OffsetMhz, string Verdict, double? Gbps, double? LatencyNs, double? MemMhz, double? TempP50,
    double? TempMax, double? MaxCvPct, IReadOnlyList<PatternResult>? Patterns, long Errors, int? DriverResets, string? Signal, string? Detail,
    string? PState = null, double? BusyBeforePct = null, ulong? OthersVramMib = null, bool Shared = false, string? Watch = null);

public sealed record SweepStatus(string Job, string State, DateTime StartedUtc, DateTime UpdatedUtc, int? AtMhz, ulong? BufferMib,
    IReadOnlyList<SweepStep> Steps, int? BestMhz, string? Why, string? Next)
{
    public const string Running = "running", Done = "done", Stopped = "stopped", Failed = "failed";
}

/// <summary>
/// ST02, the memory sweep: raise the memory offset a step at a time, each as a trial through the service, and measure each
/// with the lab at the same buffer: bit errors and chain errors, bandwidth in five access orders, latency. GDDR7 hides its
/// errors by retrying them (EDR): past its edge the memory does not fail, it gets slower, first in latency, then in
/// bandwidth. So a step stops the sweep when it fails, or when it is slower and the step before was already slower the
/// same way (<see cref="Edge"/>): one slower step is watched, not trusted, because the memory's timings change at some
/// clocks and a single run can be noise. The best offset is the step before the slowdown began. It runs in a process of its own with no GPU library
/// loaded: each measurement is a child lab process, so a hang or a driver reset ends a measurement, not the sweep.
/// </summary>
public sealed class MemorySweep(SweepSpec spec, string jobDir, string labExe, Action<string>? log = null)
    : LabJob(spec.StableId, spec.Pci, spec.GraceSeconds, jobDir, labExe)
{
    /// <summary>Bandwidth under the best step before it by more than this (0.5 %), at two steps in a row: the memory is
    /// retrying. It need not rise: on the reference card +0/+179/+358 gave 1437/1440/1439 GB/s, the crossbar and not the DRAM
    /// setting the rate (XBAR +250 gave 1455 → 1474), so a rule that wanted bandwidth to follow the memory clock would stop a
    /// healthy sweep. One step under is noise as often as not: +1558 read 1.2 % under +1358 and +1658 was the best yet.</summary>
    public const double BandwidthFall = 0.005;
    /// <summary>Latency over the lowest step before it by more than this (1 %), rising, after a step that rose too: a retried
    /// burst costs a round trip, and retries grow with the clock. Latency follows the DRAM: 531 → 518 ns (-2.4 %) for +0 →
    /// +358 on the reference card. A timing change rises once and then falls with the clock again: +858 → +958 read 460.6 →
    /// 477.7 ns there (+3.7 %), then 475.3, 472.3 ... 451.8 at +1958 with bandwidth climbing and no error.</summary>
    public const double LatencyRise = 0.01;
    /// <summary>A rise from the step before smaller than this (0.2 %, ~1 ns) is the chase's own spread, not a rise.</summary>
    public const double LatencyNoise = 0.002;

    private readonly List<SweepStep> _steps = [];
    private bool _wrote;
    private readonly DateTime _started = DateTime.UtcNow;
    private ulong? _bufferMib;

    /// <summary>Why the last step stops the sweep, and whether the slowdown began at the step before it; null to go on. It stops
    /// when the step failed (a bad bit, a wrong chain, a crash), could not be judged (a shared GPU, an offset not in force),
    /// or is slower the way the step before already was: bandwidth under the best step before it twice in a row, or latency
    /// over the lowest and rising again after a step that rose too. One slower step alone is <see cref="Watch"/>ed.</summary>
    public static Stop? Edge(IReadOnlyList<SweepStep> steps)
    {
        var last = steps[^1];
        if (last.Verdict != LabResult.Pass) return new(last.Detail ?? $"the step {last.Verdict}", false);
        if (last.Shared)
            return new(Inv($"other work kept sharing the GPU at this step for {SharedWaitMinutes} minutes ({last.OthersVramMib} MiB of video memory, {last.BusyBeforePct:0} % busy): its speed is not the memory's alone; close it (a screen saver too) and run again"), false);
        if (steps.Count < 2) return null;
        var prev = steps[^2];
        // NVML's memory clock follows the offset 1:1 (13801 → 14159 MHz at +358 on the reference card). One that did not move
        // means the offset is not in force while the lab runs (a P-state without it): nothing after can be judged.
        if (last.OffsetMhz > prev.OffsetMhz && last.MemMhz is { } mm && prev.MemMhz is { } pm && mm - pm < (last.OffsetMhz - prev.OffsetMhz) / 2.0)
            return new(Inv($"the memory clock did not follow the offset ({pm:0} → {mm:0} MHz for {last.OffsetMhz - prev.OffsetMhz:+0} MHz, {last.PState ?? "P?"}): the offset is not in force while the lab runs"), false);
        int n = steps.Count - 1;
        if (Under(steps, n) is { } u && Under(steps, n - 1) is { } u0)
            return new(Inv($"bandwidth stayed under the best step's: {u0.Gbps:0.#} then {u.Gbps:0.#} GB/s ({u0.Pct:+0.0;-0.0} %, {u.Pct:+0.0;-0.0} %): the memory is retrying"), true);
        if (Over(steps, n) is { } o && Rising(steps, n) && (Over(steps, n - 1) != null || Rising(steps, n - 1)))
            return new(Inv($"latency rose again to {o.Ns:0.#} ns, {o.Pct:+0.0} % over the lowest step's {o.Lowest:0.#}, after rising at {prev.OffsetMhz:+0;-0;0} MHz too: a timing change rises once and then falls with the clock; this kept rising, the memory is retrying"), true);
        return null;
    }

    public sealed record Stop(string Why, bool BeganAtStepBefore);

    /// <summary>The last step's stop reason, or null to go on.</summary>
    public static string? Signal(IReadOnlyList<SweepStep> steps) => Edge(steps)?.Why;

    /// <summary>A slower step that does not stop the sweep on its own: bandwidth under the best once, or a latency jump (a timing
    /// change the next step shows falling again, or the start of retrying the next step shows rising). Null when neither.</summary>
    public static string? Watch(IReadOnlyList<SweepStep> steps)
    {
        int n = steps.Count - 1;
        if (n < 1 || Edge(steps) != null) return null;
        if (Under(steps, n) is { } u) return Inv($"bandwidth {u.Pct:+0.0;-0.0} % under the best step's {u.Best:0.#} GB/s, once: noise or the start of retrying; the next step says which");
        if (Over(steps, n) is { } o && Rising(steps, n))
            return Inv($"latency {o.Pct:+0.0} % over the lowest step's {o.Lowest:0.#} ns, once: a timing change falls with the clock at the next step, retrying keeps rising");
        return null;
    }

    private static (double Gbps, double Best, double Pct)? Under(IReadOnlyList<SweepStep> steps, int i)
    {
        if (i < 1 || steps[i].Gbps is not { } g || steps.Take(i).Max(s => s.Gbps) is not { } best || g >= best * (1 - BandwidthFall)) return null;
        return (g, best, (g / best - 1) * 100);
    }

    private static (double Ns, double Lowest, double Pct)? Over(IReadOnlyList<SweepStep> steps, int i)
    {
        if (i < 1 || steps[i].LatencyNs is not { } l || steps.Take(i).Min(s => s.LatencyNs) is not { } low || l <= low * (1 + LatencyRise)) return null;
        return (l, low, (l / low - 1) * 100);
    }

    private static bool Rising(IReadOnlyList<SweepStep> steps, int i) =>
        i >= 1 && steps[i].LatencyNs is { } l && steps[i - 1].LatencyNs is { } p && l > p * (1 + LatencyNoise);

    /// <summary>The highest offset to keep: the step before the slowdown that stopped the sweep began (the stopping step's, or
    /// the one before it for the two-step rules), or the last when nothing stopped it.</summary>
    public static int? Best(IReadOnlyList<SweepStep> steps)
    {
        if (steps.Count == 0) return null;
        for (int i = 0; i < steps.Count; i++)
            if (Edge(steps.Take(i + 1).ToList()) is { } e)
            {
                int onset = e.BeganAtStepBefore ? i - 1 : i;
                return onset > 0 ? steps[onset - 1].OffsetMhz : null;
            }
        return steps[^1].OffsetMhz;
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
        using var card = ClaimCard();
        if (card == null) return Finish(SweepStatus.Failed, "another lab job is running on this card: lab_status, or lab_stop", null);
        try
        {
            var show = Broker("show");
            if (show is not { Ok: true, Result: { } res } || !res.TryGetProperty("experiment", out var exp) || exp.ValueKind != JsonValueKind.Object)
                return Finish(SweepStatus.Failed, "no experiment session is open: begin_session first", null);
            if (exp.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object)
                return Finish(SweepStatus.Failed, "a trial is in force: back_to_daily (or keep_as_daily) first, so the sweep starts from the daily profile", null);

            Save(SweepStatus.Running, null, null, $"warming the memory for {spec.WarmupSeconds} s at the daily offset");
            var warm = MeasureAlone(spec.WarmupSeconds, "memory", null, log);
            if (warm.Verdict != LabResult.Pass)
                return Finish(SweepStatus.Failed, $"the warm-up at the daily offset ({spec.DailyMhz:+0;-0;0} MHz) did not pass: {string.Join("; ", warm.Reasons)}", null);
            _bufferMib = warm.BufferMib;

            // The steps from FromMhz by StepMhz, and ToMhz itself when the steps do not land on it (+358 by 100 to +3000: +2958, +3000).
            var offsets = new List<int>();
            for (int o = spec.FromMhz; o <= spec.ToMhz; o += spec.StepMhz) offsets.Add(o);
            if (offsets.Count > 0 && offsets[^1] != spec.ToMhz) offsets.Add(spec.ToMhz);
            foreach (int offset in offsets)
            {
                if (StopAsked) return Finish(SweepStatus.Stopped, "stopped (lab_stop)", null);
                // Only the first step can lean on the daily offset being on the card: a sweep that starts under it reaches it
                // with the step before's trial still in force, so from then on every step is written, the daily value too.
                bool trial = offset != spec.DailyMhz || _steps.Count > 0;
                LabResult r;
                DateTime started;
                // The step's write and its measurement under this job's measurement lease: no other write lands between
                // them, and the trial on the card must still be the one written when the run ends.
                using (var lease = Hold(log))
                {
                    if (lease == null)
                        return StopAsked ? Finish(SweepStatus.Stopped, "stopped (lab_stop)", offset)
                            : Finish(SweepStatus.Failed, $"another run held the card for {LeaseWaitMinutes} minutes", offset);
                    if (trial)
                    {
                        var w = Try(offset);
                        if (w is not { Ok: true }) return Finish(SweepStatus.Failed, $"the service did not write {offset:+0;-0;0} MHz: {w?.Error ?? "no answer"}", offset);
                        Thread.Sleep(3000);
                    }
                    string? stamp = TrialAt();
                    // A step that writes nothing measures the daily profile: no trial may be in force then (one another
                    // agent wrote during the warm-up would be measured as this offset).
                    if (!trial && stamp != null)
                        return Finish(SweepStatus.Failed, "a trial this sweep did not write is in force: back_to_daily, then run it again", offset);
                    Save(SweepStatus.Running, offset, null, $"measuring {offset:+0;-0;0} MHz for {spec.Seconds} s");
                    started = DateTime.UtcNow;
                    r = MeasureAlone(spec.Seconds, "memory,latency", _bufferMib, log);
                    // lab_stop kills the measuring child: its run is inconclusive, and that is not the memory's failure.
                    if (StopAsked) return Finish(SweepStatus.Stopped, "stopped (lab_stop)", offset);
                    if (r.Verdict != LabResult.Fail && TrialAt() != stamp)
                        return Finish(SweepStatus.Failed, $"{offset:+0;-0;0} MHz: the trial on the card changed during the measurement: the step is not this offset's", offset);
                }
                int? resets = OperatingSystem.IsWindows() ? TryCount(started) : null;
                var step = Step(offset, r, resets);
                _steps.Add(step);
                string? signal = Signal(_steps);
                _steps[^1] = step with { Signal = signal, Watch = signal == null ? Watch(_steps) : null };
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
                // Too hot, telemetry lost, the runner failing: nothing was measured, and nothing is blamed on the memory.
                if (step.Verdict != LabResult.Pass)
                    return Finish(SweepStatus.Failed, $"{offset:+0;-0;0} MHz could not be judged ({step.Detail}): nothing said about the memory", offset);
                if (trial) Broker("stable", $"tune_memory: {line}");
                // Shared GPU or an offset not in force: the step says nothing about the memory, so there is no result.
                bool unjudged = step.Shared || signal?.Contains("not in force", StringComparison.Ordinal) == true;
                if (signal != null && (unjudged || !spec.Trend)) return Finish(unjudged ? SweepStatus.Failed : SweepStatus.Done, $"{offset:+0;-0;0} MHz: {signal}", offset);
            }
            return Finish(SweepStatus.Done, _steps.Any(s => s.Signal != null)
                ? Inv($"reached {spec.ToMhz:+0;-0;0} MHz (the trend: the sweep would have stopped at {_steps.First(s => s.Signal != null).OffsetMhz:+0;-0;0} MHz)")
                : _steps.Any(s => s.Watch != null)
                    ? Inv($"reached {spec.ToMhz:+0;-0;0} MHz with no slowdown that lasted (slower once at {string.Join(", ", _steps.Where(s => s.Watch != null).Select(s => Inv($"{s.OffsetMhz:+0;-0;0}")))} MHz, faster again after)")
                    : $"reached {spec.ToMhz:+0;-0;0} MHz with nothing slower", null);
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
        (s.Signal != null ? $" — {s.Signal}" : s.Watch != null ? $" — watching: {s.Watch}" : "");

    private int Finish(string state, string why, int? at)
    {
        // The daily offset goes back on whatever happened (after a driver reset the service has done it already), and the
        // status says so when it did not.
        if (_wrote && DailyBack("tune_memory finished") is { } notBack) why += "; " + notBack;
        int? best = Best(_steps);
        string? next = state == SweepStatus.Done && best is { } b
            ? Inv($"the edge's side is {b:+0;-0;0} MHz (the step before any slowdown that lasted). Read the steps for where bandwidth stops rising: past that an offset buys latency only, which frames did not feel on the reference card (3DMark Speedway and Steel Nomad at +2900 were no better than at +1758). Tell the person; set_memory_offset there in steps of at most 250 MHz, judge it against the daily offset with a memory-sensitive benchmark A B B A (a ray-traced one, as Speedway), their games have the last word, keep_as_daily (ask); if a game fails, step down")
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
        var r = Broker("try", JsonSerializer.Serialize(args));
        // Written, whether or not its step is ever recorded (a stop between the write and the run): the end puts it back.
        if (r is { Ok: true }) _wrote = true;
        return r;
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
