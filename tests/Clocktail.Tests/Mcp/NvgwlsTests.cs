using System.Diagnostics;
using Clocktail.Domain.Platform;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class NvgwlsTests
{
    [Fact]
    public void Exit_code_2_with_a_driver_reset_is_a_hang_the_card_failed()
    {
        Assert.StartsWith("hang: the driver reset", Nvgwls.Verdict(2, 1));
        Assert.True(Nvgwls.Failed(2, 1));
    }

    [Fact]
    public void Exit_code_2_without_a_reset_is_a_run_that_could_not_start()
    {
        Assert.Equal(Nvgwls.Verdict(2), Nvgwls.Verdict(2, 0));
        Assert.False(Nvgwls.Failed(2, 0));
    }

    [Fact]
    public void An_unreadable_log_leaves_exit_code_2_open_and_the_others_as_they_were()
    {
        Assert.Contains("could not be read", Nvgwls.Verdict(2, null));
        Assert.False(Nvgwls.Failed(2, null));
        Assert.Equal("pass", Nvgwls.Verdict(0, null));
        Assert.True(Nvgwls.Failed(6, null));
    }

    [Fact]
    public void A_reset_fails_even_a_run_that_exited_0()
    {
        Assert.True(Nvgwls.Failed(0, 2));
        Assert.Contains("2 reset events", Nvgwls.Verdict(0, 2));
    }
}

/// <summary>stress_test's temperature stop: the limit, or within 2 °C of the driver's slowdown point.</summary>
public class StressThermalStopTests
{
    [Fact]
    public void Too_hot_is_the_limit_or_the_last_two_degrees_of_margin()
    {
        Assert.False(Clocktail.Mcp.Nvgwls.TooHot(84, 6, 85));
        Assert.True(Clocktail.Mcp.Nvgwls.TooHot(85, 5, 85));
        Assert.True(Clocktail.Mcp.Nvgwls.TooHot(80, 2, 85));     // the driver says it is about to slow down
        Assert.False(Clocktail.Mcp.Nvgwls.TooHot(null, null, 85)); // nothing read: not a reason to stop
    }

    /// <summary>With no maxTempC the one stop is the margin to NVIDIA's slowdown point, and the reason says which it was.</summary>
    [Fact]
    public void With_no_max_temp_only_the_margin_stops_a_run_and_the_reason_names_it()
    {
        Assert.False(Clocktail.Mcp.Nvgwls.TooHot(95, 3, null));
        Assert.True(Clocktail.Mcp.Nvgwls.TooHot(88, 2, null));
        Assert.False(Clocktail.Mcp.Nvgwls.TooHot(120, null, null));   // no margin read, no limit asked: firmware's call
        Assert.Equal("within 2 °C of NVIDIA's slowdown point", Clocktail.Mcp.Nvgwls.HotWhy(88, null));
        Assert.Equal("within 2 °C of NVIDIA's slowdown point", Clocktail.Mcp.Nvgwls.HotWhy(80, 85));
        Assert.Equal("at the maxTempC of 85 °C asked for", Clocktail.Mcp.Nvgwls.HotWhy(85, 85));
        Assert.Null(Clocktail.Mcp.TuningTools.MaxTemp(null));
        Assert.Equal(80, Clocktail.Mcp.TuningTools.MaxTemp(80));
        Assert.Throws<ArgumentException>(() => Clocktail.Mcp.TuningTools.MaxTemp(2147483647));
    }
}

/// <summary>stress_test's run is over only when its process is: on every way out (an exit, a stop, an overrun, a sampling
/// error) the process is gone by the time Run returns, and what it started dies with the job it sits in.</summary>
public class NvgwlsRunTests
{
    private static ProcessStartInfo Hidden(string exe, string args) =>
        new(exe, args) { UseShellExecute = false, CreateNoWindow = true };

    private static ProcessStartInfo LongRunning() => Hidden("ping", "-n 60 127.0.0.1");

    /// <summary>A handle of our own on the process Run starts: Run disposes its own before it returns.</summary>
    private static Action<Process> Keep(List<Process> into) => p => into.Add(Process.GetProcessById(p.Id));

    [Fact]
    public void A_normal_exit_returns_its_code()
    {
        if (!OperatingSystem.IsWindows()) return;
        int? code = Nvgwls.Run(Hidden("cmd", "/c exit 3"), 30, () => false, out bool stopped, out bool exited);
        Assert.Equal(3, code);
        Assert.False(stopped);
        Assert.True(exited);
    }

    [Fact]
    public void A_stop_ends_the_process_before_run_returns()
    {
        if (!OperatingSystem.IsWindows()) return;
        var kept = new List<Process>();
        try
        {
            int? code = Nvgwls.Run(LongRunning(), 60, () => true, out bool stopped, out bool exited, started: Keep(kept));
            Assert.Null(code);
            Assert.True(stopped);
            Assert.True(exited);
            Assert.True(kept.Single().HasExited);
        }
        finally { foreach (var p in kept) { ChildProcess.Stop(p); p.Dispose(); } }
    }

    [Fact]
    public void An_overrun_ends_the_process_before_run_returns()
    {
        if (!OperatingSystem.IsWindows()) return;
        var kept = new List<Process>();
        try
        {
            int? code = Nvgwls.Run(LongRunning(), 1, () => false, out bool stopped, out bool exited, graceS: 0, started: Keep(kept));
            Assert.Null(code);
            Assert.False(stopped);
            Assert.True(exited);
            Assert.True(kept.Single().HasExited);
        }
        finally { foreach (var p in kept) { ChildProcess.Stop(p); p.Dispose(); } }
    }

    [Fact]
    public void A_sampling_error_ends_the_process_before_it_reaches_the_caller()
    {
        if (!OperatingSystem.IsWindows()) return;
        var kept = new List<Process>();
        try
        {
            var e = Assert.Throws<InvalidOperationException>(() =>
                Nvgwls.Run(LongRunning(), 60, () => throw new InvalidOperationException("the card went away"), out _, out _, started: Keep(kept)));
            Assert.Equal("the card went away", e.Message);   // it exited: nothing added to the message
            Assert.True(kept.Single().HasExited);
        }
        finally { foreach (var p in kept) { ChildProcess.Stop(p); p.Dispose(); } }
    }

    /// <summary>A process the stressor started and left behind (the tree Kill walks is gone once its root exits) dies with
    /// the kill-on-close job when Run returns, as it would if this server ended mid-run.</summary>
    [Fact]
    public void What_the_process_started_dies_with_its_job()
    {
        if (!OperatingSystem.IsWindows()) return;
        string signal = "ClocktailNvgwlsRun" + Guid.NewGuid().ToString("N")[..12];
        var before = Process.GetProcessesByName("waitfor").Select(p => p.Id).ToHashSet();
        var left = new Dictionary<int, Process>();
        bool Watch()
        {
            foreach (var p in Process.GetProcessesByName("waitfor"))
                if (before.Contains(p.Id) || !left.TryAdd(p.Id, p)) p.Dispose();
            return false;
        }
        try
        {
            int? code = Nvgwls.Run(Hidden("cmd", $"/c \"start /b waitfor /t 60 {signal} >nul 2>&1 & ping -n 4 127.0.0.1 >nul\""),
                                   30, Watch, out _, out bool exited);
            Assert.Equal(0, code);
            Assert.True(exited);
            Assert.NotEmpty(left);
            Assert.All(left.Values, p => Assert.True(p.WaitForExit(5_000), $"waitfor {p.Id} outlived the run"));
        }
        finally { foreach (var p in left.Values) { ChildProcess.Stop(p); p.Dispose(); } }
    }
}