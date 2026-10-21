using System.Text.Json;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Domain.Broker;

/// <summary>One GPU as the broker sees it: the session to drive it, and the per-GPU journal and sentinel that
/// record what it owes. In production these are backed by the driver and files under the user's local
/// application data; in tests by the fake driver and a temp directory.</summary>
public interface IGpuTarget
{
    string StableId { get; }
    string Name { get; }
    GpuSession Session { get; }
    OwnershipJournal Journal { get; }
    ApplySentinel Sentinel { get; }
}

/// <summary>The GPUs the broker manages. A null id selects the first, matching the CLI's default adapter.</summary>
public interface IGpuTargets
{
    IReadOnlyList<string> Ids { get; }
    IGpuTarget? Resolve(string? id);
}

/// <summary>
/// The single place a privileged operation is performed and turned into result JSON. It runs the same whether
/// the CLI calls it in-process (already elevated) or the broker calls it on a client's behalf over the pipe: the
/// pipe only moves <em>where</em> this runs, not <em>what</em> it does. Everything that makes a write safe lives
/// here, so neither caller can skip it:
/// <list type="bullet">
/// <item>the firmware ceiling is whatever the write path's rules enforce (board max for power, the standard rail
/// ceiling, the driver's clock range, the fan's firmware range), in the plan before the write and in the cell's
/// post-write check inside the transaction; this dispatcher exposes no knob that widens it;</item>
/// <item>only one write window is open at a time (<see cref="_writeWindow"/>), so two writes never land together
/// and leave a black screen no one can attribute;</item>
/// <item>a write refuses while a stability run holds its lock, so an automatic write never changes the state
/// another tool is measuring: looked at before the window opens, and again by the sentinel once this program's
/// own lock is on disk, so two tools that each looked and saw nothing cannot both go ahead;</item>
/// <item>marks from before this boot are settled (<see cref="CrashLog.SettleReboot"/>) before any op reads or
/// drops them, so the one record of a write that took the machine down is kept whichever op comes first.</item>
/// </list>
/// The operation set is the whitelist, and the write verb reaches only the <see cref="WriteRegistry"/>: there is
/// no raw-slot or register verb to call.
/// </summary>
public sealed class BrokerDispatcher
{
    private readonly NvClient _hw;
    private readonly IGpuTargets _gpus;
    private readonly IHost _host;
    private readonly bool _canWrite;
    private readonly Func<string?> _judgeBusyReason;
    private readonly SemaphoreSlim _writeWindow = new(1, 1);

    /// <summary>How long a write waits for the window before it answers <see cref="BrokerCode.WindowBusy"/> instead of hanging its client.</summary>
    public static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(30);
    private readonly object _settle = new();
    /// <summary>Serializes the experiment file: a trial written by a client and a driver reset's step never interleave.</summary>
    private readonly object _experiment = new();

    /// <param name="canWrite">Whether this process may write to the driver (elevated). The broker passes true;
    /// a non-elevated in-process caller passes false and write ops report that instead of touching the driver.</param>
    /// <param name="judgeBusyReason">Returns why a write must wait (a live stability run), or null when the GPU is free.</param>
    /// <param name="frames">The frame-rate capture (PresentMon); null where this process cannot run one.</param>
    public BrokerDispatcher(NvClient hw, IGpuTargets gpus, IHost host, bool canWrite, Func<string?>? judgeBusyReason = null, Fps.IFrameCapture? frames = null)
    {
        _hw = hw;
        _gpus = gpus;
        _host = host;
        _canWrite = canWrite;
        _judgeBusyReason = judgeBusyReason ?? (() => null);
        _frames = frames;
    }

    private readonly Fps.IFrameCapture? _frames;

    public BrokerResponse Dispatch(BrokerRequest req)
    {
        try
        {
            if (req.Op == BrokerRequest.OpPing)
                return Ok(new
                {
                    pong = true, pid = _host.Pid, can_write = _canWrite, gpus = _gpus.Ids,
                    paths = WriteRegistry.All.Select(p => new { kind = p.Kind, keys = p.Keys, values = p.Values, checks_after_write = p.AfterWrite, equal_value_proof = p.ProveEqual != null }),
                });

            var target = _gpus.Resolve(req.GpuId);
            if (target == null)
                return Fail(req.GpuId == null ? "no GPU is available" : $"no GPU with id '{req.GpuId}'", BrokerCode.NoGpu);

            if (!req.IsWrite)
                return req.Op switch
                {
                    BrokerRequest.OpOwnership => Ownership(target),
                    BrokerRequest.OpProfile => Profile(target, req),
                    BrokerRequest.OpExperiment => ExperimentOp(target, req),
                    BrokerRequest.OpFps => FpsOp(target, req),
                    _ => Fail($"unknown operation '{req.Op}'", BrokerCode.UnknownOp),
                };

            // A write: one window at a time, and never while a stability run is measuring.
            if (!_writeWindow.Wait(WindowWait))
                return Fail($"another write has held the window for over {WindowWait.TotalSeconds:0} s; nothing was written, try again", BrokerCode.WindowBusy);
            try
            {
                if (_judgeBusyReason() is { } busy) return Fail(JudgeBusy(busy), BrokerCode.JudgeBusy);
                Settle(target);
                return req.Op switch
                {
                    BrokerRequest.OpPower => Power(target, req),
                    BrokerRequest.OpClock => Clock(target, req),
                    BrokerRequest.OpWrite => Write(target, req),
                    BrokerRequest.OpProve => Prove(target, req),
                    BrokerRequest.OpRecover => Recover(target, req),
                    _ => Fail($"unknown operation '{req.Op}'", BrokerCode.UnknownOp),
                };
            }
            finally { _writeWindow.Release(); }
        }
        catch (StandDownException e)
        {
            return Fail(JudgeBusy(e.Reason), BrokerCode.JudgeBusy);   // the lock appeared after the first look; the sentinel saw it, nothing was written
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            return Fail(e.Message, BrokerCode.BadRequest);   // a key, value or slot name the request got wrong
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or InvalidDataException or NvApiException or NvmlException
                                      or IOException or UnauthorizedAccessException)
        {
            return Fail(e.Message, BrokerCode.Failed);   // a file the state lives in can be locked or gone too (the responder retries these)
        }
    }

    /// <summary>
    /// Closes the write window for good, for a broker about to hand over to a fresh process because its driver went away:
    /// waits up to <paramref name="wait"/> for a write in flight to finish, then keeps the window, so no new write starts
    /// (each answers <see cref="BrokerCode.WindowBusy"/>). True when no write was in flight by then.
    /// </summary>
    public bool Close(TimeSpan wait) => _writeWindow.Wait(wait);

    /// <summary>At start, before any client asks: every GPU's marks from before this boot go through
    /// <see cref="CrashLog.SettleReboot"/>, so a window that was open when the machine went down is on record
    /// whichever op comes first. <paramref name="uncleanShutdown"/>, when the last boot ended in a bugcheck or a power
    /// loss, makes held cells from before the boot a crash too (no window need be open). Returns the GPUs whose crash
    /// was recorded, with the log's path.</summary>
    public IReadOnlyList<(string GpuId, string CrashLog)> SettleAll(string? uncleanShutdown = null)
    {
        var recorded = new List<(string, string)>();
        foreach (var id in _gpus.Ids)
            if (_gpus.Resolve(id) is { } t && Settle(t, uncleanShutdown) is { } path) recorded.Add((id, path));
        return recorded;
    }

    private string? Settle(IGpuTarget target, string? uncleanShutdown = null)
    {
        lock (_settle) return CrashLog.SettleReboot(target.Journal, target.Sentinel, _host, uncleanShutdown);
    }

    private static string JudgeBusy(string busy) => $"a stability run is in progress ({busy}); the write window is held, try again when it finishes";

    private BrokerResponse Ownership(IGpuTarget target)
    {
        var settled = Settle(target);   // a reboot's marks go to the crash log before this report reads them
        var plan = target.Journal.Inspect();
        var crashLog = CrashLog.PathBeside(target.Sentinel.Path);
        return Ok(new
        {
            path = target.Journal.Path,
            reason = plan.Reason.ToString(),
            detail = plan.Detail,
            items = plan.Items.Select(i => new { i.Kind, i.Key, i.Original, i.ClaimedAt, i.Note }),
            sentinel = target.Sentinel.IsBlocked(out var what) ? what : null,
            boot_id = _host.BootId,
            // Write windows found open across a reboot: what was in flight and what was held (the last five).
            crash_log = crashLog,
            crashes = CrashLog.ReadLast(crashLog, 5).Select(c => new { found_at = c.FoundAt, boot_id = c.BootId, sentinel = c.Sentinel, items = c.Items.Select(i => new { i.Kind, i.Key, i.Original, i.ClaimedAt, i.Note }) }),
            // The crash this very call put on record, if the marks of one were still up.
            crash_recorded = settled,
        });
    }

    private BrokerResponse Recover(IGpuTarget target, BrokerRequest req)
    {
        string? resetLogged = null;
        if (req.Kind == BrokerRequest.RecoverAfterDriverReset && target.Journal.Inspect() is { Items.Count: > 0 } held)
        {
            // What was on the card when the driver reset is the evidence the tuner needs, as for a reboot: on record first.
            string window = target.Sentinel.IsBlocked(out var inFlight) ? $"; a write window was open: {inFlight}" : "";
            resetLogged = CrashLog.PathBeside(target.Sentinel.Path);
            CrashLog.Append(resetLogged, new CrashRecord(_host.Now, _host.BootId, $"driver reset ({req.Value ?? "no detail"}){window}", held.Items));
        }
        var r = new Recovery(RecoveryClient(), target.Journal, target.Sentinel, _host).Run(target.Session, _canWrite);
        return new BrokerResponse(r.Clean, Result: Element(new
        {
            success = r.Clean,
            reason = r.Reason.ToString(),
            detail = r.Detail,
            can_write = _canWrite,
            needs_write = r.NeedsWrite,
            items = r.Items.Select(i => new { i.Kind, i.Key, i.Original, i.Current, action = i.Action.ToString(), i.Detail, state_known = i.StateKnown, tx = i.Tx?.ToString(), log = i.Tx?.Log }),
            journal_cleared = r.JournalCleared,
            sentinel_cleared = r.SentinelCleared,
            crash_recorded = r.CrashRecorded ?? resetLogged,
        }));
    }

    private BrokerResponse Profile(IGpuTarget target, BrokerRequest req)
    {
        var path = DailyProfile.PathBeside(target.Journal.Path);
        var profile = DailyProfile.Load(path);
        switch (req.Kind)
        {
            case "show":
                return Ok(new { success = true, path, profile });
            case "keep":
            {
                var writes = new List<ProfileWrite>();
                var writer = new OwnedWriter(_hw, target.Journal, target.Sentinel, _host);
                // A wall a trial set was lowered to sample the curve lower down: never the daily one, with a profile kept
                // or not (the daily wall is set on its own: "wall").
                bool trialWall = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial?.PowerMw != null;
                foreach (var item in target.Journal.Inspect().Items)
                {
                    if (TrialOnly(item)) continue;   // a trial's current limit or fixed fan duty, never the daily profile's
                    if (trialWall && item.Kind == PowerCap.Kind) continue;
                    var wp = WriteRegistry.Find(item.Kind) ?? throw new InvalidOperationException($"this version cannot keep '{item.Id}'");
                    var cell = wp.Prepare(_hw, target.Session, item.Key, item.Original, WriteOptions.Recovery).Cell;
                    var now = writer.Look(target.Session, cell) ?? throw new InvalidOperationException($"{cell.Id} could not be read; nothing kept");
                    if (now != item.Original) writes.Add(new ProfileWrite(item.Kind, item.Key, now));
                }
                // Once a profile is kept, what an agent tunes is taken from the card (DailyProfile.KeptFromCard): the curves,
                // the memory offset, MSVDD, XBAR/SYS/Video. The wall stays as kept, so a wall lowered to sample the curve
                // lower down never becomes the daily one.
                if (profile != null)
                    writes = [.. profile.Writes.Where(w => !DailyProfile.KeptFromCard(w.Kind, w.Key)), .. writes.Where(w => DailyProfile.KeptFromCard(w.Kind, w.Key))];
                if (writes.Count == 0) return Fail("nothing this broker holds differs from stock; nothing to keep", BrokerCode.CannotWrite);
                var trialKept = CloseTrial(target, Experiment.Stable, "kept as the daily profile");
                string listed = string.Join("; ", writes.Select(w => $"{w.Kind}/{w.Key}={w.Value}"));
                var kept = new DailyProfile(DailyProfile.InWriteOrder(writes), _host.Now, History: profile?.History)
                    .Noted(_host.Now, req.Value is { } why ? $"kept: {listed} ({why})" : $"kept: {listed}");
                kept.Save(path);
                // A trial's lowered wall or current limit is still on the card: the kept wall goes back on now (and the
                // limits back to their defaults), not at the next start.
                var wall = trialKept?.PowerMw != null || HeldTrialCells(target).Any()
                    ? ApplyProfile(target, kept, path, only: w => w.Kind == PowerCap.Kind) : null;
                Log(target, "Kept as the new daily profile", [
                    req.Value is { } note ? $"Note: {note}" : null,
                    trialKept != null ? $"From trial: {trialKept.Game}, {trialKept.Change} ({Limits(trialKept)})" : null,
                    profile != null
                        ? "Taken from the card: V/F curve, fan curve, memory offset, MSVDD offsets, XBAR/SYS/Video offsets and voltage demands; the power limit stays the old daily profile's; current limits and fixed fan duties are not kept"
                        : trialWall ? "A trial's power limit, current limits and fixed fan duties are not kept" : "Current limits and fixed fan duties are not kept",
                    .. kept.Writes.Select(w => $"New: {w.Kind}/{w.Key} = {w.Value}"),
                    .. (profile?.Writes ?? []).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
                    .. wall != null ? ["Daily power limit put back:", .. Writes(wall)] : Array.Empty<string>()]);
                return Ok(new { success = true, path, profile = kept });
            }
            case "off":
                CloseTrial(target, Experiment.Survived, "the card went back to stock");
                if (profile == null) return Ok(new { success = true, path, profile });
                profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                profile.Save(path);
                Log(target, "Daily profile suspended", ["The card is at stock until the daily profile is loaded or kept again"]);
                return Ok(new { success = true, path, profile });
            case "apply":
            {
                // Value: why, for the log ("the broker started", an agent's back_to_daily).
                CloseTrial(target, Experiment.Survived, "the daily profile went back on");
                var r = ApplyProfile(target, profile, path);
                Log(target, "Daily profile applied", [req.Value is { } why ? $"Reason: {why}" : null, profile is { Suspended: true } ? "Daily profile suspended: nothing written" : null, .. Writes(r)]);
                return r;
            }
            case "resume":
            {
                // Value: what woke it. The card may have lost what the profile wrote, or kept it all: applying again writes
                // only what differs. A trial in force is left alone: the agent running it reads the card and decides.
                if (Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial != null)
                {
                    Log(target, "Woke up: a trial is in force, nothing written", [req.Value is { } w ? $"Reason: {w}" : null]);
                    return Ok(new { success = true, skipped = "a trial is in force: the agent running it decides", profile });
                }
                var r = ApplyProfile(target, profile, path);
                Log(target, "Daily profile applied after waking up", [req.Value is { } why ? $"Reason: {why}" : null, profile is { Suspended: true } ? "Daily profile suspended: nothing written" : null, .. Writes(r)]);
                return r;
            }
            case "load":
            {
                // The tray's "Load": the person puts the profile back on, so a suspension ends here, not at the next keep.
                if (profile == null) return Fail("no daily profile is kept; nothing to load", BrokerCode.CannotWrite);
                CloseTrial(target, Experiment.Survived, "the daily profile went back on");
                profile = profile.Noted(_host.Now, "loaded by request") with { Suspended = false };
                profile.Save(path);
                var r = ApplyProfile(target, profile, path);
                Log(target, "Daily profile loaded", Writes(r));
                return r;
            }
            case "preset":
            {
                // Value: a preset's name, or a saved profile's. It replaces the daily profile (the old one's writes go into
                // the log) and goes on now.
                var preset = Presets.Find(req.Value);
                if (preset == null) return LoadSaved(target, req.Value, profile, path);
                if (!preset.Fits(target.Name))
                    return Fail($"'{preset.Name}' was tuned on an {preset.Gpu}; this card is {target.Name}. Nothing was written", BrokerCode.CannotWrite);
                CloseTrial(target, Experiment.Survived, $"preset '{preset.Name}' went on");
                var loaded = new DailyProfile(DailyProfile.InWriteOrder(preset.Writes), _host.Now, History: profile?.History)
                    .Noted(_host.Now, $"preset '{preset.Name}' loaded");
                var r = ApplyProfile(target, loaded, path);
                if (!r.Ok) return NotLoaded(target, $"preset '{preset.Name}'", r, profile, path);
                loaded.Save(path);
                Log(target, $"Preset loaded · {preset.Title}", [
                    .. (profile?.Writes ?? []).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
                    .. Writes(r)]);
                return r;
            }
            case "save":
            {
                // Value: the name, any name. The daily profile as it is kept now, saved under it; one already saved under
                // the name (whatever its case) is replaced.
                var (name, bad) = SavedProfiles.Normalize(req.Value);
                if (name == null) return Fail(bad!, BrokerCode.BadRequest);
                if (profile == null) return Fail("no daily profile is kept yet: keep_as_daily (or load a preset) first, then save it", BrokerCode.CannotWrite);
                var file = SavedProfiles.PathBeside(target.Journal.Path);
                var saved = new DailyProfile(profile.Writes, _host.Now).Noted(_host.Now, $"saved as '{name}' from the daily profile kept {profile.KeptAt:yyyy-MM-dd HH:mm}");
                bool replaced = SavedProfiles.Save(file, name, saved);
                Log(target, $"Profile saved · {name}", [replaced ? "Replaced the profile saved under this name" : null, .. profile.Writes.Select(w => $"{w.Kind}/{w.Key} = {w.Value}")]);
                return Ok(new { success = true, name, path = file, replaced, profile = saved });
            }
            case "saved":
                return Ok(new
                {
                    success = true,
                    profiles = SavedProfiles.All(SavedProfiles.PathBeside(target.Journal.Path)).Select(p => new { name = p.Name, saved_at = p.Profile.KeptAt, writes = p.Profile.Writes }),
                });
            case "forget":
            {
                // Value: a saved profile's name. The daily profile and the presets are never deleted here.
                var gone = SavedProfiles.Delete(SavedProfiles.PathBeside(target.Journal.Path), req.Value);
                if (gone == null) return Fail($"no profile is saved as '{req.Value}'", BrokerCode.CannotWrite);
                Log(target, $"Saved profile deleted · {gone.Value.Name}", [.. gone.Value.Profile.Writes.Select(w => $"Was: {w.Kind}/{w.Key} = {w.Value}")]);
                return Ok(new { success = true, name = gone.Value.Name, deleted = gone.Value.Profile });
            }
            case "wall":
            {
                // The daily power limit, set on its own (a trial's wall is never kept): it replaces the profile's, or starts
                // one. It goes on now, unless the card is at stock (suspended) or a trial runs under its own wall, which
                // stays until the trial ends.
                if (!uint.TryParse(req.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mw) || mw == 0)
                    return Fail("wall needs the daily power limit in mW", BrokerCode.BadRequest);
                string key = PowerCap.Key(PowerRequestKind.Ordinary), value = mw.ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool trialWall = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial?.PowerMw != null;
                BrokerResponse? now = null;
                if (profile is not { Suspended: true } && !trialWall)
                {
                    now = Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: PowerCap.Kind, Key: key, Value: value));
                    if (!now.Ok) return now;   // refused (out of the board's range): the profile stays as it was
                }
                var walled = new DailyProfile(DailyProfile.InWriteOrder([.. (profile?.Writes ?? []).Where(w => w.Kind != PowerCap.Kind || w.Key != key), new ProfileWrite(PowerCap.Kind, key, value)]),
                    _host.Now, profile?.Suspended ?? false, profile?.History).Noted(_host.Now, $"daily power limit {mw / 1000.0:0.#} W");
                walled.Save(path);
                Log(target, $"Daily power limit · {mw / 1000.0:0.#} W", [
                    .. (profile?.Writes ?? []).Where(w => w.Kind == PowerCap.Kind && w.Key == key).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
                    now != null ? null : profile is { Suspended: true } ? "The card stays at stock (the daily profile is suspended)" : "A trial runs under its own wall: the daily one goes on when the trial ends",
                    .. now != null ? Writes(now) : []]);
                return Ok(new { success = true, path, profile = walled, applied = now?.Result is { } res && res.TryGetProperty("applied", out var ap) ? ap : (JsonElement?)null });
            }
            case "apply-power":
            {
                // Right after a driver reset, before the GPU is idle: the limit went back to 575 W, and stock clocks under
                // the kept limit are still a safe state.
                if (profile is null or { Suspended: true }) return Ok(new { success = true, path, profile, applied = Array.Empty<object>() });
                var r = ApplyProfile(target, profile, path, only: w => w.Kind == PowerCap.Kind);
                Log(target, "Driver reset: power limit put back first", Writes(r));
                return r;
            }
            case "crashed":
            {
                // A crash: a driver reset (the held cells already given back), or the machine going down while it held
                // cells. Recorded at once, before anything waits for an idle GPU: a trial in force is the one that crashed
                // (a data point), and one the agent writes after this is not blamed for it. Nothing is written here; the
                // daily profile goes back on unchanged in "after-reset". No step-down: the person is at the machine and
                // the agent adjusts from there (the user, 2026-10-19).
                var crash = CloseTrial(target, Experiment.Crash, req.Value, req.Uv > 0 ? req.Uv : null);
                if (profile != null)
                {
                    profile = profile.Noted(_host.Now, crash != null
                        ? $"{req.Value ?? "driver reset"} during an experiment trial ({crash.Change}): a data point; put back unchanged"
                        : $"{req.Value ?? "driver reset"}: put back unchanged");
                    profile.Save(path);
                }
                Log(target, crash != null ? $"Crash · during trial · {crash.Game}" : "Crash · daily profile", [
                    $"Evidence: {req.Value ?? "driver reset"}",
                    req.Uv > 0 ? $"Crash voltage from the flight recorder: {req.Uv / 1000.0:0.#} mV" : "The flight recorder found no crash point",
                    crash != null ? $"Trial: {crash.Change}, ran {crash.Minutes} min; written at {Mv(crash.AtUv)}, {crash.AtMhz} MHz; {Limits(crash)}" : null,
                    crash != null ? $"Trial curve: {crash.Curve}" : null,
                    profile == null ? "No daily profile: the card stays at stock" : "The daily profile goes back on unchanged once the GPU is idle"]);
                return Ok(new { success = true, path, profile, point = crash, applied = Array.Empty<object>() });
            }
            case "after-reset":
            {
                // The daily profile back on after a crash ("crashed" recorded it), once the GPU is idle. A trial in force
                // now was written after the crash: the agent running it decides, as on waking up.
                if (Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial != null)
                {
                    Log(target, "After the crash: a trial is in force, nothing written", [req.Value is { } w ? $"Reason: {w}" : null]);
                    return Ok(new { success = true, skipped = "a trial began after the crash: the agent running it decides", profile });
                }
                var r = profile == null ? Ok(new { success = true, path, profile, applied = Array.Empty<object>() }) : ApplyProfile(target, profile, path);
                Log(target, "Daily profile put back after the crash", [
                    req.Value is { } why ? $"Reason: {why}" : null,
                    profile == null ? "No daily profile: the card stays at stock" : profile is { Suspended: true } ? "Daily profile suspended: nothing written" : null,
                    .. Writes(r)]);
                return r;
            }
            default:
                return Fail($"profile needs show, keep, off, apply, resume, load, preset, save, saved, forget, wall, apply-power, crashed or after-reset, not '{req.Kind}'", BrokerCode.BadRequest);
        }
    }

    /// <summary>Live tuning in a game: see <see cref="BrokerRequest.OpExperiment"/> and <see cref="Experiment"/>.</summary>
    private BrokerResponse ExperimentOp(IGpuTarget target, BrokerRequest req)
    {
        string path = Experiment.PathBeside(target.Journal.Path), data = Experiment.DataPathBeside(target.Journal.Path);
        switch (req.Kind)
        {
            case "show":
                return Ok(new { success = true, path, data, experiment = Experiment.Load(path), points = Experiment.ReadLast(data, 20) });
            case "begin":
            {
                var a = Args(req.Value);
                string game = Text(a, "game") ?? throw new ArgumentException("begin needs a game");
                // A trial still in force from the session before ends as "end" ends it: its settings go back with the daily
                // profile, so the new session's trials run on, and are recorded under, the daily wall and limits it says.
                var closed = CloseTrial(target, Experiment.Survived, "a new session began");
                IReadOnlyList<string> writes = [];
                BrokerResponse? back = closed != null ? Back(target, closed, out writes) : null;
                if (back is { Ok: false })
                {
                    Log(target, $"Session not started · {game}", [$"Last trial: {closed!.Change}", "Back to the daily profile did not all land:", .. writes]);
                    return new BrokerResponse(false, $"the trial in force did not all go back ({back.Error}); the new session did not begin", back.Result, back.Code);
                }
                lock (_experiment)
                {
                    var exp = new Experiment(game, Text(a, "note"), _host.Now);
                    exp.Save(path);
                    Log(target, $"Session started · {game}", [exp.Note,
                        .. closed != null ? (string?[])[$"Last trial: {closed.Change}, ran {closed.Minutes} min", "Back to the daily profile:", .. writes] : []]);
                    return Ok(new { success = true, path, data, experiment = exp, back = back?.Result });
                }
            }
            case "note":
            {
                // The agent's own words: what it saw, what it plans, what the person said.
                if (string.IsNullOrWhiteSpace(req.Value)) return Fail("a note needs text", BrokerCode.BadRequest);
                var exp = Experiment.Load(path);
                Log(target, exp != null ? $"Note · {exp.Game}" : "Note", [req.Value]);
                return Ok(new { success = true });
            }
            case "try":
            {
                var a = Args(req.Value);
                string curve = Text(a, "curve") ?? throw new ArgumentException("try needs the whole curve");
                string change = Text(a, "change") ?? "unspecified";
                // power_mw: a board power limit for this trial (a lower wall moves the operating point down the curve, so
                // other points can be sampled); keep_curve: the curve is the one on the card, recorded and not written.
                uint? powerMw = UInt(a, "power_mw"), nvvddMa = UInt(a, "nvvdd_ma"), msvddMa = UInt(a, "msvdd_ma"), fanPct = UInt(a, "fan_pct");
                int? msvddRel = Int(a, "msvdd_rel_uv"), msvddVmin = Int(a, "msvdd_vmin_uv");
                // fan_curve: both curve policies; memory_khz: the P0 memory offset (it zeroes the V/F points, so the curve
                // is written again after it, keep_curve or not); xbar_khz / xbar_vdemand_uv: the XBAR clock offset and the
                // voltage demand that keeps MSVDD off its floor under it.
                string? fanCurve = Text(a, "fan_curve");
                int? memoryKhz = Int(a, "memory_khz"), xbarKhz = Int(a, "xbar_khz"), xbarVdemand = Int(a, "xbar_vdemand_uv");
                bool keepCurve = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("keep_curve", out var kc) && kc.ValueKind == JsonValueKind.True
                    && memoryKhz == null;
                lock (_experiment)
                {
                    var exp = Experiment.Load(path) ?? throw new InvalidOperationException("no experiment session is open; begin one first");
                    // The writer's context (the readings and each point before and after, from the MCP server) goes in as given.
                    var context = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                    var written = new List<BrokerResponse>();
                    if (powerMw is { } mw)
                        written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: PowerCap.Kind,
                            Key: PowerCap.Key(PowerRequestKind.Ordinary), Value: mw.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    // nvvdd_ma / msvdd_ma: a current limit for the trial (a lower one caps the rail's power below the
                    // 400 W the board request stops at); never kept, put back to its original when the trial ends.
                    foreach (var (rail, ma) in new[] { (Rail.Nvvdd, nvvddMa), (Rail.Msvdd, msvddMa) })
                        if (ma is { } v && written.All(x => x.Ok))
                            written.Add(Dispatch(LimitWrite(target, CurrentLimitWrites.Key(rail), v.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    // The fan curve goes on every policy the firmware lets be edited (curve 0 and 1 on a 5090), as the
                    // daily profile's does; a fixed duty on every fan channel is trial-only, as a current limit is (fan_pct 0:
                    // the fans back to the curve).
                    if (fanCurve != null)
                        foreach (var i in FanPolicyReader.ReadInfo(_hw, target.Session).Editable.Select(p => p.Index))
                            if (written.All(x => x.Ok))
                                written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: FanPolicyWrites.Kind, Key: FanPolicyWrites.Key(i), Value: fanCurve)));
                    if (fanPct is { } pct)
                        foreach (var id in FanReader.Snapshot(_hw, target.Session).Channels.Select(c => c.Id))
                            if (written.All(x => x.Ok))
                                written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: FanControl.Kind, Key: FanControl.Key(id), Value: (pct == 0 ? FanState.Auto : new FanState(true, pct)).ToString())));
                    if (memoryKhz is { } mem && written.All(x => x.Ok))
                    {
                        string memKey = ClockOffsets.Key(ClockDomain.Memory), memValue = mem.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (CurveAside(target, memKey, memValue) is { } aside) written.Add(aside);
                        if (written.All(x => x.Ok))
                            written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: ClockOffsets.Kind, Key: memKey, Value: memValue)));
                    }
                    // msvdd_rel_uv / msvdd_vmin_uv: the MSVDD rail's offsets for the trial, through the rails path (its
                    // rules and post-write MAX check hold); putting the daily profile back puts its offsets back (the
                    // originals when it keeps none), and a keep takes them from the card.
                    if ((msvddRel != null || msvddVmin != null) && written.All(x => x.Ok))
                    {
                        var slots = new List<string>();
                        if (msvddRel is { } rel) slots.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"rel={rel}"));
                        if (msvddVmin is { } vmin) slots.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"vmin={vmin}"));
                        written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: RailWrites.Kind, Key: RailWrites.Key(Rail.Msvdd), Value: string.Join(",", slots))));
                    }
                    if (xbarVdemand is { } vd && written.All(x => x.Ok))
                        written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: ClockVdemand.Kind, Key: ClockVdemand.Key(ClockDomainId.Xbar),
                            Value: vd.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    if (xbarKhz is { } xk && written.All(x => x.Ok))
                        written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: ClockDomainOffsets.Kind, Key: ClockDomainOffsets.Key(ClockDomainId.Xbar),
                            Value: xk.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    if (!keepCurve && written.All(x => x.Ok))
                        written.Add(Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: VfWrites.Kind, Key: VfWrites.CoreKey, Value: curve,
                            EnableSlots: [nameof(Slot.ClockClientClkVfPointsSetControl)])));
                    if (written.FirstOrDefault(x => !x.Ok) is { } failed)
                    {
                        // The items before the one that failed landed: they go back to what they were, last first (the
                        // order they went on in respects every write's constraint, so the reverse does too: a curve set
                        // aside for a memory write comes back after the memory offset), and the trial before stays in force.
                        var undone = PutBackLanded(target, written);
                        bool clean = undone.All(x => x.Ok);
                        Log(target, $"Trial did not land · {exp.Game}", [$"Change: {change}", .. context, .. written.SelectMany(Writes),
                            undone.Count == 0 ? "The previous trial (if any) is still on the card"
                            : clean ? "The items written before it were put back: the previous trial (if any) is still on the card"
                            : "The items written before it did not all go back: the card holds part of this trial",
                            .. undone.SelectMany(Writes)]);
                        if (undone.Count == 0) return failed;   // nothing landed (or it was put back): the trial before stays in force
                        // Put back cleanly, the card is as it was: only the failed write's own transaction is in the answer, so
                        // the agent is told nothing changed. Otherwise every write is, so it is told to read the card.
                        return new BrokerResponse(false,
                            (failed.Error ?? "a write of the trial failed") + (clean
                                ? "; the items written before it were put back, so the card is as it was"
                                : "; the items written before it did not all go back: read the card"),
                            clean
                                ? Element(new { success = false, applied = AppliedOf(failed), put_back = undone.SelectMany(Writes) })
                                : Element(new { success = false, applied = (JsonElement[])[.. written.SelectMany(AppliedOf), .. undone.SelectMany(AppliedOf)] }),
                            failed.Code);
                    }
                    var replaced = exp.Trial is { Marked: false } ? exp.Point(Experiment.Survived, _host.Now, "replaced by the next trial") : null;
                    if (replaced != null) Experiment.Append(data, replaced);
                    // A curve trial runs under the wall the trial before it set, until the daily profile goes back on.
                    var trial = new ExperimentTrial(_host.Now, curve, change, UInt(a, "at_uv"), UInt(a, "at_mhz"), PowerMw: powerMw ?? exp.Trial?.PowerMw,
                        NvvddMa: nvvddMa ?? exp.Trial?.NvvddMa, MsvddMa: msvddMa ?? exp.Trial?.MsvddMa,
                        MsvddRelUv: msvddRel ?? exp.Trial?.MsvddRelUv, MsvddVminUv: msvddVmin ?? exp.Trial?.MsvddVminUv,
                        FanCurve: fanCurve ?? exp.Trial?.FanCurve, FanPct: fanPct == 0 ? null : fanPct ?? exp.Trial?.FanPct, MemoryKhz: memoryKhz ?? exp.Trial?.MemoryKhz,
                        XbarKhz: xbarKhz ?? exp.Trial?.XbarKhz, XbarVdemandUv: xbarVdemand ?? exp.Trial?.XbarVdemandUv);
                    (exp with { Trial = trial }).Save(path);
                    Log(target, $"Trial · {exp.Game}", [
                        $"Change: {change}",
                        $"Working point at write: {Mv(trial.AtUv)}, {trial.AtMhz} MHz",
                        $"Limits: {Limits(trial)}",
                        .. context,
                        replaced != null ? $"The previous trial ({replaced.Change}) ran {replaced.Minutes} min without a crash: recorded as survived" : null,
                        keepCurve ? "Curve unchanged (recorded only)" : null,
                        .. written.SelectMany(Writes)]);
                    return Ok(new { success = true, trial, writes = written.Select(x => x.Result) });
                }
            }
            case "stable":
                lock (_experiment)
                {
                    var exp = Experiment.Load(path);
                    if (exp is not { Trial: { } t }) return Fail("no trial is in force; nothing to mark", BrokerCode.CannotWrite);
                    var point = exp.Point(Experiment.Stable, _host.Now, req.Value)!;
                    Experiment.Append(data, point);
                    (exp with { Trial = t with { Marked = true } }).Save(path);
                    Log(target, $"Stable · {exp.Game}", [$"Trial: {point.Change}, ran {point.Minutes} min, {Limits(point)}", point.Note]);
                    return Ok(new { success = true, point });
                }
            case "crash":
            {
                // The game fell to the desktop with no driver reset: nothing the broker can see, so the player says so.
                var point = CloseTrial(target, Experiment.Crash, req.Value ?? "the game crashed (reported by the player)");
                if (point == null) return Fail("no trial is in force; nothing to mark", BrokerCode.CannotWrite);
                var r = Back(target, point, out var writes);
                Log(target, $"Crash · reported · {point.Game}", [   // the player, or a stress run that failed
                    $"What happened: {point.Note}",
                    $"Trial: {point.Change}, ran {point.Minutes} min; written at {Mv(point.AtUv)}, {point.AtMhz} MHz; {Limits(point)}",
                    $"Trial curve: {point.Curve}",
                    "Back to the daily profile:", .. writes]);
                return r;
            }
            case "end":
            {
                var exp = Experiment.Load(path);
                var point = CloseTrial(target, Experiment.Survived, "the session ended");
                lock (_experiment) Experiment.Delete(path);
                var r = Back(target, point, out var writes);
                Log(target, $"Session ended · {exp?.Game}", [
                    point != null ? $"Last trial: {point.Change}, ran {point.Minutes} min" : null,
                    "Back to the daily profile:", .. writes]);
                return r;
            }
            default:
                return Fail($"experiment needs show, begin, try, stable, crash, note or end, not '{req.Kind}'", BrokerCode.BadRequest);
        }
    }

    /// <summary>Back to the daily profile when one is on, else to stock, so a trial never stays behind; the answer carries
    /// the trial's data point beside the writes.</summary>
    private BrokerResponse Back(IGpuTarget target, DataPoint? point, out IReadOnlyList<string> writes)
    {
        var path = DailyProfile.PathBeside(target.Journal.Path);
        var profile = DailyProfile.Load(path);
        var back = profile is { Suspended: false }
            ? ApplyProfile(target, profile, path)
            : Dispatch(new BrokerRequest(BrokerRequest.OpRecover, target.StableId));
        writes = profile is { Suspended: false } ? [.. Writes(back)] : ["No daily profile in force: back to stock", .. Writes(back)];
        return new BrokerResponse(back.Ok, back.Error, Element(new { success = back.Ok, point, back = back.Result }), back.Code);
    }

    private void Log(IGpuTarget target, string title, IEnumerable<string?> lines) =>
        TuningLog.Append(TuningLog.PathBeside(target.Journal.Path), _host.Now, title, lines);

    private static string Mv(uint? uv) => uv is { } v ? $"NVVDD {v / 1000.0:0.#} mV" : "NVVDD unread";
    private static string Wall(uint? mw) => mw is { } v ? $"{v / 1000.0:0.#} W (set by the trial)" : "the daily profile's";
    private static string Limits(uint? powerMw, uint? nvvddMa, uint? msvddMa, int? msvddRelUv, int? msvddVminUv,
        string? fanCurve, uint? fanPct, int? memoryKhz, int? xbarKhz, int? xbarVdemandUv) =>
        $"power limit {Wall(powerMw)}" + (nvvddMa is { } n ? $", NVVDD current limit {n / 1000.0:0.#} A" : "") + (msvddMa is { } m ? $", MSVDD current limit {m / 1000.0:0.#} A" : "")
        + (msvddRelUv is { } r ? $", MSVDD REL offset {r / 1000.0:0.#} mV" : "") + (msvddVminUv is { } v ? $", MSVDD VMIN offset {v / 1000.0:0.#} mV" : "")
        + (fanCurve is { } fc ? $", fan curve {fc}" : "") + (fanPct is { } fp ? $", fans held at {fp} %" : "")
        + (memoryKhz is { } mk ? $", memory offset {mk / 1000.0:+0.#;-0.#;0} MHz" : "")
        + (xbarKhz is { } xk ? $", XBAR offset {xk / 1000.0:+0.#;-0.#;0} MHz" : "") + (xbarVdemandUv is { } xv ? $", XBAR voltage demand {xv / 1000.0:+0.#;-0.#;0} mV" : "");
    private static string Limits(DataPoint p) => Limits(p.PowerMw, p.NvvddMa, p.MsvddMa, p.MsvddRelUv, p.MsvddVminUv, p.FanCurve, p.FanPct, p.MemoryKhz, p.XbarKhz, p.XbarVdemandUv);
    private static string Limits(ExperimentTrial t) => Limits(t.PowerMw, t.NvvddMa, t.MsvddMa, t.MsvddRelUv, t.MsvddVminUv, t.FanCurve, t.FanPct, t.MemoryKhz, t.XbarKhz, t.XbarVdemandUv);

    /// <summary>A current-limit write: its setter is off until a request enables it, as the V/F points' is.</summary>
    private static BrokerRequest LimitWrite(IGpuTarget target, string key, string value) =>
        new(BrokerRequest.OpWrite, target.StableId, Kind: CurrentLimitWrites.Kind, Key: key, Value: value, EnableSlots: [nameof(Slot.PowerPolicySetControl)]);

    /// <summary>A cell only a trial sets and no daily profile holds: a rail's current limit, a fixed fan duty.</summary>
    private static bool TrialOnly(OwnedItem i) => i.Kind is CurrentLimitWrites.Kind or FanControl.Kind;

    /// <summary>The trial-only cells this broker holds.</summary>
    private static IEnumerable<OwnedItem> HeldTrialCells(IGpuTarget target) => target.Journal.Inspect().Items.Where(TrialOnly);

    /// <summary>What goes back to its original before a profile goes on, in write order. The whole profile going on: every
    /// cell held that the profile does not write itself (whatever a trial set beside it: a curve, a wall, a fan curve,
    /// the memory offset, XBAR, MSVDD), so the card is the profile and stock everywhere else. Part of it (the wall alone,
    /// right after a driver reset or at a keep): only the trial-only cells, a wall and MSVDD's offsets the profile does
    /// not hold, nothing that needs an idle GPU.</summary>
    /// <summary>"preset" with a saved profile's name: it becomes the daily profile and goes on, as a preset does.</summary>
    private BrokerResponse LoadSaved(IGpuTarget target, string? value, DailyProfile? profile, string path)
    {
        var file = SavedProfiles.PathBeside(target.Journal.Path);
        if (SavedProfiles.Find(file, value) is not var (name, saved))
        {
            string known = string.Join(", ", [.. Presets.All.Select(p => p.Name), .. SavedProfiles.All(file).Select(p => $"'{p.Name}'")]);
            return Fail($"no preset or saved profile named '{value}' (known: {known})", BrokerCode.CannotWrite);
        }
        CloseTrial(target, Experiment.Survived, $"saved profile '{name}' went on");
        var loaded = new DailyProfile(DailyProfile.InWriteOrder(saved.Writes), _host.Now, History: profile?.History)
            .Noted(_host.Now, $"saved profile '{name}' loaded");
        var r = ApplyProfile(target, loaded, path);
        if (!r.Ok) return NotLoaded(target, $"saved profile '{name}'", r, profile, path);
        loaded.Save(path);
        Log(target, $"Saved profile loaded · {name}", [
            .. (profile?.Writes ?? []).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
            .. Writes(r)]);
        return r;
    }

    /// <summary>A preset or saved profile that did not all go on (a write this card refuses, e.g. a wall above its board
    /// maximum) does not become the daily profile: the daily profile stays as it was and goes back on, so the card is
    /// never left holding part of a profile nothing keeps. The answer carries both runs' writes.</summary>
    private BrokerResponse NotLoaded(IGpuTarget target, string what, BrokerResponse failed, DailyProfile? profile, string path)
    {
        var back = ApplyProfile(target, profile, path);
        Log(target, $"Not loaded · {what}", [
            $"Reason: {failed.Error}",
            .. Writes(failed),
            back.Ok ? "The daily profile stays as it was and went back on" : $"The daily profile stays as it was, but it did not all go back on: {back.Error}",
            .. Writes(back)]);
        string error = $"{failed.Error}; {what} was not loaded: the daily profile stays as it was" +
            (back.Ok ? " and is back on the card" : $", but it did not all go back on ({back.Error}): read the card");
        return new BrokerResponse(false, error, Element(new { success = false, path, profile, applied = (JsonElement[])[.. AppliedOf(failed), .. AppliedOf(back)] }), failed.Code);
    }

    /// <summary>The write transactions an answer carries (its "applied" list), none for a refusal.</summary>
    private static JsonElement[] AppliedOf(BrokerResponse r) =>
        r.Result is { } res && res.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray()] : [];

    /// <summary>Each write that landed, put back to the value it replaced, last first: a write that changed nothing is
    /// skipped. Every put-back is an ordinary write, so the journal releases a cell that is back at its original.</summary>
    private List<BrokerResponse> PutBackLanded(IGpuTarget target, IReadOnlyList<BrokerResponse> written)
    {
        var undone = new List<BrokerResponse>();
        foreach (var w in written.Where(x => x.Ok).Reverse())
            foreach (var item in AppliedOf(w).Reverse())
            {
                string? Get(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (Get("outcome") != nameof(TxOutcome.Applied) || Get("kind") is not { } kind || Get("key") is not { } key || Get("before") is not { } before || before == Get("after"))
                    continue;
                undone.Add(Dispatch(kind == CurrentLimitWrites.Kind ? LimitWrite(target, key, before)
                    : new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: kind, Key: key, Value: before,
                        EnableSlots: kind == VfWrites.Kind ? [nameof(Slot.ClockClientClkVfPointsSetControl)] : null)));
            }
        return undone;
    }

    private static IEnumerable<OwnedItem> CellsToPutBack(IGpuTarget target, DailyProfile? profile, bool whole)
    {
        bool Written(OwnedItem i) => profile is { Suspended: false } p && p.Writes.Any(w => w.Kind == i.Kind && w.Key == i.Key);
        return target.Journal.Inspect().Items
            .Where(i => TrialOnly(i) || !Written(i) && (whole || i.Kind == PowerCap.Kind || i.Kind == RailWrites.Kind && i.Key == RailWrites.Key(Rail.Msvdd)))
            .OrderBy(i => DailyProfile.Rank(i.Kind));
    }

    /// <summary>The write that puts a held cell back to its original, with the setter it needs enabled.</summary>
    private static BrokerRequest PutBack(IGpuTarget target, OwnedItem held) => held.Kind == CurrentLimitWrites.Kind
        ? LimitWrite(target, held.Key, held.Original)
        : new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: held.Kind, Key: held.Key, Value: held.Original,
            EnableSlots: held.Kind == VfWrites.Kind ? [nameof(Slot.ClockClientClkVfPointsSetControl)] : null);

    /// <summary>
    /// A P0 offset write (pstates20) resets every V/F point offset, so its path refuses one while any is set. When this
    /// write changes the P0 offset and the curve on the card is one this broker holds, the curve goes to 0 first; the
    /// caller writes it back after it in the same run (the trial's curve, the profile's, or the held original: a P0
    /// write ranks before the curve). Null when nothing needs setting aside. A curve this broker does not hold stays,
    /// and the path's refusal stands: it is not ours to wipe.
    /// </summary>
    private BrokerResponse? CurveAside(IGpuTarget target, string key, string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var khz)) return null;
        var domain = key == ClockOffsets.Key(ClockDomain.Memory) ? ClockDomain.Memory : ClockDomain.Core;
        if (Pstates20Reader.Read(_hw, target.Session).P0(domain)?.DeltaKhz == khz) return null;   // no change: nothing is reset
        var offsets = VfReader.Read(_hw, target.Session).Offsets();
        if (offsets.All(o => o == 0)) return null;
        if (!target.Journal.Inspect().Items.Any(i => i.Kind == VfWrites.Kind)) return null;
        return Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: VfWrites.Kind, Key: VfWrites.CoreKey,
            Value: VfOffsets.Encode(new int[offsets.Length]), EnableSlots: [nameof(Slot.ClockClientClkVfPointsSetControl)]));
    }

    /// <summary>Each write of a profile apply, a write op or a recovery, as one line: cell, before → after, outcome.</summary>
    private static IEnumerable<string> Writes(BrokerResponse r)
    {
        if (r.Result is { } res)
            foreach (var name in new[] { "applied", "items" })
                if (res.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var w in arr.EnumerateArray())
                    {
                        string Get(string a, string b) =>
                            w.TryGetProperty(a, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! :
                            w.TryGetProperty(b, out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "?";
                        yield return $"{Get("kind", "Kind")}/{Get("key", "Key")}: {Get("before", "Current")} → {Get("after", "Original")} ({Get("outcome", "action")})";
                    }
        if (!r.Ok) yield return $"Failed: {r.Error}";
    }

    /// <summary>Closes the trial in force, if any, with this outcome and returns its data point; null when no trial was in
    /// force. A trial already marked stable is not recorded a second time, unless it crashed. The session stays open.</summary>
    private DataPoint? CloseTrial(IGpuTarget target, string outcome, string? note = null, uint? crashUv = null)
    {
        lock (_experiment)
        {
            var path = Experiment.PathBeside(target.Journal.Path);
            if (Experiment.Load(path) is not { Trial: { } trial } exp) return null;
            var point = trial.Marked && outcome != Experiment.Crash ? null : exp.Point(outcome, _host.Now, note, crashUv);
            if (point != null) Experiment.Append(Experiment.DataPathBeside(target.Journal.Path), point);
            (exp with { Trial = null }).Save(path);
            return point ?? exp.Point(outcome, _host.Now, note, crashUv);
        }
    }

    private static JsonElement Args(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException e) { throw new ArgumentException($"the value is not JSON: {e.Message}"); }
    }

    private static string? Text(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static uint? UInt(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : null;

    /// <summary>Each write of a profile that is not suspended (those <paramref name="only"/> picks, all by default), in its
    /// order, through the ordinary write op; stops at the first that fails.</summary>
    private BrokerResponse ApplyProfile(IGpuTarget target, DailyProfile? profile, string path, Func<ProfileWrite, bool>? only = null)
    {
        var applied = new List<JsonElement>();
        // A trial's current limits and fixed fans are never part of a profile, nor anything else a trial set unless the
        // profile writes it too: whatever puts the whole profile on puts them back first.
        foreach (var held in CellsToPutBack(target, profile, whole: only == null).ToList())
        {
            if (held.Kind == ClockOffsets.Kind && CurveAside(target, held.Key, held.Original) is { } aside)
            {
                if (aside.Result is { } ar && ar.TryGetProperty("applied", out var aa)) applied.Add(aa[0]);
                if (!aside.Ok) return new BrokerResponse(false, aside.Error ?? "the curve could not be set aside for a P0 write", Element(new { success = false, path, profile, applied }), aside.Code);
            }
            var r = Dispatch(PutBack(target, held));
            if (r.Result is { } res && res.TryGetProperty("applied", out var a)) applied.Add(a[0]);
            if (!r.Ok) return new BrokerResponse(false, r.Error ?? $"{held.Id} did not go back", Element(new { success = false, path, profile, applied }), r.Code);
        }
        if (profile is { Suspended: false })
            foreach (var w in profile.Writes.Where(only ?? (_ => true)))
            {
                if (w.Kind == ClockOffsets.Kind && CurveAside(target, w.Key, w.Value) is { } aside)
                {
                    if (aside.Result is { } ar && ar.TryGetProperty("applied", out var aa)) applied.Add(aa[0]);
                    if (!aside.Ok) return new BrokerResponse(false, aside.Error ?? "the curve could not be set aside for a P0 write", Element(new { success = false, path, profile, applied }), aside.Code);
                }
                var r = Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: w.Kind, Key: w.Key, Value: w.Value,
                    EnableSlots: w.Kind == VfWrites.Kind ? [nameof(Slot.ClockClientClkVfPointsSetControl)] : null));
                if (r.Result is { } res && res.TryGetProperty("applied", out var a)) applied.Add(a[0]);
                if (!r.Ok) return new BrokerResponse(false, r.Error ?? $"{w.Kind}/{w.Key} did not apply", Element(new { success = false, path, profile, applied }), r.Code);
            }
        return Ok(new { success = true, path, profile, applied });
    }

    /// <summary>The general write: set kind/key to value through the registry. A limit the driver reveals only after
    /// the write is the cell's own post-write check; a veto comes back as a failed, restored transaction.</summary>
    private BrokerResponse Write(IGpuTarget target, BrokerRequest req)
    {
        if (!_canWrite) return Fail(NeedsAdmin, BrokerCode.NeedsAdmin);
        var path = WriteRegistry.Find(req.Kind);
        if (path == null) return Fail($"'{req.Kind}' is not a write path (one of: {string.Join(", ", WriteRegistry.Kinds)})", BrokerCode.NotAPath);
        if (req.Key == null || req.Value == null) return Fail("a write names a kind, a key and a value", BrokerCode.BadRequest);
        var hw = Client(req.EnableSlots);
        var writer = new OwnedWriter(hw, target.Journal, target.Sentinel, _host);

        // The origin a path may judge the request against is the journal's, never the request's: what the hardware
        // held before this program first touched the cell (a lowered stock value may be put back, even above the
        // ceiling). Asked before planning, with the same adoption rule the write itself applies.
        var original = writer.AdoptedOriginal(path.Kind, req.Key);
        var p = path.Prepare(hw, target.Session, req.Key, req.Value, new WriteOptions(req.AllowCreate, Original: original));
        if (p.Plan == null) return Fail(p.CannotWrite ?? $"{p.Cell.Id} cannot be written from '{req.Value}'", BrokerCode.CannotWrite);

        var r = writer.Apply(target.Session, p.Cell, p.Plan, req.TakeOver, req.Value);
        return new BrokerResponse(r.Tx.Succeeded, Result: Element(new
        {
            success = r.Tx.Succeeded,
            applied = new[]
            {
                new
                {
                    kind = path.Kind, key = p.Cell.Key, requested = req.Value, unit = p.Cell.Unit,
                    before = r.Before, after = r.After,
                    outcome = r.Tx.Outcome.ToString(), restore = r.Tx.Restore.ToString(), detail = r.Tx.Detail, veto = r.Tx.Veto,
                    driver = r.Tx.Driver?.ToString(), ownership = r.Ownership, sentinel_cleared = r.SentinelCleared, log = r.Tx.Log,
                    // The journal's view, as values a program can switch on; `ownership` is the same for a person.
                    hold = r.Hold.ToString(), original = r.Original, state_known = r.StateKnown,
                    checked_after_write = p.Cell.Spec.CheckAfterWrite != null,
                    origin = original,
                    enabled_slots = req.EnableSlots,
                },
            },
        }));
    }

    /// <summary>A setter's equal-value drill, for the paths that define one.</summary>
    private BrokerResponse Prove(IGpuTarget target, BrokerRequest req)
    {
        if (!_canWrite) return Fail(NeedsAdmin, BrokerCode.NeedsAdmin);
        var path = WriteRegistry.Find(req.Kind);
        if (path == null) return Fail($"'{req.Kind}' is not a write path (one of: {string.Join(", ", WriteRegistry.Kinds)})", BrokerCode.NotAPath);
        if (path.ProveEqual == null) return Fail($"'{path.Kind}' defines no equal-value proof; its setter is proven by its first write", BrokerCode.NoProof);
        if (req.Key == null) return Fail("a proof names a kind and a key", BrokerCode.BadRequest);
        var hw = Client(req.EnableSlots);
        var p = path.ProveEqual(hw, target.Session, target.Sentinel, req.Key);
        return new BrokerResponse(p.Proven, Result: Element(new
        {
            success = p.Proven, kind = path.Kind, key = req.Key, written = p.Written, driver = p.Driver?.ToString(),
            first_difference = p.FirstDifference, before = p.Before, after = p.After, enabled_slots = req.EnableSlots,
        }));
    }

    private BrokerResponse Power(IGpuTarget target, BrokerRequest req)
    {
        if (!_canWrite) return Fail(NeedsAdmin, BrokerCode.NeedsAdmin);
        var kind = ParseKind(req.Kind);
        var pc = new PowerCap(_hw, target.Journal, target.Sentinel, _host);
        var r = pc.Apply(target.Session, kind, req.Mw, req.TakeOver, req.AllowCreate);
        return new BrokerResponse(r.Succeeded, Result: Element(new
        {
            success = r.Succeeded,
            applied = new[]
            {
                new
                {
                    request = PowerCap.Key(kind), command = $"0x{(uint)kind:x}", requested_mw = req.Mw,
                    before_mw = r.BeforeMw, after_mw = r.AfterMw,
                    outcome = r.Tx.Outcome.ToString(), restore = r.Tx.Restore.ToString(), detail = r.Tx.Detail, veto = r.Tx.Veto,
                    driver = r.Tx.Driver?.ToString(), ownership = r.Ownership, sentinel_cleared = r.SentinelCleared, log = r.Tx.Log,
                },
            },
        }));
    }

    private BrokerResponse Clock(IGpuTarget target, BrokerRequest req)
    {
        if (!_canWrite) return Fail(NeedsAdmin, BrokerCode.NeedsAdmin);
        var domain = ParseDomain(req.Domain);
        var co = new ClockOffsets(_hw, target.Journal, target.Sentinel, _host);
        var r = co.Apply(target.Session, domain, req.Khz, req.TakeOver);
        return new BrokerResponse(r.Succeeded, Result: Element(new
        {
            success = r.Succeeded,
            applied = new[]
            {
                new
                {
                    domain = domain.ToString().ToLowerInvariant(), requested_khz = req.Khz,
                    before_khz = r.BeforeKhz, after_khz = r.AfterKhz, freq_before_khz = r.FreqBeforeKhz, freq_after_khz = r.FreqAfterKhz,
                    outcome = r.Tx.Outcome.ToString(), restore = r.Tx.Restore.ToString(), detail = r.Tx.Detail, veto = r.Tx.Veto,
                    driver = r.Tx.Driver?.ToString(), ownership = r.Ownership, sentinel_cleared = r.SentinelCleared, log = r.Tx.Log,
                },
            },
        }));
    }

    /// <summary>
    /// The client for one request: the shared one, or a copy whose policy set has the named setters switched on.
    /// The policy refuses a setter whose rule set is not completely decoded, and a forbidden one, so a request can
    /// open the drill gate of a decoded setter and nothing else.
    /// </summary>
    private NvClient Client(string[]? enable)
    {
        if (enable is not { Length: > 0 }) return _hw;
        var policies = _hw.Policies;
        foreach (var name in enable)
        {
            if (!Enum.TryParse<Slot>(name, ignoreCase: true, out var slot) || !Enum.IsDefined(slot))
                throw new ArgumentException($"'{name}' is not a slot name");
            policies = policies.WithEnabled(slot);
        }
        return new NvClient(_hw.Api, policies);
    }

    /// <summary>
    /// The client recovery gives back through: every setter whose rule set is complete is open. A journal entry exists
    /// only because a write through its setter landed, so that gate was open then; the give-back writes the value the
    /// card held before, checked by the same rules. A setter still off for want of its live drill must not strand an
    /// original on the card. Incomplete and forbidden setters stay shut.
    /// </summary>
    private NvClient RecoveryClient()
    {
        var policies = _hw.Policies;
        foreach (var p in _hw.Policies.All.Where(p => p.State == PolicyState.Disabled && p.Complete))
            policies = policies.WithEnabled(p.Slot);
        return new NvClient(_hw.Api, policies);
    }

    private const string NeedsAdmin = "writing to the GPU needs the elevated broker (NVAPI returns -137 otherwise); install the broker or run from an elevated terminal";

    private static PowerRequestKind ParseKind(string? s) => s?.ToLowerInvariant() switch
    {
        "ordinary" or "fe" or "power-limit" => PowerRequestKind.Ordinary,
        "additional" or "f8" or "power-cap" => PowerRequestKind.Additional,
        _ => throw new ArgumentException($"power kind must be 'ordinary'/'fe' or 'additional'/'f8', not '{s}'"),
    };

    private static ClockDomain ParseDomain(string? s) => s?.ToLowerInvariant() switch
    {
        "core" => ClockDomain.Core,
        "memory" or "mem" => ClockDomain.Memory,
        _ => throw new ArgumentException($"clock domain must be 'core' or 'memory', not '{s}'"),
    };

    private static JsonElement Element(object o) => JsonSerializer.SerializeToElement(o, BrokerResponse.Json);
    private static BrokerResponse Ok(object payload) => new(true, Result: Element(payload));
    /// <summary>A refusal before the op ran: the sentence for a person, the code for a program.</summary>
    /// <summary>A frame-rate capture for the GPU's folder (fps\ beside its tuning log); not a write.</summary>
    private BrokerResponse FpsOp(IGpuTarget target, BrokerRequest req)
    {
        if (_frames == null) return Fail("this service cannot capture frame rates (it is not the elevated broker)", BrokerCode.CannotWrite);
        switch (req.Kind)
        {
            case "start":
            {
                var a = Args(req.Value);
                int seconds = a.TryGetProperty("seconds", out var sv) && sv.TryGetInt32(out var n) ? n : throw new ArgumentException("start needs seconds");
                string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target.Journal.Path))!, "fps");
                return Ok(_frames.Start(dir, seconds, Text(a, "process")));
            }
            case "stop":
                return Ok(_frames.Stop());
            default:
                return Fail($"unknown fps request '{req.Kind}' (start, stop)", BrokerCode.BadRequest);
        }
    }

    private static BrokerResponse Fail(string error, string code) => new(false, error, Code: code);
}
