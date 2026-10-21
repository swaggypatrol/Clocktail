using System.Text.Json;
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
}
