using Clocktail.Domain.Broker;
using Microsoft.Extensions.Hosting;

namespace Clocktail.Mcp;

/// <summary>
/// Tells the service this agent is still connected, once a minute, for its trial lease (<see cref="TrialLease"/>): when it
/// stops (the server killed with the agent), the service hands back a fixed fan duty and lowered current limits after 5
/// minutes. A service that does not answer, or is older and does not know "alive", is no reason to stop.
/// </summary>
public sealed class Heartbeat(Card card) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TrialLease.Every);
        try
        {
            do
            {
                try { card.Broker(BrokerRequest.OpExperiment, "alive"); } catch (Exception) { }
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}
