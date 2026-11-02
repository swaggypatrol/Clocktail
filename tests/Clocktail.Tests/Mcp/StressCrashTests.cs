using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>stress_test after a run that saw a driver reset: the service records the crash (with the flight recorder's
/// voltage), so the tool waits for that rather than recording it a second time, and records it itself only when the
/// service has not.</summary>
public class StressCrashTests
{
    private static BrokerAnswer Show(object? trial) =>
        new(true, JsonSerializer.SerializeToElement(new { success = true, experiment = new { Game = "drill", Trial = trial } }), null);

    [Fact]
    public void A_trial_in_force_is_read_from_the_experiment_answer()
    {
        Assert.True(TuningTools.TrialInForce(Show(new { Change = "+15" })));
        Assert.False(TuningTools.TrialInForce(Show(null)));
        Assert.False(TuningTools.TrialInForce(new BrokerAnswer(true, JsonSerializer.SerializeToElement(new { success = true, experiment = (object?)null }), null)));
        Assert.Null(TuningTools.TrialInForce(new BrokerAnswer(false, null, "the Clocktail service does not answer")));
    }

    [Fact]
    public void The_wait_ends_as_soon_as_the_service_has_closed_the_trial()
    {
        var answers = new Queue<bool?>([true, null, true, false]);
        var slept = TimeSpan.Zero;
        Assert.True(TuningTools.TrialClosedWithin(() => answers.Dequeue(), TimeSpan.FromSeconds(30), t => slept += t));
        Assert.Empty(answers);
        Assert.Equal(TimeSpan.FromSeconds(3), slept);
    }

    [Fact]
    public void A_service_that_has_not_recorded_it_in_time_leaves_it_to_the_tool()
    {
        int asked = 0;
        var slept = TimeSpan.Zero;
        Assert.False(TuningTools.TrialClosedWithin(() => { asked++; return true; }, TimeSpan.FromSeconds(30), t => slept += t));
        Assert.Equal(31, asked);
        Assert.Equal(TimeSpan.FromSeconds(30), slept);
        Assert.False(TuningTools.TrialClosedWithin(() => null, TimeSpan.FromSeconds(2), _ => { }));   // unreadable is not closed
    }

    private static object Trial(string curve, uint? fanPct = null, string at = "10:00", string change = "+15", params object[] writes) =>
        new { At = at, Change = change, Curve = curve, Marked = false, PowerMw = 450_000u, FanPct = fanPct, Writes = writes.Length == 0 ? null : writes };

    /// <summary>The trial a run measured is known by its settings: the service handing the fans back mid-run (the agent gone
    /// quiet) writes the trial again (a new "At", a longer change, no fixed duty, no trial-only writes) with the same settings, and a crash
    /// still goes to it; other settings are another trial.</summary>
    [Fact]
    public void The_trial_a_run_measured_is_known_by_its_settings_not_by_when_it_was_written()
    {
        var boostLock = new { Kind = "clocklimit", Key = "voltage", Value = "1500000" };
        var boost = new { Kind = "boost", Key = "legacy", Value = "100" };
        string? measured = TuningTools.TrialSettings(Show(Trial("0*127", fanPct: 30, writes: [boostLock, boost])));
        string? handedBack = TuningTools.TrialSettings(Show(Trial("0*127", at: "10:05", change: "+15 (fans handed back to the curve: 87 °C)", writes: [boost])));
        Assert.NotNull(measured);
        Assert.Equal(measured, handedBack);
        Assert.NotEqual(measured, TuningTools.TrialSettings(Show(Trial("15000*127", fanPct: 30, writes: [boostLock, boost]))));   // another curve
        Assert.NotEqual(measured, TuningTools.TrialSettings(Show(Trial("0*127", fanPct: 30, writes: [boostLock]))));            // no boost
        Assert.Null(TuningTools.TrialSettings(Show(null)));
        Assert.Null(TuningTools.TrialSettings(new BrokerAnswer(false, null, "the Clocktail service does not answer")));
    }

    private static object WithId(string curve, string? id, uint? fanPct = null) =>
        new { At = "10:00", Change = "+15", Curve = curve, Marked = false, PowerMw = 450_000u, FanPct = fanPct, Id = id };

    /// <summary>Settings cannot tell a trial apart from the next one that changes only what they leave out (set_fan_speed
    /// starts a trial with the same curve and a fixed duty): the identity can.</summary>
    [Fact]
    public void A_trial_is_known_by_its_identity_where_the_settings_cannot_tell()
    {
        var measured = Show(WithId("0*127", "t1"));
        var next = Show(WithId("0*127", "t2", fanPct: 80));
        Assert.Equal(TuningTools.TrialSettings(measured), TuningTools.TrialSettings(next));
        Assert.Equal("t1", TuningTools.TrialId(measured));
        Assert.Equal("t2", TuningTools.TrialId(next));
        Assert.Null(TuningTools.TrialId(Show(WithId("0*127", null))));   // a service that gives none
        Assert.Null(TuningTools.TrialId(Show(null)));
        Assert.Null(TuningTools.TrialId(new BrokerAnswer(false, null, "the Clocktail service does not answer")));
    }

    private static BrokerAnswer Point() => new(true, JsonSerializer.SerializeToElement(new { success = true, point = new { Outcome = "crash" } }), null);

    /// <summary>A failed run with the measured trial's identity records its crash in one request that names it, with no
    /// look first: the service checks the identity as it records. Without one it looks, and blames only a trial with the
    /// measured settings and no identity of its own (a trial written since, by a service that gives them, is another).</summary>
    [Fact]
    public void A_failed_run_names_the_trial_it_measured_in_the_crash_request()
    {
        var asked = new List<(string Kind, string? Key)>();
        BrokerAnswer Service(string kind, string? value, string? key, BrokerAnswer show)
        {
            asked.Add((kind, key));
            return kind == "show" ? show : Point();
        }
        string? settings = TuningTools.TrialSettings(Show(WithId("0*127", "t1")));

        Assert.True(TuningTools.RecordCrash((k, v, key) => Service(k, v, key, Show(WithId("0*127", "t2", 80))), settings, "t1", true, 0, "lab: 3 errors", _ => { }));
        Assert.Equal([("crash", (string?)"t1")], asked);

        asked.Clear();   // no identity: the look decides, and a trial with one now is not the measured one
        Assert.False(TuningTools.RecordCrash((k, v, key) => Service(k, v, key, Show(WithId("0*127", "t2", 80))), settings, null, true, 0, "lab: 3 errors", _ => { }));
        Assert.Equal([("show", (string?)null)], asked);

        asked.Clear();
        Assert.True(TuningTools.RecordCrash((k, v, key) => Service(k, v, key, Show(WithId("0*127", null))), settings, null, true, 0, "lab: 3 errors", _ => { }));
        Assert.Equal([("show", (string?)null), ("crash", null)], asked);

        asked.Clear();   // no trial when the run began: nothing to blame, nothing asked
        Assert.False(TuningTools.RecordCrash((k, v, key) => Service(k, v, key, Show(null)), null, null, true, 0, "lab: 3 errors", _ => { }));
        Assert.Empty(asked);
    }

    private static BrokerAnswer Busy() => new(false, null, "another operation has held the card for over 30 s; nothing was done, try again", BrokerCode.WindowBusy);

    /// <summary>A measurement starts once the service says nothing is in flight ("quiet"), asked again while it is busy;
    /// a service from before 0.3.7 does not know "quiet", and its "show" stands in (said, so stress_test holds no lock that
    /// service cannot honour); one that cannot be reached writes nothing, so the run goes on; one that stays busy, or stops
    /// answering, runs nothing.</summary>
    [Fact]
    public void A_run_starts_once_the_service_is_quiet_and_never_in_the_middle_of_a_write()
    {
        var asked = new List<string>();
        int sleeps = 0;
        var answers = new Queue<BrokerAnswer>([Busy(), Busy(), Show(Trial("t1"))]);
        Assert.Contains("Curve=\"t1\"", TuningTools.TrialSettings(TuningTools.Quiet(k => { asked.Add(k); return answers.Dequeue(); }, 4, _ => sleeps++, out bool older)));
        Assert.Equal(["quiet", "quiet", "quiet"], asked);
        Assert.Equal(2, sleeps);
        Assert.False(older);

        asked.Clear();
        var old = TuningTools.Quiet(k => { asked.Add(k); return k == "quiet" ? new BrokerAnswer(false, null, "experiment needs show, ...", BrokerCode.BadRequest) : Show(Trial("t2")); }, 4, _ => { }, out older);
        Assert.Contains("Curve=\"t2\"", TuningTools.TrialSettings(old));
        Assert.Equal(["quiet", "show"], asked);
        Assert.True(older);

        var none = TuningTools.Quiet(_ => new BrokerAnswer(false, null, "the Clocktail service does not answer", BrokerCode.BrokerUnreachable), 4, _ => { }, out older);
        Assert.Equal(BrokerCode.BrokerUnreachable, none.Code);
        Assert.Null(TuningTools.TrialSettings(none));
        Assert.False(older);

        Assert.Throws<InvalidOperationException>(() => TuningTools.Quiet(_ => Busy(), 4, _ => { }, out _));
        Assert.Throws<InvalidOperationException>(() => TuningTools.Quiet(_ => new BrokerAnswer(false, null, "stopped answering", "no_answer"), 4, _ => { }, out _));
    }
}
