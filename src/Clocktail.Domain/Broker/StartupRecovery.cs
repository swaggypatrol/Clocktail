namespace Clocktail.Domain.Broker;

/// <summary>
/// The broker's start, one pass every few seconds: the daily profile at start (<see cref="DriverResetResponder.ApplyAtStart"/>,
/// which records a crash from before first) runs once, and an interrupted game test's recovery record is recovered while
/// it is there. While a record is there the start runs inside the first recovery attempt that verifies, before that
/// recovery puts the daily profile back itself, so the crash records come first. A recovery that verifies and then
/// fails is asked again on the next pass, the start is not: until 0.8.1 it ran again every pass, and each run recorded
/// the crash found at start again ("Crash · daily profile") and put the profile on again.
/// </summary>
public sealed class StartupRecovery(Action applyAtStart)
{
    /// <summary>The start has run.</summary>
    public bool Started { get; private set; }

    /// <param name="gameRecord">Whether the game recovery record (or its backup) is there.</param>
    /// <param name="recoverGame">One recovery attempt, given what to run once the record verifies; it does not decide
    /// whether the start has run.</param>
    public void Pass(bool gameRecord, Func<Action, bool> recoverGame)
    {
        if (gameRecord) recoverGame(Started ? () => { } : Start);
        else if (!Started) Start();
    }

    private void Start()
    {
        applyAtStart();
        Started = true;
    }
}
