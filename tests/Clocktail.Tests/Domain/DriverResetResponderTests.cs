using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

public class DriverResetResponderTests
{
    private DateTimeOffset _now = new(2026, 10, 18, 13, 37, 0, TimeSpan.Zero);
    private readonly List<string> _log = [];
    private readonly List<BrokerRequest> _asked = [];

    private readonly List<BrokerRequest> _profile = [];

    private DriverResetResponder Responder(Func<int, BrokerResponse> answer) => new(
        req =>
        {
            if (req.Op == BrokerRequest.OpProfile) { _profile.Add(req); return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true, profile = (object?)null })); }
            _asked.Add(req);
            return answer(_asked.Count);
        }, ["gpu-A"], _log.Add, () => _now, t => _now += t);

    private static BrokerResponse Recovered(bool ok, string action) => new(ok, Result: JsonSerializer.SerializeToElement(new
    {
        success = ok, items = new[] { new { Kind = "vf", Key = "core", action } },
    }));

    [Fact]
    public void A_reset_waits_out_a_stability_run_then_gives_back_and_records_it_once()
    {
        var r = Responder(n => n switch
        {
            1 => new BrokerResponse(false, "a stability run is in progress", Code: BrokerCode.JudgeBusy),
            2 => Recovered(false, "Failed"),
            _ => Recovered(true, "Written"),
        });
        Assert.True(r.Respond("nvlddmkm 153"));
        Assert.Equal(3, _asked.Count);
        Assert.All(_asked, a => Assert.Equal(BrokerRequest.OpRecover, a.Op));
        Assert.Equal(BrokerRequest.RecoverAfterDriverReset, _asked[0].Kind);
        Assert.Equal(BrokerRequest.RecoverAfterDriverReset, _asked[1].Kind);   // the first never reached recovery
        Assert.Null(_asked[2].Kind);                                           // the second did: recorded once
        Assert.Contains("vf/core Written", _log[^4]);
        // The crash on record and the limit at once, the profile once idle.
        Assert.Equal(["crashed", "apply-power", "after-reset"], _profile.Select(p => p.Kind));
        Assert.Contains("none kept", _log[^1]);
    }

    [Fact]
    public void A_reset_that_never_gets_the_window_gives_up_and_says_so()
    {
        var r = Responder(_ => new BrokerResponse(false, "held", Code: BrokerCode.WindowBusy));
        Assert.False(r.Respond("Display 4101"));
        Assert.Contains("not given back after 15 min", _log[^1]);
        Assert.InRange(_asked.Count, 150, 181);
    }

    [Fact]
    public void A_refusal_that_waiting_cannot_fix_is_not_retried()
    {
        var r = Responder(_ => new BrokerResponse(false, "not elevated", Code: BrokerCode.NeedsAdmin));
        Assert.False(r.Respond("nvlddmkm 153"));
        Assert.Single(_asked);
        Assert.Empty(_profile);   // nothing given back, so no profile goes on top of an unknown state
    }

    [Fact]
    public void The_profile_goes_back_on_only_after_the_GPU_has_been_idle_three_looks_in_a_row()
    {
        var busy = new Queue<bool>([false, true, false, true, true, true]);
        bool atStart = false;
        var r = new DriverResetResponder(req =>
        {
            if (req.Op == BrokerRequest.OpProfile)
            {
                _profile.Add(req);
                if (req.Kind == "after-reset") Assert.Empty(busy); else if (!atStart) Assert.Equal(6, busy.Count);   // the record and the wall do not wait
                return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true }));
            }
            return Recovered(true, "Written");
        }, ["gpu-A"], _log.Add, () => _now, t => _now += t, _ => busy.Count == 0 || busy.Dequeue());
        Assert.True(r.Respond("nvlddmkm 153", new Dictionary<string, uint> { ["gpu-A"] = 950_000 }));
        Assert.Equal(["crashed", "apply-power", "after-reset"], _profile.Select(p => p.Kind));
        Assert.Equal(950_000u, _profile[0].Uv);   // the flight recorder's crash point goes with the record
        _profile.Clear();
        atStart = true;
        r.ApplyAtStart(["gpu-A"]);   // idle now: the queue is spent, so a look would throw
        Assert.Equal(["crashed", "after-reset"], _profile.Select(p => p.Kind));
    }

    [Fact]
    public void An_answer_that_throws_is_logged_and_the_next_reset_is_still_answered()
    {
        int calls = 0;
        var r = Responder(_ => ++calls == 1 ? throw new IOException("the journal is locked") : Recovered(true, "Written"));
        Assert.False(r.Respond("nvlddmkm 153"));
        Assert.Contains("the answer failed", _log[^1]);
        Assert.Contains("the journal is locked", _log[^1]);
        Assert.True(r.Respond("nvlddmkm 153"));
    }

    [Fact]
    public void A_profile_at_start_that_throws_is_logged_not_lost()
    {
        var r = new DriverResetResponder(_ => throw new UnauthorizedAccessException("profile.json is read-only"), ["gpu-A"], _log.Add, () => _now, t => _now += t);
        r.ApplyAtStart([]);
        Assert.Contains("daily profile at start: failed", _log[^1]);
    }

    [Fact]
    public void Waking_up_asks_for_the_profile_once_the_GPU_is_idle()
    {
        var busy = new Queue<bool>([false, true, true, true]);
        var r = new DriverResetResponder(req => { _profile.Add(req); return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true, skipped = "a trial is in force: the agent running it decides" })); },
            ["gpu-A"], _log.Add, () => _now, t => _now += t, _ => busy.Count == 0 || busy.Dequeue());
        r.AfterSleep("Kernel-Power 107");
        Assert.Equal("resume", _profile.Single().Kind);
        Assert.Equal("Kernel-Power 107", _profile.Single().Value);
        Assert.Empty(busy);   // it waited for three idle looks first
        Assert.Contains("after waking up: not applied: a trial is in force", _log[^1]);
    }
}
