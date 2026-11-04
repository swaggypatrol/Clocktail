using System.Diagnostics;

namespace Clocktail.Lab;

/// <summary>
/// The lab's phases. Burn: every thread on fma_rot (steady power; steered to a board power when one is asked for). Light:
/// one thread in LightDiv, so few SMs draw current and the card climbs to the top of its curve (64 SMs: 2887 MHz at 360 W
/// on the reference card at stock). Pulse: one short full-grid launch per game-like frame at 30-120 fps (the load step at
/// the top of the curve). Memory: fill and verify the buffer in the five access orders. Latency: the pointer chase. L2: fill
/// and verify a 32 MiB region over and over, which stays in L2: the crossbar's bandwidth, the path XBAR clocks. Solo: the
/// integer soloist judged per pipe, played divisi under the power wall so the clock stays on the curve's point
/// (<see cref="SoloKernel"/>; 2026-10-27, from Etude): the core's most sensitive load, and the one that reaches the top. Leap:
/// the soloist on the whole chip, then on a quarter of it, over and over, so the clock leaps from the middle of the curve to its
/// top as it does when a game's heavy scene turns light (<see cref="LeapRules"/>; 2026-10-27, after six game hangs there).
/// </summary>
public enum LabPhase { Burn, Light, Pulse, Memory, Latency, L2, Solo, Leap }

/// <summary>What a run does. MaxTempC: the person's limit, when they gave one (none by default: infinity); the run stops when
/// the GPU reaches it (two readings in a row), as stress_test does;
/// TelemetryLostSeconds: it stops when NVML gives no temperature for that long, since a card nobody can watch is not
/// loaded. Either stop leaves the run inconclusive, not a pass and not a crash, with the reason in
/// <see cref="LabRunner.Interrupted"/>; every entry point (lab_test, the jobs' measurements) runs through it.</summary>
public sealed record LabConfig(IReadOnlyList<(LabPhase Kind, double Seconds)> Phases, double TargetW = 0, int LightDiv = 64,
    uint Seed = 0x5EED, int SampleMs = 100, int ChunkMs = 150, double PulseDuty = 0.6, double MaxTempC = double.PositiveInfinity, double TelemetryLostSeconds = 10)
{
    public static IReadOnlyList<(LabPhase, double)> Parse(string list, double seconds)
        => Clocktail.Domain.Lab.LabTestPlan.Parse(list, seconds).Select(s => (Enum.Parse<LabPhase>(s.Phase, true), s.Seconds)).ToArray();
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

    // The soloist (Solo only): the share of SMs that played in the hold and how many, units per chunk, the SMs' own clock
    // while they worked (median over SMs and chunks), the share of chunks in which every SM meant to play ran its full
    // occupancy, the board's enforced limit, whether the point held (null: it could not be told) and why not; the wrong
    // lanes by SM and pipe, and whether a lane lost its majority.
    public double? SoloBreadth { get; set; }
    public int? SoloSms { get; set; }
    public uint? SoloUnits { get; set; }
    public double? EffMhz { get; set; }
    public double? Fill { get; set; }
    public double? LimitW { get; set; }
    public bool? Held { get; set; }
    public string? HeldWhy { get; set; }
    public List<LaneMismatch>? LaneMismatches { get; set; }
    public bool? MajorityLost { get; set; }

    // The leap (Leap only; also SoloBreadth = the light half's share, EffMhz = the SMs' own clock in the light halves, Held =
    // whether the clock leapt often enough): the heavy-light cycles played and those whose clock leapt, the heavy halves'
    // settled clock and the light halves' peak (NVML, P50 over the cycles).
    public int? Leaps { get; set; }
    public int? Leapt { get; set; }
    public double? HeavyMhz { get; set; }
    public double? PeakMhz { get; set; }
}

/// <summary>Blocks of one SM that computed one pipe's lane wrong, over the phase.</summary>
public sealed record LaneMismatch(int Sm, string Lane, int Blocks);

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
        if (kind == LabPhase.Solo) return RunSolo(index, seconds, ct, pr, t0);
        if (kind == LabPhase.Leap) return RunLeap(index, seconds, ct, pr, t0);

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

    /// <summary>The busy time a soloist chunk aims at: well under the WDDM watchdog, long enough that launches cost little.</summary>
    public const double SoloChunkMs = 120;
    /// <summary>How long each window runs before its board power is read for the fit.</summary>
    public const double SoloFitSeconds = 3;

    /// <summary>
    /// The soloist: units calibrated to ~<see cref="SoloChunkMs"/> of busy time, then windows of <see cref="SoloFitSeconds"/> from
    /// the whole chip down, each read for its board power and fitted (<see cref="SoloRules.Next"/>): the share is cut while on
    /// the wall and held once off it. Every chunk is judged, the fit's too: a wrong lane anywhere fails the phase. The stretch
    /// since the last cut, the hold, decides <see cref="PhaseResult.Held"/>.
    /// </summary>
    private PhaseResult RunSolo(int index, double seconds, CancellationToken ct, PhaseResult pr, double t0)
    {
        // a card whose driver cannot compile the soloist: nothing ran, nothing failed (inconclusive, with the reason)
        if (engine.SoloUnavailable is { } why) { pr.HeldWhy = why; return FinishSolo(index, pr, t0, true, ct); }
        var solo = new Soloist(engine, pr, cfg.Seed ^ ((uint)index * 0x9E3779B9u));
        solo.Calibrate(ct);

        // Windows of SoloFitSeconds from the whole chip: one that sat on the wall cuts the share and starts the hold over, so
        // the hold is the stretch since the last cut, at one share, off the wall; it alone says whether the point held. A card
        // that warms onto the wall later in the phase is cut there too.
        double breadth = SoloRules.MaxBreadth;
        int holdFrom = SampleCount(), chunks = 0, fullChunks = 0;
        var effs = new List<double>();
        bool fitting = true;
        while (solo.Alive && !ct.IsCancellationRequested && _now() - t0 < seconds)
        {
            int from = SampleCount();
            double ws = _now();
            while (solo.Alive && !ct.IsCancellationRequested && _now() - ws < SoloFitSeconds && _now() - t0 < seconds)
            {
                var o = solo.Play(breadth);
                if (!solo.Alive) break;
                chunks++;
                if (o.Judgement!.Fill >= 1 - 1e-9) fullChunks++;
                if (o.Judgement.EffMhz is { } e) effs.Add(e);
            }
            if (!solo.Alive || _now() - ws < SoloFitSeconds * 0.5) break;   // the phase's last scrap is too short to read
            var (watts, limit) = WindowPower(index, from);
            if (watts == null || limit == null) continue;
            var next = SoloRules.Next(breadth, watts.Value, limit.Value);
            if (next == null)
            {
                if (fitting) log?.Invoke("solo fit: on the wall at the lightest share; held there");
                fitting = false;
                continue;
            }
            if (Math.Abs(next.Value - breadth) < 1e-9)
            {
                if (fitting) log?.Invoke(Inv($"solo fit: {breadth:P0} of the SMs drew {watts:0} W of {limit:0}: held there"));
                fitting = false;
                continue;
            }
            log?.Invoke(Inv($"solo fit: {breadth:P0} of the SMs drew {watts:0} W of {limit:0}: {next:P0} next"));
            breadth = next.Value;
            fitting = true;
            holdFrom = SampleCount(); chunks = 0; fullChunks = 0; effs.Clear();
        }
        pr.SoloBreadth = breadth;
        pr.SoloSms = SoloRules.Players(engine.SmCount, breadth);
        effs.Sort();
        pr.EffMhz = effs.Count == 0 ? null : Math.Round(effs[effs.Count / 2], 1);
        pr.Fill = chunks == 0 ? null : Math.Round((double)fullChunks / chunks, 3);
        List<LabSample> hold;
        lock (_lock) hold = _samples.Skip(holdFrom).Where(x => x.Phase == index).ToList();
        double? smP50 = P50(hold.Select(x => (double?)x.SmMhz)), wattsP50 = P50(hold.Select(x => x.BoardW));
        pr.LimitW = P50(hold.Select(x => x.LimitW));
        if (chunks > 0) (pr.Held, pr.HeldWhy) = SoloRules.Held(pr.EffMhz, smP50, pr.Fill ?? 0, wattsP50, pr.LimitW);
        solo.Report();
        return FinishSolo(index, pr, t0, solo.Alive, ct);
    }

    /// <summary>
    /// The leap (<see cref="LeapRules"/>): units calibrated as the soloist's, then cycles of <see cref="LeapRules.HeavySeconds"/>
    /// on the whole chip and <see cref="LeapRules.LightSeconds"/> on <see cref="LeapRules.LightBreadth"/> of it, the window moving
    /// on every chunk. Every chunk is judged, a wrong lane fails the phase and a hang shows as the GPU fault it leaves. Each
    /// cycle's heavy half (its later half, once the clock has come down) and light half (its peak) say whether the clock leapt.
    /// </summary>
    private PhaseResult RunLeap(int index, double seconds, CancellationToken ct, PhaseResult pr, double t0)
    {
        if (engine.SoloUnavailable is { } why) { pr.HeldWhy = why; return FinishSolo(index, pr, t0, true, ct); }
        var solo = new Soloist(engine, pr, cfg.Seed ^ ((uint)index * 0x9E3779B9u));
        solo.Calibrate(ct);
        var heavies = new List<double>();
        var peaks = new List<double>();
        var effs = new List<double>();
        int cycles = 0, leapt = 0;
        while (solo.Alive && !ct.IsCancellationRequested && _now() - t0 < seconds)
        {
            double hs = _now();
            int heavyFrom = SampleCount();
            while (solo.Alive && !ct.IsCancellationRequested && _now() - hs < LeapRules.HeavySeconds) solo.Play(1.0);
            int heavyTo = SampleCount();
            if (!solo.Alive || ct.IsCancellationRequested) break;
            double ls = _now();
            while (solo.Alive && !ct.IsCancellationRequested && _now() - ls < LeapRules.LightSeconds)
                if (solo.Play(LeapRules.LightBreadth).Judgement?.EffMhz is { } e) effs.Add(e);
            if (!solo.Alive || ct.IsCancellationRequested) break;
            List<LabSample> heavy, light;
            lock (_lock)
            {
                heavy = _samples.Skip(heavyFrom).Take(heavyTo - heavyFrom).Where(x => x.Phase == index).ToList();
                light = _samples.Skip(heavyTo).Where(x => x.Phase == index).ToList();
            }
            double? heavyMhz = P50(heavy.Skip(heavy.Count / 2).Select(x => (double?)x.SmMhz));
            double? peakMhz = light.Max(x => (double?)x.SmMhz);
            cycles++;
            if (heavyMhz is { } h) heavies.Add(h);
            if (peakMhz is { } p) peaks.Add(p);
            if (LeapRules.Leapt(heavyMhz, peakMhz)) leapt++;
        }
        pr.Leaps = cycles;
        pr.Leapt = leapt;
        pr.HeavyMhz = P50(heavies.Select(x => (double?)x));
        pr.PeakMhz = P50(peaks.Select(x => (double?)x));
        pr.SoloBreadth = LeapRules.LightBreadth;
        pr.SoloSms = SoloRules.Players(engine.SmCount, LeapRules.LightBreadth);
        effs.Sort();
        pr.EffMhz = effs.Count == 0 ? null : Math.Round(effs[effs.Count / 2], 1);
        if (solo.Alive) (pr.Held, pr.HeldWhy) = LeapRules.Held(cycles, leapt, pr.HeavyMhz, pr.PeakMhz);
        log?.Invoke(Inv($"leap: {leapt} of {cycles} cycles leapt, {pr.HeavyMhz} MHz heavy, {pr.PeakMhz} MHz peak"));
        solo.Report();
        return FinishSolo(index, pr, t0, solo.Alive, ct);
    }

    private PhaseResult FinishSolo(int index, PhaseResult pr, double t0, bool alive, CancellationToken ct)
    {
        pr.Seconds = Math.Round(_now() - t0, 1);
        pr.Completed = alive && pr.Error == null && !ct.IsCancellationRequested;
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
        return pr;
    }

    /// <summary>The soloist's launches for one phase: the rotating window, the per-lane tally, the units calibrated on the
    /// blocks' own busy time (the silent SMs' hold is not work).</summary>
    private sealed class Soloist(ILoadEngine engine, PhaseResult pr, uint seed)
    {
        private readonly Dictionary<(int Sm, int Lane), int> _wrong = [];
        private long _chunk;
        private uint _seed = seed, _units = 64;
        private bool _lost;
        public bool Alive { get; private set; } = true;

        public SoloOutcome Play(double breadth)
        {
            int players = SoloRules.Players(engine.SmCount, breadth);
            var playing = SoloRules.Window(engine.SmCount, players, _chunk++);
            var o = engine.RunSolo(_units, _seed++, SoloRules.Mask(playing), playing);
            pr.Launches++;
            if (o.Error != null) { pr.Error = o.Error; Alive = false; return o; }
            var j = o.Judgement!;
            foreach (var (_, sm, lane) in j.Mismatches) _wrong[(sm, lane)] = _wrong.GetValueOrDefault((sm, lane)) + 1;
            pr.ComputeMismatches += j.Mismatches.Count;
            _lost |= j.MajorityLost;
            return o;
        }

        /// <summary>Three launches on the whole chip, the units refined twice toward <see cref="SoloChunkMs"/>.</summary>
        public void Calibrate(CancellationToken ct)
        {
            for (int k = 0; k < 3 && Alive && !ct.IsCancellationRequested; k++)
            {
                var o = Play(1.0);
                if (!Alive) break;
                double perUnit = Math.Max(o.Judgement!.BusyMsMax, 0.001) / _units;
                _units = (uint)Math.Clamp(SoloChunkMs / perUnit, 8, 1u << 22);
            }
            pr.SoloUnits = _units;
        }

        public void Report()
        {
            pr.LaneMismatches = _wrong.OrderBy(kv => kv.Key.Sm).ThenBy(kv => kv.Key.Lane)
                .Select(kv => new LaneMismatch(kv.Key.Sm, SoloKernel.LaneNames[kv.Key.Lane], kv.Value)).ToList();
            pr.MajorityLost = _lost;
        }
    }

    private int SampleCount() { lock (_lock) return _samples.Count; }

    /// <summary>The board power and enforced limit over this phase's samples since <paramref name="from"/> (P50 each).</summary>
    private (double? Watts, double? Limit) WindowPower(int index, int from)
    {
        List<LabSample> w;
        lock (_lock) w = _samples.Skip(from).Where(x => x.Phase == index).ToList();
        return (P50(w.Select(x => x.BoardW)), P50(w.Select(x => x.LimitW)));
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
            if (p.Kind is LabPhase.Solo or LabPhase.Leap && p.ComputeMismatches > 0)
                reasons.Add($"{p.Kind}: {p.ComputeMismatches} wrong lane{(p.ComputeMismatches == 1 ? "" : "s")} (compute error): " +
                    string.Join(", ", (p.LaneMismatches ?? []).Select(m => $"SM {m.Sm} {m.Lane} x{m.Blocks}")) +
                    (p.MajorityLost == true ? "; a lane lost its majority" : ""));
            else if (p.ComputeMismatches > 0) reasons.Add($"{p.Kind}: {p.ComputeMismatches} wrong floats (compute error)");
            if (p.MemoryErrors > 0) reasons.Add($"{p.Kind}: {p.MemoryErrors} bad words, first at word {p.FirstBadWord} (memory error)");
            if (p.ChainErrors > 0) reasons.Add($"{p.Kind}: {p.ChainErrors} chains ended on the wrong slot (memory error)");
            if (p.Error != null) reasons.Add($"{p.Kind}: {p.Error} (GPU fault)");
        }
        if (reasons.Count > 0) return (LabResult.Fail, reasons);
        if (interrupted != null) return (LabResult.Inconclusive, [interrupted]);
        if (phases.Count < planned || phases.Any(p => !p.Completed)) return (LabResult.Inconclusive, ["the run stopped before every phase finished"]);
        // a soloist whose clock never held its point passed nothing: the point did not run; a leap whose clock never leapt
        // made no jump to survive
        var unheld = phases.Where(p => p.Kind is LabPhase.Solo or LabPhase.Leap && p.Held != true).ToList();
        if (unheld.Count > 0)
            return (LabResult.Inconclusive, unheld.Select(p => p.Held == false
                ? p.Kind == LabPhase.Leap ? $"{p.Kind}: {p.HeldWhy}: no evidence" : $"{p.Kind}: the point did not hold ({p.HeldWhy}): no evidence"
                : $"{p.Kind}: could not be judged ({p.HeldWhy ?? "no hold measured"})").ToList());
        if (phases.Sum(p => p.Seconds) < 10) return (LabResult.Inconclusive, ["under 10 s of load"]);
        return (LabResult.Pass, []);
    }
}
