using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Domain.Platform;

/// <summary>
/// Starts this program's worker and waits for it, for a <see cref="Supervisor"/>. The worker's standard output and error
/// come back line by line (the runtime writes its "Fatal error" and stack there), so the supervisor's log is the one
/// writer of its file. Every worker goes into a job object that kills it when the supervisor's handle closes: ending the
/// supervisor (the task being ended, the installer, the tray's Quit) ends the worker with it instead of leaving it orphaned.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkerProcess(string exe)
{
    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly KillOnCloseJob _job = new();   // held until this process ends: its closing is what kills the worker

    /// <summary>How long <see cref="Run"/> waits, once the worker has exited, for the last of its output.</summary>
    public static readonly TimeSpan DrainFor = TimeSpan.FromSeconds(2);

    /// <summary>For the worker: when its output goes to a pipe (a supervisor reads it), write it as UTF-8, which is how
    /// <see cref="Run"/> reads it, instead of the console's code page.</summary>
    public static void WriteUtf8WhenRedirected()
    {
        if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true });
        if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true });
    }

    /// <summary>
    /// For the worker: its standard output and error (the supervisor's pipes) are not handed down to what it starts.
    /// .NET starts every child with the parent's inheritable handles, and a pipe end inherited from the supervisor stays
    /// inheritable, so PresentMon, started by the worker for a frame capture of up to 4 h, held the supervisor's pipes
    /// open after the worker itself had exited.
    /// </summary>
    public static void KeepOutputFromChildren()
    {
        foreach (int std in new[] { StdOutput, StdError })
            if (GetStdHandle(std) is var h && h != 0 && h != -1) SetHandleInformation(h, HandleFlagInherit, 0);
    }

    /// <summary>Start <c>exe arguments</c>, pass each line it prints to <paramref name="line"/>, and return its exit code.</summary>
    public int Run(string arguments, Action<string> line, Action<string>? log = null)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            // Not the console's code page (437 here): the broker's lines carry the profile's Chinese notes. The worker
            // writes UTF-8 to a redirected output (WriteUtf8WhenRedirected).
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        using var p = new Process { StartInfo = psi };
        // A line that cannot be written (a full disk) is lost, never the supervisor: these run on pool threads, where an
        // exception would end the process and, through the job, the worker with it.
        void Pass(string? text)
        {
            if (text == null) return;
            try { line(text); } catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
        }
        p.OutputDataReceived += (_, e) => Pass(e.Data);
        p.ErrorDataReceived += (_, e) => Pass(e.Data);
        try { p.Start(); }
        catch (Win32Exception e)
        {
            log?.Invoke($"could not start the worker ({e.Message})");
            return e.NativeErrorCode == 0 ? 1 : e.NativeErrorCode;
        }
        if (!Contain(p)) log?.Invoke($"worker pid {p.Id} could not be tied to the supervisor (error {Marshal.GetLastPInvokeError()}); it would outlive a killed supervisor");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        // The process, not its output: the parameterless wait also waits for the pipes to close, and a process the worker
        // started that still holds them (see KeepOutputFromChildren) kept the supervisor from restarting a worker that had
        // died, for as long as that process ran. A finite wait returns at the exit; then a short drain for the last lines.
        while (!p.WaitForExit(1000)) { }
        using (var drain = new CancellationTokenSource(DrainFor))
            try { p.WaitForExitAsync(drain.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { log?.Invoke($"worker pid {p.Id} exited, but its output stayed open {DrainFor.TotalSeconds:0} s longer (a process it started holds it): not waiting for it"); }
        return p.ExitCode;
    }

    /// <summary>Ties a process this one started some other way to the same job: it ends when this process does.</summary>
    public bool Adopt(Process p) => Contain(p);

    private bool Contain(Process p) => _job.Add(p);

    private const int StdOutput = -11, StdError = -12;
    private const uint HandleFlagInherit = 1;

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
