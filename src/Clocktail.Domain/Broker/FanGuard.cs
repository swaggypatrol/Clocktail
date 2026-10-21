namespace Clocktail.Domain.Broker;

/// <summary>
/// The service's own watch over a fixed fan duty, a trial-only setting the agent is told to watch (set_fan_speed allows
/// 30 %; on the reference card 45 % at the 600 W wall passed 85 °C within 40 s). An agent that is connected but not
/// looking (a stress run blocking, a game being played) cannot. Once a second it looks at each GPU's temperature; at
/// <see cref="HotC"/> on two looks in a row, or with no temperature for <see cref="UnreadFor"/>, it asks for "fans-back":
/// the fans go back to the curve, nothing else changes, and the trial is recorded as such. While no fan duty is held the
/// ask writes nothing.
/// </summary>
public sealed class FanGuard(Func<BrokerRequest, BrokerResponse> perform, Action<string> log)
{
    public const double HotC = 85;
    public static readonly TimeSpan UnreadFor = TimeSpan.FromSeconds(30);
    /// <summary>After an ask, the next one waits this long while the reason stands.</summary>
    public static readonly TimeSpan Again = TimeSpan.FromSeconds(30);

    private sealed class State(DateTimeOffset now)
    {
        public int HotLooks;
        public DateTimeOffset LastRead = now, LastAsk = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<string, State> _gpus = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One look at a GPU: its temperature now, null when it could not be read. True when the fans were asked back.</summary>
    public bool Look(string gpuId, double? tempC, DateTimeOffset now)
    {
        if (!_gpus.TryGetValue(gpuId, out var s)) _gpus[gpuId] = s = new State(now);
        if (tempC is { } t) { s.LastRead = now; s.HotLooks = t >= HotC ? s.HotLooks + 1 : 0; }
        string? why = s.HotLooks >= 2 ? $"{tempC:0} °C (at {HotC:0} °C or more)"
            : now - s.LastRead >= UnreadFor ? $"no temperature read for {(now - s.LastRead).TotalSeconds:0} s" : null;
        if (why == null || now - s.LastAsk < Again) return false;
        s.LastAsk = now;
        var r = perform(new BrokerRequest(BrokerRequest.OpExperiment, gpuId, Kind: "fans-back", Value: $"the fan guard: {why}"));
        bool released = r.Result is { } res && res.TryGetProperty("released", out var rel) && rel.ValueKind == System.Text.Json.JsonValueKind.Array && rel.GetArrayLength() > 0;
        if (released || !r.Ok) log($"gpu {gpuId}: fan guard: {why}: {(r.Ok ? "fans handed back to the curve" : $"could not hand the fans back: {r.Error}")}");
        return true;
    }
}
