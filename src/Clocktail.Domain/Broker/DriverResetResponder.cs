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
/// and it retries until <see cref="GiveUpAfter"/>. The crash is on record from the first attempt that does not land for
/// a reason other than a run measuring (<see cref="HeldByRun"/>), or once it gives up: a run measuring lets go and the
/// give-back lands, and until then the run's own job sees the reset and reads the trial on the card unchanged.
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
    private sealed record Seen(string Evidence, IReadOnlyDictionary<string, uint> CrashUv,
        IReadOnlyDictionary<string, (DateTimeOffset Answered, DateTimeOffset Stopped)> Hung);
    /// <summary>The resets signalled and not yet answered, oldest first, each with its own evidence and hang.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Seen> _seen = new();

    /// <summary>
    /// Called once a reset is seen (the broker answers a burst of events once, ResetBursts): each signal is answered as a
    /// reset of its own, in order. <paramref name="crashUv"/> is each GPU's core
    /// voltage at the hang from the flight recorder, where it found one, for the record; <paramref name="hung"/> when each
    /// GPU stopped answering and the last look before at which it still answered (<see cref="Profiles.FlightRecorder.Hang"/>),
    /// so that the crash goes to the trial on the card then, whatever took it off the record before the crash is recorded.
    /// Until 0.8.1 one slot held the last signal, so a reset signalled while an earlier one was answered replaced the
    /// evidence and hang of any other still waiting.
    /// </summary>
    public void Signal(string evidence, IReadOnlyDictionary<string, uint>? crashUv = null,
        IReadOnlyDictionary<string, (DateTimeOffset Answered, DateTimeOffset Stopped)>? hung = null)
    {
        _seen.Enqueue(new Seen(evidence, crashUv ?? new Dictionary<string, uint>(), hung ?? new Dictionary<string, (DateTimeOffset, DateTimeOffset)>()));
        _signal.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            sleep(SettleFor);
            while (_signal.Wait(0)) { }   // each signal is in the queue: the count is not needed
            // The resets signalled by now, in order; one signalled while they are answered is settled for in the next pass
            // (its release comes after this count).
            for (int n = _seen.Count; n > 0 && _seen.TryDequeue(out var seen); n--) Respond(seen.Evidence, seen.CrashUv, seen.Hung);
        }
    }

    /// <summary>Gives back every GPU's held cells; true when each one ended clean. A GPU whose answer throws is logged and
    /// counted as not clean: the next GPU, and the next reset, are still answered.</summary>
    public bool Respond(string evidence, IReadOnlyDictionary<string, uint>? crashUv = null,
        IReadOnlyDictionary<string, (DateTimeOffset Answered, DateTimeOffset Stopped)>? hung = null)
    {
        bool clean = true;
        foreach (var id in gpuIds)
        {
            try { clean &= RespondFor(id, evidence, crashUv?.GetValueOrDefault(id) ?? 0, hung != null && hung.TryGetValue(id, out var h) ? h : null); }
            catch (Exception e) { log($"gpu {id}: driver reset ({evidence}): the answer failed: {e}"); clean = false; }
        }
        return clean;
    }

    private bool RespondFor(string gpuId, string evidence, uint crashUv, (DateTimeOffset Answered, DateTimeOffset Stopped)? hung = null)
    {
        var deadline = now() + GiveUpAfter;
        bool recorded = false, crashOnRecord = false;
        while (true)
        {
            // Only the first attempt that reaches recovery records the crash; a retry is the same reset, and says so: a plain
            // recovery would take the trial off the record before "crashed" could record its crash.
            var r = perform(new BrokerRequest(BrokerRequest.OpRecover, gpuId, Kind: recorded ? BrokerRequest.RecoverAfterDriverResetAgain : BrokerRequest.RecoverAfterDriverReset, Value: evidence));
            recorded |= r.Result != null;
            if (r.Ok)
            {
                log($"gpu {gpuId}: driver reset ({evidence}): {Summary(r)}");
                // The crash on record at once, so a trial the agent writes while the GPU is still busy is not blamed for it;
                // the power limit at once (the reset put it back to 575 W; stock clocks under it are safe); the curve once idle.
                Record(gpuId, evidence, "after the reset", crashUv, hung);
                var power = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: "apply-power"));
                log($"gpu {gpuId}: daily profile's power limit after the reset: {ProfileSummary(power)}");
                return Profile(gpuId, "after-reset", evidence, "after the reset");
            }
            bool retry = r.Code is BrokerCode.JudgeBusy or BrokerCode.WindowBusy or BrokerCode.Failed || r.Result != null;
            bool givingUp = !retry || now() + RetryEvery > deadline;
            // A give-back that has not landed (a cell refused, the window never free): the crash on record all the same, its
            // trial kept in force while the card may still hold it ("crashed-held"), so that nothing marks it stable or
            // records it as survived meanwhile, and a give-back that never lands still leaves its data point. "crashed"
            // closes it once one lands. Not while a run measuring holds it off: the trial's ending would change under the
            // run, whose job would read the trial on the card as changed, or the crash as given back, before it is.
            if (!crashOnRecord && (givingUp || !HeldByRun(r))) crashOnRecord = RecordHeld(gpuId, evidence, crashUv, hung);
            if (givingUp)
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
                if (!down)
                {
                    // "start": the trial in force when the service began ends with it; one the agent writes while this
                    // waits for an idle GPU stays.
                    RecoverLeftovers(id);
                    Profile(id, "start", "the broker started", "at start");
                    continue;
                }
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

    /// <summary>
    /// At start in the same boot: a write window left open (a write whose outcome became unknown, or a worker ended
    /// mid-write by a handover or an update) refuses every write, the start's daily profile included, and nothing else
    /// would clear it. Ordinary recovery runs first when the sentinel is up or the journal's owner has died.
    /// </summary>
    private void RecoverLeftovers(string gpuId)
    {
        var own = perform(new BrokerRequest(BrokerRequest.OpOwnership, gpuId));
        if (own.Result is not { } res) return;
        bool sentinel = res.TryGetProperty("sentinel", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String;
        bool died = res.TryGetProperty("reason", out var why) && why.GetString() == "OwnerDied";
        if (!sentinel && !died) return;
        var r = perform(new BrokerRequest(BrokerRequest.OpRecover, gpuId));
        log($"gpu {gpuId}: at start, {(sentinel ? "a write window was left open" : "the last worker's cells were still held")}: recovered: {Summary(r)}");
    }

    /// <summary>The crash recorded ("crashed"): the trial on the card when the GPU stopped answering (<paramref name="hung"/>;
    /// without it, the trial in force) made a data point, nothing written.</summary>
    private void Record(string gpuId, string evidence, string when, uint crashUv = 0, (DateTimeOffset Answered, DateTimeOffset Stopped)? hung = null)
    {
        var c = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: "crashed", Value: evidence, Uv: crashUv,
            HungAt: hung?.Stopped, AnsweredAt: hung?.Answered));
        log($"gpu {gpuId}: crash recorded {when}: {(c.Ok ? CrashSummary(c) : c.Error ?? "failed")}");
    }

    /// <summary>Whether a give-back was held off by a run measuring (judge.lock) or by another operation in the window: it
    /// lands once that lets go.</summary>
    private static bool HeldByRun(BrokerResponse r) => r.Code is BrokerCode.JudgeBusy or BrokerCode.WindowBusy;

    /// <summary>The crash recorded while its give-back waits ("crashed-held"); true once the service has it.</summary>
    private bool RecordHeld(string gpuId, string evidence, uint crashUv, (DateTimeOffset Answered, DateTimeOffset Stopped)? hung)
    {
        var c = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: "crashed-held", Value: evidence, Uv: crashUv,
            HungAt: hung?.Stopped, AnsweredAt: hung?.Answered));
        log($"gpu {gpuId}: crash recorded before the give-back landed: {(c.Ok ? CrashSummary(c) : c.Error ?? "failed")}");
        return c.Ok;
    }

    private static string CrashSummary(BrokerResponse r) =>
        r.Result is { } res && res.TryGetProperty("point", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Object
            && p.TryGetProperty("Change", out var change)
            ? res.TryGetProperty("on_record_already", out var before) && before.ValueKind == System.Text.Json.JsonValueKind.True
                ? $"the trial's crash ({change.GetString()}) was on record already; the card is off the trial now"
                : $"the trial in force ({change.GetString()}) is a crash data point"
            : "no trial was in force";

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
        // After a reset the wait gives way: "after-reset" leaves a trial in force alone, so once the agent has begun one
        // the step is sent at once (and skips it), and a newer reset puts the profile back on itself once answered. The
        // loop that answers resets waits here, so until 0.8.1 a game kept running after a crash held every later reset's
        // give-back and record until the GPU idled, up to 4 h.
        bool afterReset = kind == "after-reset";
        switch (WaitIdle(gpuId, afterReset ? () => !_seen.IsEmpty : null, afterReset ? () => TrialInForce(gpuId) : null))
        {
            case Waited.TooLong:
                log($"gpu {gpuId}: daily profile {when}: the GPU was not idle for {IdleWaitAtMost.TotalHours:0} h; not applied (the card stays at stock)");
                return false;
            case Waited.NewerReset:
                log($"gpu {gpuId}: daily profile {when}: a newer driver reset came first; it is answered now, and its answer puts the profile back once the GPU is idle");
                return false;
        }
        var p = perform(new BrokerRequest(BrokerRequest.OpProfile, gpuId, Kind: kind, Value: evidence));
        log($"gpu {gpuId}: daily profile {when}: {ProfileSummary(p)}");
        return p.Ok;
    }

    private enum Waited { Idle, TrialInForce, NewerReset, TooLong }

    /// <summary>Until <see cref="IdleLooks"/> idle looks in a row, at most <see cref="IdleWaitAtMost"/>; after a busy look it
    /// gives way to a newer reset (<paramref name="newerReset"/>) or a trial in force (<paramref name="trialInForce"/>).</summary>
    private Waited WaitIdle(string gpuId, Func<bool>? newerReset = null, Func<bool>? trialInForce = null)
    {
        if (idle == null) return Waited.Idle;
        var deadline = now() + IdleWaitAtMost;
        for (int run = 0; run < IdleLooks;)
        {
            run = idle(gpuId) ? run + 1 : 0;
            if (run == IdleLooks) break;
            if (run == 0 && newerReset?.Invoke() == true) return Waited.NewerReset;
            if (run == 0 && trialInForce?.Invoke() == true) return Waited.TrialInForce;
            if (now() > deadline) return Waited.TooLong;
            sleep(RetryEvery);
        }
        return Waited.Idle;
    }

    /// <summary>Whether a trial is in force now ("show"); false when that cannot be read.</summary>
    private bool TrialInForce(string gpuId) =>
        perform(new BrokerRequest(BrokerRequest.OpExperiment, gpuId, Kind: "show")).Result is { } res
        && res.TryGetProperty("experiment", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Object
        && e.TryGetProperty("Trial", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Object;

    public static string ProfileSummary(BrokerResponse r)
    {
        if (r.Result is { } skip && skip.TryGetProperty("skipped", out var why) && why.ValueKind == System.Text.Json.JsonValueKind.String)
            return $"not applied: {why.GetString()}";
        if (r.Result is not { } res || !res.TryGetProperty("profile", out var p) || p.ValueKind != System.Text.Json.JsonValueKind.Object)
            return r.Ok ? "none kept" : r.Error ?? "failed";
        if (p.GetProperty("Suspended").GetBoolean()) return "suspended, the card stays at stock";
        int n = res.TryGetProperty("applied", out var a) ? a.GetArrayLength() : 0;
        string last = p.TryGetProperty("History", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Array && h.GetArrayLength() > 0 ? h[h.GetArrayLength() - 1].GetString()! : "";
        bool partial = res.TryGetProperty("partial", out var pa) && pa.ValueKind == System.Text.Json.JsonValueKind.True;
        return r.Ok ? $"{n} write(s) applied; {last}" : partial ? $"partly applied, {n} write(s): {r.Error}; {last}" : $"not applied: {r.Error}; {last}";
    }

    private static string Summary(BrokerResponse r)
    {
        if (r.Result is not { } res || !res.TryGetProperty("items", out var items)) return r.Error ?? "no result";
        var each = items.EnumerateArray().Select(i => $"{i.GetProperty("Kind").GetString()}/{i.GetProperty("Key").GetString()} {i.GetProperty("action").GetString()}").ToList();
        return each.Count == 0 ? "nothing was held" : string.Join(", ", each);
    }
}
