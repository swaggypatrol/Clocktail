using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Clocktail.Domain.Fps;

namespace Clocktail.Broker;

/// <summary>
/// Game frame rates through Intel's PresentMon (MIT; the console build, ETW, nothing injected into the game). Capturing
/// needs administrator rights, which is why the broker runs it: a basic-user token gets "failed to start trace session:
/// access denied" (measured, 2026-10-20). Only the PresentMon.exe installed beside the broker runs, and only when its
/// SHA-256 is the release pinned here, so no file a user process could have swapped is ever started elevated. One
/// capture at a time, in one named ETW session that a new capture or Stop clears even after a hard kill.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FrameCapture : IFrameCapture
{
    public const string ExeName = "PresentMon.exe";
    /// <summary>PresentMon-2.6.0-x64.exe, GitHub's digest for the release asset, Authenticode-signed by Intel Corporation.</summary>
    public const string PinnedSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const string Session = "Clocktail";
    public const int MaxSeconds = 4 * 3600;

    private readonly object _gate = new();
    private Process? _running;
    private string? _path;

    private static string Exe => Path.Combine(AppContext.BaseDirectory, ExeName);

    public object Start(string dir, int seconds, string? process)
    {
        if (seconds is < 1 or > MaxSeconds) throw new ArgumentException($"seconds is 1..{MaxSeconds}");
        if (process is { } p && (p.Length > 260 || p.IndexOfAny(['"', '\\', '/', ':', '*', '?', '<', '>', '|']) >= 0))
            throw new ArgumentException($"'{process}' is not a process name");
        string exe = Exe;
        if (!File.Exists(exe)) throw new InvalidOperationException($"{ExeName} is not installed beside the service ({exe}): run the setup again");
        if (!Verified()) throw new InvalidOperationException($"{exe} is not the PresentMon release this version of Clocktail runs (its SHA-256 differs); it was not started");
        lock (_gate)
        {
            StopLocked();
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{(process is { } n ? "-" + Path.GetFileNameWithoutExtension(n) : "")}.csv");
            // PresentMon's own --output_file is not shared for reading while it writes (a live watch read nothing until the
            // capture ended, 2026-10-20), so it writes to this process, which writes the file and shares it.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--output_stdout", "--timed", seconds.ToString(CultureInfo.InvariantCulture), "--terminate_after_timed",
                         "--stop_existing_session", "--session_name", Session, "--no_track_input" })
                psi.ArgumentList.Add(a);
            if (process != null) { psi.ArgumentList.Add("--process_name"); psi.ArgumentList.Add(process); }
            var file = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var err = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { lock (file) file.Dispose(); return; }   // the end of its output: the capture is over
                lock (file) try { file.WriteLine(e.Data); } catch (Exception x) when (x is IOException or ObjectDisposedException) { }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            try { proc.Start(); }
            catch (System.ComponentModel.Win32Exception e) { file.Dispose(); throw new InvalidOperationException($"PresentMon did not start: {e.Message}"); }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            // A capture that cannot start (no rights, a session it cannot take) ends at once with its reason on stderr.
            if (proc.WaitForExit(1500) && proc.ExitCode != 0)
            {
                proc.WaitForExit();
                lock (err) throw new InvalidOperationException($"PresentMon could not start capturing (exit {proc.ExitCode}): {err.ToString().Trim()}");
            }
            _running = proc;
            return new { success = true, path = _path, seconds, process, pid = proc.Id };
        }
    }

    public object Stop()
    {
        lock (_gate)
        {
            bool was = _running is { HasExited: false };
            StopLocked();
            return new { success = true, stopped = was, path = _path };
        }
    }

    /// <summary>The installed PresentMon is the pinned release: nothing else is ever started from this elevated process.</summary>
    private static bool Verified()
    {
        try
        {
            using var s = File.OpenRead(Exe);
            return Convert.ToHexString(SHA256.HashData(s)).Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private void StopLocked()
    {
        if (_running is { } p)
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            p.Dispose();
            _running = null;
        }
        // A hard kill leaves the ETW session running; ending it by name is harmless when there is none.
        if (!Verified()) return;
        try
        {
            using var t = Process.Start(new ProcessStartInfo(Exe, ["--terminate_existing_session", "--session_name", Session])
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true });
            t?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
