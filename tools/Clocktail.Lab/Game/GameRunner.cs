using System.Globalization;

namespace Clocktail.Lab.Game;

/// <summary>
/// Everything the runner touches, so it can be played against a fake game in the tests. The real one (GameTest) is Quake
/// II RTX over loopback rcon, its process and window, the System log, NVML and the service.
/// </summary>
public interface IGameWorld
{
    DateTime Now { get; }
    void Sleep(TimeSpan t);
    bool StopAsked { get; }

    /// <summary>One console command (rcon): fire and forget.</summary>
    void Send(string command);
    /// <summary>A step's settings, all at once (the real game: a .cfg in the run's own home, executed).</summary>
    void Apply(int step, IReadOnlyList<(string Name, string Value)> cvars);
    /// <summary>The state query; null when no answer came within its timeout.</summary>
    GameFrame? Query();
    /// <summary>The game's exit code once it has exited, null while it runs.</summary>
    int? ExitCode { get; }
    /// <summary>The game's window has the foreground: Windows runs it at 60 fps otherwise (the game's own rule when unfocused).</summary>
    bool InFront();
    void ToFront();

    /// <summary>Driver resets (nvlddmkm 153, Display 4101) in the System log since the run began; null when it could not be read.</summary>
    int? DriverResets();
    /// <summary>The GPU's temperature, °C, from the latest sample; null when unknown.</summary>
    double? TempC();
    /// <summary>A card-side lever, written by the service as a trial: null when it landed, else why not.</summary>
    string? Lever(Lever lever, GameStep step);
    /// <summary>Called on every look while a step runs (the fan heat soak's release lives here).</summary>
    void Govern();
    /// <summary>Why the run cannot go on (the game found rendering on another GPU), or null.</summary>
    string? Halt { get; }
    void Log(string line);
    /// <summary>A record for the trace (frames.jsonl / steps.jsonl).</summary>
    void Trace(string kind, object value);
}

/// <summary>How a run ended. Survived: every credited second played and nothing failed. Failed: the card's, as far as a game
/// can tell (a driver reset, the game crashing or hanging under load, a non-number in the frame). Unjudged: a test that could
/// not be made, which says nothing about the setting and is never recorded as its crash. Stopped: lab_stop.</summary>
public enum GameEnd { Survived, Failed, Unjudged, Stopped }

public sealed record GameOutcome(GameEnd End, string Why, string? FailKind = null, int? AtStep = null, double Credited = 0, int? ExitCode = null)
{
    public const string Reset = "driver-reset", Crash = "game-crash", Hang = "hang", Nan = "non-number";

    public GameOutcome AfterGameLog(string? tail)
    {
        if (FailKind != Crash || tail == null) return this;
        if (new[] { "VK_ERROR_OUT_OF_DEVICE_MEMORY", "VK_ERROR_OUT_OF_HOST_MEMORY", "Failed to allocate GPU memory" }
            .Any(message => tail.Contains(message, StringComparison.OrdinalIgnoreCase)))
            return this with { End = GameEnd.Unjudged, FailKind = null, Why = Why + "; the game reported an allocation failure: reduce maxScale or free memory; nothing judged" };
        return tail.Contains("Device lost", StringComparison.OrdinalIgnoreCase) || tail.Contains("VK_ERROR_DEVICE_LOST", StringComparison.OrdinalIgnoreCase)
            ? this with { Why = Why + ": Vulkan reported the device lost" } : this;
    }

    public GameOutcome AfterRecovery(string? error) => error == null ? this : this with
    {
        End = End == GameEnd.Failed ? GameEnd.Failed : GameEnd.Unjudged,
        Why = Why + "; recovery not confirmed: " + error,
    };
}

/// <summary>One step as it played: credited seconds (in front, rendering, progressing), the game's fps and render scale.</summary>
public sealed record StepResult(int Index, string Pattern, string Name, string Kind, DateTime StartUtc, DateTime EndUtc, double Credited,
    int? FpsP50, int? FpsMin, int? ScaleMin, int? ScaleMax, string? Map, double? DemoFrom, double? DemoTo, string? Note);

/// <summary>The runner's clocks. Defaults are for the real game; the tests shorten nothing (their clock is virtual).</summary>
public sealed record GameTimings(
    double StartSeconds = 240, double LoadSeconds = 120, double HangAfterSeconds = 10, double ResetGraceSeconds = 45,
    double StallSeconds = 6, double NotInFrontMinutes = 10, double ToFrontEverySeconds = 15, double ResetCheckSeconds = 5,
    int? MaxTempC = null, int NanStreak = 2, double WallFactor = 2.0, double TelemetryGraceSeconds = 10);

/// <summary>
/// Plays a <see cref="GamePlan"/> on the game: levers, scene, settings and the play pattern's commands at each step's start,
/// then a look about once a second until the step's seconds are credited. A second is credited only while the game's window
/// is in front, it answers, renders (fps above 0), holds the step's settings and moves on (the demo's clock, or the live
/// game's clock and camera); a menu, a paused or frozen frame is not a test. A game that stops answering is waited for
/// until the driver resets, the game exits or the TDR delay and a margin pass: each is a failure, classified.
/// </summary>
public sealed class GameRunner(IGameWorld world, GameTimings? timings = null)
{
    private readonly GameTimings _t = timings ?? new();
    private readonly List<StepResult> _results = [];
    private Random _rng = new(0);
    private string? _demoMap, _demo, _api, _playMap;
    private string _scene = "";
    private bool _photo, _buttons;
    private IReadOnlyList<string> _arrival = [];
    private double _credited, _notInFront, _stall;
    private GameFrame? _progress;
    private DateTime _lastToFront = DateTime.MinValue, _lastResetCheck = DateTime.MinValue, _lastPause = DateTime.MinValue;
    private int _nanRun;
    private DateTime _deadline;
    private DateTime? _temperatureMissingSince;

    public IReadOnlyList<StepResult> Results => _results;
    public double Credited => _credited;
    /// <summary>The map the demo plays (learnt when it first rendered).</summary>
    public string? DemoMap => _demoMap;

    public GameOutcome Run(GamePlan plan, string demo)
    {
        _demo = demo;
        _rng = new Random(plan.Seed ^ 0x5eed);
        _deadline = world.Now.AddSeconds(plan.Seconds * _t.WallFactor + 600);
        if (Ready() is { } notReady) return notReady;
        foreach (var step in plan.Steps)
        {
            if (Checks(step) is { } stopped) return stopped;
            if (_credited >= plan.Seconds - 1e-6) break;
            if (Play(step, plan.Seconds) is { } end) return end;
        }
        if (_credited < plan.Seconds - 1e-6)
            return Done(GameEnd.Unjudged, "the script ended before the requested play time was credited: nothing judged");
        return new GameOutcome(GameEnd.Survived, Inv($"survived {_credited:0} s of play"), Credited: _credited);
    }

    private GameOutcome Done(GameEnd end, string why, GameStep? step = null, string? kind = null, int? code = null) =>
        new(end, why, kind, step?.Index, _credited, code);

    /// <summary>Until the demo renders and moves on: the game is up, answers, and this build has what the test reads.</summary>
    private GameOutcome? Ready()
    {
        var until = world.Now.AddSeconds(_t.StartSeconds);
        GameFrame? first = null;
        while (world.Now < until)
        {
            if (Checks() is { } stopped) return stopped;
            if (world.ExitCode is int code)
                return world.DriverResets() > 0 ? Done(GameEnd.Failed, "a driver reset while the game started", kind: GameOutcome.Reset, code: code)
                    : Done(GameEnd.Unjudged, $"the game exited before it rendered (exit code {code}): it could not start, which says nothing about the card", code: code);
            world.ToFront();
            var f = world.Query();
            if (Checks() is { } checkEnd) return checkEnd;
            if (f != null && f.Missing() is { Count: > 0 } missing)
                return Done(GameEnd.Unjudged, $"this Quake II RTX build does not answer {string.Join(", ", missing)}: the test cannot read its settings back");
            if (f is { Fps: > 0, DemoSeconds: > 0 } && f.Map.Length > 0 && f.Get("vid_rtx") == "1")
            {
                if (f.Hdr == null)
                    return Done(GameEnd.Unjudged, "cl_hdr_color did not contain a valid colour readback: this build cannot be judged");
                if (first != null && f.DemoSeconds > first.DemoSeconds && f.Map == first.Map)
                {
                    _demoMap = f.Map;
                    _api = f.Get("ray_tracing_api");
                    _scene = Scene.Demo;
                    world.Trace("ready", new { map = f.Map, api = _api, fps = f.Fps });
                    world.Log($"Quake II RTX is rendering the demo on {f.Map} ({f.Fps} fps)");
                    return null;
                }
                first = f;
            }
            world.Sleep(TimeSpan.FromSeconds(1));
        }
        return world.DriverResets() > 0 ? Done(GameEnd.Failed, "a driver reset while the game started", kind: GameOutcome.Reset)
            : Done(GameEnd.Unjudged, $"the demo did not render within {_t.StartSeconds:0} s: nothing judged");
    }

    private GameOutcome? Play(GameStep step, double target)
    {
        if (step.Levers is { } levers)
            foreach (var lever in levers)
                if (world.Lever(lever, step) is { } refused)
                    return Done(GameEnd.Unjudged, $"the service did not write {lever.Words()}: {refused}", step);
        if (step.RtApi != null && Restart(step) is { } r) return r;
        if (step.Scene != null && Switch(step.Scene, step) is { } s) return s;
        Pause(step.Render.Photo);
        world.Apply(step.Index, step.Render.Cvars());
        // A live map's arrival commands went with its load (god mode toggles: sending them twice would turn it off).
        if (step.Actions is { } actions && step.Scene?.Kind != Scene.Play)
            foreach (var a in actions)
                if (GameScript.AllowedAction(a))
                {
                    world.Send(a);
                    if (a.StartsWith('+')) _buttons = true;
                }
        return Dwell(step, target);
    }

    /// <summary>The photo state the step wants, by the pause command (it toggles), at most once a second.</summary>
    private void Pause(bool photo)
    {
        if (_photo == photo) return;
        world.Send("pause");
        _photo = photo;
        _lastPause = world.Now;
    }

    private GameOutcome? Dwell(GameStep step, double target)
    {
        var start = world.Now;
        double credited = 0, mismatchFor = 0;
        DateTime? silentSince = null;
        var fps = new List<int>();
        int? scaleMin = null, scaleMax = null;
        double? demoFrom = null, demoTo = null;
        string? note = null, map = null;
        bool resent = false;
        double want = Math.Min(step.Seconds, target - _credited);
        var last = world.Now;
        while (credited < want - 1e-6)
        {
            if (Checks(step) is { } stopped) return stopped;
            world.Sleep(TimeSpan.FromSeconds(Math.Min(1.0, want - credited)));
            var f = world.Query();
            var now = world.Now;
            double dt = Math.Clamp((now - last).TotalSeconds, 0, 1.5);
            last = now;
            if (world.ExitCode is int code) return Exited(code, step);
            if (Checks(step) is { } checkEnd) return checkEnd;
            if (f == null)
            {
                silentSince ??= now.AddSeconds(-dt);
                if ((now - silentSince.Value).TotalSeconds >= _t.HangAfterSeconds)
                {
                    if (Silent(step, silentSince.Value) is { } end) return end;
                    silentSince = null;
                    last = world.Now;
                }
                continue;
            }
            silentSince = null;
            // Preserve the exact driver readback as text: JSON numeric arrays cannot contain NaN or infinity.
            world.Trace("frame", new { step = step.Index, f.Map, demo = f.DemoSeconds, timer = f.TimerSeconds, f.Fps, scale = f.RenderScale, hdr = f.Get("cl_hdr_color"), paused = f.Get("cl_paused") });
            if (f.Hdr == null) return Done(GameEnd.Unjudged, "cl_hdr_color stopped returning a valid colour readback: nothing judged", step);
            if (f.NonFinite)
            {
                if (++_nanRun >= _t.NanStreak)
                    return Done(GameEnd.Failed, Inv($"the frame's centre pixel read {f.Get("cl_hdr_color")} in {_nanRun} looks in a row: a NaN in the path tracer or its filter (hardware or renderer fault)"), step, GameOutcome.Nan);
            }
            else _nanRun = 0;
            // The window out of front: the game drops to 60 fps by its own rule, so nothing is credited until it is back.
            if (!world.InFront() || f.Fps <= 0)
            {
                _notInFront += dt;
                if ((now - _lastToFront).TotalSeconds >= _t.ToFrontEverySeconds) { world.ToFront(); _lastToFront = now; }
                if (_notInFront > _t.NotInFrontMinutes * 60)
                    return Done(GameEnd.Unjudged, $"the game's window was out of front (or not drawing) for over {_t.NotInFrontMinutes:0} minutes: Windows runs it at 60 fps then, so the test could not be made; nothing judged", step);
                note ??= "out of front for a while (not credited)";
                continue;
            }
            // The scene the step plays: a level exit changed the live map, or the demo reached its end.
            if (_scene == Scene.Play && _playMap != null && !string.Equals(f.Map, _playMap, StringComparison.OrdinalIgnoreCase))
            {
                note = $"the level changed to {f.Map}: back to {_playMap}";
                if (LoadPlay(_playMap, step) is { } loadEnd) return loadEnd;
                last = world.Now;
                continue;
            }
            if (_photo != f.Paused && (now - _lastPause).TotalSeconds >= 2)
            {
                world.Send("pause");   // the pause toggles: set it back to what the step wants
                _lastPause = now;
            }
            // Moving on: the demo's clock (a tenth of a second), the live game's clock (a second) or camera. A look that sees
            // no move is still credited for a moment (the live clock ticks once a second); past that it is a stall.
            if (Moved(_progress, f, step.Render.Photo)) { _stall = 0; _progress = f; }
            else
            {
                _stall += dt;
                if (_stall >= _t.StallSeconds)
                {
                    if (_scene == Scene.Demo && f.DemoSeconds >= GameScript.DemoSeconds - 8)
                    {
                        Seek(Math.Floor(_rng.NextDouble() * 30));
                        note = "the demo reached its end: seeked back";
                        continue;
                    }
                    if (_stall >= _t.StallSeconds * 3)
                        return Done(GameEnd.Unjudged, $"the game answered and drew ({f.Fps} fps) but stopped moving on for {_stall:0} s ({_scene} on {f.Map}): a menu or a stuck scene is not a test; nothing judged", step);
                }
                if (_stall > 2.5) continue;
            }
            // The step's settings: re-sent once by name if the game does not hold them after a second; then the test stops.
            var wrong = f.Mismatches(step.Render);
            if (wrong.Count > 0)
            {
                mismatchFor += dt;
                if (mismatchFor >= 1 && !resent)
                {
                    foreach (var (n, v, _) in wrong) world.Send($"set {n} {v}");
                    resent = true;
                }
                else if (mismatchFor >= 3)
                    return Done(GameEnd.Unjudged, $"the game did not take {string.Join(", ", wrong.Select(w => $"{w.Name} {w.Want} (reads '{w.Got}')"))}: nothing judged", step);
                continue;
            }
            mismatchFor = 0;
            double played = Math.Min(dt, want - credited);
            credited += played;
            _credited += played;
            fps.Add(f.Fps);
            if (f.RenderScale is int sc) { scaleMin = Math.Min(scaleMin ?? sc, sc); scaleMax = Math.Max(scaleMax ?? sc, sc); }
            if (f.DemoSeconds is double d && _scene == Scene.Demo) { demoFrom ??= d; demoTo = d; }
            map = f.Map;
        }
        var ordered = fps.Order().ToList();
        var result = new StepResult(step.Index, step.Pattern, step.Name, step.Kind, start, world.Now, Math.Round(credited, 2),
            ordered.Count > 0 ? ordered[ordered.Count / 2] : null, ordered.Count > 0 ? ordered[0] : null, scaleMin, scaleMax, map, demoFrom, demoTo, note);
        _results.Add(result);
        world.Trace("step", result);
        return null;
    }

    /// <summary>The same safety checks in every wait, including startup, loads and a silent game.</summary>
    private GameOutcome? Checks(GameStep? step = null)
    {
        if (world.StopAsked) return Done(GameEnd.Stopped, "stopped (lab_stop)", step);
        var now = world.Now;
        if (now >= _deadline) return Done(GameEnd.Unjudged, "the wall-time limit was reached (slow loads, hitches or time out of front): nothing judged", step);
        if ((now - _lastResetCheck).TotalSeconds >= _t.ResetCheckSeconds)
        {
            _lastResetCheck = now;
            if (world.DriverResets() is > 0 and var n)
                return Done(GameEnd.Failed, $"a driver reset ({n}) during {step?.Pattern ?? "startup"} ({step?.Name})", step, GameOutcome.Reset);
        }
        if (world.TempC() is double c && double.IsFinite(c))
        {
            _temperatureMissingSince = null;
            if (_t.MaxTempC is { } maxC && c >= maxC)
                return Done(GameEnd.Unjudged, Inv($"thermal stop at {c:0} °C (the maxTempC of {maxC} °C asked for): stopped by the test, not a pass or a crash"), step);
        }
        else
        {
            _temperatureMissingSince ??= now;
            if ((now - _temperatureMissingSince.Value).TotalSeconds >= _t.TelemetryGraceSeconds)
                return Done(GameEnd.Unjudged, "GPU temperature is unavailable: an unattended test cannot continue safely", step);
        }
        if (step != null) world.Govern();
        if (world.Halt is { } halt) return Done(GameEnd.Unjudged, halt, step);
        return null;
    }

    /// <summary>The game exited by itself: a crash unless it exited cleanly (someone closed it).</summary>
    private GameOutcome Exited(int code, GameStep? step)
    {
        if (world.DriverResets() is > 0 and var n)
            return Done(GameEnd.Failed, $"a driver reset ({n}) during {step?.Pattern} ({step?.Name}); the game exited with code {code}", step, GameOutcome.Reset, code);
        if (code == 0)
            return Done(GameEnd.Unjudged, "the game was closed (exit code 0: someone at the PC, or the game quit by itself): not a crash; nothing judged", step, code: code);
        return Done(GameEnd.Failed, Inv($"the game crashed to the desktop under load (exit code {code}) during {step?.Pattern} ({step?.Name}, {step?.Render.Words()})"), step, GameOutcome.Crash, code);
    }

    /// <summary>The game stopped answering: a hang the driver is about to reset (it freezes the game's frame loop), a crash on
    /// its way out, or a long hitch. Waited for until the TDR delay and a margin; null when it answers again.</summary>
    private GameOutcome? Silent(GameStep step, DateTime since)
    {
        world.Log(Inv($"the game stopped answering at {step.Name}: waiting up to {_t.ResetGraceSeconds:0} s for the driver or the game"));
        while ((world.Now - since).TotalSeconds < _t.ResetGraceSeconds)
        {
            if (Checks(step) is { } stopped) return stopped;
            if (world.ExitCode is int code) return Exited(code, step);
            if (world.Query() != null) { world.Log("it answers again (a long hitch)"); return null; }
            world.Sleep(TimeSpan.FromSeconds(1));
        }
        return Done(GameEnd.Failed, Inv($"the game froze during {step.Pattern} ({step.Name}, {step.Render.Words()}): no answer for {(world.Now - since).TotalSeconds:0} s and no driver reset followed (a hang the driver did not recover, cause unclassified)"), step, GameOutcome.Hang);
    }

    /// <summary>Whether the game moved on since the last look that did: the demo's clock (not in photo mode, which pauses
    /// it), the live game's clock or camera. The first look of a step has nothing to compare with and counts.</summary>
    private bool Moved(GameFrame? before, GameFrame now, bool photo)
    {
        if (photo) return now.Paused || before == null;
        if (before == null) return true;
        return _scene == Scene.Demo
            ? now.DemoSeconds > before.DemoSeconds
            : now.TimerSeconds > before.TimerSeconds || now.ViewDir != before.ViewDir;
    }

    private void Seek(double seconds)
    {
        world.Send("seek " + ((int)seconds).ToString(CultureInfo.InvariantCulture));
        _progress = null;   // the demo's clock jumps: the next look starts the comparison again
        _stall = 0;
    }

    private GameOutcome? Switch(Scene scene, GameStep step)
    {
        if (scene.Kind == Scene.Demo)
        {
            if (_scene != Scene.Demo)
            {
                Release();
                if (_photo) Pause(false);
                world.Send("demo " + _demo);
                if (WaitLoad(f => f.Map == _demoMap && f.DemoSeconds > 0 && f.Fps > 0, $"the demo ({_demoMap})", step) is { } end) return end;
                _scene = Scene.Demo;
                _playMap = null;
            }
            Seek(scene.Seek);
            return null;
        }
        Release();
        if (_photo) Pause(false);
        _arrival = step.Actions ?? [];
        return LoadPlay(scene.Map!, step);
    }

    private GameOutcome? LoadPlay(string map, GameStep step)
    {
        world.Send("map " + map);
        if (WaitLoad(f => string.Equals(f.Map, map, StringComparison.OrdinalIgnoreCase) && f.Fps > 0 && f.DemoSeconds is null or 0, $"the map {map}", step) is { } end) return end;
        _scene = Scene.Play;
        _playMap = map;
        _progress = null;
        _stall = 0;
        foreach (var a in _arrival.Where(GameScript.AllowedAction)) world.Send(a);
        _buttons = _arrival.Any(a => a.StartsWith('+'));
        return null;
    }

    /// <summary>Lets go of every button the play pattern held.</summary>
    private void Release()
    {
        if (!_buttons) return;
        foreach (var r in GameScript.Releases) world.Send(r);
        _buttons = false;
    }

    /// <summary>The renderer restarted on the other ray-tracing path (query / pipeline); its window is made again.</summary>
    private GameOutcome? Restart(GameStep step)
    {
        string next = string.Equals(_api, "pipeline", StringComparison.OrdinalIgnoreCase) ? "query" : "pipeline";
        Release();
        world.Send("set ray_tracing_api " + next);
        if (WaitLoad(f => string.Equals(f.Get("ray_tracing_api"), next, StringComparison.OrdinalIgnoreCase) && f.Fps > 0, $"the renderer on ray tracing '{next}'", step) is { } end) return end;
        _api = next;
        world.ToFront();
        _lastToFront = world.Now;
        return null;
    }

    /// <summary>A scene loading (a map, the demo, a renderer restart): the game may not answer while it loads; up to
    /// <see cref="GameTimings.LoadSeconds"/>, nothing is credited. A crash or reset meanwhile is classified as anywhere else.</summary>
    private GameOutcome? WaitLoad(Func<GameFrame, bool> loaded, string what, GameStep step)
    {
        var until = world.Now.AddSeconds(_t.LoadSeconds);
        while (world.Now < until)
        {
            if (Checks(step) is { } stopped) return stopped;
            world.Sleep(TimeSpan.FromSeconds(0.5));
            if (world.ExitCode is int code) return Exited(code, step);
            var f = world.Query();
            if (Checks(step) is { } checkEnd) return checkEnd;
            if (f != null && loaded(f)) return null;
        }
        return Done(GameEnd.Unjudged, $"{what} did not load within {_t.LoadSeconds:0} s while the game kept running: nothing judged", step);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
