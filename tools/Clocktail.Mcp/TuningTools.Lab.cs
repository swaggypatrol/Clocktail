using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Lab;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// The lab (clocktail-lab.exe beside this server): StabilityLab's load engine, every result checked bit for bit, as a process
/// of its own. lab_test is one judged run, as stress_test is with nvgwls; tune_memory starts ST02's memory sweep, which runs
/// on after the agent goes away; lab_status and lab_stop follow and end it.
/// </summary>
public sealed partial class TuningTools
{
    public const int MaxLabSeconds = 600;
    private static readonly JsonSerializerOptions SnakeJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The lab: CLOCKTAIL_LAB_EXE, or clocktail-lab.exe beside this server (setup installs them together).</summary>
    public static string? LabExe() =>
        Environment.GetEnvironmentVariable("CLOCKTAIL_LAB_EXE") is { Length: > 0 } e && File.Exists(e) ? e
        : Path.Combine(AppContext.BaseDirectory, "clocktail-lab.exe") is var p && File.Exists(p) ? p : null;

    private string LabDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", card.StableId, "lab");

    /// <summary>One measurement's hold on the card (<see cref="HoldCard"/>): judge.lock, given back when disposed. Trial: the
    /// settings of the trial in force once the card was quiet (<see cref="TrialSettings"/>; null for none), the one the run
    /// measures; TrialId: that trial's identity (<see cref="TrialId"/>; null for none, or from a service that gives none);
    /// Known: the service could be asked then (one that could not be reached says nothing about it).</summary>
    private sealed class CardHold(string path, string runId, string? trial, string? trialId, bool known) : IDisposable
    {
        public string? Trial => trial;
        public string? TrialId => trialId;
        public bool Known => known;
        public void Dispose() => JudgeLock.Release(path, runId);
    }

    /// <summary>
    /// The card to one measurement (stress_test, lab_test): judge.lock taken, so no write but the run's own lands from
    /// here, then the service asked to finish what it is in the middle of
    /// (<see cref="Quiet"/>; the lock says the run waits so, JudgeLock.Info.Waits, so that operation finishes rather than
    /// stop part-way), and the trial in force then, to hold the result to. Until 0.3.7 stress_test took no lock at
    /// all, so another agent could change the card under it and its pass or crash went to whatever trial was in force at
    /// the end; lab_test took the lock but could start in the middle of a write. On a service from before 0.3.7 a run that
    /// held no lock then (<paramref name="olderToo"/> false: stress_test) still holds none: that service's fan guard (removed in
    /// 0.6.0) could not pass the lock, and an agent blocked in the run cannot watch the temperature.
    /// </summary>
    private CardHold HoldCard(string label, bool olderToo)
    {
        string runId = $"{label.Replace('_', '-')}-{Guid.NewGuid():n}", lockPath = JudgeLock.DefaultPath();
        if (!JudgeLock.TryAcquire(lockPath, new JudgeLock.Info(Environment.ProcessId, runId, label, DateTimeOffset.UtcNow, Waits: true), new SystemHost(),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), out var heldBy))
            throw new InvalidOperationException($"another run holds the card ({heldBy}): wait for it to finish (lab_status), or lab_stop");
        try
        {
            var quiet = Quiet(k => card.Broker(BrokerRequest.OpExperiment, k), QuietTries, Thread.Sleep, out bool older);
            if (older && !olderToo) JudgeLock.Release(lockPath, runId);
            return new CardHold(lockPath, runId, TrialSettings(quiet), TrialId(quiet), quiet.Code != BrokerCode.BrokerUnreachable);
        }
        catch { JudgeLock.Release(lockPath, runId); throw; }
    }

    /// <summary>
    /// A measurement that failed: a driver reset is the service's to record (it closes the trial with the flight recorder's
    /// voltage seconds after the event; recording it here too wrote a second entry, and the service's then blamed the daily
    /// profile), so that is waited for; otherwise it is recorded here, against the trial the run measured only, the one in
    /// force at its start (<paramref name="hold"/>), or the one at its end when the service could not be asked then: a trial
    /// with other settings now is not blamed. The crash's data point is on record even when the daily profile could not go
    /// back on yet (the card held busy): true then too, so nobody records it again.
    /// </summary>
    private bool RecordCrash(CardHold hold, int? resets, string what) =>
        RecordCrash((kind, value, key) => card.Broker(BrokerRequest.OpExperiment, kind, value, key), hold.Trial, hold.TrialId, hold.Known, resets, what, Thread.Sleep);

    /// <summary>
    /// <see cref="RecordCrash(CardHold, int?, string)"/> over the service's experiment op (<paramref name="experiment"/>: kind,
    /// value, key). With the measured trial's identity (<paramref name="measuredId"/>) the crash goes in one request that
    /// names it, and the service refuses it when another trial is in force: the run gave judge.lock back before this, so a
    /// look and then a record in two requests could blame a trial written in between. Without one (a service that gives
    /// trials none) the trial in force must have the measured settings (<see cref="TrialSettings"/>) and no identity.
    /// </summary>
    public static bool RecordCrash(Func<string, string?, string?, BrokerAnswer> experiment, string? measured, string? measuredId, bool known,
        int? resets, string what, Action<TimeSpan> sleep)
    {
        if (known && measured == null) return false;
        if (resets > 0 && TrialClosedWithin(() => TrialInForce(experiment("show", null, null)), ServiceRecordsWithin, sleep))
            return true;
        if (measuredId == null)
        {
            var show = experiment("show", null, null);
            if (TrialSettings(show) is not { } now || known && (now != measured || TrialId(show) != null)) return false;
        }
        var c = experiment("crash", what, measuredId);
        return c.Ok || c.Result is { } res && res.TryGetProperty("point", out var p) && p.ValueKind == JsonValueKind.Object;
    }

    [McpServerTool(Name = "lab_test", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Validate current settings by goal: core (default: burn, high-point solo and heavy/light leap), memory, uncore, all, aria or symphony. Each profile gives its key phases their own time. Advanced callers can choose custom phases instead. Verify the intended operating point actually ran; interrupted/shared or inconclusive measurements do not establish stability. It stops within 2 °C of NVIDIA's slowdown point, and at maxTempC when one is given.")]
    public string LabTest(
        [Description("Advanced custom phases instead of a profile: burn, light, pulse, solo, leap, memory, latency, l2. Bare lists split seconds evenly (default 60 s); phase:seconds lists give individual durations. Omit for the core profile.")] string? phases = null,
        [Description("Total run budget, up to 600 s. Omit for the profile's budget: core 150, memory 90, uncore 60, all 270, aria 180, symphony 330 s; for a phase:seconds schedule, its sum. Profiles can be lengthened; custom phases allow shorter screening runs, 10 s or more, at least 1 s per phase.")] int? seconds = null,
        [Description("Steer burn to this board power, W (default: no steering, every thread).")] double? targetW = null,
        [Description("Light: one thread in this many (default 64: 64 SMs busy).")] int lightDiv = 64,
        [Description("What this run is meant to show, for the log.")] string? note = null,
        [Description("Also stop the run if the GPU reaches this temperature, °C. Omitted: it stops only within 2 °C of NVIDIA's slowdown point.")] int? maxTempC = null,
        [Description("Test goal: core (default), memory, uncore, all, aria (C01) or symphony (Aria followed by core). Omit when giving custom phases.")] string? profile = null,
        [Description("Aria only: fraction of CUDA grid, 0.01..1 (default 1).")] double? intensity = null,
        [Description("Aria only: unsigned 64-bit hexadecimal input seed.")] string? seed = null) => Guard(() =>
    {
        var plan = LabTestPlan.Create(profile, phases, seconds);
        bool containsAria = plan.Stages.Any(s => s.Phase == "c01");
        if (!containsAria && (intensity != null || seed != null)) throw new ArgumentException("intensity and seed are for aria only");
        if (containsAria && targetW != null) throw new ArgumentException("targetW is not supported with aria");
        bool ariaOnly = containsAria && plan.Stages.Count == 1;
        return RunLabTest(plan, targetW, lightDiv, note, ariaOnly ? maxTempC ?? 85 : maxTempC,
            containsAria ? intensity ?? 1 : null, containsAria ? seed ?? "5eed5eed5eed5eed" : null);
    });

    [McpServerTool(Name = "c01_test", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Check current GPU settings with C01, a sustained FP32 FMA workload with 16 accumulators and an exact CPU oracle. Use intensity to shrink the CUDA grid while looking for higher measured clocks under a power constraint. Check measured voltage and frequency before using the result as evidence for a curve point.")]
    public string C01Test(
        [Description("Run budget in seconds, 10–600. Default 180; short runs can miss intermittent errors.")] int seconds = 180,
        [Description("Fraction of the full thread-block grid, 0.01–1. Default 1. Try 0.16–0.20 if the full grid is power limited, then compare measured clocks.")] double intensity = 1,
        [Description("Optional hexadecimal 64-bit input seed for a repeatable test.")] string seed = "5eed5eed5eed5eed",
        [Description("GPU temperature stop in °C. Default 85. The driver's slowdown margin also applies.")] int maxTempC = 85,
        [Description("Purpose of the test for the tuning log.")] string? note = null) => Guard(() =>
    {
        return RunLabTest(LabTestPlan.Create("aria", seconds: seconds) with { Profile = "c01" },
            null, 64, note, maxTempC, intensity, seed);
    });

    private static void ValidateAria(double intensity, string seed, int maxTempC)
    {
        if (intensity is < 0.01 or > 1 || !double.IsFinite(intensity))
            throw new ArgumentException("intensity must be in 0.01..1");
        if (maxTempC is < 40 or > 100)
            throw new ArgumentException("maxTempC must be in 40..100");
        if (seed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) seed = seed[2..];
        if (!ulong.TryParse(seed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("seed must be an unsigned 64-bit hexadecimal number");
    }

    private string RunLabTest(LabTestPlan plan, double? targetW, int lightDiv, string? note, int? maxTempC,
        double? c01Intensity = null, string? seed = null)
    {
        string exe = LabExe() ?? throw new InvalidOperationException("clocktail-lab.exe is not beside this server: run npx -y clocktail@latest setup");
        int duration = plan.Seconds;
        var list = plan.Stages.Select(s => s.Phase).ToList();
        if (duration < 10 || duration > MaxLabSeconds) throw new ArgumentException($"seconds is 10..{MaxLabSeconds}");
        if (lightDiv is < 2 or > 4096) throw new ArgumentException("lightDiv is 2..4096");
        if (targetW is { } tw && (!double.IsFinite(tw) || tw <= 0)) throw new ArgumentException("targetW must be positive and finite");
        int? maxC = MaxTemp(maxTempC);
        bool hasAria = plan.Stages.Any(s => s.Phase == "c01");
        bool aria = hasAria && plan.Stages.Count == 1;
        bool symphony = hasAria && !aria;
        if (hasAria) ValidateAria(c01Intensity ?? 1, seed ?? "5eed5eed5eed5eed", maxC ?? 85);

        string dir = Path.Combine(Path.GetTempPath(), "clocktail-lab");
        Directory.CreateDirectory(dir);
        var samples = new List<Card.Sample>();
        int hotRun = 0;
        double? stopC = null;
        string? hotWhy = null;
        int grace = (OperatingSystem.IsWindows() ? Clocktail.Domain.Platform.TdrSettings.Read() : null)?.StressGraceS ?? 30;
        int? code = null, memoryOffset = null;
        bool hot = false, hung = false;
        using var children = OperatingSystem.IsWindows() ? new KillOnCloseJob() : null;
        Process? p = null;
        JsonElement? lab = null;
        var hold = HoldCard(aria ? "c01_test" : "lab_test", olderToo: true);
        Card.Look before;
        var started = DateTime.UtcNow;
        try
        {
            before = card.Read();
            started = DateTime.UtcNow;
            residency.StressUntil(DateTimeOffset.Now.AddSeconds(duration + grace + (symphony ? 240 : aria ? 120 : 30)));

            SymphonyScore.MovementResult RunMovement(string command, int seconds, string? phases)
            {
                string path = Path.Combine(dir, $"run-{Guid.NewGuid():n}.json");
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { command, "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--json", path, "--gpu-pci", card.PciBusId })
                    psi.ArgumentList.Add(a);
                string[] args = command == "c01"
                    ? ["--intensity", (c01Intensity ?? 1).ToString(CultureInfo.InvariantCulture), "--seed", seed ?? "5eed5eed5eed5eed"]
                    : ["--phases", phases!, "--light-div", lightDiv.ToString(CultureInfo.InvariantCulture)];
                foreach (string arg in args) psi.ArgumentList.Add(arg);
                if (targetW is { } w) { psi.ArgumentList.Add("--target-w"); psi.ArgumentList.Add(w.ToString(CultureInfo.InvariantCulture)); }
                if (maxC is { } mc) { psi.ArgumentList.Add("--max-temp-c"); psi.ArgumentList.Add(mc.ToString(CultureInfo.InvariantCulture)); }
                try
                {
                    p = Process.Start(psi) ?? throw new InvalidOperationException("the lab did not start");
                    if (OperatingSystem.IsWindows() && children?.Add(p) != true)
                        throw new InvalidOperationException("could not contain the lab worker");
                    _ = p.StandardError.ReadToEndAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(seconds + grace + (command == "c01" ? 120 : 30));
                    while (!p.WaitForExit(500))
                    {
                        try
                        {
                            var sample = card.ReadSample();
                            samples.Add(sample);
                            hotRun = Nvgwls.TooHot(sample.Telemetry?.TemperatureC, sample.Telemetry?.TempMarginC, maxC) ? hotRun + 1 : 0;
                        }
                        catch (DriverBusyException) { }
                        if (hotRun >= 2) { hot = true; stopC = samples[^1].Telemetry?.TemperatureC; hotWhy = Nvgwls.HotWhy(stopC, maxC); }
                        if (DateTime.UtcNow > deadline) hung = true;
                        if (hot || hung) break;
                    }
                    int? exit = !hot && !hung ? p.ExitCode : null;
                    // Never start another movement or give back the card until the worker is actually gone.
                    if (!ChildProcess.Stop(p)) p.WaitForExit();
                    p.Dispose();
                    p = null;
                    JsonElement? report = null;
                    try { if (File.Exists(path)) report = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone(); }
                    catch (JsonException) { }
                    return new(exit, report, hot ? "temperature stop" : hung ? "movement timed out" : null);
                }
                finally { try { File.Delete(path); } catch (IOException) { } }
            }

            if (symphony)
            {
                var score = SymphonyScore.Perform(SymphonyScore.Movements(plan), m =>
                    RunMovement(m.Name == "aria" ? "c01" : "run", m.Seconds, m.Phases));
                code = score.Verdict switch { "pass" => 0, "fail" => 4, _ => 5 };
                lab = ScoreReport(score);
            }
            else
            {
                var result = RunMovement(aria ? "c01" : "run", duration, plan.PhaseArgument);
                code = result.ExitCode;
                lab = result.Report;
            }
            if (code != null && list.Contains("memory")) memoryOffset = MemoryOffsetMhz();
        }
        finally
        {
            if (p != null)
            {
                if (!ChildProcess.Stop(p)) p.WaitForExit();
                p.Dispose();
            }
            hold.Dispose();
            residency.StressUntil(DateTimeOffset.MinValue);
        }

        int? resets = Nvgwls.DriverResets(started, code is 0 or 4 or 5 ? 0 : 2);
        string labVerdict = lab is { } l && l.TryGetProperty("verdict", out var v) ? v.GetString() ?? "?" : "none";
        var reasons = lab is { } l2 && l2.TryGetProperty("reasons", out var rs) ? rs.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        double? busy = lab is { } l3 && l3.TryGetProperty("busy_before_pct", out var bb) && bb.ValueKind == JsonValueKind.Number ? bb.GetDouble() : null;
        double? others = lab is { } l6 && l6.TryGetProperty("others_vram_mib", out var ov) && ov.ValueKind == JsonValueKind.Number ? ov.GetDouble() : null;
        double? busyAfter = lab is { } l7 && l7.TryGetProperty("busy_after_pct", out var ba) && ba.ValueKind == JsonValueKind.Number ? ba.GetDouble() : null;
        bool shared = lab is { } l8 && l8.TryGetProperty("shared", out var sh) && sh.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? sh.GetBoolean() : others > 4096 || busy > 50 || busyAfter > 10;
        // The lab stops itself at the person's maxTempC (it samples every 100 ms, this loop every 500 ms, so it is usually
        // first): the same thermal stop as one seen here, not an inconclusive run with nothing to do next.
        if (!hot && !hung && LabStoppedHot(lab, out double? labC))
        {
            hot = true;
            stopC = labC ?? samples.LastOrDefault()?.Telemetry?.TemperatureC;
            hotWhy = Nvgwls.HotWhy(stopC, maxC);
        }
        // The card's failure is a bad bit, a dead context or a driver reset; a lab that hung with no reset (a GPU hang brings
        // one within the TDR delay), crashed or wrote nothing is the runner's, and says nothing about the setting.
        bool failed = resets > 0 || labVerdict == "fail";
        bool runner = !failed && !hot && (hung || code is not (0 or 4 or 5) || labVerdict == "none");
        string verdict = resets > 0 ? $"driver reset ({resets}) during the run"
            : hung ? $"the lab did not finish within {duration + grace + (symphony ? 240 : aria ? 120 : 30)} s and no driver reset followed: the run's fault, not the card's"
            : hot ? string.Create(CultureInfo.InvariantCulture, $"thermal stop at {stopC:0} °C ({hotWhy}): stopped by lab_test, not a pass or a crash")
            : labVerdict == "none" ? $"the lab ended without a result (exit code {code})"
            : labVerdict;

        IReadOnlyList<Card.Sample> held = samples.Count > 4 ? samples.Skip(4).ToList() : samples;
        if (failed) held = WithoutFrozenTail(held);
        static double? P50(IEnumerable<double?> xs)
        {
            var o = xs.Where(x => x != null).Select(x => x!.Value).Order().ToList();
            return o.Count == 0 ? null : Math.Round(o[o.Count / 2], 1);
        }
        double? mv = P50(held.Select(s => s.NvvddUv / 1000.0)), mhz = P50(held.Select(s => (double?)s.Telemetry?.GraphicsClockMhz));
        // A composite's medians span movements and preparation; they cannot name an individual curve point.
        int? pointIndex = !symphony && mv is { } m && Nearest(before.Curve, (uint)(m * 1000)) is { } pt ? pt.Index : null;
        double? xbarMhz = P50(held.Select(s => s.Mhz(Clocktail.Domain.Power.AllClocksLayout.Xbar)));

        // ST02: each memory run that passed clean is kept by its setup (SameMemorySetup), and compared with the last one like it.
        object? memoryVsLast = null;
        if (lab is { } r && Phase(r, "memory") is { } mem)
            memoryVsLast = KeepsMemoryRun(labVerdict, failed, shared, hot, hung)
                ? KeepMemoryRun(mem, Phase(r, "latency"), r.TryGetProperty("buffer_mib", out var bm) ? bm.GetInt64() : 0, memoryOffset, xbarMhz)
                : new { kept = false, note = "not kept or compared: only a run that passed, whole and with the GPU to itself, is a reference for later runs" };

        bool trial = hold.Trial != null;   // in force when the run began: the trial it measured
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Lab {plan.Profile}: {string.Join(",", list)}, {duration} s: {verdict}{(reasons.Count > 0 ? $" ({string.Join("; ", reasons)})" : "")}. P50 {mhz} MHz @ NVVDD {mv} mV (curve point {pointIndex}); max {held.Max(s => s.Telemetry?.TemperatureC)} °C")
            + (shared ? $"; other work shared the GPU ({others:0} MiB, {busy:0} %)" : "") + (note != null ? $". Purpose: {note}" : "");
        card.Broker(BrokerRequest.OpExperiment, "note", summary);
        bool crashRecorded = failed && RecordCrash(hold, resets, $"lab {string.Join(",", list)} {duration} s: {verdict}");
        return Out(new
        {
            passed = LabPassed(failed, hot, hung, runner, code, labVerdict),
            verdict,
            profile = plan.Profile,
            planned_seconds = duration,
            schedule = plan.Stages.Select(s => new { phase = s.Phase, seconds = s.Seconds }),
            reasons,
            exit_code = code,
            driver_resets = resets,
            shared = shared ? $"other programs held {others:0} MiB of video memory and kept the GPU {busy:0} % busy before the run and {busyAfter:0} % after (a local AI model, a game, a screen saver drawing): speeds are not the card's alone; bits are judged all the same" : null,
            phases = lab is { } l4 && l4.TryGetProperty("phases", out var ph) ? ph : (JsonElement?)null,
            movements = lab is { } lm && lm.TryGetProperty("movements", out var mov) ? mov : (JsonElement?)null,
            phase_observations = PhaseObservations(lab, samples),
            target_vf_qualified = (bool?)null,
            buffer_mib = lab is { } l5 && l5.TryGetProperty("buffer_mib", out var bmb) ? bmb.GetInt64() : (long?)null,
            card = new
            {
                scope = symphony ? "whole score, including preparation; not a single curve point" : "whole run, including preparation",
                clock_mhz = mhz, nvvdd_mv = mv, curve_point = pointIndex,
                msvdd_mv = P50(held.Select(s => s.MsvddUv / 1000.0)),
                board_w = P50(held.Select(s => s.Telemetry?.PowerWatts)),
                temp_max_c = held.Max(s => s.Telemetry?.TemperatureC),
                memory_c = P50(held.Select(s => s.MemoryC)),
                xbar_mhz = xbarMhz,
            },
            memory_vs_last = memoryVsLast,
            trial_in_force = trial,
            crash_recorded = crashRecorded,
            next = failed ? "the card failed: read data_points / read_log before the next step"
                : hot ? "too hot to test this way: a steeper fan curve, fans back to auto, or a lower wall, then run again"
                : runner ? "the run itself failed, not the card: nothing was recorded against the setting; run it again"
                : lab is { } l9 && Phase(l9, "solo") is { } so && so.TryGetProperty("held", out var hd) && hd.ValueKind == JsonValueKind.False
                    ? "the soloist did not hold the point, so its pass is no evidence: close other GPU work, or lower the power the share draws (a lower point, or set_clock_lock lockMv on the point to test), and run it again"
                : lab is { } l10 && Phase(l10, "leap") is { } lp && lp.TryGetProperty("held", out var lh) && lh.ValueKind == JsonValueKind.False
                    ? "the clock did not leap, so the leap's pass is no evidence: the whole chip did not hold the clock under the curve's top (a flat curve from the wall's voltage, or a high wall); run it at the daily wall with nothing else on the GPU"
                : hasAria && labVerdict == "pass" ? "correctness passed; check observed voltage and frequency for each movement before using it as V/F evidence"
                : null,
        });
    }

    public static JsonElement ScoreReport(SymphonyScore.ScoreResult score)
    {
        var phases = new JsonArray();
        foreach (var report in score.Reports)
            if (report.TryGetProperty("phases", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var phase in p.EnumerateArray()) phases.Add(JsonNode.Parse(phase.GetRawText()));
        double? Max(string field) => score.Reports
            .Select(r => r.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null).DefaultIfEmpty().Max();
        var json = new JsonObject
        {
            ["verdict"] = score.Verdict, ["reasons"] = JsonSerializer.SerializeToNode(score.Reasons),
            ["phases"] = phases, ["busy_before_pct"] = Max("busy_before_pct"),
            ["busy_after_pct"] = Max("busy_after_pct"), ["others_vram_mib"] = Max("others_vram_mib"),
            ["movements"] = JsonSerializer.SerializeToNode(score.Reports),
        };
        if (score.Reports.Any(r => r.TryGetProperty("shared", out var shared) && shared.ValueKind == JsonValueKind.True)) json["shared"] = true;
        string? interrupted = score.Reports.Select(r => r.TryGetProperty("interrupted", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(v => v != null);
        if (interrupted != null) json["interrupted"] = interrupted;
        return JsonSerializer.SerializeToElement(json);
    }

    /// <summary>Observations within each worker-reported phase window, including its CPU checks and pauses.
    /// These medians describe sampled values; they do not establish continuous residency at a curve point.</summary>
    public static object[] PhaseObservations(JsonElement? report, IReadOnlyList<Card.Sample> samples)
    {
        if (report is not { ValueKind: JsonValueKind.Object } r || !r.TryGetProperty("phases", out var phases)
            || phases.ValueKind != JsonValueKind.Array) return [];
        var result = new List<object>();
        foreach (var phase in phases.EnumerateArray())
        {
            if (!phase.TryGetProperty("started_utc", out var start) || start.ValueKind != JsonValueKind.String || !start.TryGetDateTimeOffset(out var from)
                || !phase.TryGetProperty("finished_utc", out var finish) || finish.ValueKind != JsonValueKind.String || !finish.TryGetDateTimeOffset(out var to) || to < from) continue;
            var window = samples.Where(s => s.At >= from && s.At <= to).ToArray();
            static double? Median(IEnumerable<double?> values)
            {
                var ordered = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
                return ordered.Length == 0 ? null : Math.Round(ordered[ordered.Length / 2], 1);
            }
            result.Add(new { phase = phase.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                started_utc = from, finished_utc = to, samples = window.Length,
                clock_mhz = Median(window.Select(s => (double?)s.Telemetry?.GraphicsClockMhz)),
                nvvdd_mv = Median(window.Select(s => s.NvvddUv / 1000.0)),
                scope = "sampled phase window including CPU checks and pauses; not proof of a held V/F point" });
        }
        return result.ToArray();
    }

    private static JsonElement? Phase(JsonElement lab, string kind) =>
        lab.TryGetProperty("phases", out var ps) ? ps.EnumerateArray().FirstOrDefault(p => p.TryGetProperty("kind", out var k) && k.GetString() == kind) is { ValueKind: JsonValueKind.Object } x ? x : null : null;

    public static bool LabPassed(bool failed, bool hot, bool hung, bool runner, int? exitCode, string verdict) =>
        !failed && !hot && !hung && !runner && exitCode == 0 && verdict == "pass";

    private static double? Num(JsonElement? e, string name) =>
        e is { } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>The memory offset on the card, MHz (P0's), or null when the driver could not be read.</summary>
    private int? MemoryOffsetMhz()
    {
        try { return card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Memory)?.DeltaKhz / 1000; }
        catch (Exception e) when (e is InvalidOperationException or DriverBusyException) { return null; }
    }

    /// <summary>Whether lab_test keeps a run's memory phase as the reference later runs are compared with: only a run that
    /// passed, and was not cut short (too hot, hung), failed or shared. In 0.8.1 an inconclusive run was kept too, a memory
    /// phase the lab had stopped at the temperature limit among them, and the next run at the offset was compared with its
    /// part of a minute.</summary>
    public static bool KeepsMemoryRun(string labVerdict, bool failed, bool shared, bool hot, bool hung) =>
        labVerdict == "pass" && !failed && !shared && !hot && !hung;

    /// <summary>Whether the lab stopped the run itself at the temperature limit (its JSON's interrupted is "temperature"), and
    /// the hottest its phases saw (temp_max; null when none says).</summary>
    public static bool LabStoppedHot(JsonElement? lab, out double? atC)
    {
        atC = null;
        if (lab is not { ValueKind: JsonValueKind.Object } l || !l.TryGetProperty("interrupted", out var i)
            || i.ValueKind != JsonValueKind.String || i.GetString() != "temperature") return false;
        if (l.TryGetProperty("phases", out var ps) && ps.ValueKind == JsonValueKind.Array)
            atC = ps.EnumerateArray().Select(p => Num(p, "temp_max")).Max();
        return true;
    }

    /// <summary>The share of the crossbar clock two memory runs may differ by and still compare (1 %). On the reference card
    /// XBAR +250 moved bandwidth 1455 → 1474 GB/s, over the ~1 % the comparison reads as slower.</summary>
    public const double XbarSameShare = 0.01;

    /// <summary>Whether a kept memory run (<paramref name="was"/>, a memory.jsonl line) was measured on the setup this one
    /// (<paramref name="now"/>) was, so their speeds compare: the same memory offset, buffer, P-state and driver, and a crossbar
    /// clock within <see cref="XbarSameShare"/> (both unknown counts as the same). A line kept by 0.8.1 or before has no
    /// driver or crossbar clock and matches nothing.</summary>
    public static bool SameMemorySetup(JsonObject was, JsonObject now)
    {
        static double? N(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) : null;
        static string? S(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        if (N(was["driver"]) is not { } d || d != N(now["driver"])) return false;
        if (N(was["offset_mhz"]) != N(now["offset_mhz"]) || N(was["buffer_mib"]) != N(now["buffer_mib"]) || S(was["pstate"]) != S(now["pstate"])) return false;
        return (N(was["xbar_mhz"]), N(now["xbar_mhz"])) switch
        {
            (null, null) => true,
            ({ } a, { } b) => Math.Abs(a - b) <= XbarSameShare * Math.Max(a, b),
            _ => false,
        };
    }

    /// <summary>Appends this memory run to the card's lab/memory.jsonl and compares it with the last one on the same setup
    /// (<see cref="SameMemorySetup"/>): bandwidth or latency moving the wrong way on the same setup is the memory slower there,
    /// from error retries or a hotter card (the temperatures are beside it). <paramref name="offsetMhz"/>: the memory offset
    /// read while the run held the card; <paramref name="xbarMhz"/>: the crossbar clock's P50 over the run.</summary>
    private object? KeepMemoryRun(JsonElement mem, JsonElement? lat, long bufferMib, int? offsetMhz, double? xbarMhz)
    {
        var entry = new JsonObject
        {
            ["at"] = DateTimeOffset.Now, ["offset_mhz"] = offsetMhz, ["buffer_mib"] = bufferMib,
            ["pstate"] = mem.TryGetProperty("p_state", out var ps) ? ps.GetString() : null,
            ["xbar_mhz"] = xbarMhz, ["driver"] = card.DriverVersion,
            ["mem_mhz"] = Num(mem, "mem_mhz_p50"), ["gbps"] = Num(mem, "gbps"), ["latency_ns"] = Num(lat, "latency_ns"), ["temp_c"] = Num(mem, "temp_p50"),
            ["patterns"] = mem.TryGetProperty("patterns", out var pat) ? JsonNode.Parse(pat.GetRawText()) : null,
        };
        string path = Path.Combine(LabDir(), "memory.jsonl");
        Directory.CreateDirectory(LabDir());
        JsonObject? last = null;
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path).Reverse())
                if (JsonNode.Parse(line) is JsonObject o && SameMemorySetup(o, entry))
                { last = o; break; }
        File.AppendAllText(path, entry.ToJsonString() + "\n");
        if (last == null) return new { first = true, note = "the first memory run kept at this memory offset, buffer, P-state, crossbar clock and driver: later runs on the same are compared with it" };
        static double? Pct(double? now, double? was) => now is { } n && was is > 0 ? Math.Round((n / was.Value - 1) * 100, 2) : null;
        return new
        {
            last_at = (string?)last["at"],
            gbps = (double?)entry["gbps"], gbps_last = (double?)last["gbps"], gbps_change_pct = Pct((double?)entry["gbps"], (double?)last["gbps"]),
            latency_ns = (double?)entry["latency_ns"], latency_last = (double?)last["latency_ns"], latency_change_pct = Pct((double?)entry["latency_ns"], (double?)last["latency_ns"]),
            temp_c = (double?)entry["temp_c"], temp_last = (double?)last["temp_c"],
            note = "same memory offset, buffer, P-state, crossbar clock and driver: bandwidth down or latency up by more than ~1 % means the memory got slower here, from error retries or a hotter card (the temperatures are beside it)",
        };
    }

    [McpServerTool(Name = "tune_memory", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Search memory offsets in the background. Screen the requested range, recheck suspected slowdowns A B B A, then compare up to three candidates, including a bandwidth/latency compromise when a slot is free, against the daily offset and confirm the winner. Equivalent performance prefers a lower offset. Missing actual clocks, disturbed comparisons or an unacknowledged failure record name no best. lab_status separates the scan boundary from a confirmed recommendation; lab_stop cancels. Restore daily settings and keep nothing automatically.")]
    public string TuneMemory(
        [Description("The highest memory offset to try, MHz, within this GPU's reported range.")] int toMhz,
        [Description("MHz per step, 25..100 (default 50).")] int stepMhz = 50,
        [Description("The first offset, MHz (default: the offset currently on this GPU, within its reported range).")] int? fromMhz = null,
        [Description("Seconds the lab measures each step, 30..300 (default 60).")] int seconds = 60,
        [Description("true: a slower step does not stop the sweep, only a failure does, so it shows the whole trend of " +
            "bandwidth and latency up to toMhz. The recommendation is still chosen by paired candidate comparisons.")] bool trend = false) => Guard(() =>
    {
        if (stepMhz is < 25 or > 100) throw new ArgumentException("stepMhz is 25..100");
        if (seconds is < 30 or > 300) throw new ArgumentException("seconds is 30..300");
        using var starting = StartLock();   // the check and the start as one: no second job slips in between
        var (exe, look) = JobPreflight();
        var p0 = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Memory) ?? throw new InvalidOperationException("the memory offset is unreadable");
        int daily = p0.DeltaKhz / 1000, from = fromMhz ?? daily;
        if ((long)from * 1000 < p0.MinKhz || (long)toMhz * 1000 > p0.MaxKhz) throw new ArgumentException($"the driver's range is {p0.MinKhz / 1000}..{p0.MaxKhz / 1000} MHz");
        if (toMhz <= from) throw new ArgumentException($"toMhz must be above the first offset ({from} MHz)");

        var (job, dir, detached) = StartJob(exe, "sweep", "sweep-memory", new
        {
            stable_id = card.StableId, pci = card.PciBusId, curve = VfOffsets.Encode(look.Curve.Offsets()), daily_mhz = daily,
            from_mhz = from, to_mhz = toMhz, step_mhz = stepMhz, seconds, warmup_seconds = 60, grace_seconds = 45, trend,
        });
        int steps = (toMhz - from + stepMhz - 1) / stepMhz + 1;
        card.Broker(BrokerRequest.OpExperiment, "note", string.Create(CultureInfo.InvariantCulture, $"tune_memory started: {from:+0;-0;0} → {toMhz:+0;-0;0} MHz in {stepMhz} MHz steps, {seconds} s each ({job})"));
        return Out(new
        {
            started = true, job, dir, from_mhz = from, to_mhz = toMhz, step_mhz = stepMhz, steps, trend,
            scan_minutes_estimate = Math.Ceiling((60 + steps * (seconds + 20)) / 60.0),
            confirmation_runs_at_most = 32,
            timing_note = "the scan estimate excludes paired slowdown rechecks, candidate confirmation and waits for other GPU work; each comparison is four runs and may retry once",
            detached = detached ? null : StaysInJob,
            next = "lab_status to follow it (each step adds a line); lab_stop ends it. The daily profile goes back on at the end; nothing is kept without the person",
        });
    });

    private const string StaysInJob = "the job stays in this agent's job object (its host does not let a process leave it): closing the agent may end it; lab_status says where it got to";

    /// <summary>What a background lab job needs before it starts: the lab, an open session with no trial in force, no other
    /// job running, and the GPU to itself (it measures speed). The lab and the card as read now.</summary>
    /// <summary>Held around a job's preflight and start, so two agents that both found the card free cannot both start one.</summary>
    private IDisposable StartLock()
    {
        var m = new Mutex(false, LabCard.StartMutex(card.StableId));
        try
        {
            if (!m.WaitOne(TimeSpan.FromSeconds(30))) { m.Dispose(); throw new InvalidOperationException("another agent is starting a lab job on this card: lab_status in a moment"); }
        }
        catch (AbandonedMutexException) { }
        return new MutexHeld(m);
    }

    private sealed class MutexHeld(Mutex m) : IDisposable
    {
        public void Dispose() { try { m.ReleaseMutex(); } catch (ApplicationException) { } m.Dispose(); }
    }

    /// <summary>Whether a lab job holds the card (the mutex it holds for its life).</summary>
    private bool CardTaken()
    {
        using var m = new Mutex(false, LabCard.JobMutex(card.StableId));
        try
        {
            if (!m.WaitOne(0)) return true;
        }
        catch (AbandonedMutexException) { }
        m.ReleaseMutex();
        return false;
    }

    private (string Exe, Card.Look Look) JobPreflight()
    {
        string exe = LabExe() ?? throw new InvalidOperationException("clocktail-lab.exe is not beside this server: run npx -y clocktail@latest setup");
        var show = card.Broker(BrokerRequest.OpExperiment, "show");
        if (!(show.Ok && show.Result is { } sr && sr.TryGetProperty("experiment", out var exp) && exp.ValueKind == JsonValueKind.Object))
            throw new InvalidOperationException("no session is open: begin_session first");
        if (TrialInForce(show) == true) throw new InvalidOperationException("a trial is in force: back_to_daily (or keep_as_daily) first, so the job starts from the daily profile");
        if (Running() is { } running) throw new InvalidOperationException($"a lab job is running already ({Path.GetFileName(running)}): lab_status, or lab_stop");
        if (CardTaken()) throw new InvalidOperationException("a lab job is running already on this card: lab_status, or lab_stop");
        var look = card.Read();
        if (look.Telemetry?.VramUsedBytes is > 4UL << 30 and var used)
            throw new InvalidOperationException($"other programs hold {used >> 20} MiB of video memory (a local AI model, a game): the job measures speed and needs the card to itself, so close them first");
        if (look.Telemetry?.GpuUtilPercent is > 50 and var u)
            throw new InvalidOperationException($"the GPU is {u} % busy with other work: the job measures speed, so close it first");
        return (exe, look);
    }

    /// <summary>Writes the job's spec.json under lab\{prefix}-{time} and has the lab start it out of this process's tree (and its
    /// job, where the host allows that). The job's name, folder, and whether it left the job.</summary>
    private (string Job, string Dir, bool Detached) StartJob(string exe, string prefix, string command, object spec)
    {
        string job = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}";   // unique even for two in one second
        string dir = Path.Combine(LabDir(), job);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.json"), JsonSerializer.Serialize(spec, SnakeJson));
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { command, "--job", dir }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the lab did not start");
        string answer = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"the lab did not start the job (exit code {p.ExitCode})");
        // Not started until it holds the card: then the start lock can go, and a second start sees it.
        for (var until = DateTime.UtcNow.AddSeconds(15); DateTime.UtcNow < until && !CardTaken() && Status(dir) is not { } _;) Thread.Sleep(200);
        return (job, dir, answer.Contains("detached", StringComparison.Ordinal));
    }

    /// <summary>tune_uncore's candidates, "XBAR MHz/demand mV/ratio/boost %;..." as the lab's settings (the ratio raw,
    /// U16.16); null when none are given.</summary>
    public static List<object>? Candidates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<object>();
        foreach (var c in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = c.Split('/', StringSplitOptions.TrimEntries);
            var inv = CultureInfo.InvariantCulture;
            if (p.Length != 4 || !int.TryParse(p[0], NumberStyles.Integer, inv, out var x) || !int.TryParse(p[1], NumberStyles.Integer, inv, out var d)
                || !double.TryParse(p[2], NumberStyles.Float, inv, out var r) || !int.TryParse(p[3], NumberStyles.Integer, inv, out var b))
                throw new ArgumentException($"'{c}' is not XBAR MHz/demand mV/ratio/boost %");
            if (!double.IsFinite(r) || Math.Round(r * 65536) is < ClockRatio.MinRaw or > uint.MaxValue)
                throw new ArgumentException($"'{c}': the ratio must be above 0 and fit U16.16");
            if ((long)x * 1000 is < int.MinValue or > int.MaxValue || (long)d * 1000 is < int.MinValue or > int.MaxValue)
                throw new ArgumentException($"'{c}': clock and demand must fit signed driver fields");
            if (b is < 0 or > (int)VoltBoost.MaxPercent) throw new ArgumentException($"'{c}': the boost is 0..{VoltBoost.MaxPercent} %");
            list.Add(new { xbar_mhz = x, demand_mv = d, ratio_raw = (uint)Math.Round(r * 65536), boost_pct = b });
        }
        if (list.Count is 0 or > 30) throw new ArgumentException("candidates: 1 to 30 settings");
        return list;
    }

    [McpServerTool(Name = "tune_uncore", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Run a background search or explicit candidate comparisons for available XBAR offset, voltage demand, ratio and boost controls. Start from current settings, not a supplied profile. Search bounds are algorithm parameters, not a tuning envelope. A B B A comparisons assess cache/DRAM speed and core cost; explicit candidates have no mandatory MHz/mV pairing. Restore the user's daily profile or baseline and keep nothing automatically.")]
    public string TuneUncore(
        [Description("The highest XBAR offset the search should try, MHz (algorithm default 500, bounded by this GPU's reported range; not a recommended daily offset).")] int xbarMaxMhz = 500,
        [Description("Seconds the lab measures each run, 15..60 (default 20; a comparison is four runs).")] int seconds = 20,
        [Description("Settings to compare instead of the search, each A B B A against the settings on the card: " +
            "'XBAR MHz/demand mV/ratio/boost %' separated by ';', e.g. '400/50/0.9/100;400/50/0.925/100'. One outside the " +
            "driver field encoding is refused; no reference-card cliff rule is enforced; at most 30.")] string? candidates = null) => Guard(() =>
    {
        if (seconds is < 15 or > 60) throw new ArgumentException("seconds is 15..60");
        var list = Candidates(candidates);
        using var starting = StartLock();   // the check and the start as one: no second job slips in between
        var (exe, look) = JobPreflight();
        var (xbarKhz, demandUv) = SettingsTools.Domain(card, ClockDomainId.Xbar);
        var (_, xbarTop) = OffsetRange(ClockDomainId.Xbar);
        int xbarMax = Math.Min(xbarMaxMhz, xbarTop);
        if (list == null && xbarMax <= xbarKhz / 1000) throw new ArgumentException($"xbarMaxMhz must be above the XBAR offset on the card ({xbarKhz / 1000} MHz)");
        uint ratioRaw = card.WithDriver(ClockRatio.Read).Raw;
        // The boost on the path the card takes (set_voltage_boost's): the legacy byte on a 5090, whose NVVDD is kind 3.
        var boost = card.WithDriver((hw, s) => BoostReader.Observe(hw, s));
        var path = boost.DefaultRule(railsWritable: true) ?? throw new InvalidOperationException("this card has no boost path");
        int boostPct = (int)(path == BoostPath.Independent ? boost.IndependentPercent ?? 0 : boost.LegacyPercent);
        var start = new { xbar_mhz = xbarKhz / 1000, demand_mv = demandUv / 1000, ratio_raw = ratioRaw, boost_pct = boostPct };
        var (job, dir, detached) = StartJob(exe, "uncore", "tune-uncore", new
        {
            stable_id = card.StableId, pci = card.PciBusId, curve = VfOffsets.Encode(look.Curve.Offsets()), start,
            xbar_max_mhz = xbarMax, xbar_step_mhz = 50, demand_max_mv = 50, demand_step_mv = 10,
            ratio_max = 0.95, ratio_step = 0.025, boost_max_pct = (int)VoltBoost.MaxPercent, boost_step_pct = 25,
            seconds, warmup_seconds = 60, grace_seconds = 45, boost_key = path == BoostPath.Independent ? "independent" : "legacy", candidates = list,
        });
        card.Broker(BrokerRequest.OpExperiment, "note", string.Create(CultureInfo.InvariantCulture,
            $"tune_uncore started from XBAR {xbarKhz / 1000:+0;-0;0} MHz / {demandUv / 1000:+0;-0;0} mV, ratio {ratioRaw / 65536.0:0.0000}, boost {boostPct} %; XBAR up to {xbarMax:+0} MHz ({job})"));
        return Out(new
        {
            started = true, job, dir, start, xbar_max_mhz = xbarMax,
            detached = detached ? null : StaysInJob,
            next = "lab_status to follow it (each comparison adds a line); lab_stop ends it. The daily profile goes back on at the end; nothing is kept without the person",
        });
    });

    /// <summary>The job folder of a job whose process still runs.</summary>
    private string? Running() => Jobs().FirstOrDefault(Alive);

    /// <summary>Whether the job's process still runs: the pid and start time in its alive.json (written every minute, apart
    /// from its progress: a measurement may wait 10 minutes for a screen saver), checked against a reused pid. A job from
    /// before alive.json counts while its status says running and was written within 10 minutes.</summary>
    private static bool Alive(string dir)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "alive.json"))) is JsonObject a
                && a["pid"]?.GetValue<int>() is int pid && a["started_utc"]?.GetValue<DateTime>() is DateTime started)
                return new SystemHost().IsProcessAlive(pid, new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc)));
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException) { }
        return Status(dir) is { } s && (string?)s["state"] == "running"
            && DateTime.TryParse((string?)s["updated_utc"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
            && DateTime.UtcNow - at < TimeSpan.FromMinutes(10);
    }

    private IEnumerable<string> Jobs() => Directory.Exists(LabDir())
        ? Directory.GetDirectories(LabDir()).Where(d => IsJob(Path.GetFileName(d))).OrderByDescending(d => Directory.GetCreationTimeUtc(d)) : [];

    /// <summary>A lab job's folder by its name: tune_memory's sweep-…, tune_uncore's uncore-…, game_test's game-….</summary>
    public static bool IsJob(string name) => name.StartsWith("sweep-", StringComparison.Ordinal) || name.StartsWith("uncore-", StringComparison.Ordinal)
        || name.StartsWith("game-", StringComparison.Ordinal);

    private static JsonObject? Status(string dir)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "status.json"))) as JsonObject; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    [McpServerTool(Name = "lab_status", ReadOnly = true, OpenWorld = false), Description(
        "Where a lab job is: running, done, stopped or failed, and what to do next. tune_memory's sweep: every step so far " +
        "(offset, bandwidth, latency, memory clock, temperature, P-state, spread), raw paired comparisons, edge_mhz and candidate_mhz; best_mhz is set only after confirmation and recovery. failure_recording reports confirmed/rejected/unknown and the measured trial_id; unknown failure records stop further candidates. " +
        "tune_uncore: each comparison, the best setting and failure_recording; unresolved failures stop the search, and incomplete recovery suppresses recommendations. game_test: the step it plays, the levers applied, per pattern the " +
        "fps, clocks, power and temperature, the heavy → light jumps and how many leapt, the seconds near the curve's top, its " +
        "result (survived, failed, unjudged), recovery errors and why. With no job, the latest.")]
    public string LabStatus([Description("The job (sweep-…, uncore-… or game-…); omit for the latest.")] string? job = null) => Guard(() =>
    {
        string dir = job != null ? Path.Combine(LabDir(), Path.GetFileName(job)) : Jobs().FirstOrDefault() ?? throw new InvalidOperationException("no lab job has run on this card");
        var s = Status(dir) ?? throw new InvalidOperationException($"{Path.GetFileName(dir)} has no status yet: it is starting, or its process ended before it wrote one");
        if ((string?)s["state"] == "running" && !Alive(dir))
            s["warning"] = "its process is not running (a reboot, or it was ended): the status stops where it got to; read its sweep.log";
        s["log"] = File.Exists(Path.Combine(dir, "sweep.log")) ? Path.Combine(dir, "sweep.log") : null;
        return s.ToJsonString(SnakeJson);
    });

    [McpServerTool(Name = "lab_stop", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Request a lab job's stop (tune_memory, tune_uncore, game_test). Compute measurements poll within a second; " +
        "game_test finishes its current bounded query/write and closes the game. The process is waited for before restoring " +
        "settings (game_test: the starting controls, otherwise the daily profile). Check lab_status for completion or " +
        "recovery errors; steps so far remain there. With no job, the one running.")]
    public string LabStop([Description("The job (sweep-…, uncore-… or game-…) to end; omit for the one running.")] string? job = null) => Guard(() =>
    {
        string dir = job != null ? Path.Combine(LabDir(), Path.GetFileName(job)) : Running() ?? throw new InvalidOperationException("no lab job is running");
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"no job {Path.GetFileName(dir)} on this card");
        File.WriteAllText(Path.Combine(dir, "stop"), DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        return Out(new { stopping = Path.GetFileName(dir), running = Alive(dir), next = "lab_status in a few seconds shows it stopped" });
    });
}
