using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>The real uncore job, with measurements and the broker transport replaced, including ambiguous delivery.</summary>
public class UncoreWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private static readonly UncoreSetting Start = new(0, 0, 58976, 0);

    public UncoreWorkflowTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Token(Action? release = null) : IDisposable
    {
        private bool _released;
        public void Dispose() { if (!_released) { _released = true; release?.Invoke(); } }
    }

    private sealed class Rig(string dir) : UncoreTune(new("fake", "fake", "0", Start,
        Candidates: [Start with { XbarMhz = 50 }, Start with { XbarMhz = 100 }]), dir, "fake")
    {
        public string? CrashReply, Preflight, RestoreReply;
        public int FaultRun, ResetRun, FaultOffset = 50;
        public bool ResetRecorded = true, MissingTrialId;
        public int Generation, CrashAttempts, Crashes, WritesAfterFault, Restores, Measurements;
        public string? FaultTrial, RecordedTrial, RecoveryStamp;
        public bool LeaseHeld;
        private int _offset, _candidateRuns, _comparisonRuns;
        private string? _id, _stamp;
        private bool _faulted, _reset;

        protected override IDisposable? ClaimCard() => new Token();
        protected override IDisposable? Hold(Action<string>? log)
        {
            Assert.False(LeaseHeld);
            LeaseHeld = true;
            return new Token(() => LeaseHeld = false);
        }
        protected override Timer Heartbeat() => new(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        protected override void Settle() { }
        protected override int? CountResets(DateTime since) => _reset ? 1 : 0;
        protected override bool CrashRecorded(string? stamp)
        {
            Assert.False(LeaseHeld); // the service cannot recover until this job releases its lease
            RecoveryStamp = stamp;
            if (ResetRecorded) { _id = null; _stamp = null; _offset = 0; }
            return ResetRecorded;
        }

        protected override BrokerResponse? Send(BrokerRequest request)
        {
            switch (request.Kind)
            {
                case "show":
                    if (Generation == 0)
                    {
                        if (Preflight == "missing") return null;
                        if (Preflight == "rejected") return new(false, "no GPU");
                        if (Preflight == "closed") return new(true, Result: JsonSerializer.SerializeToElement(new { experiment = (object?)null }));
                        if (Preflight == "trial") return new(true, Result: JsonSerializer.SerializeToElement(new { experiment = new { Trial = new { At = "old", Id = "other" } } }));
                    }
                    object? trial = _id == null ? null : new { At = _stamp, Id = MissingTrialId ? null : _id };
                    return new(true, Result: JsonSerializer.SerializeToElement(new { experiment = new { Trial = trial } }));
                case "try":
                    Assert.True(LeaseHeld);
                    if (_faulted) WritesAfterFault++;
                    var args = JsonSerializer.Deserialize<JsonElement>(request.Value!);
                    _offset = args.GetProperty("xbar_khz").GetInt32() / 1000;
                    _id = $"trial-{++Generation}";
                    _stamp = $"stamp-{Generation}";
                    return new(true);
                case "crash":
                    CrashAttempts++;
                    Assert.Equal(_id, request.Key);
                    RecordedTrial = request.Key;
                    switch (CrashReply)
                    {
                        case "missing": return null;
                        case "timeout": throw new TimeoutException("request lost before delivery");
                        case "io": throw new IOException("pipe broke before delivery");
                        case "refused": return new(false, "failure record rejected");
                    }
                    Crashes++;
                    _id = null; _stamp = null; _offset = 0;
                    if (CrashReply == "recorded-no-reply") return null;
                    if (CrashReply == "recorded-timeout") throw new TimeoutException("record committed; reply lost");
                    return new(true);
                case "finish-job":
                    Assert.False(LeaseHeld);
                    Restores++;
                    if (RestoreReply == "missing") return null;
                    if (RestoreReply == "rejected") return new(false, "simulated readback mismatch");
                    if (RestoreReply == "partial") return new(false, "one group did not apply", JsonSerializer.SerializeToElement(new { partial = true }));
                    if (RestoreReply == "skipped") return new(true, Result: JsonSerializer.SerializeToElement(new { skipped = "another trial is in force" }));
                    _id = null; _stamp = null; _offset = 0;
                    return new(true);
                default: return new(true);
            }
        }

        protected override LabResult Measure(int seconds, string phases, ulong? capMib)
        {
            Measurements++;
            _reset = Generation > 0 && ++_comparisonRuns == ResetRun;
            bool fault = _offset == FaultOffset && ++_candidateRuns == FaultRun || _reset;
            if (fault) { _faulted = true; FaultTrial = _id; }
            var result = phases.Split(',').Select(p => new PhaseResult
            {
                Kind = Enum.Parse<LabPhase>(p, true), Seconds = seconds, Completed = true,
                Gbps = p == "l2" ? 4780 + _offset : 1570, SmMhzP50 = 2970, TempMax = 60,
                MemoryErrors = fault ? 1 : 0,
            }).ToList();
            return new("fake", "fake", 1024, 100, DateTime.UtcNow, DateTime.UtcNow, result,
                fault ? LabResult.Fail : LabResult.Pass, fault ? ["simulated bad bit"] : [], 0, 512, 0);
        }
    }

    private UncoreStatus Status() => JsonSerializer.Deserialize<UncoreStatus>(File.ReadAllText(Path.Combine(_dir, "status.json")), LabJson.Options)!;

    [Theory]
    [InlineData("missing", RecordingResult.Unknown, false, 1)]
    [InlineData("timeout", RecordingResult.Unknown, false, 1)]
    [InlineData("io", RecordingResult.Unknown, false, 1)]
    [InlineData("recorded-no-reply", RecordingResult.Unknown, true, 1)]
    [InlineData("recorded-timeout", RecordingResult.Unknown, true, 1)]
    [InlineData("refused", RecordingResult.Rejected, false, 1)]
    [InlineData("missing", RecordingResult.Unknown, false, 2)]
    [InlineData("timeout", RecordingResult.Unknown, false, 2)]
    [InlineData("io", RecordingResult.Unknown, false, 2)]
    [InlineData("recorded-no-reply", RecordingResult.Unknown, true, 2)]
    [InlineData("recorded-timeout", RecordingResult.Unknown, true, 2)]
    [InlineData("refused", RecordingResult.Rejected, false, 2)]
    public void An_unresolved_failure_stops_before_another_trial(string reply, string state, bool committed, int run)
    {
        var rig = new Rig(_dir) { CrashReply = reply, FaultRun = run };
        Assert.Equal(1, rig.Run());
        var status = Status();
        Assert.Equal(SweepStatus.Failed, status.State);
        Assert.Equal(Start, status.Best);
        Assert.Equal(Comparison.Unjudged, Assert.Single(status.Comparisons).Outcome);
        Assert.Equal(state, status.FailureRecording!.State);
        Assert.Equal(rig.FaultTrial, status.FailureRecording.TrialId);
        Assert.Equal(rig.FaultTrial, rig.RecordedTrial);
        Assert.Equal(1, rig.CrashAttempts);
        Assert.Equal(committed ? 1 : 0, rig.Crashes);
        Assert.Equal(0, rig.WritesAfterFault);
        Assert.Equal(1, rig.Restores);
        Assert.Contains("No recommendation", status.Next);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Unconfirmed_reset_recovery_stops_at_each_position_in_A_B_B_A(int run)
    {
        var rig = new Rig(_dir) { ResetRun = run, ResetRecorded = false };
        Assert.Equal(1, rig.Run());
        var status = Status();
        Assert.Equal(Start, status.Best);
        Assert.Equal(Comparison.Unjudged, Assert.Single(status.Comparisons).Outcome);
        Assert.Equal(RecordingResult.Unknown, status.FailureRecording!.State);
        Assert.Equal(rig.FaultTrial, status.FailureRecording.TrialId);
        Assert.NotNull(rig.RecoveryStamp);
        Assert.Equal(0, rig.CrashAttempts);
        Assert.Equal(0, rig.WritesAfterFault);
        Assert.Equal(1, rig.Restores);
        Assert.Contains("No recommendation", status.Next);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_confirmed_candidate_reset_allows_other_candidates(int run)
    {
        var rig = new Rig(_dir) { ResetRun = run };
        Assert.Equal(0, rig.Run());
        var status = Status();
        Assert.Equal(Start with { XbarMhz = 100 }, status.Best);
        Assert.Equal(RecordingResult.Confirmed, status.FailureRecording!.State);
        Assert.Equal(rig.FaultTrial, status.FailureRecording.TrialId);
        Assert.Contains(status.Comparisons, c => c.Stage == "confirm" && c.Outcome == Comparison.Better);
        Assert.True(rig.WritesAfterFault > 0);
        Assert.Equal(0, rig.CrashAttempts);
        Assert.Equal(1, rig.Restores);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void An_incumbent_reset_ends_the_search_even_when_recovered(int run)
    {
        var rig = new Rig(_dir) { ResetRun = run };
        Assert.Equal(1, rig.Run());
        var status = Status();
        Assert.Equal(Start, status.Best);
        Assert.Equal(RecordingResult.Confirmed, status.FailureRecording!.State);
        Assert.Equal(rig.FaultTrial, status.FailureRecording.TrialId);
        Assert.Equal(0, rig.WritesAfterFault);
        Assert.Equal(1, rig.Restores);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void An_acknowledged_live_fault_can_be_followed_by_a_confirmed_winner(int run)
    {
        var rig = new Rig(_dir) { FaultRun = run };
        Assert.Equal(0, rig.Run());
        var status = Status();
        Assert.Equal(Start with { XbarMhz = 100 }, status.Best);
        Assert.Equal(RecordingResult.Confirmed, status.FailureRecording!.State);
        Assert.Contains(status.Comparisons, c => c.Stage == "confirm" && c.Outcome == Comparison.Better);
        Assert.True(rig.WritesAfterFault > 0);
        Assert.Equal(1, rig.Crashes);
        Assert.Equal(1, rig.Restores);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("rejected")]
    [InlineData("partial")]
    [InlineData("skipped")]
    public void A_winner_is_not_recommended_when_cleanup_does_not_restore_daily(string reply)
    {
        var rig = new Rig(_dir) { RestoreReply = reply };
        Assert.Equal(1, rig.Run());
        var status = Status();
        Assert.Equal(SweepStatus.Failed, status.State);
        Assert.Equal(Start, status.Best);
        Assert.Contains(status.Comparisons, c => c.Stage == "confirm" && c.Outcome == Comparison.Better);
        Assert.Contains("Recovery is incomplete", status.Next);
        Assert.Equal(1, rig.Restores);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("recorded-no-reply")]
    [InlineData("refused")]
    public void An_unresolved_failure_also_suppresses_an_earlier_winner(string reply)
    {
        var rig = new Rig(_dir) { FaultOffset = 100, FaultRun = 1, CrashReply = reply };
        Assert.Equal(1, rig.Run());
        var status = Status();
        Assert.Equal(Start, status.Best);
        Assert.Equal(Comparison.Better, status.Comparisons[0].Outcome);
        Assert.Equal(Comparison.Unjudged, status.Comparisons[1].Outcome);
        Assert.DoesNotContain(status.Comparisons, c => c.Stage == "confirm");
        Assert.Equal(rig.FaultTrial, status.FailureRecording!.TrialId);
        Assert.Equal(0, rig.WritesAfterFault);
        Assert.Equal(1, rig.Restores);
    }

    [Fact]
    public void An_unidentified_incumbent_cannot_confirm_reset_recovery()
    {
        var rig = new Rig(_dir) { ResetRun = 1, MissingTrialId = true };
        Assert.Equal(1, rig.Run());
        var status = Status();
        Assert.Equal(RecordingResult.Unknown, status.FailureRecording!.State);
        Assert.Contains("could not be identified", status.FailureRecording.Detail);
        Assert.Null(rig.RecoveryStamp);
        Assert.Equal(0, rig.WritesAfterFault);
        Assert.Equal(1, rig.Restores);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("rejected")]
    [InlineData("closed")]
    [InlineData("trial")]
    public void The_job_rechecks_its_session_before_warming_or_writing(string reply)
    {
        var rig = new Rig(_dir) { Preflight = reply };
        Assert.Equal(1, rig.Run());
        Assert.Equal(SweepStatus.Failed, Status().State);
        Assert.Equal(0, rig.Generation);
        Assert.Equal(0, rig.Measurements);
        Assert.Equal(0, rig.Restores);
    }
}
