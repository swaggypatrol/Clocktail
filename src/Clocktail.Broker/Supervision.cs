using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Platform;
using Clocktail.Hw;

namespace Clocktail.Broker;

/// <summary>
/// The two halves of keeping the broker alive across a driver that goes away. The logon task's process is a
/// <see cref="Supervisor"/> that runs the broker as a worker (a plain "run") and starts it again whenever it ends without
/// being asked to: an access violation inside nvml.dll cannot be caught in .NET, and the task's RestartOnFailure never
/// sees a process that started and then died (2026-10-19 21:48:29Z: the broker died that way during a device restart and
/// the card stayed at stock until the task was run by hand). The worker, for its part, watches whether the driver it
/// opened is still there and hands over to a fresh worker when it is not, rather than going on through dead handles.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class Supervision
{
    /// <summary>How long a write already in the driver gets to finish before a worker whose driver went away ends.</summary>
    public static readonly TimeSpan HandOverWait = TimeSpan.FromSeconds(10);

    /// <summary>Keep a worker running; its lines go to <paramref name="line"/> (this process's log), the supervisor's own to
    /// <paramref name="log"/>. This process loads no driver library, so nothing that kills a worker can reach it.</summary>
    public static int Supervise(string exe, Action<string> line, Action<string> log)
    {
        var worker = new WorkerProcess(exe);
        return new Supervisor(_ => worker.Run("run", line, log), log, () => DateTimeOffset.Now, Thread.Sleep).Run();
    }

    /// <summary>
    /// Once a second: is the driver this process opened still there (<see cref="DriverLoss"/>)? When it is not, no new write
    /// may start (one in flight gets <see cref="HandOverWait"/>), the reason goes to the log, and the process ends at once
    /// with <see cref="Supervisor.DriverLost"/>, without tearing down NVML or NVAPI through the driver that went away.
    /// </summary>
    public static async Task WatchDriver(GpuManager mgr, IReadOnlyList<GpuSession> sessions, BrokerDispatcher dispatcher, Action<string> log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (DriverLoss.Why(mgr.Nvml, sessions) is { } why)
            {
                if (ct.IsCancellationRequested) return;
                bool quiet = dispatcher.Close(HandOverWait);
                if (ct.IsCancellationRequested) return;
                log($"the driver this broker opened is gone ({why}); {(quiet ? "" : "a write is still waiting on it; ")}handing over to a fresh broker (exit {Supervisor.DriverLost})");
                EndNow(Supervisor.DriverLost);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Ends this process at once with an exit code: no finalizers, no DLL detach, so nothing runs through nvml.dll's or
    /// nvapi64.dll's state for a driver that is gone.
    /// </summary>
    private static void EndNow(int code)
    {
        Console.Out.Flush();
        TerminateProcess(GetCurrentProcess(), unchecked((uint)code));
        Environment.Exit(code);   // only if TerminateProcess failed
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);
}
