namespace Clocktail.Domain.Platform;

/// <summary>
/// The names the lab's background jobs and the MCP server that starts them agree on, per card: the mutex one job holds for
/// its whole life (one job at a time on a card), and the one the server holds around a job's preflight and start (two agents
/// that both found the card free cannot both start one). Local names: the jobs and the server run in the person's session.
/// </summary>
public static class LabCard
{
    public static string JobMutex(string stableId) => $@"Local\Clocktail.Lab.{stableId}";
    public static string StartMutex(string stableId) => $@"Local\Clocktail.LabStart.{stableId}";
}
