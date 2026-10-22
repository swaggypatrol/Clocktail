using System.Runtime.InteropServices;
using System.Text;

namespace Clocktail.Lab;

/// <summary>
/// Starts the sweep so that nothing above it ends it: not the MCP server's worker (it ends with its whole process tree when
/// the driver goes away) and not a job object its host put it in (a process started the ordinary way stays in its parent's
/// job, and a host that closes a kill-on-close job ends it with everything else: the sweep of 2026-10-21 died that way
/// before its first line). CREATE_BREAKAWAY_FROM_JOB leaves the job when the job allows it; when it does not, the sweep
/// starts in it all the same and the answer says so.
/// </summary>
public static class Detach
{
    private const uint CreateBreakawayFromJob = 0x01000000, CreateNoWindow = 0x08000000, CreateNewProcessGroup = 0x00000200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, nint pa, nint ta, bool inherit, uint flags, nint env, string? cwd,
        ref StartupInfo si, out ProcessInformation pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, nint job, out bool result);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    /// <summary>Starts <paramref name="exe"/> with <paramref name="args"/>; the process id and whether it is still in a job.</summary>
    public static (int Pid, bool InJob) Start(string exe, IEnumerable<string> args)
    {
        var cmd = new StringBuilder(Quote(exe));
        foreach (var a in args) cmd.Append(' ').Append(Quote(a));
        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        uint flags = CreateNoWindow | CreateNewProcessGroup;
        if (!CreateProcessW(exe, new StringBuilder(cmd.ToString()), 0, 0, false, flags | CreateBreakawayFromJob, 0, null, ref si, out var pi)
            && !CreateProcessW(exe, new StringBuilder(cmd.ToString()), 0, 0, false, flags, 0, null, ref si, out pi))
            throw new InvalidOperationException($"the sweep did not start (error {Marshal.GetLastWin32Error()})");
        bool inJob = IsProcessInJob(pi.hProcess, 0, out var j) && j;
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return (pi.dwProcessId, inJob);
    }

    /// <summary>One argument as CommandLineToArgvW reads it back: quoted, inner quotes and the backslashes before them escaped.</summary>
    public static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}
