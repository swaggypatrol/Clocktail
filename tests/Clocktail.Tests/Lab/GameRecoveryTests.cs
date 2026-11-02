using System.Text.Json;
using Clocktail.Domain.Profiles;
using Clocktail.Lab;
using Clocktail.Lab.Game;

namespace Clocktail.Tests.Lab;

/// <summary>Offline tests of the real job's recovery state and telemetry ownership, not just the simulated game loop.</summary>
public class GameRecoveryTests
{
    private static readonly DateTimeOffset Began = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static GameSpec Spec() => new("card", "0000:01:00.0", "q2rtx.exe", 120, 7, ["heavy"], ["power", "fans"], SessionAt: Began);

    [Theory]
    [InlineData(GameEnd.Survived, GameEnd.Unjudged)]
    [InlineData(GameEnd.Stopped, GameEnd.Unjudged)]
    [InlineData(GameEnd.Failed, GameEnd.Failed)]
    public void Failed_recovery_never_leaves_a_survived_result(GameEnd before, GameEnd after)
    {
        var result = new GameOutcome(before, "original outcome", Credited: 120);
        Assert.Equal(result, result.AfterRecovery(null));
        var failed = result.AfterRecovery("the service did not answer");
        Assert.Equal(after, failed.End);
        Assert.Contains("recovery not confirmed", failed.Why);
    }

    [Theory]
    [InlineData("VK_ERROR_OUT_OF_DEVICE_MEMORY", GameEnd.Unjudged)]
    [InlineData("VK_ERROR_OUT_OF_HOST_MEMORY", GameEnd.Unjudged)]
    [InlineData("Failed to allocate GPU memory for screen-space textures!", GameEnd.Unjudged)]
    [InlineData("VK_ERROR_DEVICE_LOST", GameEnd.Failed)]
    public void Allocation_failure_is_not_recorded_as_an_overclock_crash(string log, GameEnd expected)
    {
        var result = new GameOutcome(GameEnd.Failed, "game exited", GameOutcome.Crash).AfterGameLog(log);
        Assert.Equal(expected, result.End);
        if (expected == GameEnd.Unjudged) Assert.Null(result.FailKind);
        else Assert.Contains("device lost", result.Why);
        var reset = new GameOutcome(GameEnd.Failed, "driver reset", GameOutcome.Reset);
        Assert.Equal(reset, reset.AfterGameLog(log));
    }

    [Fact]
    public void The_job_wire_contract_contains_no_client_supplied_restoration_values()
    {
        string wire = JsonSerializer.Serialize(Spec(), LabJson.Options);
        var spec = JsonSerializer.Deserialize<GameSpec>(wire, LabJson.Options)!;
        spec.Validate();
        Assert.Contains("session_at", wire);
        using var json = JsonDocument.Parse(wire);
        foreach (string field in new[] { "curve", "start_power_mw", "min_power_mw", "top_mhz", "restore" })
            Assert.False(json.RootElement.TryGetProperty(field, out _));
    }

    [Fact]
    public void A_direct_job_spec_cannot_bypass_the_tool_parameter_bounds()
    {
        var spec = Spec();
        spec.Validate();
        foreach (var bad in new[]
        {
            spec with { Seconds = 0 }, spec with { Seconds = 14401 },
            spec with { WindowPct = 89 }, spec with { MaxScale = 201 }, spec with { GraceSeconds = -1 },
            spec with { Patterns = [] }, spec with { Patterns = ["unknown"] }, spec with { Levers = ["unknown"] },
            spec with { Pci = "invalid" }, spec with { SessionAt = null },
        }) Assert.Throws<ArgumentException>(bad.Validate);
    }

    [Theory]
    [InlineData("nonsense", "nonsense")]
    [InlineData("0000:01:00.0", "0000:01:00.1")]
    [InlineData("0000:01:00.0", "0000:01:20.0")]
    [InlineData("0000:01:00.0", "0000:01:00")]
    public void Pci_matching_does_not_drop_the_function_or_match_two_parse_failures(string target, string device)
    {
        Assert.NotNull(VulkanDevices.Refusal([new RtDevice("RTX 5090", 0x10de, 0x2b85, device)], target));
    }

    [Fact]
    public void An_oversized_screen_is_refused_instead_of_silently_using_a_small_window() =>
        Assert.Throws<NotSupportedException>(() => WindowPlan.For(12000, 4320, 90, 60));

    [Fact]
    public void Old_temperature_samples_do_not_keep_a_stalled_sampler_looking_healthy()
    {
        var now = Began.UtcDateTime;
        GameSample Sample(int seconds, uint? temp) => new(now.AddSeconds(seconds), 2800, 14000, 500, 600, temp, 99, 0);
        Assert.Equal(64, GameSampler.FreshTemperature([Sample(-1, 64), Sample(0, 66)], now));
        Assert.Null(GameSampler.FreshTemperature([Sample(-10, 64), Sample(-9, 66)], now));
        Assert.Null(GameSampler.FreshTemperature([Sample(-1, null), Sample(0, 66)], now));
        Assert.Null(GameSampler.FreshTemperature([], now));
    }

    [Fact]
    public void A_short_light_step_does_not_borrow_the_following_heavy_steps_clock()
    {
        var start = Began.UtcDateTime;
        StepResult Step(int i, string kind, double from, double to) => new(i, "leap", kind, kind, start.AddSeconds(from), start.AddSeconds(to),
            to - from, 100, 100, 100, 100, "demo2", null, null, null);
        var steps = new[] { Step(0, GameStep.Heavy, 0, 1), Step(1, GameStep.Light, 1, 1.25), Step(2, GameStep.Heavy, 1.25, 3) };
        GameSample Sample(double at, uint mhz) => new(start.AddSeconds(at), mhz, 14000, 500, 600, 65, 99, 0);
        var result = GameTest.Cover(steps, [Sample(0.8, 2800), Sample(1.1, 2800), Sample(1.5, 3200)], 3200);
        Assert.Equal((1, 0), (result.Transitions, result.Leapt));
        Assert.Equal(0, GameTest.Cover([], [], 3200).SecondsNearTop);
    }

    [Fact]
    public void Suspended_launch_quotes_paths_and_does_not_execute_before_containment()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("\"\"", SuspendedGame.Quote(""));
        Assert.Equal("\"a\\\"b\"", SuspendedGame.Quote("a\"b"));
        Assert.Equal("\"C:\\path with spaces\\\\\"", SuspendedGame.Quote(@"C:\path with spaces\"));
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/d", "/c", "exit", "0" }) start.ArgumentList.Add(arg);
        using (var launch = SuspendedGame.Start(start))
        using (var child = launch.Process)
        using (var job = new Clocktail.Domain.Platform.KillOnCloseJob())
        {
            Assert.False(child.HasExited); // even an immediate exit cannot run while suspended
            Assert.True(job.Add(child));
            launch.Resume();
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(0, child.ExitCode);
        }
        var cancelled = SuspendedGame.Start(start);
        using var neverResumed = cancelled.Process;
        cancelled.Dispose();
        Assert.True(neverResumed.WaitForExit(5000));
    }

    private sealed class BlockingSampler(ManualResetEventSlim entered, ManualResetEventSlim release, ManualResetEventSlim disposed) : ISampler
    {
        public LabSample Read(double t, int phase)
        {
            entered.Set();
            release.Wait();
            return new(t, phase, 500, 65, 2800, 14000, 99);
        }
        public void Dispose() => disposed.Set();
    }

    [Fact]
    public void Disposing_telemetry_never_unloads_a_transport_with_a_call_still_blocked_in_it()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        string path = Path.Combine(Path.GetTempPath(), "ct-telemetry-" + Guid.NewGuid().ToString("N"));
        var sampler = new GameSampler(() => new BlockingSampler(entered, release, disposed), path);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            sampler.Dispose();
            Assert.False(disposed.IsSet);
        }
        finally
        {
            release.Set();
            sampler.Dispose();
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)));
            File.Delete(path);
        }
    }

    [Fact]
    public void Telemetry_startup_failure_is_reported_instead_of_escaping_a_background_thread()
    {
        string path = Path.Combine(Path.GetTempPath(), "ct-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sampler = new GameSampler(() => throw new InvalidOperationException("offline fake failed"), path);
            Assert.True(SpinWait.SpinUntil(() => sampler.Failure != null, TimeSpan.FromSeconds(5)));
            Assert.Contains("offline fake failed", sampler.Failure);
        }
        finally { File.Delete(path); }
    }
}
