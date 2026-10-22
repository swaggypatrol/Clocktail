using System.Diagnostics;
using Clocktail.Domain.Platform;

namespace Clocktail.Tests.Domain;

/// <summary>A measurement is over only when its process is: stopping one waits until it has exited, and one tied to a
/// kill-on-close job ends when the job closes (the process that started it ending, however it ends).</summary>
public class ChildProcessTests
{
    private static Process LongRunning() =>
        Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

    [Fact]
    public void Stop_returns_once_the_process_has_exited()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var p = LongRunning();
        Assert.False(p.HasExited);
        Assert.True(ChildProcess.Stop(p));
        Assert.True(p.HasExited);
        Assert.True(ChildProcess.Stop(p));   // already gone: still true
    }

    [Fact]
    public void A_process_in_a_kill_on_close_job_ends_when_the_job_closes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var p = LongRunning();
        var job = new KillOnCloseJob();
        Assert.True(job.Add(p));
        job.Dispose();
        Assert.True(p.WaitForExit(10_000));
    }
}
