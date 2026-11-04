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
/// Trend: scan through performance dips; a failed or unjudgeable measurement still ends the scan. The scan boundary and
/// the independently compared recommendation are separate status fields.
public sealed record SweepSpec(string StableId, string Pci, string Curve, int DailyMhz, int FromMhz, int ToMhz, int StepMhz,
    int Seconds = 60, int WarmupSeconds = 60, int GraceSeconds = 45, bool Trend = false);

/// <summary>One step of the sweep: the offset, how the lab judged it, and the numbers the stop rules read.</summary>
public sealed record SweepStep(int OffsetMhz, string Verdict, double? Gbps, double? LatencyNs, double? MemMhz, double? TempP50,
    double? TempMax, double? MaxCvPct, IReadOnlyList<PatternResult>? Patterns, long Errors, int? DriverResets, string? Signal, string? Detail,
    string? PState = null, double? BusyBeforePct = null, ulong? OthersVramMib = null, bool Shared = false, string? Watch = null);

public sealed record SweepStatus(string Job, string State, DateTime StartedUtc, DateTime UpdatedUtc, int? AtMhz, ulong? BufferMib,
    IReadOnlyList<SweepStep> Steps, int? BestMhz, string? Why, string? Next, int? EdgeMhz = null, int? CandidateMhz = null,
    IReadOnlyList<MemoryComparison>? Comparisons = null, bool Confirmed = false)
{
    public const string Running = "running", Done = "done", Stopped = "stopped", Failed = "failed";
}

/// <summary>
/// ST02, the memory sweep: raise the memory offset a step at a time, each as a trial through the service, and measure each
/// with the lab at the same buffer: bit errors and chain errors, bandwidth in five access orders, latency. GDDR7 hides its
/// errors by retrying them (EDR): past its edge the memory does not fail, it gets slower, first in latency, then in
/// bandwidth. So a step stops the sweep when it fails, or when it is slower and the step before was already slower the
/// same way (<see cref="Edge"/>): one slower step is watched, not trusted, because the memory's timings change at some
/// clocks and a single run can be noise. A suspected slowdown is rechecked; a short list is compared and confirmed before
/// naming a recommendation, separately from the scan boundary. It runs in a process of its own with no GPU library
/// loaded: each measurement is a child lab process, so a hang or a driver reset ends a measurement, not the sweep.
/// </summary>
public class MemorySweep(SweepSpec spec, string jobDir, string labExe, Action<string>? log = null)
    : LabJob(spec.StableId, spec.Pci, spec.GraceSeconds, jobDir, labExe)
{
    /// <summary>Bandwidth under the best step before it by more than this (0.5 %), at two steps in a row: the memory got
    /// slower (error retries past its edge, or a warmer card: the words name both). It need not rise: on the reference card +0/+179/+358 gave 1437/1440/1439 GB/s, the crossbar and not the DRAM
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
    private readonly List<SweepStep> _effective = [];
    private readonly List<MemoryComparison> _comparisons = [];
    private int? _best;
    private bool _confirmed;
    private bool _confirmationAttempted;
    /// <summary>Why the candidates must not be compared (a failure not on record), or null.</summary>
    private string? _noConfirmation;
    /// <summary>The scan put no step ahead of the daily offset: it stands with nothing compared.</summary>
    private bool _dailyUnbeaten;
    /// <summary>The warm-up at the daily offset: the memory clock the first step is checked against.</summary>
    private (int OffsetMhz, double MemMhz)? _warm;
    private bool _wrote;
    private readonly DateTime _started = DateTime.UtcNow;
    private ulong? _bufferMib;

    /// <summary>Why the last step stops the sweep, and whether the slowdown began at the step before it; null to go on. It stops
    /// when the step failed (a bad bit, a wrong chain, a crash), could not be judged (a shared GPU, an offset not in force),
    /// or is slower the way the step before already was: bandwidth under the best step before it twice in a row, or latency
    /// over the lowest and rising again after a step that rose too. One slower step alone is <see cref="Watch"/>ed.
    /// <paramref name="warm"/>: the warm-up's offset and memory clock, which the first step's clock must follow as each
    /// later step's follows the one before (a fine sweep starts away from the daily offset, and its first step was marked
    /// stable with nothing to check it against).</summary>
    public static Stop? Edge(IReadOnlyList<SweepStep> steps, (int OffsetMhz, double MemMhz)? warm = null)
    {
        var last = steps[^1];
        if (last.Verdict != LabResult.Pass) return new(last.Detail ?? $"the step {last.Verdict}", false, Unjudged: last.Verdict != LabResult.Fail);
        if (last.Shared)
            return new(Inv($"other work kept sharing the GPU at this step for {SharedWaitMinutes} minutes ({last.OthersVramMib} MiB of video memory, {last.BusyBeforePct:0} % busy): its speed is not the memory's alone; close it (a screen saver too) and run again"), false, Unjudged: true);
        if (!PairedMetric.Valid(last.MemMhz) || !PairedMetric.Valid(last.Gbps) || !PairedMetric.Valid(last.LatencyNs))
            return new("the measured memory clock, bandwidth or latency is missing: the step cannot establish that the offset ran", false, Unjudged: true);
        if (steps.Count < 2)
            return warm is { } w && InForce(w.OffsetMhz, w.MemMhz, last) is { } first ? new(first, false, Unjudged: true) : null;
        var prev = steps[^2];
        if (InForce(prev.OffsetMhz, prev.MemMhz, last) is { } why) return new(why, false, Unjudged: true);
        int n = steps.Count - 1;
        if (Under(steps, n) is { } u && Under(steps, n - 1) is { } u0)
            return new(Inv($"bandwidth stayed under the best step's: {u0.Gbps:0.#} then {u.Gbps:0.#} GB/s ({u0.Pct:+0.0;-0.0} %, {u.Pct:+0.0;-0.0} %){Warmer(u.FromC, last.TempP50)}: slower two steps running. {SlowerWhy}"), true,
                AgainstMhz: steps.Take(n).First(s => s.Gbps == u.Best).OffsetMhz);
        if (Over(steps, n) is { } o && Rising(steps, n) && (Over(steps, n - 1) != null || Rising(steps, n - 1)))
            return new(Inv($"latency rose again to {o.Ns:0.#} ns, {o.Pct:+0.0} % over the lowest step's {o.Lowest:0.#}, after rising at {prev.OffsetMhz:+0;-0;0} MHz too{Warmer(o.FromC, last.TempP50)}: a timing change rises once and then falls with the clock; this kept rising. {SlowerWhy}"), true,
                AgainstMhz: steps.Take(n).First(s => s.LatencyNs == o.Lowest).OffsetMhz);
        return null;
    }

    /// <summary>What a slowdown that lasted can be: the sweep measured speed, not its cause. The steps run back to back, so the
    /// card is warmer at each, and heat slows the memory too.</summary>
    private const string SlowerWhy = "Past GDDR7's edge that is error retries (EDR), but a warmer card reads the same: run lab_test memory at the best step's offset again to tell";

    /// <summary>The GPU's temperature at the step compared with and at this one, for the words; empty when either is unknown.</summary>
    private static string Warmer(double? fromC, double? toC) => fromC is { } a && toC is { } b ? Inv($", GPU {a:0} → {b:0} °C") : "";

    /// <summary>Why a step's offset was not in force: NVML's memory clock follows the offset 1:1 (13801 → 14159 MHz at +358 on
    /// the reference card), so a clock that rose less than half the step from where it was means the offset is not in force
    /// while the lab runs (a P-state without it), and nothing after can be judged. Missing clocks cannot establish that it ran.</summary>
    public static string? InForce(int fromMhz, double? fromClock, SweepStep last) =>
        !PairedMetric.Valid(fromClock) || !PairedMetric.Valid(last.MemMhz) ? "the actual memory clock could not be read: whether the offset ran is unknown"
        : last.OffsetMhz != fromMhz && last.MemMhz is { } mm && fromClock is { } pm
            && (mm - pm) * Math.Sign((long)last.OffsetMhz - fromMhz) < Math.Abs((long)last.OffsetMhz - fromMhz) / 2.0
            ? Inv($"the memory clock did not follow the offset ({pm:0} → {mm:0} MHz for {last.OffsetMhz - fromMhz:+0} MHz, {last.PState ?? "P?"}): the offset is not in force while the lab runs")
            : null;

    /// <summary>Why the sweep stops at a step; <paramref name="Unjudged"/>: the step says nothing about the memory (it could
    /// not be measured, other work shared the GPU, the offset was not in force), as a value the sweep acts on, not words.
    /// <paramref name="AgainstMhz"/>: for a slowdown, the step it was measured against (the best bandwidth or the lowest
    /// latency before it), which is what a recheck must measure again: a lucky reading there is what makes later steps
    /// look slower.</summary>
    public sealed record Stop(string Why, bool BeganAtStepBefore, bool Unjudged = false, int? AgainstMhz = null);

    /// <summary>The last step's stop reason, or null to go on.</summary>
    public static string? Signal(IReadOnlyList<SweepStep> steps) => Edge(steps)?.Why;

    public enum StepEnd { Crash, Unjudged, Stable, StableThenStop }

    /// <summary>What a measured step leads to, decided before anything is recorded: a failure is its trial's crash and ends
    /// the sweep; a step that says nothing about the memory ends it with no result and marks nothing; any other is its trial's
    /// stable point, and then a slowdown ends the sweep unless it runs for the trend. A stable point is what a later agent
    /// reads as the offset proved: in 0.3.3 a step whose offset was not in force was marked stable first and found unjudged
    /// after.</summary>
    public static StepEnd End(SweepStep step, Stop? edge, bool trend) =>
        step.Verdict == LabResult.Fail ? StepEnd.Crash
        : step.Verdict != LabResult.Pass || edge is { Unjudged: true } ? StepEnd.Unjudged
        : edge != null && !trend ? StepEnd.StableThenStop
        : StepEnd.Stable;

    /// <summary>A slower step that does not stop the sweep on its own: bandwidth under the best once, or a latency jump (a timing
    /// change the next step shows falling again, or the start of retrying the next step shows rising). Null when neither.</summary>
    public static string? Watch(IReadOnlyList<SweepStep> steps)
    {
        int n = steps.Count - 1;
        if (n < 1 || Edge(steps) != null) return null;
        if (Under(steps, n) is { } u) return Inv($"bandwidth {u.Pct:+0.0;-0.0} % under the best step's {u.Best:0.#} GB/s{Warmer(u.FromC, steps[n].TempP50)}, once: noise, a warmer card or the start of error retries; the next step says whether it lasts");
        if (Over(steps, n) is { } o && Rising(steps, n))
            return Inv($"latency {o.Pct:+0.0} % over the lowest step's {o.Lowest:0.#} ns{Warmer(o.FromC, steps[n].TempP50)}, once: a timing change falls with the clock at the next step; error retries or a warming card keep it rising");
        return null;
    }

    /// <summary>Bandwidth under the best step before <paramref name="i"/>: this step's, the best, the change, and the best step's temperature.</summary>
    private static (double Gbps, double Best, double Pct, double? FromC)? Under(IReadOnlyList<SweepStep> steps, int i)
    {
        if (i < 1 || steps[i].Gbps is not { } g || steps.Take(i).Max(s => s.Gbps) is not { } best || g >= best * (1 - BandwidthFall)) return null;
        return (g, best, (g / best - 1) * 100, steps.Take(i).First(s => s.Gbps == best).TempP50);
    }

    /// <summary>Latency over the lowest step before <paramref name="i"/>: this step's, the lowest, the change, and the lowest step's temperature.</summary>
    private static (double Ns, double Lowest, double Pct, double? FromC)? Over(IReadOnlyList<SweepStep> steps, int i)
    {
        if (i < 1 || steps[i].LatencyNs is not { } l || steps.Take(i).Min(s => s.LatencyNs) is not { } low || l <= low * (1 + LatencyRise)) return null;
        return (l, low, (l / low - 1) * 100, steps.Take(i).First(s => s.LatencyNs == low).TempP50);
    }

    private static bool Rising(IReadOnlyList<SweepStep> steps, int i) =>
        i >= 1 && steps[i].LatencyNs is { } l && steps[i - 1].LatencyNs is { } p && l > p * (1 + LatencyNoise);

    /// <summary>The scan-side boundary candidate (legacy helper name): the step before the first scan edge, or the last
    /// when no edge was seen. Reported as edge_mhz, never used by itself as the final recommendation.</summary>
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

    /// <summary>Whether the first stop in the steps was a slowdown that lasted (the two-step rules), not a failure or a step
    /// that could not be judged: the best offset then rests on speed alone, which a warmer card lowers too.</summary>
    public static bool SlowedDown(IReadOnlyList<SweepStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
            if (Edge(steps.Take(i + 1).ToList()) is { } e) return e.BeganAtStepBefore;
        return false;
    }

    /// <summary>What the next words say of a slowdown that lasted (the two-step rules), with <paramref name="best"/> the
    /// edge's side: when it ended the sweep (its last step, not for the trend), that a lab_test memory at the best offset as
    /// slow as the stopping step's means heat; for the trend, which runs on past a slowdown, where it began. Empty when no
    /// slowdown came first, or when a slowdown did not end a sweep that is not for the trend.</summary>
    /// <summary>Why the service did not record a step's result, or null when it did (or did not answer: a lost answer says
    /// nothing either way, as before). A refusal is the service's word that the trial in force is not the step's.</summary>
    public static string? Refused(BrokerResponse? r) => r is { Ok: false } ? r.Error ?? r.Code ?? "refused" : null;

    public static string SlowdownWords(IReadOnlyList<SweepStep> steps, bool trend, int best)
    {
        for (int i = 0; i < steps.Count; i++)
            if (Edge(steps.Take(i + 1).ToList()) is { } e)
            {
                if (!e.BeganAtStepBefore) return "";
                if (trend)
                    return Inv($"A slowdown that lasted began at {steps[i - 1].OffsetMhz:+0;-0;0} MHz (the trend ran on past it), and a warmer card slows the memory as error retries do: the steps ran back to back, so read their temperatures beside their speed. ");
                return i == steps.Count - 1
                    ? Inv($"A slowdown stopped it, and a warmer card slows the memory as error retries do: at {best:+0;-0;0} MHz, a lab_test memory as slow as the stopping step's means heat, not the edge. ")
                    : "";
            }
        return "";
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
            LabResult warm;
            using (var lease = Hold(log))
            {
                if (lease == null) return Finish(StopAsked ? SweepStatus.Stopped : SweepStatus.Failed, "the baseline could not acquire the measurement lease", null);
                var baseline = TrialAt(out bool answered);
                if (!answered || baseline != null)
                    return Finish(SweepStatus.Failed, "the daily baseline could not be verified before warm-up; no offsets were tested", null);
                warm = MeasureAlone(spec.WarmupSeconds, "memory", null, log);
                if (!SameTrial(TrialAt(out answered), baseline) || !answered)
                    return Finish(SweepStatus.Failed, "the baseline changed during warm-up; no offsets were tested", null);
            }
            if (StopAsked) return Finish(SweepStatus.Stopped, "stopped (lab_stop)", null);
            if (warm.Verdict != LabResult.Pass)
                return Finish(SweepStatus.Failed, $"the warm-up at the daily offset ({spec.DailyMhz:+0;-0;0} MHz) did not pass: {string.Join("; ", warm.Reasons)}", null);
            if (warm.Shared || warm.Phases.FirstOrDefault(p => p.Kind == LabPhase.Memory) is not { MemMhzP50: > 0, Gbps: > 0 } wm
                || !PairedMetric.Valid(wm.MemMhzP50) || !PairedMetric.Valid(wm.Gbps))
                return Finish(SweepStatus.Failed, "the warm-up lacks an unshared, measured memory clock and bandwidth: no offsets were tested", null);
            _bufferMib = warm.BufferMib;
            _warm = (spec.DailyMhz, wm.MemMhzP50!.Value);

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
                string? stamp, trialId;
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
                        if (w is not { Ok: true })
                            return Finish(SweepStatus.Failed, w == null
                                ? $"no answer from the service for {offset:+0;-0;0} MHz: it may have landed, so the daily profile goes back on"
                                : $"the service did not write {offset:+0;-0;0} MHz: {w.Error}", offset);
                        Settle();
                    }
                    stamp = TrialAt(out bool answered, out trialId);
                    if (!answered || trial && string.IsNullOrWhiteSpace(trialId))
                        return Finish(SweepStatus.Failed, "the service could not identify the trial to measure: no measurement started; update the service and rerun", offset);
                    // A step that writes nothing measures the daily profile: no trial may be in force then (one another
                    // agent wrote during the warm-up would be measured as this offset).
                    if (!trial && stamp != null)
                        return Finish(SweepStatus.Failed, "a trial this sweep did not write is in force: back_to_daily, then run it again", offset);
                    Save(SweepStatus.Running, offset, null, $"measuring {offset:+0;-0;0} MHz for {spec.Seconds} s");
                    started = DateTime.UtcNow;
                    r = MeasureAlone(spec.Seconds, "memory,latency", _bufferMib, log);
                    // lab_stop kills the measuring child: its run is inconclusive, and that is not the memory's failure.
                    if (StopAsked) return Finish(SweepStatus.Stopped, "stopped (lab_stop)", offset);
                    if (r.Verdict != LabResult.Fail && !SameTrial(TrialAt(), stamp))
                        return Finish(SweepStatus.Failed, $"{offset:+0;-0;0} MHz: the trial on the card changed during the measurement: the step is not this offset's", offset);
                }
                int? resets = CountResets(started);
                var step = Step(offset, r, resets);
                _steps.Add(step);
                _effective.Add(step);
                var edge = Edge(_effective, _warm);
                string? signal = edge?.Why;
                _steps[^1] = step with { Signal = signal, Watch = signal == null ? Watch(_effective) : null };
                File.AppendAllText(Path.Combine(JobDir, "steps.jsonl"), JsonSerializer.Serialize(_steps[^1], JsonLine) + "\n");
                string line = Line(_steps[^1]);
                log?.Invoke(line);
                Broker("note", $"tune_memory: {line}");
                var end = End(step, edge, spec.Trend);
                if (end == StepEnd.Crash)
                {
                    // A driver reset is the service's to record, and the daily offset goes back once it has; a bad bit with the
                    // GPU alive is recorded here.
                    // The job has let go of the card by now: the crash names the trial it measured, and the service records it
                    // only against that one (until 0.8.3 it went to whatever trial was in force, one written since too).
                    // The candidates are compared only once the failure is on record and the card is back: comparisons write
                    // new trials, and one written over an unrecorded crash (or a reset still settling) takes its blame.
                    if (trial && step.DriverResets is not > 0 && Refused(Broker("crash", $"tune_memory {offset:+0;-0;0} MHz: {step.Detail}", key: trialId)) is { } nc)
                    {
                        _noConfirmation = "the failure was not recorded against its trial";
                        return Finish(SweepStatus.Done, $"{offset:+0;-0;0} MHz failed: {step.Detail} (not recorded against a trial: {nc})", offset);
                    }
                    if (trial && step.DriverResets is > 0 && !CrashRecorded(stamp))
                        _noConfirmation = Inv($"the service had not recorded the driver reset within {CrashRecordWait.TotalSeconds:0} s");
                    return Finish(SweepStatus.Done, $"{offset:+0;-0;0} MHz failed: {step.Detail}", offset);
                }
                // Too hot, telemetry lost, the runner failing, a shared GPU, an offset not in force: the step says nothing
                // about the memory, so there is no result, nothing is blamed on the memory and nothing is marked stable.
                if (end == StepEnd.Unjudged)
                    return Finish(SweepStatus.Failed, step.Verdict != LabResult.Pass
                        ? $"{offset:+0;-0;0} MHz could not be judged ({step.Detail}): nothing said about the memory"
                        : $"{offset:+0;-0;0} MHz: {signal}", offset);
                // The same for a pass: a trial someone wrote since is not marked stable with it, and the sweep stops rather
                // than write its next step over that trial.
                if (trial && Refused(Broker("stable", $"tune_memory: {line}", key: trialId)) is { } ns)
                    return Finish(SweepStatus.Failed, $"{offset:+0;-0;0} MHz passed, but it was not marked stable: {ns}", offset);
                if (end == StepEnd.StableThenStop)
                {
                    // Measure again the step the slowdown was judged against: when its scan reading was lucky, the recheck
                    // corrects it and the steps after stop reading as slower (the step before the onset need not be it).
                    int reference = edge?.AgainstMhz ?? Best(_effective) ?? spec.DailyMhz;
                    var recheck = Compare("slowdown recheck", reference, offset);
                    if (recheck.Outcome == MemoryComparison.Unjudged)
                        return Finish(StopAsked ? SweepStatus.Stopped : SweepStatus.Failed, recheck.Why, offset);
                    MemoryComparisonRules.Recheck(_effective, recheck);
                    if (recheck.Outcome is MemoryComparison.Slower or MemoryComparison.Failed)
                        return Finish(SweepStatus.Done, $"{offset:+0;-0;0} MHz: {recheck.Why}", offset);
                    Broker("note", $"tune_memory: the scan's slowdown at {offset:+0;-0;0} MHz did not repeat in A B B A ({recheck.Outcome}); continuing");
                }
            }
            return Finish(SweepStatus.Done, $"reached {spec.ToMhz:+0;-0;0} MHz; scan readings and any paired rechecks are kept separately", null);
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

    protected virtual void Settle()
    {
        for (int i = 0; i < 3 && !StopAsked; i++) Thread.Sleep(1000);
    }

    protected virtual int? CountResets(DateTime since) => OperatingSystem.IsWindows() ? TryCount(since) : null;

    /// <summary>One comparison, with one retry for disturbed measurements. All writes, runs and result attribution use
    /// the same lease protocol as the scan. The measuring child has ended before settings or records change.</summary>
    private MemoryComparison Compare(string stage, int a, int b)
    {
        MemoryComparison Once()
        {
            var runs = new List<SweepStep>();
            string? stamp = null, trialId = null, problem = null;
            string outcome = MemoryComparison.Unjudged;
            bool fault = false, reset = false;
            int faultOffset = a;

            bool Write(int offset)
            {
                if (StopAsked) { problem = "stopped (lab_stop)"; return false; }
                var w = Try(offset);
                if (w is not { Ok: true }) { problem = $"the comparison could not write {offset:+0;-0;0} MHz: {w?.Error ?? "no answer"}"; return false; }
                Settle();
                stamp = TrialAt(out bool answered, out trialId);
                if (!answered || string.IsNullOrWhiteSpace(trialId)) { problem = "the comparison's trial identity could not be read"; return false; }
                return true;
            }

            bool Run(int offset)
            {
                if (StopAsked) { problem = "stopped (lab_stop)"; return false; }
                Save(SweepStatus.Running, offset, null, $"{stage}: A {a:+0;-0;0}, B {b:+0;-0;0} MHz; run {runs.Count + 1}/4");
                var started = DateTime.UtcNow;
                var r = MeasureAlone(spec.Seconds, "memory,latency", _bufferMib, log);
                var step = Step(offset, r, CountResets(started));
                runs.Add(step);
                if (StopAsked) { problem = "stopped (lab_stop)"; return false; }
                if (step.Verdict == LabResult.Fail)
                {
                    fault = true; reset = step.DriverResets > 0; faultOffset = offset;
                    outcome = offset == b ? MemoryComparison.Failed : MemoryComparison.Unjudged;
                    problem = $"{offset:+0;-0;0} MHz failed: {step.Detail}";
                    return false;
                }
                if (!SameTrial(TrialAt(), stamp)) { problem = "the trial changed during the comparison"; return false; }
                if (Edge([step]) is { } invalid) { problem = invalid.Why; return false; }
                if (_warm is not { } warm) { problem = "the comparison has no measured baseline clock"; return false; }
                if (InForce(warm.OffsetMhz, warm.MemMhz, step) is { } unknown) { problem = unknown; return false; }
                return true;
            }

            MemoryComparison? result = null;
            using (var lease = Hold(log))
            {
                if (lease == null) problem = StopAsked ? "stopped (lab_stop)" : "another run holds the card";
                else if (Write(a) && Run(a) && Write(b) && Run(b) && Run(b))
                {
                    // Both candidate runs are on this exact trial, and the real clock followed the offset in both.
                    var preliminary = InForce(runs[0].OffsetMhz, runs[0].MemMhz, runs[1])
                        ?? InForce(runs[0].OffsetMhz, runs[0].MemMhz, runs[2]);
                    if (preliminary != null) problem = preliminary;
                    else if (Refused(Broker("stable", $"tune_memory {stage}: two bit-checked runs at {b:+0;-0;0} MHz", key: trialId)) is { } refused)
                        problem = "the candidate's measured trial could not be recorded: " + refused;
                    else if (Write(a) && Run(a)) result = MemoryComparisonRules.Judge(stage, a, b, runs);
                }
            }
            // Release the measurement lease before a reset's recovery or a live fault's restoration.
            if (fault)
            {
                if (reset && !CrashRecorded(stamp))
                {
                    outcome = MemoryComparison.Unjudged;
                    problem += "; reset recovery has not been confirmed; no recommendation";
                }
                else if (!reset && Refused(Broker("crash", $"tune_memory {stage}: {faultOffset:+0;-0;0} MHz: {problem}", key: trialId)) is { } refused)
                {
                    outcome = MemoryComparison.Unjudged;
                    problem += "; the failing trial could not be recorded: " + refused;
                }
            }
            return result ?? new MemoryComparison(stage, a, b, outcome, problem ?? "the comparison did not finish", runs);
        }

        MemoryComparison? comparison = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            comparison = Once();
            _comparisons.Add(comparison);
            File.AppendAllText(Path.Combine(JobDir, "comparisons.jsonl"), JsonSerializer.Serialize(comparison, JsonLine) + "\n");
            string line = $"tune_memory {stage}: {b:+0;-0;0} vs {a:+0;-0;0} MHz: {comparison.Outcome}: {comparison.Why}";
            log?.Invoke(line);
            Broker("note", line);
            if (!comparison.Disturbed || StopAsked) break;
        }
        return comparison!;
    }

    private string? ConfirmCandidates()
    {
        var shortlist = Candidates();
        if (shortlist.Count == 0) return "no scan candidate had complete measured results";
        // The daily offset alone on the shortlist: no step measured ahead of it, and A B B A of a setting against itself
        // can only say "equivalent", or fail the job when a run is disturbed. It stays, with nothing written.
        if (shortlist.All(c => c == spec.DailyMhz))
        {
            (_best, _confirmed, _dailyUnbeaten) = (spec.DailyMhz, true, true);
            return null;
        }
        var compared = new List<MemoryComparison>();
        foreach (int candidate in shortlist.Where(c => c != spec.DailyMhz))
        {
            var c = Compare("candidate", spec.DailyMhz, candidate);
            compared.Add(c);
            if (c.Outcome == MemoryComparison.Unjudged) return c.Why;
        }
        _best = MemoryComparisonRules.Pick(spec.DailyMhz, compared);
        if (_best == null) return "none of the candidate comparisons could establish a recommendation";
        if (_best != spec.DailyMhz)
        {
            var confirm = Compare("confirm", spec.DailyMhz, _best.Value);
            if (confirm.Outcome is MemoryComparison.Unjudged or MemoryComparison.Failed) { _best = null; return confirm.Why; }
            _best = MemoryComparisonRules.Pick(spec.DailyMhz, [confirm]);
        }
        _confirmed = _best != null;
        return null;
    }

    private static readonly JsonSerializerOptions JsonLine = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string Line(SweepStep s) => Inv($"{s.OffsetMhz:+0;-0;0} MHz: {s.Verdict}") +
        (s.Gbps is { } g ? Inv($", {g:0.#} GB/s linear") : "") + (s.LatencyNs is { } l ? Inv($", {l:0.#} ns") : "") +
        (s.MemMhz is { } m ? Inv($", memory {m:0} MHz") : "") + (s.TempP50 is { } t ? Inv($", {t:0} °C") : "") +
        (s.MaxCvPct is { } cv ? Inv($", spread {cv:0.##} %") : "") + (s.PState != null ? $", {s.PState}" : "") + (s.Errors > 0 ? $", {s.Errors} errors" : "") +
        (s.Signal != null ? $" — {s.Signal}" : s.Watch != null ? $" — watching: {s.Watch}" : "");

    private int Finish(string state, string why, int? at)
    {
        if (state == SweepStatus.Done && !_confirmationAttempted && _effective.Count > 0)
        {
            _confirmationAttempted = true;
            if (_noConfirmation != null)
                why += "; no candidates were compared: " + _noConfirmation;
            else if (ConfirmCandidates() is { } unjudged)
            {
                _best = null;
                state = StopAsked ? SweepStatus.Stopped : SweepStatus.Failed;
                why += "; confirmation incomplete: " + unjudged;
            }
        }
        if (state != SweepStatus.Done) { _best = null; _confirmed = false; }
        // The daily offset goes back on whatever happened (after a driver reset the service has done it already), and the
        // status says so when it did not.
        string? recovery = _wrote ? DailyBack("tune_memory finished") : null;
        if (recovery != null)
        {
            why += "; " + recovery;
            state = SweepStatus.Failed;
            _best = null;
            _confirmed = false;
        }
        string? next = state == SweepStatus.Done && _confirmed && _best is { } b
            ? (_dailyUnbeaten
                ? Inv($"no scanned offset measured ahead of the daily {b:+0;-0;0} MHz, so it stays; nothing needed comparing. ")
                : Inv($"paired comparisons recommend {b:+0;-0;0} MHz; equivalent performance prefers a lower offset. "))
                + "The daily profile has been restored. Validate actual gains in a repeatable game or benchmark before keeping a candidate as daily. The scan boundary is reported separately as edge_mhz."
            : recovery != null ? "Recovery is incomplete: inspect the broker's answer and use back_to_daily before further tuning."
            : state == SweepStatus.Done && _noConfirmation != null
                ? "No recommendation: the failure must be on record before candidates are compared. back_to_daily, check the card (card_report), then run tune_memory again below the failing offset."
            : null;
        Save(state, at, why, next);
        log?.Invoke($"{state}: {why}");
        return state == SweepStatus.Failed ? 1 : 0;
    }

    private void Save(string state, int? at, string? why, string? next)
    {
        var candidates = Candidates();
        var s = new SweepStatus(Path.GetFileName(JobDir), state, _started, DateTime.UtcNow, at, _bufferMib, _steps,
            _confirmed ? _best : null, why, next, Best(_effective), candidates.Count > 0 ? candidates[0] : null, _comparisons, _confirmed);
        SaveStatus(s);
    }

    private IReadOnlyList<int> Candidates()
    {
        var failed = _comparisons.Where(c => c.Outcome == MemoryComparison.Failed).Select(c => c.B).ToHashSet();
        return MemoryComparisonRules.Shortlist(_effective.Where(s => !failed.Contains(s.OffsetMhz)).ToArray());
    }

    private BrokerResponse? Try(int offsetMhz)
    {
        var args = new Dictionary<string, object?>
        {
            ["curve"] = spec.Curve, ["change"] = Inv($"memory offset → {offsetMhz:+0;-0;0} MHz (tune_memory)"), ["keep_curve"] = true,
            ["memory_khz"] = offsetMhz * 1000, ["context"] = new[] { "tune_memory: the lab measures each step" },
        };
        // May be written from the moment it is sent, whether or not its step is ever recorded (a stop between the write and the
        // run) or its answer arrives (lost on the way, a half-landed trial whose put-back did not finish): the end puts the
        // daily profile back, which writes only what differs. Until 0.3.7 only an answer of success counted, so a write
        // whose answer was lost was left on the card.
        _wrote = true;
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
