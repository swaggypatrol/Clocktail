using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Tests.Domain;

/// <summary>The service's fan guard: two looks at 85 °C or more, or 30 s without a temperature, ask for the fans back;
/// then not again for 30 s.</summary>
public class FanGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly List<BrokerRequest> _asked = [];
    private readonly List<string> _log = [];

    private FanGuard Guard(bool held = true) => new(req =>
    {
        _asked.Add(req);
        return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true, released = held ? new object[] { new { kind = "fan" } } : [] }));
    }, _log.Add);

    [Fact]
    public void Two_hot_looks_in_a_row_ask_for_the_fans_back_once()
    {
        var g = Guard();
        Assert.False(g.Look("gpu-A", 84, T0));
        Assert.False(g.Look("gpu-A", 86, T0.AddSeconds(1)));     // one hot look is not enough
        Assert.False(g.Look("gpu-A", 84, T0.AddSeconds(2)));
        Assert.False(g.Look("gpu-A", 86, T0.AddSeconds(3)));
        Assert.True(g.Look("gpu-A", 87, T0.AddSeconds(4)));
        var a = Assert.Single(_asked);
        Assert.Equal(("fans-back", BrokerRequest.OpExperiment, "gpu-A"), (a.Kind, a.Op, a.GpuId));
        Assert.Contains("87 °C", a.Value);
        Assert.Contains("fans handed back", Assert.Single(_log));
        Assert.False(g.Look("gpu-A", 88, T0.AddSeconds(10)));    // not again within 30 s
        Assert.True(g.Look("gpu-A", 88, T0.AddSeconds(35)));
    }

    [Fact]
    public void A_temperature_unread_for_30_seconds_asks_too_and_nothing_held_logs_nothing()
    {
        var g = Guard(held: false);
        Assert.False(g.Look("gpu-A", null, T0));
        Assert.False(g.Look("gpu-A", null, T0.AddSeconds(29)));
        Assert.True(g.Look("gpu-A", null, T0.AddSeconds(30)));
        Assert.Contains("no temperature", _asked[0].Value);
        Assert.Empty(_log);                                       // no fan duty held: nothing to say
    }
}
