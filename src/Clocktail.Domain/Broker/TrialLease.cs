namespace Clocktail.Domain.Broker;

/// <summary>
/// A lease on what only a watching agent may hold (a fixed fan duty, lowered current limits). An agent's MCP server says
/// "alive" every <see cref="Every"/>; a clean disconnect hands these back at once (the relay's "release"), but a server
/// killed outright (the agent's process ended, the relay killed, the PC asleep with the agent gone) says nothing. When no
/// agent has been heard for <see cref="Timeout"/>, the lease asks for "release" on every GPU: the fans go back to the curve
/// and the limits to their defaults, the rest of the trial stays. While nothing trial-only is held, release writes nothing.
/// </summary>
public sealed class TrialLease(Func<BrokerRequest, BrokerResponse> perform, IReadOnlyList<string> gpuIds, Func<DateTimeOffset> lastAlive, Action<string> log)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private DateTimeOffset _asked = DateTimeOffset.MinValue;

    /// <summary>One look; true when it asked for release (at most once per lapse, until an agent is heard again).</summary>
    public bool Look(DateTimeOffset now)
    {
        var alive = lastAlive();
        if (now - alive < Timeout || _asked > alive) return false;
        _asked = now;
        foreach (var id in gpuIds)
        {
            var r = perform(new BrokerRequest(BrokerRequest.OpExperiment, id, Kind: "release",
                Value: $"no agent has been heard from for {(now - alive).TotalMinutes:0} min"));
            bool released = r.Result is { } res && res.TryGetProperty("released", out var rel) && rel.ValueKind == System.Text.Json.JsonValueKind.Array && rel.GetArrayLength() > 0;
            if (released || !r.Ok) log($"gpu {id}: trial lease lapsed: {(r.Ok ? "fans and current limits handed back" : $"could not hand them back: {r.Error}")}");
        }
        return true;
    }
}
