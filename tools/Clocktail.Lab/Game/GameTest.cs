using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Game;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;

namespace Clocktail.Lab.Game;

/// <summary>Job options only. Actual settings and restoration values are captured by the card host under the lease.</summary>
public sealed record GameSpec(string StableId, string Pci, string Exe, int Seconds, int Seed,
    IReadOnlyList<string> Patterns, IReadOnlyList<string> Levers, int WindowPct = 90, int? MaxScale = null,
    int? MaxTempC = null, int GraceSeconds = 45, string? Note = null,
    DateTimeOffset? TrialAt = null, DateTimeOffset? SessionAt = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StableId) || VulkanDevices.Address(Pci) == null || string.IsNullOrWhiteSpace(Exe) || SessionAt == null)
            throw new ArgumentException("the game spec must identify the card, PCI address, game executable and session");
        if (Seconds is < 60 or > 240 * 60 || WindowPct is < 90 or > 100 || MaxScale is < 100 or > 200 || MaxTempC is < 1 or > 120 || GraceSeconds < 0)
            throw new ArgumentException("the game spec has an out-of-range duration, window, scale, temperature or reset grace");
        if (Patterns == null || Patterns.Count == 0 || Patterns.Any(p => !GameCatalog.AllPatterns.Contains(p))
            || Levers == null || Levers.Any(l => !GameCatalog.AllLevers.Contains(l)))
            throw new ArgumentException("the game spec has invalid patterns or levers");
    }

}

/// <summary>One look at the card (NVML, about 4 a second).</summary>
public sealed record GameSample(DateTime At, uint? Mhz, uint? MemMhz, double? BoardW, double? LimitW, uint? TempC, uint? UtilPct, int? PState);

/// <summary>NVML on the card under test, about 4 times a second on a thread of its own, every look to samples.jsonl. Reads only.</summary>
public sealed class GameSampler : IDisposable
{
    private readonly StreamWriter _out;
    private readonly List<GameSample> _all = [];
    private readonly Thread _thread;
    private volatile bool _stop;
    private IReadOnlyList<uint>? _graphicsPids;
    private string? _failure;

    public GameSampler(string pci, string path) : this(() => new NvmlSampler(pci), path) { }

    public GameSampler(Func<ISampler> open, string path)
    {
        _out = new StreamWriter(path, append: true) { AutoFlush = true };
        _thread = new Thread(() => Loop(open)) { IsBackground = true, Name = "game-sampler" };
        _thread.Start();
    }

    private void Loop(Func<ISampler> open)
    {
        try
        {
            // All native calls and disposal stay on this thread. Never unload NVML under a call still blocked in it.
            using var nvml = open();
            var nextProcesses = DateTime.MinValue;
            while (!_stop)
            {
                var s = nvml.Read(0, 0);
                var g = new GameSample(DateTime.UtcNow, s.SmMhz, s.MemMhz, s.BoardW, s.LimitW, s.TempC, s.UtilPct, s.PState);
                lock (_all) _all.Add(g);
                _out.WriteLine(JsonSerializer.Serialize(g, GameTest.Line));
                if (g.At >= nextProcesses)
                {
                    var pids = (nvml as NvmlSampler)?.GraphicsPids();
                    lock (_all) _graphicsPids = pids;
                    nextProcesses = g.At.AddSeconds(1);
                }
                Thread.Sleep(250);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (_all) _failure = $"GPU telemetry stopped: {e.GetType().Name}: {e.Message}";
        }
        finally { try { _out.Dispose(); } catch (IOException) { } }
    }

    public string? Failure { get { lock (_all) return _failure; } }

    public IReadOnlyList<GameSample> All() { lock (_all) return [.. _all]; }

    /// <summary>The lower of the last two temperatures: two looks in a row at or over a limit, as lab_test stops on.</summary>
    public double? TempC()
    {
        lock (_all) return FreshTemperature(_all, DateTime.UtcNow);
    }

    public static double? FreshTemperature(IReadOnlyList<GameSample> samples, DateTime now) =>
        samples.Count >= 2 && now - samples[^2].At <= TimeSpan.FromSeconds(5)
            && samples[^1].TempC is uint a && samples[^2].TempC is uint b ? Math.Min(a, b) : null;

    public uint? LastMhz() { lock (_all) return _all.Count > 0 ? _all[^1].Mhz : null; }

    public IReadOnlyList<uint>? GraphicsPids() { lock (_all) return _graphicsPids; }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));   // if native code is stuck, leave its resources to that thread/process
    }
}

/// <summary>Where a game test stands, written whole to status.json (lab_status shows it).</summary>
public sealed record GameStatus(string Job, string State, DateTime StartedUtc, DateTime UpdatedUtc, string? Result, string? Why, string? Next,
    int SecondsTarget, double SecondsPlayed, string? Doing, string? Window, string? Build, IReadOnlyList<string> Patterns, IReadOnlyList<string> Levers,
    IReadOnlyList<string> Epochs, IReadOnlyList<string> LeversApplied, object? PerPattern, object? Coverage, object? Card,
    IReadOnlyList<object> LastSteps, bool? TrialInForce, bool? CrashRecorded, object Files, string? RecoveryError = null)
{
    public const string Running = "running", Done = "done", Stopped = "stopped", Failed = "failed";
}

/// <summary>
/// The game test: Quake II RTX, the window at 90 % of the primary screen or more, played by a seeded script of load patterns
/// (<see cref="GameScript"/>) for the minutes asked, with the card-side levers (the wall, the fans, Boost Lock) on a slower
/// clock as trials through the service. It holds judge.lock for the whole run, so no other write lands under it; its own
/// levers carry its lease. A failure (a driver reset, the game crashing or freezing under load, a non-number in the frame)
/// is the trial's crash, as a game crash to the desktop is: the service records a reset itself, the rest is recorded here,
/// against the trial in force only if it is still the one the test measured. Anything else that ends a run (a thermal stop,
/// the window kept out of front, the game closed cleanly, a setting the game did not take) says nothing about the card and
/// records nothing. At the end the trial the test began with is put back (or the daily profile, when it began on it).
/// </summary>
public sealed class GameTest(GameSpec spec, string jobDir, string lab, Action<string>? log = null)
    : LabJob(spec.StableId, spec.Pci, spec.GraceSeconds, jobDir, lab)
{
    internal static readonly JsonSerializerOptions Line = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly DateTime _started = DateTime.UtcNow;
    private GamePlan? _plan;
    private GameRunner? _runner;
    private GameSampler? _sampler;
    private WindowPlan? _window;
    private string? _build, _home, _doing;
    private GameSnapshot? _baseline;
    private string? _recoveryError;
    private DateTime? _loadStarted;
    private int? _warmRelease;
    private readonly List<string> _applied = [];
    private Process? _game;
    private string? _halt;
    private StreamWriter? _frames, _steps, _events;

    public int Run()
    {
        using var heartbeat = Heartbeat();
        using var card = ClaimCard();
        if (card == null) return Finish(GameStatus.Failed, null, "another lab job is running on this card: lab_status, or lab_stop", null);
        try
        {
            spec.Validate();
            if (!OperatingSystem.IsWindows()) return Finish(GameStatus.Failed, null, "the game test runs on Windows", null);
            if (TdrSettings.Read() is { Unsafe: true } tdr)
                return Finish(GameStatus.Failed, "unjudged", tdr.Describe() + ": unattended game testing is refused", null);
            Desktop.PhysicalPixels();
            // The game and its data: the demo the script plays and the live maps it may use.
            if (!File.Exists(spec.Exe)) return Finish(GameStatus.Failed, null, $"Quake II RTX is not at {spec.Exe} any more", null);
            string sha = QuakeInstall.Sha256(spec.Exe);
            _build = sha == QuakeLocator.KnownSha256 ? "Quake II RTX 1.8.0 (the inspected build)" : $"Quake II RTX, an uninspected build ({sha[..12]}…): its answers are checked as it runs";
            var (demos, maps) = QuakeInstall.Content(QuakeInstall.PakEntries(Path.Combine(Path.GetDirectoryName(spec.Exe)!, "baseq2")));
            string? demo = demos.Contains("q2demo1") ? "q2demo1" : demos.FirstOrDefault();
            if (demo == null) return Finish(GameStatus.Failed, null, "the game's data has no demo (demos/*.dm2 in baseq2's .pak files): the shareware pak0.pak Quake II RTX ships is needed", null);
            if (Process.GetProcessesByName("q2rtx") is { Length: > 0 } running)
            {
                foreach (var r in running) r.Dispose();
                return Finish(GameStatus.Failed, null, "Quake II RTX is running already: close it first (the test controls only a game it starts)", null);
            }
            var (sw, sh, hz) = Desktop.PrimaryScreen();
            _window = WindowPlan.For(sw, sh, spec.WindowPct, hz);
            var info = GameBroker("info");
            if (info is not { Ok: true, Result: { } ir } || !ir.TryGetProperty("target", out var t))
                return Finish(GameStatus.Failed, "unjudged", "the card host lacks guarded game sessions", null);
            var identity = t.Deserialize<Clocktail.Hw.GpuTarget>() ?? throw new InvalidDataException("missing target identity");
            if (!Desktop.ConfirmModel(identity)) return Finish(GameStatus.Stopped, "unjudged", "the unvalidated model was not confirmed", null);
            Save(GameStatus.Running, null, null, null, "waiting for the card (judge.lock)");

            using var lease = Hold(log);
            if (lease == null)
                return Finish(StopAsked ? GameStatus.Stopped : GameStatus.Failed, null, StopAsked ? "stopped (lab_stop)" : $"another run held the card for {LeaseWaitMinutes} minutes", null);
            var begin = GameBroker("begin", new(Pci: spec.Pci, SessionAt: spec.SessionAt, TrialAt: spec.TrialAt, Levers: spec.Levers));
            if (begin is not { Ok: true, Result: { } result } || !result.TryGetProperty("baseline", out var b))
            {
                if (begin == null || begin.Ok)
                {
                    var cleanup = GameBroker("finish", new(Mode: "baseline"));
                    if (cleanup is not { Ok: true }) _recoveryError = cleanup?.Error ?? "baseline registration/cleanup could not be confirmed; keep any card-host recovery record";
                }
                return Finish(GameStatus.Failed, "unjudged", begin?.Error ?? "the guarded card host did not return a locked baseline", null);
            }
            _baseline = b.Deserialize<GameSnapshot>() ?? throw new InvalidDataException("empty card-host baseline");
            try
            {
                _plan = GameScript.Plan(new GamePlanOptions(spec.Seconds, spec.Seed, spec.Patterns, maps,
                    spec.MaxScale ?? QuakeLocator.MaxScaleFor(_baseline.VramBytes), spec.Levers, _baseline.PowerMw, _baseline.MinPowerMw,
                    hz, WarmC: spec.MaxTempC is { } m ? Math.Min(76, m - 6) : 76));
                File.WriteAllText(Path.Combine(JobDir, "baseline.json"), b.GetRawText());
                File.WriteAllText(Path.Combine(JobDir, "plan.json"), JsonSerializer.Serialize(_plan, StatusJson));
                return RunHeld(lease, demo);
            }
            catch
            {
                GameBroker("finish", new(Mode: "baseline")); // the host keeps the record if verification fails
                throw;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Finish(GameStatus.Failed, "unjudged", $"the job stopped: {e.GetType().Name}: {e.Message}", "read the job log and card state before running again");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private int RunHeld(IDisposable lease, string demo)
    {
        GameOutcome outcome;
        bool stopped = false;
        try
        {
            _frames = Writer("frames.jsonl");
            _steps = Writer("steps.jsonl");
            _events = Writer("events.jsonl");
            _sampler = new GameSampler(spec.Pci, Path.Combine(JobDir, "samples.jsonl"));

            _home = QuakeInstall.MakeHome(JobDir);
            int port = QuakeInstall.FreePort();
            string password = QuakeRcon.NewPassword();
            using var rcon = new QuakeRcon(port, password);
            using var launch = SuspendedGame.Start(QuakeInstall.StartInfo(spec.Exe, _home, port, password, _window!.Geometry, demo));
            _game = launch.Process;
            if (!Contain(_game)) throw new InvalidOperationException("Windows would not contain the suspended game: it was not resumed");
            var registered = GameBroker("launch", new(ProcessId: _game.Id, ProcessStartedAt: new DateTimeOffset(_game.StartTime.ToUniversalTime())));
            if (registered is not { Ok: true }) throw new InvalidOperationException(registered?.Error ?? "game process registration was not confirmed: it was not resumed");
            _loadStarted = DateTime.UtcNow;
            launch.Resume();
            Desktop.StayAwake();
            Broker("note", Inv($"game_test started: Quake II RTX, {spec.Seconds / 60.0:0.#} min, seed {spec.Seed}, patterns [{string.Join(", ", spec.Patterns)}], levers [{string.Join(", ", spec.Levers)}], {_window!.Words} ({RunId})") + (spec.Note != null ? $". Purpose: {spec.Note}" : ""));
            Save(GameStatus.Running, null, null, null, "starting Quake II RTX");
            _runner = new GameRunner(new World(this, rcon), new GameTimings(ResetGraceSeconds: Math.Max(30, spec.GraceSeconds), MaxTempC: spec.MaxTempC));
            outcome = _runner.Run(_plan!, demo);
            // An orderly exit first (the game tears its Vulkan device down), then the kill below for one that does not go.
            if (outcome.End != GameEnd.Failed && _game is { HasExited: false })
            {
                rcon.Send("quit");
                _game.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            outcome = new GameOutcome(GameEnd.Unjudged, $"the test stopped: {e.GetType().Name}: {e.Message}", Credited: _runner?.Credited ?? 0);
        }
        finally
        {
            stopped = StopGame();
            if (OperatingSystem.IsWindows()) Desktop.LetSleep();
        }
        if (!stopped)
        {
            _recoveryError = "the game could not be confirmed stopped; no restoration writes were attempted under its load";
            return Finish(GameStatus.Failed, "unjudged", _recoveryError, "close this job's Quake process, then back_to_daily; do not mark stability");
        }
        return Conclude(outcome, lease);
    }

    private StreamWriter Writer(string name) => new(Path.Combine(JobDir, name), append: true) { AutoFlush = true };

    private bool StopGame()
    {
        try { return _game == null || ChildProcess.Stop(_game, TimeSpan.FromSeconds(10)); }
        finally
        {
            _game?.Dispose();
            _sampler?.Dispose();
            foreach (var w in new[] { _frames, _steps, _events }) try { w?.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>
    /// After the game is gone: a failure is the trial's crash (a reset the service records itself, after judge.lock is given
    /// back so it can put the daily profile on; anything else recorded here, only against the trial the test measured), and
    /// a run that did not fail puts back what it began with. The summary goes in the tuning log.
    /// </summary>
    private int Conclude(GameOutcome outcome, IDisposable? lease)
    {
        if (DriverResets() is > 0 and var resets)
            outcome = outcome with { End = GameEnd.Failed, FailKind = GameOutcome.Reset, Why = $"a target GPU reset ({resets}); {outcome.Why}" };
        if (_halt != null && outcome.End != GameEnd.Failed)
            outcome = outcome with { End = GameEnd.Unjudged, Why = outcome.Why + "; " + _halt };
        if (_home != null) outcome = outcome.AfterGameLog(QuakeInstall.LogTail(_home, 64));
        for (int i = 0; i <= 8 && outcome.End == GameEnd.Failed && outcome.FailKind != GameOutcome.Reset; i++)
        {
            if (DriverResets() is > 0) outcome = outcome with { FailKind = GameOutcome.Reset, Why = outcome.Why + "; a target GPU reset followed" };
            else if (i < 8) Thread.Sleep(1000);
        }
        BrokerResponse? restored;
        if (outcome.FailKind == GameOutcome.Reset)
        {
            lease?.Dispose(); // the service must be able to perform its reset recovery
            restored = null;
            var until = DateTime.UtcNow.AddSeconds(60);
            do
            {
                using (var recoveryLease = Hold(log))
                {
                    if (recoveryLease == null) break;
                    restored = GameBroker("finish", new(Mode: "reset"));
                    if (restored is { Ok: true }) break;
                }
                Thread.Sleep(2000); // no lease held while the service is recovering
            } while (!StopAsked && DateTime.UtcNow < until);
        }
        else restored = GameBroker("finish", new(Mode: outcome.End == GameEnd.Failed ? "failed" : "baseline", Note: outcome.Why));
        bool verified = restored is { Ok: true, Result: { } r } && r.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
        string? mode = restored?.Result is { } rr && rr.TryGetProperty("mode", out var m) ? m.GetString() : null;
        if (!verified) _recoveryError = restored?.Error ?? "no verified restoration response; the card host retained its recovery record";
        else if (outcome.End == GameEnd.Survived && mode != "baseline") _recoveryError = "daily settings were restored instead of the tested trial";
        outcome = outcome.AfterRecovery(_recoveryError);
        lease?.Dispose();
        string? finalTrial = TrialAt(out bool answered);
        bool? inForce = answered ? finalTrial != null : null;
        bool? recorded = restored?.Result is { } cr && cr.TryGetProperty("crash_recorded", out var c) ? c.GetBoolean() : null;
        var (perPattern, coverage, cardStats) = Summaries();
        string result = outcome.End switch { GameEnd.Survived => "survived", GameEnd.Failed => "failed", GameEnd.Stopped => "stopped", _ => "unjudged" };
        Broker("note", Inv($"game_test {result}: {outcome.Credited:0}/{spec.Seconds} s: {outcome.Why}") + CoverageWords(coverage));
        string next = _recoveryError != null ? "recovery is pending: read card_settings and the job log; do not mark stability or delete game-recovery.json"
            : outcome.End == GameEnd.Survived ? "the locked baseline was restored and independently read back; survival is not proof of stability"
            : "read the recorded evidence before the next adjustment";
        string state = _recoveryError != null ? GameStatus.Failed : outcome.End == GameEnd.Stopped ? GameStatus.Stopped : GameStatus.Done;
        return Finish(state, result, outcome.Why, next, perPattern, coverage, cardStats, inForce, recorded);
    }

    private BrokerResponse? GameBroker(string kind, GameCommand? command = null) =>
        Broker(kind, command == null ? null : JsonSerializer.Serialize(command), BrokerRequest.OpGame);

    private bool SameTrial() => GameBroker("check") is { Ok: true };

    private int? DriverResets()
    {
        if (!OperatingSystem.IsWindows() || _loadStarted == null || _baseline == null) return null;
        try
        {
            // Exactly one NVIDIA adapter is guaranteed by the card host, so a reset not provably another vendor's is this card's.
            var events = SystemLog.CountTarget(_baseline.Target, _loadStarted.Value);
            return events.Target + events.Ambiguous;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            _halt = "target reset evidence is unavailable: " + e.Message;
            return null;
        }
    }

    /// <summary>Per pattern, the coverage of the jump onto the curve's top, and the card's extremes, from the steps and samples.</summary>
    private (object? PerPattern, Coverage? Coverage, object? Card) Summaries()
    {
        if (_runner == null || _sampler == null) return (null, null, null);
        var steps = _runner.Results;
        var samples = _sampler.All();
        static double? P50(IEnumerable<double> xs) { var o = xs.Order().ToList(); return o.Count == 0 ? null : Math.Round(o[o.Count / 2], 1); }
        var perPattern = steps.GroupBy(s => s.Pattern).ToDictionary(g => g.Key, g =>
        {
            var w = g.SelectMany(s => samples.Where(x => x.At >= s.StartUtc && x.At <= s.EndUtc)).ToList();
            return new
            {
                steps = g.Count(), seconds = Math.Round(g.Sum(s => s.Credited), 1),
                fps_p50 = P50(g.Where(s => s.FpsP50 != null).Select(s => (double)s.FpsP50!.Value)),
                clock_p50_mhz = P50(w.Where(x => x.Mhz != null).Select(x => (double)x.Mhz!.Value)),
                clock_max_mhz = w.Max(x => x.Mhz),
                board_w_p50 = P50(w.Where(x => x.BoardW != null).Select(x => x.BoardW!.Value)),
                temp_max_c = w.Max(x => x.TempC),
            };
        });
        var coverage = Cover(steps, samples, _baseline?.TopMhz);
        object card = new
        {
            clock_max_mhz = samples.Max(x => x.Mhz), board_w_max = samples.Max(x => x.BoardW), temp_max_c = samples.Max(x => x.TempC),
            samples = samples.Count,
        };
        return (perPattern, coverage, card);
    }

    /// <summary>The jump onto the top as it happened: heavy → light/capped/idle transitions, how many leapt (the clock in the
    /// light step's first 1.5 s at least 90 MHz over the heavy step's last second, as the lab's leap counts it), and the seconds
    /// observed within 60 MHz of the curve's top. This telemetry estimate is not foreground-credited play time.</summary>
    public sealed record Coverage(int Transitions, int Leapt, int? TopMhz, double SecondsNearTop, uint? PeakMhz);

    public static Coverage Cover(IReadOnlyList<StepResult> steps, IReadOnlyList<GameSample> samples, int? topMhz)
    {
        int transitions = 0, leapt = 0;
        for (int i = 1; i < steps.Count; i++)
        {
            var a = steps[i - 1];
            var b = steps[i];
            if (a.Kind != GameStep.Heavy || b.Kind is not (GameStep.Light or GameStep.Capped or GameStep.Idle)) continue;
            var heavy = samples.Where(x => x.At >= a.StartUtc && x.At >= a.EndUtc.AddSeconds(-1) && x.At <= a.EndUtc && x.Mhz != null).Select(x => x.Mhz!.Value).Order().ToList();
            var light = samples.Where(x => x.At > b.StartUtc && x.At <= b.EndUtc && x.At <= b.StartUtc.AddSeconds(1.5) && x.Mhz != null).Select(x => x.Mhz!.Value).ToList();
            if (heavy.Count == 0 || light.Count == 0) continue;
            transitions++;
            if (light.Max() >= heavy[heavy.Count / 2] + 90) leapt++;
        }
        double near = 0;
        if (topMhz is int top)
            for (int i = 1; i < samples.Count; i++)
                if (samples[i].Mhz >= top - 60 && samples[i].UtilPct >= 30)
                    near += Math.Clamp((samples[i].At - samples[i - 1].At).TotalSeconds, 0, 0.5);
        return new Coverage(transitions, leapt, topMhz, Math.Round(near, 1), samples.Max(x => x.Mhz));
    }

    private static string CoverageWords(Coverage? c) => c == null ? ""
        : Inv($"; heavy → light jumps {c.Transitions}, {c.Leapt} leapt (90 MHz or more)") + (c.TopMhz is int t ? Inv($", {c.SecondsNearTop:0} sampled s within 60 MHz of the curve's top ({t} MHz)") : "") + (c.PeakMhz is uint p ? Inv($", peak {p} MHz") : "");

    private void Log(string line) => log?.Invoke(line);

    private int Finish(string state, string? result, string why, string? next, object? perPattern = null, Coverage? coverage = null, object? cardStats = null,
        bool? trialInForce = null, bool? recorded = null)
    {
        Save(state, result, why, next, null, perPattern, coverage, cardStats, trialInForce, recorded);
        Log($"{state}: {why}");
        return state == GameStatus.Failed ? 1 : 0;
    }

    private void Save(string state, string? result, string? why, string? next, string? doing, object? perPattern = null, Coverage? coverage = null,
        object? cardStats = null, bool? trialInForce = null, bool? recorded = null)
    {
        if (doing != null) _doing = doing;
        var last = _runner?.Results.TakeLast(8).Select(s => (object)new { s.Index, s.Pattern, s.Name, s.Credited, fps = s.FpsP50, scale = s.ScaleMax, s.Note }).ToList() ?? [];
        SaveStatus(new GameStatus(RunId, state, _started, DateTime.UtcNow, result, why, next, spec.Seconds, Math.Round(_runner?.Credited ?? 0, 1),
            state == GameStatus.Running ? _doing : null, _window?.Words, _build, spec.Patterns, spec.Levers, _plan?.Epochs ?? [], _applied,
            perPattern, coverage, cardStats, last, trialInForce, recorded, new
            {
                baseline = Path.Combine(JobDir, "baseline.json"), log = Path.Combine(JobDir, "sweep.log"), plan = Path.Combine(JobDir, "plan.json"), steps = Path.Combine(JobDir, "steps.jsonl"),
                frames = Path.Combine(JobDir, "frames.jsonl"), samples = Path.Combine(JobDir, "samples.jsonl"),
                game_log = _home != null ? Path.Combine(_home, "baseq2", "logs", "console.log") : null,
            }, _recoveryError));
    }

    /// <summary>The real world: the game over rcon, its process and window, the System log, NVML, the service.</summary>
    private sealed class World(GameTest job, QuakeRcon rcon) : IGameWorld
    {
        private DateTime _lastStatus = DateTime.MinValue;
        private int? _resets;
        private DateTime _resetsAt = DateTime.MinValue, _identityAt = DateTime.MinValue;

        public DateTime Now => DateTime.UtcNow;
        public void Sleep(TimeSpan t) => Thread.Sleep(t);
        public bool StopAsked => job.StopAsked;
        public void Send(string command) => rcon.Send(command);

        public void Apply(int step, IReadOnlyList<(string Name, string Value)> cvars)
        {
            string name = $"s{step:0000}.cfg";
            File.WriteAllLines(Path.Combine(job._home!, "baseq2", "ct", name), cvars.Select(c => $"set {c.Name} {c.Value}"));
            rcon.Send("exec ct/" + name);
            var s = job._plan!.Steps[step];
            job._doing = Inv($"{s.Pattern}: {s.Name}, {s.Render.Words()} ({s.Seconds:0.##} s)");
            if (DateTime.UtcNow - _lastStatus > TimeSpan.FromSeconds(5))
            {
                _lastStatus = DateTime.UtcNow;
                job.Save(GameStatus.Running, null, null, null, job._doing);
            }
        }

        public GameFrame? Query() => rcon.Query();
        public int? ExitCode
        {
            get
            {
                try { return job._game is { HasExited: true } g ? g.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
        }

        public bool InFront() => OperatingSystem.IsWindows() && job._game != null && Desktop.InFront(job._game.Id);
        public void ToFront()
        {
            if (OperatingSystem.IsWindows() && job._game != null) Desktop.ToFront(job._game.Id);
        }

        public int? DriverResets()
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (DateTime.UtcNow - _resetsAt < TimeSpan.FromSeconds(1)) return _resets;
            _resetsAt = DateTime.UtcNow;
            return _resets = job.DriverResets();
        }

        public double? TempC() => job._sampler?.TempC();

        public string? Lever(Lever lever, GameStep step) => job.Write(lever, step);

        public string? Halt => job._halt ?? job._sampler?.Failure;

        public void Govern()
        {
            if (Now - _identityAt >= TimeSpan.FromSeconds(15))
            {
                _identityAt = Now;
                if (!job.SameTrial()) { job._halt = "the trial changed, ended or could not be confirmed: nothing more was written"; return; }
            }
            if (job._warmRelease is int c && job._sampler?.TempC() is double t && t >= c)
            {
                if (job.Write(new Game.Lever(Game.Lever.Fans, 0, Inv($"the heat soak reached {t:0} °C: fans back to their curve")), null) is { } refused)
                    job._halt = $"the fans did not go back to their curve: {refused}";
                if (job._halt != null) return;
            }
            if (!_checked && job._game != null)
            {
                _checked = true;
                job.CheckCard();
            }
        }

        private bool _checked;

        public void Log(string line) => job.Log(line);

        public void Trace(string kind, object value)
        {
            var w = kind switch { "frame" => job._frames, "step" => job._steps, _ => job._events };
            try { w?.WriteLine(JsonSerializer.Serialize(new { kind, at = DateTime.UtcNow, value }, Line)); } catch (IOException) { }
            if (kind == "step" && value is StepResult r) job.Log(Inv($"step {r.Index} {r.Pattern}/{r.Name}: {r.Credited:0.#} s, {r.FpsP50} fps, scale {r.ScaleMin}-{r.ScaleMax} %") + (r.Note != null ? $" — {r.Note}" : ""));
        }
    }

    /// <summary>Once the game renders: NVML must not list the card's graphics processes without the game (it would render
    /// elsewhere; the Vulkan check before the start makes that unlikely), and the window's size goes in the log.</summary>
    private void CheckCard()
    {
        if (_game == null) return;
        var pids = _sampler?.GraphicsPids();
        string where = pids == null ? "NVML does not list graphics processes on this driver; the Vulkan check stands alone"
            : pids.Contains((uint)_game.Id) ? "NVML lists the game on the card under test"
            : pids.Count == 0 ? "NVML lists no graphics process on the card; the Vulkan check stands alone"
            : $"NVML lists {pids.Count} graphics processes on the card under test, the game not among them";
        var client = OperatingSystem.IsWindows() ? Desktop.ClientSize(Desktop.WindowOf(_game.Id)) : null;
        string size = client is { } c ? $"; the window's client area is {c.Width}x{c.Height}" : "";
        if (_window != null && (client == null || client.Value.Width < _window.ScreenWidth * 0.9 || client.Value.Height < _window.ScreenHeight * 0.9
            || (double)client.Value.Width * client.Value.Height < (double)_window.ScreenWidth * _window.ScreenHeight * 0.9))
            _halt = "the actual game window does not cover 90 % of the primary screen: nothing judged";
        Log(where + size);
        if (pids is { Count: > 0 } && !pids.Contains((uint)_game.Id))
            _halt = $"{where}: it may be rendering on another GPU, so the test stopped before judging anything";
        _events?.WriteLine(JsonSerializer.Serialize(new { kind = "card-check", at = DateTime.UtcNow, where, size }, Line));
    }

    /// <summary>A lever as a trial through the service, under this job's lease: the curve recorded, not written. The trial
    /// stamp after it is the one a crash from here on belongs to. Null when it landed, else why not.</summary>
    private string? Write(Lever lever, GameStep? step)
    {
        var r = GameBroker("lever", new(Lever: lever.Kind, Value: lever.Value, Note: lever.Why));
        if (r is not { Ok: true }) return r?.Error ?? "the card host did not confirm the lever; its durable recovery record remains";
        if (lever.Kind == Game.Lever.Fans) _warmRelease = lever.Value is > 0 and < 100 ? lever.ReleaseAtC : null;
        string line = Inv($"{DateTime.Now:HH:mm:ss} {lever.Words()}") + (step != null ? $" (step {step.Index}, {step.Pattern})" : "");
        _applied.Add(line);
        Log("lever: " + line);
        _events?.WriteLine(JsonSerializer.Serialize(new { kind = "lever", at = DateTime.UtcNow, lever, step = step?.Index }, Line));
        return null;
    }
}
