using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

/// <summary>S23: when no agent has said "alive" for 5 minutes, the trial-only settings go back on every GPU, once per lapse.</summary>
public class TrialLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_lapsed_lease_releases_once_until_an_agent_is_heard_again()
    {
        var alive = T0;
        var asked = new List<BrokerRequest>();
        var log = new List<string>();
        var lease = new TrialLease(r => { asked.Add(r); return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true, released = new[] { 1 } })); },
            ["gpu-A", "gpu-B"], () => alive, log.Add);

        Assert.False(lease.Look(T0.AddMinutes(4)));
        Assert.True(lease.Look(T0.AddMinutes(5)));
        Assert.Equal(["gpu-A", "gpu-B"], asked.Select(a => a.GpuId));
        Assert.All(asked, a => Assert.Equal("release", a.Kind));
        Assert.Contains("5 min", asked[0].Value);
        Assert.Equal(2, log.Count);
        Assert.False(lease.Look(T0.AddMinutes(9)));      // asked already for this lapse

        alive = T0.AddMinutes(10);                       // an agent came back
        Assert.False(lease.Look(T0.AddMinutes(12)));
        Assert.True(lease.Look(T0.AddMinutes(16)));      // and went away again
    }
}
