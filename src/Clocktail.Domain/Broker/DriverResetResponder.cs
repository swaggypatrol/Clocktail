namespace Clocktail.Domain.Broker;

/// <summary>
/// What the broker does when the display driver resets (a TDR: nvlddmkm 153 or Display 4101 in the System log).
/// A TDR is not a reboot: the driver comes back with every offset this program wrote still applied (V/F point 84
/// at +450 MHz after the +120 in-game hang, 2026-10-18), so a curve that hung a game hangs it again. Each GPU's
/// held cells are given back through the ordinary recovery op, the first attempt putting them on record in the
/// crash log. A trial in force when it crashed becomes a data point at once. Then the daily profile, if one is kept,
/// goes back on unchanged: its power limit at once, the rest once the GPU is idle, unless the agent has begun a new
/// trial by then. There is no step-down: the person is at the
/// machine when a game crashes, and the agent adjusts through the MCP server (the user, 2026-10-19). The give-back is a
/// write like any other, so it waits for the window and for a stability run to let go rather than going around either,
/// and it retries until <see cref="GiveUpAfter"/>.
/// </summary>
public sealed class DriverResetResponder(
    Func<BrokerRequest, BrokerResponse> perform,
    IReadOnlyList<string> gpuIds,
    Action<string> log,
    Func<DateTimeOffset> now,
    Action<TimeSpan> sleep,
    Func<string, bool>? idle = null,
    Func<DateTime, int?>? resetsSince = null)
{
    /// <summary>
    /// The daily profile goes back on only once the GPU has been idle for this many looks in a row: two of three hangs
    /// on 2026-10-18 came within ten seconds of a curve written under full load. Giving back to stock does not wait;
    /// it only ever lowers.
    /// </summary>
    public const int IdleLooks = 3;
    public static readonly TimeSpan IdleWaitAtMost = TimeSpan.FromHours(4);

    /// <summary>Between the reset and the first look: the driver is still re-initialising when the event is written.</summary>
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _signal = new(0);
    private sealed record Seen(string Evidence, IReadOnlyDictionary<string, uint> CrashUv);
    private Seen _last = new("", new Dictionary<string, uint>());

    /// <summary>
    /// Called once a reset is seen: a burst of events is answered once. <paramref name="crashUv"/> is each GPU's core
    /// voltage at the hang from the flight recorder, where it found one, for the record.
    /// </summary>
    public void Signal(string evidence, IReadOnlyDictionary<string, uint>? crashUv = null)
    {
        Volatile.Write(ref _last, new Seen(evidence, crashUv ?? new Dictionary<string, uint>()));
        _signal.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            sleep(SettleFor);
            while (_signal.Wait(0)) { }   // the events of one reset arrive together
            var (evidence, crashUv) = Volatile.Read(ref _last);
            Respond(evidence, crashUv);
        }
    }

    /// <summary>Gives back every GPU's held cells; true when each one ended clean. A GPU whose answer throws is logged and
    /// counted as not clean: the next GPU, and the next reset, are still answered.</summary>
    public bool Respond(string evidence, IReadOnlyDictionary<string, uint>? crashUv = null)
    {
        bool clean = true;
        foreach (var id in gpuIds)
        {
            try { clean &= RespondFor(id, evidence, crashUv?.GetValueOrDefault(id) ?? 0); }
            catch (Exception e) { log($"gpu {id}: driver reset ({evidence}): the answer failed: {e}"); clean = false; }
        }
        return clean;
    }

    private bool RespondFor(string gpuId, string evidence, uint crashUv)
    {
        var deadline = now() + GiveUpAfter;
        bool recorded = false;
        while (true)
        {
            // Only the first attempt that reaches recovery records the crash; a retry is the same reset.
            var r = perform(new BrokerRequest(BrokerRequest.OpRecover, gpuId, Kind: recorded ? null : BrokerRequest.RecoverAfterDriverReset, Value: evidence));
            recorded |= r.Result != null;
            if (r.Ok)
            {
                log($"gpu {gpuId}: driver reset ({evidence}): {Summary(r)}");
                // The crash on record at once, so a trial the agent writes while the GPU is still busy is not blamed for it;
                // the power limit at once (the reset put it back to 575 W; stock clocks under it are safe); the curve once idle.
                Record(gpuId, evidence, "after the reset", crashUv);
                var power = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: "apply-power"));
                log($"gpu {gpuId}: daily profile's power limit after the reset: {ProfileSummary(power)}");
                return Profile(gpuId, "after-reset", evidence, "after the reset");
            }
            bool retry = r.Code is BrokerCode.JudgeBusy or BrokerCode.WindowBusy or BrokerCode.Failed || r.Result != null;
            if (!retry || now() + RetryEvery > deadline)
            {
                log($"gpu {gpuId}: driver reset ({evidence}): not given back{(retry ? $" after {GiveUpAfter.TotalMinutes:0} min" : "")}: {r.Error ?? Summary(r)}");
                return false;
            }
            log($"gpu {gpuId}: driver reset ({evidence}): waiting ({r.Code ?? "give-back incomplete"}): {r.Error ?? Summary(r)}");
            sleep(RetryEvery);
        }
    }

    /// <summary>
    /// At start: each GPU's daily profile, unchanged. A GPU whose machine went down holding cells (a write window open,
    /// or an unclean shutdown) has the crash recorded first (a trial in force as a data point), then the profile once idle.
    /// </summary>
    public void ApplyAtStart(IReadOnlyCollection<string> crashed)
    {
        foreach (var id in gpuIds)
        {
            bool down = crashed.Contains(id);
            try
            {
                if (!down && UnansweredReset(id) is { } reset)
                {
                    // The worker before this one died in the hang (inside NVML), or handed over when the driver went
                    // away, before it answered the reset: answered now, as it would have been.
                    log($"gpu {id}: at start: {reset}");
                    RespondFor(id, reset, 0);
                    continue;
                }
                if (!down) { Profile(id, "apply", "the broker started", "at start"); continue; }
                const string why = "the machine went down holding the profile";
                Record(id, why, "at start");
                Profile(id, "after-reset", why, "at start");
            }
            catch (Exception e) { log($"gpu {id}: daily profile at start: failed: {e}"); }
        }
    }

    /// <summary>
    /// At start in the same boot, a trial still in force with a driver reset in the System log since it was written: that
    /// reset was never answered (an answered one closes the trial as the crash at once). A device restart of a hung GPU
    /// ends the worker within a second, before it settles to answer. Null when there is none, or the log cannot be read.
    /// </summary>
    private string? UnansweredReset(string gpuId)
    {
        if (resetsSince == null) return null;
        var show = perform(new BrokerRequest(BrokerRequest.OpExperiment, gpuId, Kind: "show"));
        if (show.Result is not { } res || !res.TryGetProperty("experiment", out var e) || e.ValueKind != System.Text.Json.JsonValueKind.Object
            || !e.TryGetProperty("Trial", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.Object
            || !t.TryGetProperty("At", out var at) || !at.TryGetDateTimeOffset(out var since))
            return null;
        return resetsSince(since.UtcDateTime) is > 0 and var n
            ? $"a driver reset while the service restarted ({n} reset event{(n == 1 ? "" : "s")} in the System log since the trial in force was written)"
            : null;
    }

    /// <summary>The crash recorded ("crashed"): the trial in force made a data point, nothing written.</summary>
    private void Record(string gpuId, string evidence, string when, uint crashUv = 0)
    {
        var c = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: "crashed", Value: evidence, Uv: crashUv));
        log($"gpu {gpuId}: crash recorded {when}: {(c.Ok ? CrashSummary(c) : c.Error ?? "failed")}");
    }

    private static string CrashSummary(BrokerResponse r) =>
        r.Result is { } res && res.TryGetProperty("point", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Object
            && p.TryGetProperty("Change", out var change) ? $"the trial in force ({change.GetString()}) is a crash data point" : "no trial was in force";

    /// <summary>
    /// After the PC wakes up: each GPU's daily profile again once it is idle, through "resume", which writes only what
    /// differs (nothing, when sleep kept it all) and leaves a trial in force alone.
    /// </summary>
    public void AfterSleep(string evidence)
    {
        sleep(SettleFor);
        foreach (var id in gpuIds)
        {
            try { Profile(id, "resume", evidence, "after waking up"); }
            catch (Exception e) { log($"gpu {id}: daily profile after waking up: failed: {e}"); }
        }
    }

    private bool Profile(string gpuId, string kind, string? evidence, string when)
    {
        if (!WaitIdle(gpuId))
        {
            log($"gpu {gpuId}: daily profile {when}: the GPU was not idle for {IdleWaitAtMost.TotalHours:0} h; not applied (the card stays at stock)");
            return false;
        }
        var p = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: kind, Value: evidence));
        log($"gpu {gpuId}: daily profile {when}: {ProfileSummary(p)}");
        return p.Ok;
    }

    private bool WaitIdle(string gpuId)
    {
        if (idle == null) return true;
        var deadline = now() + IdleWaitAtMost;
        for (int run = 0; run < IdleLooks;)
        {
            run = idle(gpuId) ? run + 1 : 0;
            if (run == IdleLooks) break;
            if (now() > deadline) return false;
            sleep(RetryEvery);
        }
        return true;
    }

    public static string ProfileSummary(BrokerResponse r)
    {
        if (r.Result is { } skip && skip.TryGetProperty("skipped", out var why) && why.ValueKind == System.Text.Json.JsonValueKind.String)
            return $"not applied: {why.GetString()}";
        if (r.Result is not { } res || !res.TryGetProperty("profile", out var p) || p.ValueKind != System.Text.Json.JsonValueKind.Object)
            return r.Ok ? "none kept" : r.Error ?? "failed";
        if (p.GetProperty("Suspended").GetBoolean()) return "suspended, the card stays at stock";
        int n = res.TryGetProperty("applied", out var a) ? a.GetArrayLength() : 0;
        string last = p.TryGetProperty("History", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Array && h.GetArrayLength() > 0 ? h[h.GetArrayLength() - 1].GetString()! : "";
        return r.Ok ? $"{n} write(s) applied; {last}" : $"not applied: {r.Error}; {last}";
    }

    private static string Summary(BrokerResponse r)
    {
        if (r.Result is not { } res || !res.TryGetProperty("items", out var items)) return r.Error ?? "no result";
        var each = items.EnumerateArray().Select(i => $"{i.GetProperty("Kind").GetString()}/{i.GetProperty("Key").GetString()} {i.GetProperty("action").GetString()}").ToList();
        return each.Count == 0 ? "nothing was held" : string.Join(", ", each);
    }
}
