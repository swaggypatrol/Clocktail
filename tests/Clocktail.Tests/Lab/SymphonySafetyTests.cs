using Bwt.Workloads;
using Clocktail.Lab;
using Clocktail.Mcp;
using Clocktail.Domain.Lab;
using System.Text.Json;

namespace Clocktail.Tests.Lab;

public sealed class SymphonySafetyTests
{
    private sealed class Sensor(Func<uint?> read) : ITemperatureSensor
    {
        public uint? ReadTemperature() => read();
        public void Dispose() { }
    }

    [Fact]
    public void Temperature_limit_cancels_before_aria_can_start()
    {
        using var cancel = new CancellationTokenSource();
        using var monitor = new SafetyMonitor(() => new Sensor(() => 86), 85, cancel);
        Assert.False(monitor.WaitReady(TimeSpan.FromSeconds(2)));
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal("temperature", monitor.Interrupted);
    }

    [Fact]
    public void Lost_temperature_cancels_instead_of_allowing_an_unwatched_run()
    {
        using var cancel = new CancellationTokenSource();
        using var monitor = new SafetyMonitor(() => new Sensor(() => null), 85, cancel, TimeSpan.FromMilliseconds(20));
        Assert.False(monitor.WaitReady(TimeSpan.FromSeconds(2)));
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal("telemetry", monitor.Interrupted);
    }

    [Fact]
    public void A_healthy_temperature_allows_preparation()
    {
        using var cancel = new CancellationTokenSource();
        using var monitor = new SafetyMonitor(() => new Sensor(() => 60), 85, cancel);
        Assert.True(monitor.WaitReady(TimeSpan.FromSeconds(2)));
        Assert.False(cancel.IsCancellationRequested);
    }

    [Theory]
    [InlineData(701, LabResult.Inconclusive)]
    [InlineData(700, LabResult.Fail)]
    public void Cuda_errors_are_classified_before_attributing_a_failure(int code, string verdict)
    {
        var phase = new PhaseResult { Kind = LabPhase.Burn, Error = "CUDA error", ErrorCode = code };
        Assert.Equal(verdict, LabRunner.Judge([phase], 1).Verdict);
    }

    [Fact]
    public void A_real_wrong_result_survives_a_later_runner_error()
    {
        var phase = new PhaseResult { Kind = LabPhase.Burn, Error = "CUDA error", ErrorCode = 701, ComputeMismatches = 1 };
        Assert.Equal(LabResult.Fail, LabRunner.Judge([phase], 1).Verdict);
    }

    [Theory]
    [InlineData(false, false, false, false, 0, "pass", true)]
    [InlineData(false, false, true, true, -1, "pass", false)]
    [InlineData(false, false, false, true, 2, "pass", false)]
    [InlineData(false, true, false, false, 0, "pass", false)]
    [InlineData(true, false, false, false, 0, "pass", false)]
    public void A_pass_requires_a_clean_exit_and_a_completed_run(bool failed, bool hot, bool hung, bool runner, int code, string verdict, bool expected) =>
        Assert.Equal(expected, TuningTools.LabPassed(failed, hot, hung, runner, code, verdict));

    private static JsonElement Report(string verdict) => JsonSerializer.SerializeToElement(new { verdict, reasons = new[] { "evidence" } });

    [Fact]
    public void Symphony_keeps_leap_with_the_core_worker_and_runs_movements_in_order()
    {
        var movements = SymphonyScore.Movements(LabTestPlan.Create("symphony"));
        Assert.Equal(["aria", "core"], movements.Select(m => m.Name));
        Assert.Equal("burn:30,solo:60,leap:60", movements[1].Phases);
        List<string> run = [];
        var result = SymphonyScore.Perform(movements, m => { run.Add(m.Name); return new(0, Report("pass")); });
        Assert.Equal("pass", result.Verdict);
        Assert.Equal(["aria", "core"], run);
    }

    [Theory]
    [InlineData(null, "pass", "inconclusive")]
    [InlineData(2, "pass", "inconclusive")]
    [InlineData(0, "inconclusive", "inconclusive")]
    [InlineData(4, "fail", "fail")]
    [InlineData(null, "fail", "fail")]
    public void An_unfinished_or_failed_movement_stops_the_score(int? exit, string verdict, string expected)
    {
        int calls = 0;
        var result = SymphonyScore.Perform(SymphonyScore.Movements(LabTestPlan.Create("symphony")), _ =>
        { calls++; return new(exit, Report(verdict)); });
        Assert.Equal(expected, result.Verdict);
        Assert.Equal(1, calls);
    }
    [Fact]
    public void Empty_score_report_is_inconclusive_and_does_not_throw()
    {
        var score = SymphonyScore.Perform([], _ => throw new InvalidOperationException());
        var report = TuningTools.ScoreReport(score);
        Assert.Equal("inconclusive", report.GetProperty("verdict").GetString());
        Assert.Empty(report.GetProperty("movements").EnumerateArray());
    }

    [Fact]
    public void Aria_parameters_match_its_embedded_kernel_image()
    {
        var p = C01Command.AriaParameters();
        Assert.Equal("16", p["acc"]);
        Assert.Equal("8", p["hash_interval"]);
        var resources = typeof(C01Fp32Fma).Assembly.GetManifestResourceNames();
        Assert.Contains("c01_fp32_fma_checkpoint8.ptx", resources);
        Assert.Contains("c01_fp32_fma_checkpoint8.sm_120.cubin", resources);
        Assert.Equal("c01-cpu-fma-exact-hash8-1", C01Fp32Fma.InfoForHashInterval(8).OracleVersion);
    }

    [Fact]
    public void Temperature_loss_after_readiness_stops_preparation()
    {
        int reads = 0;
        using var cancel = new CancellationTokenSource();
        using var monitor = new SafetyMonitor(() => new Sensor(() => Interlocked.Increment(ref reads) == 1 ? 60u : null), 85, cancel, TimeSpan.FromMilliseconds(20));
        Assert.True(monitor.WaitReady(TimeSpan.FromSeconds(2)));
        Assert.True(cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
        Assert.Equal("telemetry", monitor.Interrupted);
    }

    [Fact]
    public void A_previous_mismatch_survives_a_later_nonfatal_cuda_error()
    {
        var wrong = new PhaseResult { Kind = LabPhase.Burn, ComputeMismatches = 1, Completed = true };
        var error = new PhaseResult { Kind = LabPhase.Solo, Error = "resource error", ErrorCode = 701 };
        Assert.Equal(LabResult.Fail, LabRunner.Judge([wrong, error], 2).Verdict);
    }

    [Fact]
    public void Phase_observations_exclude_preparation_and_other_movements()
    {
        var at = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var report = JsonSerializer.SerializeToElement(new { phases = new[] { new { kind = "c01", started_utc = at, finished_utc = at.AddSeconds(2) } } });
        Card.Sample Sample(int seconds, uint mv) => new(at.AddSeconds(seconds), null, mv * 1000, null, null, null);
        var observations = TuningTools.PhaseObservations(report, [Sample(-1, 700), Sample(1, 900), Sample(3, 1100)]);
        var data = JsonSerializer.SerializeToElement(observations).EnumerateArray().Single();
        Assert.Equal(1, data.GetProperty("samples").GetInt32());
        Assert.Equal(900, data.GetProperty("nvvdd_mv").GetDouble());
    }

    [Fact]
    public void Interrupted_aria_has_no_phase_window()
    {
        var report = JsonSerializer.SerializeToElement(new { phases = new[] { new { kind = "c01", started_utc = (DateTime?)null, finished_utc = (DateTime?)null } } });
        Assert.Empty(TuningTools.PhaseObservations(report, []));
    }

}
