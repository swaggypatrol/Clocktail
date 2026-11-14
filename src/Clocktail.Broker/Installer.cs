using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Clocktail.Domain.Platform;

namespace Clocktail.Broker;

/// <summary>
/// Install = copy the broker's build output to an admin-only directory and register a logon task that runs it
/// elevated. A scheduled task with "highest privileges" starts without a prompt, as the user, so the broker's
/// per-user state lines up with the user's own tools; it is not a Windows service (LocalSystem) because nothing
/// here needs a credential-free account, and a service would have forced every lock into a machine-wide path.
/// Install and uninstall need elevation once; when not elevated they relaunch themselves with the RunAs verb,
/// which on this machine UAC auto-approves. The binary lives under Program Files so a user-level process cannot
/// replace what runs elevated at the next logon.
/// <para>
/// Three facts shape the details. The elevated copy gets a console of its own that closes with it, so it writes
/// its lines to a log file the caller prints afterwards. A running broker holds its exe and dlls open, so the
/// task is ended and the process waited for before the copy. And a task made with schtasks' switches stops its
/// action after 72 hours and shows the console on the desktop, so the task is registered from XML: no time
/// limit, one instance, the interactive session (where NVAPI behaves as it does for the user's own tools), and
/// the broker detaches its console itself.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Installer
{
    /// <param name="log">Where the elevated copy appends its lines, so the caller (whose console it cannot reach) can print them.</param>
    public static int Install(string? log = null)
    {
        // Validate the companions before any prompt and before stopping the installed task: a broken bundle must cost the
        // person nothing and leave the old service running.
#pragma warning disable IL3000
        var files = BrokerInstallBundle.Files(AppContext.BaseDirectory,
            Environment.ProcessPath ?? throw new InvalidOperationException("no broker executable path"), string.IsNullOrEmpty(typeof(Installer).Assembly.Location));
#pragma warning restore IL3000
        if (!Elevation.IsAdministrator()) return Relaunch("install");
        var say = Logger(log);
        var dst = BrokerPaths.InstallDir();
        var exe = Path.Combine(dst, "Clocktail.Broker.exe");

        Run("schtasks", $"/end /tn {BrokerPaths.TaskName}", ignoreFailure: true);
        WaitForBrokerExit(dst, say);
        CopyBuildOutput(dst, say, files);
        // The protected approval folder is made (or one not to be trusted moved aside) here, elevated and asked for,
        // before the service starts and looks for it. A failure is said and the install goes on: the service then runs
        // without approvals and says so.
        try { WindowsProfileApprovals.Repair(say); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            say($"the protected approval folder could not be prepared ({e.Message}): the service will run, but daily profiles stay off until this is fixed");
        }

        var xml = Path.Combine(Path.GetTempPath(), $"{BrokerPaths.TaskName}-{Environment.ProcessId}.xml");
        File.WriteAllText(xml, TaskXml(exe), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try { Run("schtasks", $"/create /tn {BrokerPaths.TaskName} /xml \"{xml}\" /f"); }
        finally { try { File.Delete(xml); } catch (IOException) { } }
        Run("schtasks", $"/run /tn {BrokerPaths.TaskName}");
        say($"installed: task {BrokerPaths.TaskName} runs \"{exe}\" run --detach at logon (highest privileges, no time limit, one instance), and was started now");
        return 0;
    }

    public static int Uninstall(string? log = null)
    {
        if (!Elevation.IsAdministrator()) return Relaunch("uninstall");
        var say = Logger(log);

        Run("schtasks", $"/end /tn {BrokerPaths.TaskName}", ignoreFailure: true);
        WaitForBrokerExit(BrokerPaths.InstallDir(), say);
        Run("schtasks", $"/delete /tn {BrokerPaths.TaskName} /f", ignoreFailure: true);
        var dir = BrokerPaths.InstallDir();
        bool fromThere = string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (!fromThere && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); say($"removed {dir}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { say($"could not remove {dir}: {e.Message}"); }
        }
        say($"uninstalled: task {BrokerPaths.TaskName} stopped and removed" + (fromThere ? $" (files under {dir} left in place: run uninstall from the release folder to remove them)" : ""));
        return 0;
    }

    /// <summary>
    /// Run this verb again as an elevated process and wait. Its console is its own and closes with it, so it
    /// writes to a log under the user's state root, which is printed here; the exit code is passed through.
    /// </summary>
    private static int Relaunch(string verb)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot find this executable to relaunch it elevated");
        Directory.CreateDirectory(BrokerPaths.StateRoot());
        var log = Path.Combine(BrokerPaths.StateRoot(), $"{verb}.log");
        try { File.Delete(log); } catch (IOException) { }

        var psi = new ProcessStartInfo(exe, $"{verb} --log \"{log}\"") { UseShellExecute = true, Verb = "runas" };
        int code;
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("the elevated relaunch did not start");
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch (Win32Exception e)
        {
            // 1223: the elevation prompt was declined; anything else: RunAs itself failed.
            throw new InvalidOperationException($"the elevated relaunch was refused ({e.Message}); nothing was changed");
        }
        if (File.Exists(log)) Console.Write(File.ReadAllText(log));
        if (code != 0) Console.Error.WriteLine($"broker: elevated {verb} exited with {code}" + (File.Exists(log) ? $" (log: {log})" : ", and left no log"));
        return code;
    }

    private static Action<string> Logger(string? path) => line =>
    {
        Console.WriteLine(line);
        if (path == null) return;
        try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { }
    };

    /// <summary>
    /// The task definition. Registered from XML because the schtasks switches cannot say "no execution time limit"
    /// (the default stops the action after PT72H) or "ignore a second start"; the interactive token keeps the broker
    /// in the user's own session, as the CLI runs, with the highest privileges the user holds.
    /// </summary>
    private static string TaskXml(string exe)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("cannot determine the current user's SID");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Clocktail broker: the one elevated process that performs GPU writes for this user, behind a user-only named pipe.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>run --detach</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>After the task was ended: wait for the installed broker to be gone, so its files can be replaced.</summary>
    private static void WaitForBrokerExit(string dir, Action<string> say)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var running = Process.GetProcessesByName("Clocktail.Broker").Where(p => p.Id != Environment.ProcessId && IsUnder(p, dir)).ToList();
            if (running.Count == 0) return;
            if (DateTime.UtcNow < deadline) { Thread.Sleep(200); continue; }
            foreach (var p in running)
            {
                say($"stopping broker pid {p.Id}, which did not exit when its task was ended");
                try { p.Kill(); p.WaitForExit(2000); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { say($"  could not stop it: {e.Message}"); }
            }
            return;
        }
    }

    private static bool IsUnder(Process p, string dir)
    {
        try { return p.MainModule?.FileName is { } f && f.StartsWith(dir, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>The broker's own framework-dependent output: its exe, the dlls it links, and the two run-time json files.</summary>
    private static void CopyBuildOutput(string dst, Action<string> say, IReadOnlyList<string> buildFiles)
    {
        var src = AppContext.BaseDirectory;
        if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dst).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            say($"running from {dst}; files not copied");
            return;
        }
        Directory.CreateDirectory(dst);
        // The broker and lab must both run from the protected destination, never a user-writable helper path.
        var files = buildFiles.ToList();
        // PresentMon goes with the broker, which runs it elevated from here (a folder only administrators can write). A
        // capture still running from here would hold the file: it ends first.
        if (Path.Combine(src, FrameCapture.ExeName) is var pm && File.Exists(pm) && !files.Contains(pm, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(pm);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(FrameCapture.ExeName)))
                try { if (string.Equals(p.MainModule?.FileName, Path.Combine(dst, FrameCapture.ExeName), StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                finally { p.Dispose(); }
        }
        int n = 0;
        foreach (var file in files)
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            n++;
        }
        say($"copied {n} files from {src} to {dst}");
    }

    private static void Run(string file, string args, bool ignoreFailure = false)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !ignoreFailure)
            throw new InvalidOperationException($"{file} {args} failed ({p.ExitCode}): {o}{e}".Trim());
    }
}
