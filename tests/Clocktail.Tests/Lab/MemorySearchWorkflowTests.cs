using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>The real sweep/confirmation workflow over a fake broker and measuring process: no GPU, sleeps or global locks.</summary>
public sealed class MemorySearchWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-memory-search-" + Guid.NewGuid().ToString("N"));
    public MemorySearchWorkflowTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Token : IDisposable { public void Dispose() { } }
    private sealed class Rig(string dir, int to = 100) : MemorySweep(new("fake", "fake", "0", 0, 0, to, 50), dir, "fake")
    {
        public int Offset, Generation, Restores;
        private string? _id, _stamp;
        private readonly Dictionary<int, int> _measures = [];
        public readonly List<(int Offset, string? Trial)> Stable = [];
        public readonly List<(int Offset, string? Trial)> Crashes = [];
        public Func<int, int, string, PhaseResult, PhaseResult> Alter = (_, _, _, phase) => phase;
        public bool StopInComparison, ChangeTrialInComparison, FailRestore, ChangeBaseline;
        private bool _stopFired, _changeFired;

        protected override IDisposable? ClaimCard() => new Token();
        protected override IDisposable? Hold(Action<string>? log) => new Token();
        protected override Timer Heartbeat() => new(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        protected override void Settle() { }
        protected override int? CountResets(DateTime since) => 0;

        protected override BrokerResponse? Send(BrokerRequest request)
        {
            switch (request.Kind)
            {
                case "show":
                    object? trial = _id == null ? null : new { At = _stamp, Id = _id };
                    return new(true, Result: JsonSerializer.SerializeToElement(new { experiment = new { Trial = trial } }));
                case "try":
                    using (var json = JsonDocument.Parse(request.Value!)) Offset = json.RootElement.GetProperty("memory_khz").GetInt32() / 1000;
                    _id = "trial-" + ++Generation;
                    _stamp = "stamp-" + Generation;
                    return new(true);
                case "stable":
                    if (request.Key != _id) return new(false, "trial changed");
                    Stable.Add((Offset, request.Key)); return new(true);
                case "crash":
                    if (request.Key != _id) return new(false, "trial changed");
                    Crashes.Add((Offset, request.Key)); _id = null; _stamp = null; Offset = 0; return new(true);
                case "finish-job":
                    if (FailRestore) return new(false, "simulated readback mismatch");
                    Restores++; Offset = 0; _id = null; _stamp = null; return new(true);
                default: return new(true);
            }
        }

        protected override LabResult Measure(int seconds, string phases, ulong? capMib)
        {
            if (ChangeBaseline && phases == "memory") { _id = "someone-else"; _stamp = "moved"; }
            int n = _measures.GetValueOrDefault(Offset) + 1;
            _measures[Offset] = n;
            var result = phases.Split(',').Select(p => Alter(Offset, n, phases, new PhaseResult
            {
                Kind = Enum.Parse<LabPhase>(p, true), Seconds = seconds / phases.Split(',').Length, Completed = true,
                Gbps = p == "memory" ? 1500 : null, LatencyNs = p == "latency" ? 500 : null,
                MemMhzP50 = 13801 + Offset, TempP50 = 60, TempMax = 62,
            })).ToList();
            // A candidate comparison has started after the two scan writes.
            if (Generation > 2 && StopInComparison && !_stopFired)
            { _stopFired = true; File.WriteAllText(Path.Combine(JobDir, "stop"), ""); }
            if (Generation > 2 && ChangeTrialInComparison && !_changeFired)
            { _changeFired = true; _id = "someone-else"; _stamp = "moved"; }
            var (verdict, why) = LabRunner.Judge(result, result.Count);
            return new("fake", "fake", 1024, 100, DateTime.UtcNow, DateTime.UtcNow, result, verdict, why, 0, 512, 0);
        }
    }

    private SweepStatus Status() => JsonSerializer.Deserialize<SweepStatus>(File.ReadAllText(Path.Combine(_dir, "status.json")), LabJson.Options)!;

    [Fact]
    public void A_flat_scan_recommends_the_baseline_after_paired_confirmation_and_restores_it()
    {
        var rig = new Rig(_dir);
        Assert.Equal(0, rig.Run());
        var status = Status();
        Assert.Equal(SweepStatus.Done, status.State);
        Assert.Equal(0, status.BestMhz);
        Assert.Equal(100, status.EdgeMhz);
        Assert.True(status.Confirmed);
        Assert.Equal(4, Assert.Single(status.Comparisons!).Runs.Count);
        Assert.Equal(1, rig.Restores);
        Assert.Equal(0, rig.Offset);
    }

    [Fact]
    public void A_lucky_scan_reading_is_rechecked_and_does_not_cut_off_the_search()
    {
        var rig = new Rig(_dir, 200)
        {
            Alter = (offset, nth, _, phase) =>
            {
                if (phase.Kind == LabPhase.Memory) phase.Gbps = offset == 50 && nth == 1 ? 1515 : 1500;
                return phase;
            },
        };
        Assert.Equal(0, rig.Run());
        var status = Status();
        Assert.Contains(status.Steps, s => s.OffsetMhz == 200);
        Assert.Equal(1515, status.Steps.Single(s => s.OffsetMhz == 50).Gbps);
        Assert.Contains(status.Comparisons!, c => c.Stage == "slowdown recheck" && c.Outcome == MemoryComparison.Equivalent);
        Assert.Equal(0, status.BestMhz);
        Assert.Equal(1, rig.Restores);
    }

    [Fact]
    public void Missing_measured_clock_never_marks_the_offset_stable_or_names_a_best()
    {
        var rig = new Rig(_dir)
        {
            Alter = (offset, _, _, phase) => { if (offset == 50) phase.MemMhzP50 = null; return phase; },
        };
        Assert.Equal(1, rig.Run());
        Assert.DoesNotContain(rig.Stable, s => s.Offset == 50);
        Assert.Null(Status().BestMhz);
        Assert.False(Status().Confirmed);
        Assert.Equal(1, rig.Restores);
    }

    [Fact]
    public void A_real_gain_is_compared_then_independently_confirmed()
    {
        var rig = new Rig(_dir)
        {
            Alter = (offset, _, _, phase) => { if (phase.Kind == LabPhase.Memory) phase.Gbps = 1500 + offset * 0.3; return phase; },
        };
        Assert.Equal(0, rig.Run());
        var status = Status();
        Assert.Equal(100, status.BestMhz);
        Assert.True(status.Confirmed);
        Assert.Equal(new[] { "candidate", "confirm" }, status.Comparisons!.Select(c => c.Stage));
        Assert.All(status.Comparisons!, c => Assert.Equal(MemoryComparison.Better, c.Outcome));
        Assert.Equal(1, rig.Restores);
    }

    [Fact]
    public void A_scan_gain_that_disappears_in_paired_runs_is_not_recommended()
    {
        var rig = new Rig(_dir)
        {
            Alter = (offset, nth, _, phase) => { if (phase.Kind == LabPhase.Memory) phase.Gbps = nth == 1 ? 1500 + offset * 0.3 : 1500; return phase; },
        };
        Assert.Equal(0, rig.Run());
        Assert.Equal(0, Status().BestMhz);
        Assert.All(Status().Comparisons!, c => Assert.Equal(MemoryComparison.Equivalent, c.Outcome));
    }

    [Fact]
    public void A_candidate_that_fails_its_final_confirmation_is_recorded_against_that_trial_and_names_no_best()
    {
        var rig = new Rig(_dir)
        {
            Alter = (offset, nth, _, phase) =>
            {
                if (phase.Kind == LabPhase.Memory)
                {
                    phase.Gbps = 1500 + offset * 0.3;
                    if (offset == 100 && nth == 4) phase.MemoryErrors = 1;
                }
                return phase;
            },
        };
        Assert.Equal(1, rig.Run());
        var crash = Assert.Single(rig.Crashes);
        Assert.Equal(100, crash.Offset);
        Assert.NotNull(crash.Trial);
        Assert.Null(Status().BestMhz);
        Assert.False(Status().Confirmed);
        Assert.Equal(1, rig.Restores);
    }

    [Fact]
    public void Disturbed_confirmation_is_retried_once_then_left_unjudged()
    {
        var rig = new Rig(_dir)
        {
            Alter = (offset, nth, _, phase) =>
            {
                if (phase.Kind == LabPhase.Memory) phase.Gbps = offset == 0 && nth >= 4 && nth % 2 == 0 ? 1430 : 1500 + offset * 0.3;
                return phase;
            },
        };
        Assert.Equal(1, rig.Run());
        Assert.Equal(2, Status().Comparisons!.Count);
        Assert.All(Status().Comparisons!, c => Assert.True(c.Disturbed));
        Assert.Null(Status().BestMhz);
    }

    [Fact]
    public void Recovery_that_does_not_verify_cannot_leave_a_confirmed_recommendation()
    {
        var rig = new Rig(_dir) { FailRestore = true };
        Assert.Equal(1, rig.Run());
        Assert.Equal(SweepStatus.Failed, Status().State);
        Assert.Null(Status().BestMhz);
        Assert.False(Status().Confirmed);
        Assert.Contains("Recovery is incomplete", Status().Next);
    }

    [Fact]
    public void A_changed_baseline_cannot_seed_the_search_or_overwrite_someone_elses_trial()
    {
        var rig = new Rig(_dir) { ChangeBaseline = true };
        Assert.Equal(1, rig.Run());
        Assert.Empty(Status().Steps);
        Assert.Null(Status().BestMhz);
        Assert.Equal(0, rig.Generation);
        Assert.Equal(0, rig.Restores);
    }

    [Fact]
    public void A_missing_warmup_clock_stops_before_any_offset_is_written()
    {
        var rig = new Rig(_dir)
        {
            Alter = (_, _, phases, phase) => { if (phases == "memory") phase.MemMhzP50 = null; return phase; },
        };
        Assert.Equal(1, rig.Run());
        Assert.Empty(Status().Steps);
        Assert.Null(Status().BestMhz);
        Assert.Equal(0, rig.Generation);
        Assert.Equal(0, rig.Restores);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stop_or_another_trial_during_confirmation_names_nothing_and_restores(bool stopped)
    {
        var rig = new Rig(_dir) { StopInComparison = stopped, ChangeTrialInComparison = !stopped };
        rig.Run();
        var status = Status();
        Assert.Equal(stopped ? SweepStatus.Stopped : SweepStatus.Failed, status.State);
        Assert.Null(status.BestMhz);
        Assert.False(status.Confirmed);
        Assert.Equal(1, rig.Restores);
    }
}
