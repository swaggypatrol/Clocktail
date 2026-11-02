using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

public class StartupRecoveryTests
{
    /// <summary>A game recovery record whose recovery verifies and then fails (the daily profile does not all go on, as on
    /// 621.01 with MSVDD offsets kept) is asked again every pass, and the start inside it runs once. Before, the start ran
    /// again every 5 s: a crash found at start recorded again each time ("Crash · daily profile") and the profile put on
    /// again, with judge.lock held through each idle wait.</summary>
    [Fact]
    public void The_start_runs_once_while_a_game_recovery_keeps_failing()
    {
        int starts = 0, recoveries = 0;
        var s = new StartupRecovery(() => starts++);
        for (int pass = 0; pass < 5; pass++)
            s.Pass(gameRecord: true, startup => { recoveries++; startup(); return false; });   // verified, then not restored
        Assert.Equal((1, 5), (starts, recoveries));
        Assert.True(s.Started);

        s.Pass(gameRecord: false, _ => throw new InvalidOperationException("no record, no recovery"));
        Assert.Equal(1, starts);
    }

    /// <summary>A recovery that never verifies (the game still running) runs nothing; once there is no record the start runs,
    /// once.</summary>
    [Fact]
    public void A_recovery_that_does_not_verify_leaves_the_start_for_later()
    {
        int starts = 0;
        var s = new StartupRecovery(() => starts++);
        s.Pass(gameRecord: true, _ => false);
        Assert.Equal(0, starts);
        s.Pass(gameRecord: false, _ => false);
        s.Pass(gameRecord: false, _ => false);
        Assert.Equal(1, starts);
        s.Pass(gameRecord: true, startup => { startup(); return true; });   // a later record: the start is not run again
        Assert.Equal(1, starts);
    }
}
