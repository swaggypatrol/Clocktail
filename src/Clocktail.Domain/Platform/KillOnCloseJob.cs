using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Domain.Platform;

/// <summary>
/// A job object that ends every process in it when it closes: on <see cref="Dispose"/>, or when this process ends however
/// it ends (its handle closing is what the kernel acts on). A process this one started and added never outlives it: the
/// service's worker under its supervisor, a lab measurement under the job or the MCP server that started it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KillOnCloseJob : IDisposable
{
    private readonly object _lock = new();
    private nint _job;

    /// <summary>Ties <paramref name="p"/> to this job; false when the job cannot be made or the process not added.</summary>
    public bool Add(Process p)
    {
        lock (_lock)
        {
            if (_job == 0)
            {
                var job = CreateJobObjectW(0, null);
                if (job == 0) return false;
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
                if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>())) { CloseHandle(job); return false; }
                _job = job;
            }
            return AssignProcessToJobObject(_job, p.Handle);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_job != 0) CloseHandle(_job);
            _job = 0;
        }
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int size);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

public static class ChildProcess
{
    /// <summary>How long <see cref="Stop"/> waits for a killed process to be gone.</summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Ends <paramref name="p"/> and what it started, and waits until it is gone: a measurement is over only when its
    /// process is, so nothing (giving the card back, the next write) starts while its load may still run. True when it has
    /// exited.
    /// </summary>
    public static bool Stop(Process p, TimeSpan? wait = null)
    {
        try
        {
            if (p.HasExited) return true;
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { return true; }   // never started, or already gone
        catch (Win32Exception) { }                            // exiting as we asked: wait for it below
        try { return p.WaitForExit(wait ?? ExitWait); }
        catch (InvalidOperationException) { return true; }
    }
}
