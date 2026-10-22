using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
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
    private static readonly string[] LabPhases = ["burn", "light", "pulse", "memory", "latency", "l2"];
    private static readonly JsonSerializerOptions SnakeJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The lab: CLOCKTAIL_LAB_EXE, or clocktail-lab.exe beside this server (setup installs them together).</summary>
    public static string? LabExe() =>
        Environment.GetEnvironmentVariable("CLOCKTAIL_LAB_EXE") is { Length: > 0 } e && File.Exists(e) ? e
        : Path.Combine(AppContext.BaseDirectory, "clocktail-lab.exe") is var p && File.Exists(p) ? p : null;

    private string LabDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", card.StableId, "lab");

    [McpServerTool(Name = "lab_test", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Run the lab on what is on the card now: StabilityLab's load engine (CUDA, kernels the driver compiles, no toolkit), " +
        "every result checked bit for bit on the CPU, as stress_test runs nvgwls. Phases, split evenly over the seconds: burn " +
        "(every thread on an FMA chain: steady power; targetW steers it to that board power), light (one thread in lightDiv, " +
        "so few SMs draw current and the card climbs to its curve's top: 2910 MHz at ~410 W on the reference card at stock), " +
        "pulse (one short full launch per game-like frame at 30-120 fps: the load step at the top of the curve), memory (fill " +
        "and verify the whole buffer in five access orders: linear, 64 KiB and 128 KiB strides, block-xor, permute; bandwidth " +
        "and spread per order), latency (a pointer chase: ns per DRAM round trip). Any wrong bit, wrong chain end or dead " +
        "context fails it; while a trial is in force the failure is recorded as its crash and the daily profile goes back on. " +
        "Memory runs are kept per memory offset and compared with the last at the same offset and buffer: a drop is the " +
        "memory retrying (ST02). It says when other work shared the GPU (over 4 GiB of others' video memory, over half busy " +
        "before the load or over 10 % just after it, with the clocks still up: a local AI model, a game, a screen saver " +
        "drawing): then its speeds are not the card's alone. Blocks for the run's length.")]
    public string LabTest(
        [Description("Phases, comma-separated: burn, light, pulse, memory, latency, l2 (the crossbar: a 32 MiB region walked over and over; default the first five).")] string phases = "burn,pulse,light,memory,latency",
        [Description("Run length in seconds, 10..600 (default 60), split evenly between the phases.")] int seconds = 60,
        [Description("Steer burn to this board power, W (default: no steering, every thread).")] double? targetW = null,
        [Description("Light: one thread in this many (default 64: 64 SMs busy).")] int lightDiv = 64,
        [Description("What this run is meant to show, for the log.")] string? note = null,
        [Description("Stop the run if the GPU reaches this temperature, °C (default 85, at most 88).")] int? maxTempC = null) => Guard(() =>
    {
        string exe = LabExe() ?? throw new InvalidOperationException("clocktail-lab.exe is not beside this server: run npx -y clocktail@latest setup");
        var list = phases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.ToLowerInvariant()).ToList();
        if (list.Count == 0 || list.Any(p => !LabPhases.Contains(p))) throw new ArgumentException($"phases are {string.Join(", ", LabPhases)}");
        if (seconds < 10 || seconds > MaxLabSeconds) throw new ArgumentException($"seconds is 10..{MaxLabSeconds}");
        if (lightDiv is < 2 or > 4096) throw new ArgumentException("lightDiv is 2..4096");
        if (targetW is { } tw && tw is < 100 or > 700) throw new ArgumentException("targetW is 100..700 W");
        int maxC = maxTempC ?? StressMaxC;
        if (maxC is < 50 or > StressMaxCeilingC) throw new ArgumentException($"maxTempC is 50..{StressMaxCeilingC}");

        var before = card.Read();
        string dir = Path.Combine(Path.GetTempPath(), "clocktail-lab");
        Directory.CreateDirectory(dir);
        string jsonPath = Path.Combine(dir, $"run-{Guid.NewGuid():n}.json");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "run", "--phases", string.Join(",", list), "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--json", jsonPath,
                                  "--gpu-pci", card.PciBusId, "--light-div", lightDiv.ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        if (targetW is { } w) { psi.ArgumentList.Add("--target-w"); psi.ArgumentList.Add(w.ToString(CultureInfo.InvariantCulture)); }

        var samples = new List<Card.Sample>();
        int hotRun = 0;
        double? stopC = null;
        int grace = (OperatingSystem.IsWindows() ? Clocktail.Domain.Platform.TdrSettings.Read() : null)?.StressGraceS ?? 30;
        var started = DateTime.UtcNow;
        int? code = null;
        bool hot = false, hung = false;
        residency.StressUntil(DateTimeOffset.Now.AddSeconds(seconds + 60));
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("the lab did not start");
            var stderr = p.StandardError.ReadToEndAsync();
            var deadline = started.AddSeconds(seconds + grace + 30);
            while (!p.WaitForExit(500))
            {
                try
                {
                    var s = card.ReadSample();
                    samples.Add(s);
                    hotRun = Nvgwls.TooHot(s.Telemetry?.TemperatureC, s.Telemetry?.TempMarginC, maxC) ? hotRun + 1 : 0;
                }
                catch (DriverBusyException) { }
                if (hotRun >= 2) { hot = true; stopC = samples[^1].Telemetry?.TemperatureC; }
                if (DateTime.UtcNow > deadline) hung = true;
                if (hot || hung) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } break; }
            }
            if (!hot && !hung) code = p.ExitCode;
        }
        finally { residency.StressUntil(DateTimeOffset.MinValue); }

        int? resets = Nvgwls.DriverResets(started, code is 0 or 4 or 5 ? 0 : 2);
        JsonElement? lab = null;
        try { if (File.Exists(jsonPath)) lab = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.Clone(); }
        catch (JsonException) { }
        finally { try { File.Delete(jsonPath); } catch (IOException) { } }
        string labVerdict = lab is { } l && l.TryGetProperty("verdict", out var v) ? v.GetString() ?? "?" : "none";
        var reasons = lab is { } l2 && l2.TryGetProperty("reasons", out var rs) ? rs.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        double? busy = lab is { } l3 && l3.TryGetProperty("busy_before_pct", out var bb) && bb.ValueKind == JsonValueKind.Number ? bb.GetDouble() : null;
        double? others = lab is { } l6 && l6.TryGetProperty("others_vram_mib", out var ov) && ov.ValueKind == JsonValueKind.Number ? ov.GetDouble() : null;
        double? busyAfter = lab is { } l7 && l7.TryGetProperty("busy_after_pct", out var ba) && ba.ValueKind == JsonValueKind.Number ? ba.GetDouble() : null;
        bool shared = lab is { } l8 && l8.TryGetProperty("shared", out var sh) && sh.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? sh.GetBoolean() : others > 4096 || busy > 50 || busyAfter > 10;
        bool failed = resets > 0 || hung || labVerdict == "fail" || !hot && code is not (0 or 4 or 5);
        string verdict = resets > 0 ? $"driver reset ({resets}) during the run"
            : hung ? $"the lab did not finish within {seconds + grace + 30} s: a hang"
            : hot ? string.Create(CultureInfo.InvariantCulture, $"thermal stop at {stopC:0} °C (maxTempC {maxC}): stopped by lab_test, not a pass or a crash")
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
        int? pointIndex = mv is { } m && Nearest(before.Curve, (uint)(m * 1000)) is { } pt ? pt.Index : null;

        // ST02: each memory run is kept by memory offset and buffer, and compared with the last one like it.
        object? memoryVsLast = null;
        if (lab is { } r && !failed && !shared && Phase(r, "memory") is { } mem)
            memoryVsLast = KeepMemoryRun(mem, Phase(r, "latency"), r.TryGetProperty("buffer_mib", out var bm) ? bm.GetInt64() : 0);

        bool trial = TrialInForce(card.Broker(BrokerRequest.OpExperiment, "show")) == true;
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"Lab {string.Join(",", list)}, {seconds} s: {verdict}{(reasons.Count > 0 ? $" ({string.Join("; ", reasons)})" : "")}. P50 {mhz} MHz @ NVVDD {mv} mV (curve point {pointIndex}); max {held.Max(s => s.Telemetry?.TemperatureC)} °C")
            + (shared ? $"; other work shared the GPU ({others:0} MiB, {busy:0} %)" : "") + (note != null ? $". Purpose: {note}" : "");
        card.Broker(BrokerRequest.OpExperiment, "note", summary);
        bool crashRecorded = false;
        if (failed && trial)
        {
            if (resets > 0 && TrialClosedWithin(() => TrialInForce(card.Broker(BrokerRequest.OpExperiment, "show")), ServiceRecordsWithin, Thread.Sleep))
                crashRecorded = true;
            else
                crashRecorded = card.Broker(BrokerRequest.OpExperiment, "crash", $"lab {string.Join(",", list)} {seconds} s: {verdict}").Ok;
        }
        return Out(new
        {
            passed = !failed && !hot && labVerdict == "pass",
            verdict,
            reasons,
            exit_code = code,
            driver_resets = resets,
            shared = shared ? $"other programs held {others:0} MiB of video memory and kept the GPU {busy:0} % busy before the run and {busyAfter:0} % after (a local AI model, a game, a screen saver drawing): speeds are not the card's alone; bits are judged all the same" : null,
            phases = lab is { } l4 && l4.TryGetProperty("phases", out var ph) ? ph : (JsonElement?)null,
            buffer_mib = lab is { } l5 && l5.TryGetProperty("buffer_mib", out var bmb) ? bmb.GetInt64() : (long?)null,
            card = new
            {
                clock_mhz = mhz, nvvdd_mv = mv, curve_point = pointIndex,
                msvdd_mv = P50(held.Select(s => s.MsvddUv / 1000.0)),
                board_w = P50(held.Select(s => s.Telemetry?.PowerWatts)),
                temp_max_c = held.Max(s => s.Telemetry?.TemperatureC),
                memory_c = P50(held.Select(s => s.MemoryC)),
                xbar_mhz = P50(held.Select(s => s.Mhz(Clocktail.Domain.Power.AllClocksLayout.Xbar))),
            },
            memory_vs_last = memoryVsLast,
            trial_in_force = trial,
            crash_recorded = crashRecorded,
            next = failed ? "the card failed: read data_points / read_log before the next step"
                : hot ? "too hot to test this way: a steeper fan curve, fans back to auto, or a lower wall, then run again" : null,
        });
    });

    private static JsonElement? Phase(JsonElement lab, string kind) =>
        lab.TryGetProperty("phases", out var ps) ? ps.EnumerateArray().FirstOrDefault(p => p.TryGetProperty("kind", out var k) && k.GetString() == kind) is { ValueKind: JsonValueKind.Object } x ? x : null : null;

    private static double? Num(JsonElement? e, string name) =>
        e is { } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Appends this memory run to the card's lab/memory.jsonl and compares it with the last one at the same memory
    /// offset, buffer and P-state: bandwidth or latency moving the wrong way at the same setting is the memory retrying, or a
    /// hotter card (the temperatures are beside it).</summary>
    private object? KeepMemoryRun(JsonElement mem, JsonElement? lat, long bufferMib)
    {
        int? offset = null;
        try { offset = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Memory)?.DeltaKhz / 1000; } catch (Exception e) when (e is InvalidOperationException or DriverBusyException) { }
        var entry = new JsonObject
        {
            ["at"] = DateTimeOffset.Now, ["offset_mhz"] = offset, ["buffer_mib"] = bufferMib,
            ["pstate"] = mem.TryGetProperty("p_state", out var ps) ? ps.GetString() : null,
            ["mem_mhz"] = Num(mem, "mem_mhz_p50"), ["gbps"] = Num(mem, "gbps"), ["latency_ns"] = Num(lat, "latency_ns"), ["temp_c"] = Num(mem, "temp_p50"),
            ["patterns"] = mem.TryGetProperty("patterns", out var pat) ? JsonNode.Parse(pat.GetRawText()) : null,
        };
        string path = Path.Combine(LabDir(), "memory.jsonl");
        Directory.CreateDirectory(LabDir());
        JsonObject? last = null;
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path).Reverse())
                if (JsonNode.Parse(line) is JsonObject o && (int?)o["offset_mhz"] == offset && (long?)o["buffer_mib"] == bufferMib && (string?)o["pstate"] == (string?)entry["pstate"])
                { last = o; break; }
        File.AppendAllText(path, entry.ToJsonString() + "\n");
        if (last == null) return new { first = true, note = "the first memory run kept at this offset, buffer and P-state: later runs are compared with it" };
        static double? Pct(double? now, double? was) => now is { } n && was is > 0 ? Math.Round((n / was.Value - 1) * 100, 2) : null;
        return new
        {
            last_at = (string?)last["at"],
            gbps = (double?)entry["gbps"], gbps_last = (double?)last["gbps"], gbps_change_pct = Pct((double?)entry["gbps"], (double?)last["gbps"]),
            latency_ns = (double?)entry["latency_ns"], latency_last = (double?)last["latency_ns"], latency_change_pct = Pct((double?)entry["latency_ns"], (double?)last["latency_ns"]),
            temp_c = (double?)entry["temp_c"], temp_last = (double?)last["temp_c"],
            note = "same offset, buffer and P-state: bandwidth down or latency up by more than ~1 % is the memory retrying (or a hotter card)",
        };
    }

    [McpServerTool(Name = "tune_memory", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Start ST02's memory sweep, which runs by itself in the background (minutes to an hour; it carries on when the agent " +
        "goes away): from the daily memory offset (or fromMhz) up to toMhz in stepMhz steps, each a trial through the service, " +
        "each measured by the lab for `seconds` at one fixed buffer: bit errors, bandwidth in five access orders, latency. " +
        "GDDR7 hides its errors by retrying them, so past its edge the memory gets slower before it fails: a step stops the " +
        "sweep when it fails (a bad bit, a driver reset: the service records it), or when latency rises or bandwidth falls against " +
        "the best step so far (latency follows the DRAM; bandwidth was set by the crossbar on the reference card, flat from +0 " +
        "to +358). The best offset is the step before; " +
        "the daily profile goes back on at the end, nothing is kept. CUDA work alone ran in P1 on the reference card (Boost Lock " +
        "did not change that) and the memory offset is a P0 offset, so each step checks NVML's memory clock rose with the " +
        "offset: if it did not, the sweep stops and says the offset was not in force. Needs an open session " +
        "(begin_session) with no trial in force, and an otherwise idle GPU (close games and local AI models; a run another " +
        "program shared, a screen saver drawing too, is measured again every 30 s for up to 10 minutes). Follow it with " +
        "lab_status; lab_stop ends it. Tell the person first: it raises the memory past what has been tested.")]
    public string TuneMemory(
        [Description("The highest memory offset to try, MHz.")] int toMhz,
        [Description("MHz per step, 25..100 (default 50).")] int stepMhz = 50,
        [Description("The first offset, MHz (default the daily one on the card).")] int? fromMhz = null,
        [Description("Seconds the lab measures each step, 30..300 (default 60).")] int seconds = 60,
        [Description("true: a slower step does not stop the sweep, only a failure does, so it shows the whole trend of " +
            "bandwidth and latency up to toMhz (up to 2000 MHz past the first offset); the best is still the step before the first slower one.")] bool trend = false) => Guard(() =>
    {
        if (stepMhz is < 25 or > 100) throw new ArgumentException("stepMhz is 25..100");
        if (seconds is < 30 or > 300) throw new ArgumentException("seconds is 30..300");
        var (exe, look) = JobPreflight();
        var p0 = card.WithDriver(Pstates20Reader.Read).P0(ClockDomain.Memory) ?? throw new InvalidOperationException("the memory offset is unreadable");
        int daily = p0.DeltaKhz / 1000, from = fromMhz ?? daily;
        if (from * 1000 < p0.MinKhz || toMhz * 1000 > p0.MaxKhz) throw new ArgumentException($"the driver's range is {p0.MinKhz / 1000}..{p0.MaxKhz / 1000} MHz");
        if (toMhz <= from) throw new ArgumentException($"toMhz must be above the first offset ({from} MHz)");
        if (toMhz - from > (trend ? 2000 : 1000)) throw new ArgumentException($"a sweep covers at most {(trend ? 2000 : 1000)} MHz");
        if (Math.Abs(from - daily) > 250) throw new ArgumentException($"the first offset is at most 250 MHz from the daily one ({daily} MHz)");

        var (job, dir, detached) = StartJob(exe, "sweep", "sweep-memory", new
        {
            stable_id = card.StableId, pci = card.PciBusId, curve = VfOffsets.Encode(look.Curve.Offsets()), daily_mhz = daily,
            from_mhz = from, to_mhz = toMhz, step_mhz = stepMhz, seconds, warmup_seconds = 60, grace_seconds = 45, trend,
        });
        int steps = (toMhz - from) / stepMhz + 1;
        card.Broker(BrokerRequest.OpExperiment, "note", string.Create(CultureInfo.InvariantCulture, $"tune_memory started: {from:+0;-0;0} → {toMhz:+0;-0;0} MHz in {stepMhz} MHz steps, {seconds} s each ({job})"));
        return Out(new
        {
            started = true, job, dir, from_mhz = from, to_mhz = toMhz, step_mhz = stepMhz, steps, trend,
            minutes_at_most = Math.Ceiling((60 + steps * (seconds + 20)) / 60.0),
            detached = detached ? null : StaysInJob,
            next = "lab_status to follow it (each step adds a line); lab_stop ends it. The daily profile goes back on at the end; nothing is kept without the person",
        });
    });

    private const string StaysInJob = "the job stays in this agent's job object (its host does not let a process leave it): closing the agent may end it; lab_status says where it got to";

    /// <summary>What a background lab job needs before it starts: the lab, an open session with no trial in force, no other
    /// job running, and the GPU to itself (it measures speed). The lab and the card as read now.</summary>
    private (string Exe, Card.Look Look) JobPreflight()
    {
        string exe = LabExe() ?? throw new InvalidOperationException("clocktail-lab.exe is not beside this server: run npx -y clocktail@latest setup");
        var show = card.Broker(BrokerRequest.OpExperiment, "show");
        if (!(show.Ok && show.Result is { } sr && sr.TryGetProperty("experiment", out var exp) && exp.ValueKind == JsonValueKind.Object))
            throw new InvalidOperationException("no session is open: begin_session first");
        if (TrialInForce(show) == true) throw new InvalidOperationException("a trial is in force: back_to_daily (or keep_as_daily) first, so the job starts from the daily profile");
        if (Running() is { } running) throw new InvalidOperationException($"a lab job is running already ({Path.GetFileName(running)}): lab_status, or lab_stop");
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
        string job = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}";
        string dir = Path.Combine(LabDir(), job);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.json"), JsonSerializer.Serialize(spec, SnakeJson));
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { command, "--job", dir }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the lab did not start");
        string answer = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"the lab did not start the job (exit code {p.ExitCode})");
        return (job, dir, answer.Contains("detached", StringComparison.Ordinal));
    }

    [McpServerTool(Name = "tune_uncore", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Start the uncore tuner, which runs by itself in the background (about 1 min 45 s a comparison at 20 s runs, 18 minutes on the reference card; it carries on when the agent goes " +
        "away where its host allows): from the settings on the card, one axis at a time, one step at a time, each candidate " +
        "a trial through the service compared A B B A against the best so far by the lab: L2 bandwidth (a 32 MiB region walked " +
        "over and over: the crossbar XBAR clocks), DRAM bandwidth, and the memory phase's core clock. A candidate is kept only " +
        "when L2 bandwidth rose in both pairs and by 0.5 % or more, DRAM bandwidth did not fall and the core did not fall 1 % " +
        "(5 % is the cliff: MSVDD at its floor pins it near 1610 MHz). Axes in order: XBAR's offset, each step with the demand " +
        "adjust_xbar's rule asks for (above +250 MHz +20 mV, above +300 +50 mV; more where a step still hits the cliff), the " +
        "XBAR/core clock ratio (its 0.9 x core floor), MSVDD's REL (up to +20 mV; its reliability limit is XBAR's ceiling), then " +
        "the ratio and MSVDD a step each together (XBAR's window up as a whole: on the reference card neither alone moved it), " +
        "in rounds: an axis runs again when another has moved the best since. An axis stops at a " +
        "failure (recorded as its trial's crash) or two candidates in a row that are not faster. The daily profile goes back " +
        "on at the end; nothing is kept: lab_status names the best setting to try. Needs an open session (begin_session) with " +
        "no trial in force, and the GPU to itself (a run another program shared, a screen saver drawing too, is measured again " +
        "every 30 s for up to 10 minutes). On the reference card XBAR +300 with +20 mV made Blender 6-7 % faster; a " +
        "short check of this tuner found +2.8 % L2 at MSVDD REL +10 mV, ratio 0.925, XBAR +300 / +20 mV (manual xbar-msvdd); " +
        "games not measured. Tell the person first: it raises voltages and clocks past their daily ones.")]
    public string TuneUncore(
        [Description("The highest XBAR offset to try, MHz (default 500; the driver allows 1000; past +400 is unmeasured).")] int xbarMaxMhz = 500,
        [Description("Seconds the lab measures each run, 15..60 (default 20; a comparison is four runs).")] int seconds = 20) => Guard(() =>
    {
        if (seconds is < 15 or > 60) throw new ArgumentException("seconds is 15..60");
        var (exe, look) = JobPreflight();
        var (xbarKhz, demandUv) = SettingsTools.Domain(card, ClockDomainId.Xbar);
        var (_, xbarTop) = OffsetRange(ClockDomainId.Xbar);
        int xbarMax = Math.Min(xbarMaxMhz, Math.Min(xbarTop, MaxXbarMhz));
        if (xbarMax <= xbarKhz / 1000) throw new ArgumentException($"xbarMaxMhz must be above the XBAR offset on the card ({xbarKhz / 1000} MHz)");
        uint ratioRaw = card.WithDriver(ClockRatio.Read).Raw;
        var msvdd = look.Msvdd ?? throw new InvalidOperationException("the MSVDD rail is unreadable");
        int relMv = msvdd.Offsets.RelUv / 1000;
        // ALT's limit above REL's with no offsets: a REL offset past it goes on with ALT raised by the rest (the rail's top is
        // the lowest of the two). Only while ALT holds no offset of its own.
        int? altGapMv = msvdd.Limits.AltOpUv != 0 && msvdd.Offsets.AltUv == 0
            ? (int)(((long)msvdd.Limits.AltOpUv - msvdd.Offsets.AltUv - ((long)msvdd.Limits.RelUv - msvdd.Offsets.RelUv)) / 1000) : null;
        var start = new { xbar_mhz = xbarKhz / 1000, demand_mv = demandUv / 1000, ratio_raw = ratioRaw, msvdd_rel_mv = relMv };
        var (job, dir, detached) = StartJob(exe, "uncore", "tune-uncore", new
        {
            stable_id = card.StableId, pci = card.PciBusId, curve = VfOffsets.Encode(look.Curve.Offsets()), start,
            xbar_max_mhz = xbarMax, xbar_step_mhz = 50, demand_max_mv = MaxVdemandMv, demand_step_mv = 10,
            cliff_mhz = XbarCliffMhz, cliff_demand_mv = XbarCliffDemandMv, high_mhz = XbarHighMhz, high_demand_mv = XbarHighDemandMv,
            ratio_max = 0.95, ratio_step = 0.025, msvdd_rel_max_mv = MsvddRelMaxMv, msvdd_rel_step_mv = 10,
            seconds, warmup_seconds = 60, grace_seconds = 45, msvdd_alt_gap_mv = altGapMv,
        });
        card.Broker(BrokerRequest.OpExperiment, "note", string.Create(CultureInfo.InvariantCulture,
            $"tune_uncore started from XBAR {xbarKhz / 1000:+0;-0;0} MHz / {demandUv / 1000:+0;-0;0} mV, ratio {ratioRaw / 65536.0:0.0000}, MSVDD REL {relMv:+0;-0;0} mV; XBAR up to {xbarMax:+0} MHz ({job})"));
        return Out(new
        {
            started = true, job, dir, start, xbar_max_mhz = xbarMax,
            detached = detached ? null : StaysInJob,
            next = "lab_status to follow it (each comparison adds a line); lab_stop ends it. The daily profile goes back on at the end; nothing is kept without the person",
        });
    });

    /// <summary>The job folder of a sweep still running (status.json says so and was written within the last 10 minutes).</summary>
    private string? Running() => Jobs().FirstOrDefault(d => Status(d) is { } s && (string?)s["state"] == "running"
        && DateTime.TryParse((string?)s["updated_utc"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
        && DateTime.UtcNow - at < TimeSpan.FromMinutes(10));

    private IEnumerable<string> Jobs() => Directory.Exists(LabDir())
        ? Directory.GetDirectories(LabDir()).Where(d => Path.GetFileName(d) is var n && (n.StartsWith("sweep-", StringComparison.Ordinal) || n.StartsWith("uncore-", StringComparison.Ordinal)))
            .OrderByDescending(d => Directory.GetCreationTimeUtc(d)) : [];

    private static JsonObject? Status(string dir)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "status.json"))) as JsonObject; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    [McpServerTool(Name = "lab_status", ReadOnly = true, OpenWorld = false), Description(
        "Where tune_memory's sweep is: running, done, stopped or failed; every step so far (offset, bandwidth, latency, memory " +
        "clock, temperature, P-state, spread, what stopped it), the best offset and what to do next. With no job, the latest.")]
    public string LabStatus([Description("The job (sweep-…) from tune_memory; omit for the latest.")] string? job = null) => Guard(() =>
    {
        string dir = job != null ? Path.Combine(LabDir(), Path.GetFileName(job)) : Jobs().FirstOrDefault() ?? throw new InvalidOperationException("no sweep has run on this card");
        var s = Status(dir) ?? throw new InvalidOperationException($"{Path.GetFileName(dir)} has no status yet: it is starting, or its process ended before it wrote one");
        if ((string?)s["state"] == "running" && Running() != dir)
            s["warning"] = "no update for over 10 minutes: the sweep's process may have ended (a reboot); read its sweep.log";
        s["log"] = File.Exists(Path.Combine(dir, "sweep.log")) ? Path.Combine(dir, "sweep.log") : null;
        return s.ToJsonString(SnakeJson);
    });

    [McpServerTool(Name = "lab_stop", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "End the running tune_memory sweep: its measurement stops within a second, the daily profile goes back on, and the " +
        "steps so far stay in lab_status.")]
    public string LabStop() => Guard(() =>
    {
        string dir = Running() ?? throw new InvalidOperationException("no sweep is running");
        File.WriteAllText(Path.Combine(dir, "stop"), DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        return Out(new { stopping = Path.GetFileName(dir), next = "lab_status in a few seconds shows it stopped" });
    });
}
