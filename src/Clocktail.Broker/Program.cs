using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Clocktail.Broker;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

const string Usage = """

    Clocktail.Broker <verb> [options]

      run [--detach]   serve the pipe (what the logon task runs; needs elevation). --detach drops the console, logs to
                       %LocalAppData%\Clocktail\broker.log, and keeps the broker running as a worker process (a plain
                       "run") that it starts again whenever it dies or the driver it opened goes away
      install         copy this build to Program Files and register the logon task; starts it now (self-elevates)
      uninstall        stop and remove the logon task and the Program Files copy (self-elevates)
      stock            through the running broker: suspend the daily profile and put every setting back to stock
      status           is a broker answering on the pipe? prints its ping
      help             this text

      --log PATH       (install, uninstall) also append the lines to PATH; the elevated copy uses it to report back

    The broker is the one process that performs GPU writes. Clients (the CLI, agents) send it one of a fixed set
    of operations over a named pipe only this user can open. It keeps one write window open at a time, holds the
    firmware-reported limits as a ceiling no request can lift, and stands down while a stability run is measuring.
    """;
const string NotElevated = "broker: must run elevated (the private setters answer -137 otherwise); the logon task does so, or run from an elevated terminal";

return Entry(args);

static int Entry(string[] args)
{
    var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
    string? log = null;
    bool detach = false;
    try
    {
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log": log = ++i < args.Length ? args[i] : throw new ArgumentException("--log needs a path"); break;
                case "--detach": detach = true; break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        return verb switch
        {
            "run" => detach ? Supervise() : Run(),
            "install" => OperatingSystem.IsWindows() ? Installer.Install(log) : NotWindows(),
            "uninstall" => OperatingSystem.IsWindows() ? Installer.Uninstall(log) : NotWindows(),
            "status" => Status(),
            "stock" => Stock(),
            _ => Help(),
        };
    }
    catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException
                                  or DllNotFoundException or EntryPointNotFoundException or InvalidDataException
                                  or NvApiException or NvmlException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"broker: {e.Message}");
        if (log != null) { try { File.AppendAllText(log, $"broker: {e.Message}{Environment.NewLine}"); } catch (IOException) { } }
        return 1;
    }
}

/// <summary>
/// What the logon task runs: the broker as a worker process (a plain "run") under a supervisor that starts it again
/// whenever it ends without being asked to (<see cref="Supervision"/>). The worker prints to this process, so
/// broker.log keeps a single writer.
/// </summary>
static int Supervise()
{
    if (!OperatingSystem.IsWindows()) return NotWindows();
    if (!Elevation.IsAdministrator())
    {
        Console.Error.WriteLine(NotElevated);
        return 1;
    }
    NativeConsole.Detach(Path.Combine(BrokerPaths.StateRoot(), "broker.log"));
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot find this executable to start the broker's worker");
    void Log(string line) => Console.WriteLine($"{DateTimeOffset.Now:u} supervisor: {line}");
    Log($"up: pid {Environment.ProcessId}; the broker runs as its worker and is started again whenever it dies");
    return Supervision.Supervise(exe, line => Console.WriteLine(line), Log);
}

static int Run()
{
    if (!OperatingSystem.IsWindows()) return NotWindows();
    if (!Elevation.IsAdministrator())
    {
        Console.Error.WriteLine(NotElevated);
        return 1;
    }
    WorkerProcess.WriteUtf8WhenRedirected();   // under the supervisor, this output is broker.log's
    WorkerProcess.KeepOutputFromChildren();    // and it stays the supervisor's: PresentMon does not inherit it

    using var mgr = GpuManager.Open();
    var host = new SystemHost();
    var hw = new NvClient(mgr.NvApi);                       // the default write whitelist: exactly what the CLI would use
    var judgePath = BrokerPaths.JudgeLockPath();
    Func<string?> judge = () =>
    {
        var busy = JudgeLock.BusyReason(JudgeLock.Candidates(judgePath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), host, out var trace);
        Console.WriteLine($"{DateTimeOffset.Now:u} judge: {trace} -> {(busy == null ? "free" : "busy")}");   // one line per write op
        return busy;
    };
    var targets = new WindowsGpuTargets(mgr, host, judge);  // each sentinel asks the judge again once it is on disk
    var dispatcher = new BrokerDispatcher(hw, targets, host, canWrite: true, judge, new FrameCapture());
    var server = new BrokerServer(
        () => OperatingSystem.IsWindows() ? PipeFactory.Create(BrokerTransport.PipeName) : throw new PlatformNotSupportedException("the broker's pipe ACL is Windows-only"),
        dispatcher);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    Console.WriteLine($"{DateTimeOffset.Now:u} broker up: pid {host.Pid}, boot {host.BootId}, pipe {BrokerTransport.PipeName}, gpus [{string.Join(", ", targets.Ids)}], state {BrokerPaths.StateRoot()}");
    void Log(string line) => Console.WriteLine($"{DateTimeOffset.Now:u} {line}");
    // Nothing that fails in the background vanishes: an exception that ends the process is in broker.log before the
    // supervisor starts a new worker, a one-off task's is logged, and a loop's is logged and the loop started again.
    AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"unhandled exception, the worker ends: {e.ExceptionObject}");
    TaskScheduler.UnobservedTaskException += (_, e) => { Log($"a background task failed: {e.Exception.GetBaseException()}"); e.SetObserved(); };
    void Once(string name, Action work) => _ = Task.Run(work).ContinueWith(t => Log($"{name} failed: {t.Exception!.GetBaseException()}"), TaskContinuationOptions.OnlyOnFaulted);
    void KeepRunning(string name, Func<Task> loop) => _ = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            try { await loop().ConfigureAwait(false); return; }
            catch (Exception e) { Log($"{name} failed, starting it again in 5 s: {e}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    });
    // A bugcheck or a power loss while the daily profile was held leaves no window open, only these events.
    string? unclean = null;
    try { unclean = UncleanShutdown.SinceBoot(); }
    catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { Log($"unclean shutdowns will not be seen: {e.Message}"); }
    if (unclean != null) Log($"the last boot ended uncleanly ({unclean})");
    var crashed = dispatcher.SettleAll(unclean);            // a crash while holding cells: on record before any client asks
    foreach (var (id, path) in crashed)
        Log($"gpu {id}: the machine went down holding cells (a write window open, or an unclean shutdown); recorded in {path}");

    // A driver reset keeps every offset on the card: give back what is held, through the same recovery op a client would send.
    // A reset the worker before this one never answered (it died in the hang, or handed over) is answered at start.
    static int? ResetsSince(DateTime sinceUtc)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return SystemLog.Count(SystemLog.DriverResetFilter, sinceUtc); }
        catch (InvalidOperationException) { return null; }
    }
    var responder = new DriverResetResponder(dispatcher.Dispatch, targets.Ids, Log, () => DateTimeOffset.Now, Thread.Sleep, id => Idle(mgr, targets, id), ResetsSince);
    KeepRunning("the driver-reset responder", () => responder.RunAsync(cts.Token));
    // The daily profile goes on at start once the GPU is idle, unchanged; a crash before it (a window open, an unclean
    // shutdown) is recorded first.
    Once("the daily profile at start", () => responder.ApplyAtStart(crashed.Select(c => c.GpuId).ToList()));
    // The last 60 s of each GPU in memory; on a reset the window is saved and its crash point steps the curve there.
    var sampler = new FlightSampler(mgr, hw, targets.Ids.Select(id => (id, targets.Resolve(id)!.Session)).ToList());
    KeepRunning("the flight recorder", () => sampler.RunAsync(cts.Token));
    // A fixed fan duty is the agent's to watch; when the card gets too hot under one anyway, the fans go back to the curve.
    var fanGuard = new FanGuard(dispatcher.Dispatch, Log);
    KeepRunning("the fan guard", async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            foreach (var (id, recorder) in sampler.Recorders)
            {
                var window = recorder.Freeze();
                var last = window.Count > 0 ? window[^1] : null;
                fanGuard.Look(id, last != null && now - last.At < TimeSpan.FromSeconds(3) ? last.TempC : null, now);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    });
    // The driver this process opened can go away under it (a device restart, a driver update), and nothing it holds can be
    // rebound safely after that: once a session or NVML says so, the broker hands over to a fresh worker, which puts the
    // daily profile back on as at any start.
    _ = Supervision.WatchDriver(mgr, targets.Ids.Select(id => targets.Resolve(id)!.Session).ToList(), dispatcher, Log, cts.Token);
    string driver = $"{mgr.Driver.Version / 100}.{mgr.Driver.Version % 100:00} {mgr.Driver.Branch}";
    // One reset can log many events (a device restart of a hung GPU, 2026-10-19 21:48Z: nine 153s in 100 ms, then four
    // more over 18 s, each of which saved a trace: eight identical lines in one second, and two saves of one file that
    // collided); the first of a burst saves the trace and signals the responder, which answers a burst once anyway.
    var bursts = new ResetBursts(ResetBursts.DefaultQuiet);
    void OnReset(string evidence)
    {
        var seen = DateTimeOffset.Now;
        if (!bursts.Starts(seen)) return;
        var windows = sampler.Recorders.ToDictionary(r => r.Key, r => r.Value.Freeze());   // before the recovered driver adds its own looks
        Once("the crash trace", () =>
        {
            var crashUv = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var details = new List<string>();
            try
            {
                foreach (var (id, window) in windows)
                {
                    var point = FlightRecorder.Pick(window, seen);
                    try
                    {
                        var trace = FlightRecorder.Save(targets.Resolve(id)!.Journal.Path, seen, evidence, driver, point, window);
                        Log($"gpu {id}: crash trace {trace}: {(point == null ? "no loaded sample before the hang" : $"{point.Describe()}, {point.Why}")}");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"gpu {id}: crash trace not saved: {e.Message}"); }
                    if (point?.Sample.CoreMv is { } mv) { crashUv[id] = mv * 1000; details.Add(point.Describe()); }
                }
            }
            finally   // the trace is for the record; the give-back must happen whatever became of it
            {
                responder.Signal(details.Count == 0 ? $"{evidence}, driver {driver}" : $"{evidence}, driver {driver}; {string.Join("; ", details)}", crashUv);
            }
        });
    }
    SystemLogWatch? watch = null, wakeWatch = null;
    try { watch = new SystemLogWatch(SystemLog.DriverResetQuery, "nvlddmkm 153 / Display 4101 in the System log", OnReset, Log); Log("watching the System log for driver resets"); }
    catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { Log($"driver resets will not be answered: {e.Message}"); }
    // Waking from sleep: whether the card keeps what the profile wrote is not measured (sleep is off on the reference
    // machine), so the profile goes on again once the GPU is idle; only what differs is written, and a trial is left alone.
    var wakes = new ResetBursts(TimeSpan.FromSeconds(30));
    void OnWake(string evidence) { if (wakes.Starts(DateTimeOffset.Now)) Once("the daily profile after sleep", () => responder.AfterSleep(evidence)); }
    try { wakeWatch = new SystemLogWatch(SystemLog.WakeQuery, "Kernel-Power 107 / 507 (the PC woke up)", OnWake, Log); Log("watching the System log for wake-ups"); }
    catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { Log($"wake-ups will not be answered: {e.Message}"); }

    server.RunAsync(cts.Token).GetAwaiter().GetResult();
    watch?.Dispose();
    wakeWatch?.Dispose();
    Console.WriteLine($"{DateTimeOffset.Now:u} broker down");
    return 0;
}

/// <summary>Idle enough to write a curve: NVML's GPU utilization under 15 %. No NVML reading counts as busy, and so does
/// a driver that is gone (its NVML is not read again).</summary>
static bool Idle(GpuManager mgr, WindowsGpuTargets targets, string id)
{
    var g = mgr.Gpus.FirstOrDefault(x => string.Equals(x.Identity.StableId, id, StringComparison.OrdinalIgnoreCase));
    if (g is not { HasNvml: true } || mgr.Nvml == null || targets.Resolve(id) is not { } t) return false;
    if (DriverLoss.Why(mgr.Nvml, [t.Session]) != null) return false;
    try { return mgr.Nvml.Utilization(g.NvmlHandle).Gpu < 15; }
    catch (NvmlException) { return false; }
}

static int Status()
{
    var r = BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), connectMs: 800);
    if (r == null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { reachable = false, pipe = BrokerTransport.PipeName }, BrokerResponse.Json));
        return 1;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { reachable = true, pipe = BrokerTransport.PipeName, ping = r.Result }, BrokerResponse.Json));
    return 0;
}

/// <summary>The card back to stock before an uninstall: the daily profile is suspended (so nothing puts it back) and
/// everything the broker owes is given back through the ordinary recovery.</summary>
static int Stock()
{
    var off = BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off"), connectMs: 800);
    if (off == null)
    {
        Console.Error.WriteLine("broker: no broker answers on the pipe; nothing to put back");
        return 1;
    }
    var back = BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpRecover));
    Console.WriteLine(JsonSerializer.Serialize(new { suspended = off.Ok, stock = back?.Ok ?? false, error = off.Error ?? back?.Error, recover = back?.Result }, BrokerResponse.Json));
    return off.Ok && back?.Ok == true ? 0 : 1;
}

static int NotWindows()
{
    Console.Error.WriteLine("broker: this verb needs Windows (the driver and its elevation rule are Windows-only)");
    return 1;
}

static int Help()
{
    Console.WriteLine(Usage);
    return 0;
}

/// <summary>
/// A console process started by the task scheduler in the user's session gets a console window of its own; the
/// supervisor lets go of it and writes its few lines (and its worker's) to a log file instead, so nothing sits on the
/// desktop whose closing would kill the broker.
/// </summary>
[SupportedOSPlatform("windows")]
static partial class NativeConsole
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    public static void Detach(string logPath)
    {
        FreeConsole();
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var w = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(w);
        Console.SetError(w);
    }
}
