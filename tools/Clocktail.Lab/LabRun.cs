using System.Diagnostics;

namespace Clocktail.Lab;

/// <summary>
/// The lab's phases. Burn: every thread on fma_rot (steady power; steered to a board power when one is asked for). Light:
/// one thread in LightDiv, so few SMs draw current and the card climbs to the top of its curve (64 SMs: 2887 MHz at 360 W
/// on the reference card at stock). Pulse: one short full-grid launch per game-like frame at 30-120 fps (the load step at
/// the top of the curve). Memory: fill and verify the buffer in the five access orders. Latency: the pointer chase. L2: fill
/// and verify a 32 MiB region over and over, which stays in L2: the crossbar's bandwidth, the path XBAR clocks.
/// </summary>
public enum LabPhase { Burn, Light, Pulse, Memory, Latency, L2 }

/// <summary>What a run does. MaxTempC: the run stops when the GPU reaches it (two readings in a row), as stress_test does;
/// TelemetryLostSeconds: it stops when NVML gives no temperature for that long, since a card nobody can watch is not
/// loaded. Either stop leaves the run inconclusive, not a pass and not a crash, with the reason in
/// <see cref="LabRunner.Interrupted"/>; every entry point (lab_test, the jobs' measurements) runs through it.</summary>
public sealed record LabConfig(IReadOnlyList<(LabPhase Kind, double Seconds)> Phases, double TargetW = 0, int LightDiv = 64,
    uint Seed = 0x5EED, int SampleMs = 100, int ChunkMs = 150, double PulseDuty = 0.6, double MaxTempC = 85, double TelemetryLostSeconds = 10)
{
    public static IReadOnlyList<(LabPhase, double)> Parse(string list, double seconds)
    {
        var kinds = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<LabPhase>(s, true, out var k) ? k : throw new ArgumentException($"'{s}' is not a phase (burn, light, pulse, memory, latency, l2)"))
            .ToList();
        if (kinds.Count == 0) throw new ArgumentException("no phase given");
        return kinds.Select(k => (k, seconds / kinds.Count)).ToList();
    }
}

public sealed record PatternResult(string Pattern, double Gbps, double CvPct, int Chunks);

public sealed class PhaseResult
{
    public LabPhase Kind { get; init; }
    public double PlannedSeconds { get; init; }
    public double Seconds { get; set; }
    public long Launches { get; set; }
    public long ComputeMismatches { get; set; }
    public long MemoryErrors { get; set; }
    public long ChainErrors { get; set; }
    public ulong? FirstBadWord { get; set; }
    public double? Gflops { get; set; }
    /// <summary>Gflop/s per MHz of the SM clock NVML reports: falls when the chip does less work per cycle than its clock says (clock stretching).</summary>
    public double? GflopsPerMhz { get; set; }
    /// <summary>The linear pattern's bandwidth, comparable with StabilityLab's memory phase.</summary>
    public double? Gbps { get; set; }
    public List<PatternResult>? Patterns { get; set; }
    public double? LatencyNs { get; set; }
    public int? Threads { get; set; }
    public double? SmMhzP50 { get; set; }
    public double? MemMhzP50 { get; set; }
    public double? WattsP50 { get; set; }
    public double? WattsMax { get; set; }
    public double? TempP50 { get; set; }
    public double? TempMax { get; set; }
    /// <summary>The P-state most samples saw ("P0"): the memory offset is a P0 offset, and CUDA work alone ran in P1 on the reference card.</summary>
    public string? PState { get; set; }
    public string? Error { get; set; }
    public bool Completed { get; set; }
}

public sealed record LabResult(string Device, string Pci, ulong BufferMib, int SmCount, DateTime StartedUtc, DateTime FinishedUtc,
    IReadOnlyList<PhaseResult> Phases, string Verdict, IReadOnlyList<string> Reasons, double? BusyBeforePct = null, ulong? OthersVramMib = null,
    double? BusyAfterPct = null, string? Interrupted = null)
{
    /// <summary>Why the run was cut short or never ran, when it was: the temperature limit, telemetry lost, a stop asked,
    /// the measuring process failing (no result, a hang with no driver reset). Such a run is inconclusive: it says nothing
    /// about the setting, and is never recorded as the setting's crash.</summary>
    public const string ByTemperature = "temperature", ByTelemetry = "telemetry", ByStop = "stopped", ByRunner = "runner";

    /// <summary>Other work shares the GPU, so speeds (bandwidth, latency, Gflop/s) are not the card's alone: other programs held
    /// over 4 GiB of video memory before the lab started (a local AI model, a game; the desktop holds 1-2), kept it over half
    /// busy before the load, or over <see cref="BusyAfterPct"/>'s 10 % just after it. Utilization is time, so it reads by the
    /// clock the GPU runs at: on the reference card the idle desktop read 12-15 % at idle clocks before a run and 2-3 % just
    /// after one, with the clocks still up; Windows' Mystify screen saver read 21-25 % there and cost the L2 test 19 % and
    /// DRAM 21 %. Bits are judged all the same.</summary>
    public bool Shared => OthersVramMib > 4096 || BusyBeforePct > BusyBeforeMax || BusyAfterPct > BusyAfterMax;

    public const double BusyBeforeMax = 50, BusyAfterMax = 10;

    public const string Pass = "pass", Fail = "fail", Inconclusive = "inconclusive";
    public int ExitCode => Verdict switch { Pass => 0, Fail => 4, _ => 5 };
}

/// <summary>
/// Drives the phases on the engine and samples NVML on a side thread (StabilityLab's runner, the lab's phases only). A bad
/// bit anywhere, a wrong chain end or a dead context fails the run; a run that never got to load is inconclusive.
/// </summary>
public sealed class LabRunner(LabConfig cfg, ILoadEngine engine, ISampler sampler, Func<double>? clock = null, Action<int>? sleep = null,
    Action<string>? log = null, Action<int>? sampleWait = null)
{
    private readonly Action<int> _sampleWait = sampleWait ?? Thread.Sleep;
    private readonly Func<double> _now = clock ?? SystemSeconds();
    private readonly Action<int> _sleep = sleep ?? Thread.Sleep;
    private readonly List<LabSample> _samples = [];
    private readonly object _lock = new();
    private volatile int _phase = -1;
    private volatile string? _interrupted, _interruptedBy;

    /// <summary>Why the run stopped itself (the temperature limit, telemetry lost), or null; and which (<see cref="LabResult"/>'s By*).</summary>
    public string? Interrupted => _interrupted;
    public string? InterruptedBy => _interruptedBy;

    /// <summary>The least GPU time each pattern gets per round of the memory phase.</summary>
    public const double RoundMs = 150;

    public IReadOnlyList<LabSample> Samples { get { lock (_lock) return [.. _samples]; } }

    private static Func<double> SystemSeconds()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.Elapsed.TotalSeconds;
    }

    public List<PhaseResult> Run(CancellationToken ct)
    {
        var phases = new List<PhaseResult>();
        using var stop = new CancellationTokenSource();
        using var halt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        double t0 = _now();
        var thread = new Thread(() =>
        {
            int hot = 0;
            double lastTemp = _now();
            while (!stop.IsCancellationRequested)
            {
                var s = sampler.Read(_now() - t0, _phase);
                lock (_lock) _samples.Add(s);
                // The run's own guard, whoever started it: hot twice in a row, or no temperature for too long, ends it.
                if (s.TempC is { } c)
                {
                    lastTemp = _now();
                    hot = c >= cfg.MaxTempC ? hot + 1 : 0;
                    if (hot >= 2) Halt(LabResult.ByTemperature, Inv($"the GPU reached {c} °C, the {cfg.MaxTempC:0} °C limit: stopped, not a pass or a crash"));
                }
                else if (_now() - lastTemp > cfg.TelemetryLostSeconds)
                    Halt(LabResult.ByTelemetry, Inv($"NVML gave no temperature for {cfg.TelemetryLostSeconds:0} s: stopped, the card could not be watched"));
                _sampleWait(cfg.SampleMs);
            }
        }) { IsBackground = true, Name = "nvml" };

        void Halt(string by, string why)
        {
            if (_interrupted != null) return;
            _interruptedBy = by;
            _interrupted = why;
            log?.Invoke(why);
            try { halt.Cancel(); } catch (ObjectDisposedException) { }
        }

        thread.Start();
        try
        {
            for (int i = 0; i < cfg.Phases.Count && !halt.IsCancellationRequested; i++)
            {
                var pr = RunPhase(i, cfg.Phases[i].Kind, cfg.Phases[i].Seconds, halt.Token);
                phases.Add(pr);
                if (pr.Error != null) { log?.Invoke($"{pr.Kind} stopped: {pr.Error}"); break; }
            }
        }
        finally
        {
            _phase = -1;
            stop.Cancel();
            thread.Join(2000);
        }
        return phases;
    }

    private PhaseResult RunPhase(int index, LabPhase kind, double seconds, CancellationToken ct)
    {
        var pr = new PhaseResult { Kind = kind, PlannedSeconds = seconds };
        var pacer = new FramePacer(cfg.Seed + (uint)index * 104729u);
        var ctrl = kind switch
        {
            LabPhase.Pulse => new ChunkController(pacer.LaunchTargetMs(cfg.PulseDuty), minIterations: 16, initial: 256),
            LabPhase.Latency => new ChunkController(cfg.ChunkMs, minIterations: 256, maxIterations: 1u << 24, initial: 1024),
            _ => new ChunkController(cfg.ChunkMs),
        };
        uint seed = cfg.Seed ^ ((uint)index * 0x9E3779B9u);
        int lightThreads = Math.Max(256, engine.ComputeThreads / Math.Max(1, cfg.LightDiv));
        double share = 1, lastSteer = 0, workSum = 0, msSum = 0, threadSum = 0;
        int lastThreads = -1, rounds = 0;
        var frozenMs = new List<double>();
        var perPattern = Enum.GetValues<AccessPattern>().ToDictionary(p => p, _ => new List<double>());
        var l2Gbps = new List<double>();
        double t0 = _now();
        _phase = index;
        log?.Invoke($"phase {index + 1}/{cfg.Phases.Count} {kind} for {seconds:F0} s");

        bool Compute(int threads)
        {
            uint iters = ctrl.Current;
            bool reference = ctrl.Launches == ctrl.WarmupLaunches || threads != lastThreads && ctrl.Frozen;
            lastThreads = threads;
            var o = kind == LabPhase.Latency ? engine.RunLatency(iters) : engine.RunCompute(iters, reference, threads);
            pr.Launches++;
            if (o.Error != null) { pr.Error = o.Error; return false; }
            if (kind == LabPhase.Latency) pr.ChainErrors += o.Mismatches; else pr.ComputeMismatches += o.Mismatches;
            if (o.ElapsedMs > 0 && ctrl.Frozen) { frozenMs.Add(o.ElapsedMs); workSum += o.WorkUnits; msSum += o.ElapsedMs; threadSum += threads; }
            ctrl.Observe(o.ElapsedMs);
            return true;
        }

        bool alive = true;
        while (alive && !ct.IsCancellationRequested && _now() - t0 < seconds)
        {
            switch (kind)
            {
                case LabPhase.Burn:
                    // Steered: the share of threads moves toward the board power asked for, on the latest reading, twice a second.
                    if (cfg.TargetW > 0 && _now() - lastSteer >= 0.5 && sampler.Read(_now() - t0, index) is { BoardW: > 0 } s)
                    {
                        lastSteer = _now();
                        share = Math.Clamp(Math.Round(share * (1 + 0.5 * (cfg.TargetW - s.BoardW!.Value) / cfg.TargetW) * 64) / 64, 1.0 / 64, 1.0);
                    }
                    alive = Compute(Math.Max(256, (int)(engine.ComputeThreads * share) / 256 * 256));
                    break;
                case LabPhase.Light:
                    alive = Compute(lightThreads);
                    break;
                case LabPhase.Pulse:
                    double frameStart = _now(), frameMs = pacer.NextFrameMs();
                    alive = Compute(engine.ComputeThreads);
                    int rest = (int)Math.Round(frameMs - (_now() - frameStart) * 1000);
                    if (alive && rest > 0) _sleep(rest);
                    break;
                case LabPhase.Latency:
                    alive = Compute(0);
                    break;
                case LabPhase.L2:
                    // Like the memory phase: rounds of at least RoundMs, the first five only warming the clocks.
                    double l2Spent = 0;
                    do
                    {
                        var m = engine.RunL2(seed++);
                        pr.Launches++;
                        if (m.Error != null) { pr.Error = m.Error; alive = false; break; }
                        pr.MemoryErrors += m.Mismatches;
                        if (m.FirstBadWord is { } fb && (pr.FirstBadWord == null || fb < pr.FirstBadWord)) pr.FirstBadWord = fb;
                        l2Spent += m.ElapsedMs;
                        if (m.ElapsedMs > 0 && rounds >= 5) l2Gbps.Add(m.WorkUnits / (m.ElapsedMs / 1000.0) / 1e9);
                    }
                    while (l2Spent < RoundMs && !ct.IsCancellationRequested);
                    rounds++;
                    break;
                case LabPhase.Memory:
                    // A round gives each pattern passes for at least RoundMs: a linear pass over 21.6 GiB takes ~27 ms and a
                    // strided one ~370 ms, so equal pass counts left linear with 9 passes and a 4 % spread in 10 s. The first
                    // round only warms the clocks: its passes are checked, not measured.
                    var pattern = (AccessPattern)(rounds % 5);
                    double spent = 0;
                    do
                    {
                        var m = engine.RunMemory(seed++, pattern);
                        pr.Launches++;
                        if (m.Error != null) { pr.Error = m.Error; alive = false; break; }
                        pr.MemoryErrors += m.Mismatches;
                        if (m.FirstBadWord is { } fb && (pr.FirstBadWord == null || fb < pr.FirstBadWord)) pr.FirstBadWord = fb;
                        spent += m.ElapsedMs;
                        if (m.ElapsedMs > 0 && rounds >= 5) perPattern[pattern].Add(m.WorkUnits / (m.ElapsedMs / 1000.0) / 1e9);
                    }
                    while (spent < RoundMs && !ct.IsCancellationRequested);
                    rounds++;
                    break;
            }
        }
        pr.Seconds = Math.Round(_now() - t0, 1);
        pr.Completed = alive && !ct.IsCancellationRequested;
        _phase = -1;

        List<LabSample> mine;
        lock (_lock) mine = _samples.Where(x => x.Phase == index).ToList();
        pr.SmMhzP50 = P50(mine.Select(x => (double?)x.SmMhz));
        pr.MemMhzP50 = P50(mine.Select(x => (double?)x.MemMhz));
        pr.WattsP50 = P50(mine.Select(x => x.BoardW));
        pr.WattsMax = mine.Select(x => x.BoardW).Max();
        pr.TempP50 = P50(mine.Select(x => (double?)x.TempC));
        pr.TempMax = mine.Select(x => (double?)x.TempC).Max();
        pr.PState = mine.Where(x => x.PState != null).GroupBy(x => x.PState).OrderByDescending(g => g.Count()).Select(g => $"P{g.Key}").FirstOrDefault();
        if (kind == LabPhase.L2)
        {
            pr.Gbps = P50(l2Gbps.Select(x => (double?)x));
            pr.Patterns = l2Gbps.Count > 0 ? [new PatternResult("L2", pr.Gbps ?? 0, Math.Round(Cv(l2Gbps), 2), l2Gbps.Count)] : [];
        }
        else if (kind == LabPhase.Memory)
        {
            pr.Patterns = perPattern.Where(p => p.Value.Count > 0)
                .Select(p => new PatternResult(p.Key.ToString(), P50(p.Value.Select(x => (double?)x)) ?? 0, Math.Round(Cv(p.Value), 2), p.Value.Count)).ToList();
            pr.Gbps = pr.Patterns.FirstOrDefault(p => p.Pattern == nameof(AccessPattern.Linear))?.Gbps;
        }
        else if (frozenMs.Count > 0)
        {
            frozenMs.Sort();
            if (kind == LabPhase.Latency) pr.LatencyNs = Math.Round(frozenMs[frozenMs.Count / 2] * 1e6 / ctrl.Current, 1);
            else
            {
                pr.Gflops = Math.Round(workSum / (msSum / 1000.0) / 1e9, 1);
                pr.Threads = (int)(threadSum / frozenMs.Count);
                if (pr.SmMhzP50 is > 0 && kind != LabPhase.Pulse) pr.GflopsPerMhz = Math.Round(pr.Gflops.Value / pr.SmMhzP50.Value, 3);
            }
        }
        return pr;
    }

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static double? P50(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x != null).Select(x => x!.Value).Order().ToList();
        return v.Count == 0 ? null : Math.Round(v[v.Count / 2], 1);
    }

    /// <summary>Coefficient of variation, %: the spread of one pattern's chunks (the tester's σ, relative).</summary>
    public static double Cv(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return 0;
        double mean = xs.Average();
        return mean <= 0 ? 0 : Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1)) / mean * 100;
    }

    /// <summary>The verdict on a run: any bad bit, wrong chain end or dead context fails it; one that stopped itself
    /// (<paramref name="interrupted"/>: the temperature limit, telemetry lost), never loaded the card for 10 s, or stopped
    /// early with nothing wrong, is inconclusive.</summary>
    public static (string Verdict, List<string> Reasons) Judge(IReadOnlyList<PhaseResult> phases, int planned, string? interrupted = null)
    {
        var reasons = new List<string>();
        foreach (var p in phases)
        {
            if (p.ComputeMismatches > 0) reasons.Add($"{p.Kind}: {p.ComputeMismatches} wrong floats (compute error)");
            if (p.MemoryErrors > 0) reasons.Add($"{p.Kind}: {p.MemoryErrors} bad words, first at word {p.FirstBadWord} (memory error)");
            if (p.ChainErrors > 0) reasons.Add($"{p.Kind}: {p.ChainErrors} chains ended on the wrong slot (memory error)");
            if (p.Error != null) reasons.Add($"{p.Kind}: {p.Error} (GPU fault)");
        }
        if (reasons.Count > 0) return (LabResult.Fail, reasons);
        if (interrupted != null) return (LabResult.Inconclusive, [interrupted]);
        if (phases.Count < planned || phases.Any(p => !p.Completed)) return (LabResult.Inconclusive, ["the run stopped before every phase finished"]);
        if (phases.Sum(p => p.Seconds) < 10) return (LabResult.Inconclusive, ["under 10 s of load"]);
        return (LabResult.Pass, []);
    }
}
