using System.Text.Json;
using Clocktail.Domain.Boost;
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
    /// <summary>The VBIOS the card runs, null when unknown: a daily profile is bound to it.</summary>
    string? Vbios => null;
    /// <summary>The driver version ("617.42"), null when unknown.</summary>
    string? Driver => null;
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
/// own lock is on disk, so two tools that each looked and saw nothing cannot both go ahead; an operation already
/// writing finishes first when the run waits for it (<see cref="Judged"/>);</item>
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

    /// <summary>Why a GPU looks hung right now (the broker's flight recorder; <see cref="FlightRecorder.HungNow"/>), or null.</summary>
    public Func<string, string?> HungReason { get; set; } = _ => null;

    /// <summary>
    /// One whole operation at a time: a profile op or a trial reads the profile and the experiment, writes the card and
    /// saves them, and the write window alone covers only the write in the middle (two "wall"s could leave the card at one
    /// and the profile at the other, both answering success). Reentrant, so an operation's own writes, which come back
    /// through <see cref="Dispatch"/> on the same thread, go straight through; taken before the write window, never inside it.
    /// </summary>
    private readonly object _operation = new();
    private readonly string _policyLifetime = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, long> _policyGenerations = new();

    // Called under _operation, including the read-only policy-scope operation. Never saved as authorization.
    private string? PolicyScope(IGpuTarget target)
    {
        var exp = Experiment.Load(Experiment.PathBeside(target.Journal.Path));
        return exp?.SessionId is not { } id || target.Session.Faulted || Volatile.Read(ref _closed) ? null
            : $"{_policyLifetime}:{target.Session.Generation}:{_policyGenerations.GetValueOrDefault(target.StableId)}:{id}";
    }

    /// <summary>The measurement lease of the request in flight (<see cref="BrokerRequest.Lease"/>), for the judge: the
    /// holder's own writes, nested ones too, pass the lock it holds.</summary>
    public static readonly AsyncLocal<string?> CurrentLease = new();

    /// <summary>
    /// The lease of the service's own safety action, the fan guard's fans back to the curve (a card at 85 °C, or one whose
    /// temperature cannot be read, under a fixed fan duty): it passes a measurement's judge.lock, as the holder's own writes
    /// do. A run then measures a card whose fans changed under it; the lab sees its trial change and judges nothing, and
    /// stress_test's crash still goes to the same settings. Since 0.3.7 stress_test holds the lock too, and its runs are what
    /// the fan guard was made for: an agent blocked in a run cannot watch the temperature.
    /// </summary>
    public const string SafetyLease = "clocktail-safety";

    /// <summary>
    /// The judge's word on the operation in flight (<see cref="JudgeNow"/>), null outside one. Refused at a write, the
    /// operation writes nothing after it, even if the lock is gone by its next write. Let through, it is admitted: a run
    /// that takes judge.lock after that and waits for it (<see cref="JudgeLock.Info.Waits"/>: "quiet" is answered once no
    /// operation is in flight) lets it finish (<see cref="Judge"/>). Until 0.3.7 a run starting in the
    /// middle of a put-back left the card part back and part on the trial, with the whole trial kept in force. A run that
    /// does not wait (StabilityLab, which looks for apply.lock before it starts; a Clocktail run from before 0.3.7) has every
    /// write after it stand down, as before: the operation stops where it is.
    /// </summary>
    private static readonly AsyncLocal<Verdict?> Judged = new();

    /// <summary>A judge's word on one operation: <see cref="Busy"/> why it may not write (null: it may);
    /// <see cref="Sure"/> once a write of it was let through.</summary>
    private sealed record Verdict(string? Busy, bool Sure = false)
    {
        public static readonly Verdict Open = new(Busy: null), Admitted = new(Busy: null, Sure: true);
    }

    /// <summary>
    /// The judge of every write and of the sentinel's second look, from the run holding judge.lock now (<paramref name="live"/>,
    /// null when none): its own writes pass (they carry its RunId as their lease: the lab writes its next candidate while it
    /// holds the card), so do the fan guard's (<see cref="SafetyLease"/>), and so does the rest of an operation let through
    /// before the run took the lock, when the run waits for it (<see cref="Judged"/>). Anything else waits: the run's reason.
    /// </summary>
    public static Func<string?> Judge(Func<JudgeLock.Info?> live) => () =>
    {
        if (live() is not { } run) return null;
        string? lease = CurrentLease.Value;
        if (run.RunId != null && run.RunId == lease || lease == SafetyLease) return null;
        if (run.Waits && Judged.Value is { Sure: true }) return null;
        return JudgeLock.Reason(run);
    };

    /// <summary>Set while a client's request is in flight (<see cref="FromClient"/>).</summary>
    private static readonly AsyncLocal<bool> ClientAsks = new();

    /// <summary>
    /// A client's request: over the pipe, or the CLI's in-process. The service's own requests are not a client's: the fan
    /// guard's "fans-back" and the lease it runs under (<see cref="SafetyLease"/>) pass a measurement's judge.lock, the
    /// recoveries after a driver reset leave the trial to the "crashed" step after them, and "crashed" is the service's
    /// record of a reset it saw (a client's crash is the experiment's "crash"), so a client sending any of these is refused.
    /// A recovery a client asks for takes the card off the trial in force, which then leaves the record (<see cref="DispatchOne"/>).
    /// </summary>
    public BrokerResponse FromClient(BrokerRequest req)
    {
        if (req.Lease == SafetyLease || req.Op == BrokerRequest.OpExperiment && req.Kind == "fans-back"
            || req.Op == BrokerRequest.OpRecover && req.Kind is BrokerRequest.RecoverAfterDriverReset or BrokerRequest.RecoverAfterDriverResetAgain
            || req.Op == BrokerRequest.OpProfile && req.Kind == "crashed")
            return Fail("that request is the service's own (its fan guard's, or its answer to a driver reset); a client cannot send it", BrokerCode.BadRequest);
        bool prior = ClientAsks.Value;
        ClientAsks.Value = true;
        try { return Dispatch(req); }
        finally { ClientAsks.Value = prior; }
    }

    public BrokerResponse Dispatch(BrokerRequest req)
    {
        string? prior = CurrentLease.Value;
        var judged = Judged.Value;
        if (req.Lease is { } lease && lease != SafetyLease) CurrentLease.Value = lease;   // SafetyLease: HandBack's to set, not a request's
        bool entered = false, top = false;
        try
        {
            if (Changes(req))
            {
                top = !Monitor.IsEntered(_operation);   // not one of an operation's own requests, which come back through here
                if (!Monitor.TryEnter(_operation, WindowWait))
                    return Fail($"another operation has held the card for over {WindowWait.TotalSeconds:0} s; nothing was done, try again", BrokerCode.WindowBusy);
                entered = true;
                if (top) Judged.Value = Verdict.Open;
            }
            return DispatchOne(req, top);
        }
        finally
        {
            if (entered) Monitor.Exit(_operation);
            CurrentLease.Value = prior;
            if (top) Judged.Value = judged;
        }
    }

    /// <summary>Why the operation in flight may not write now (a stability run measuring), or null: the judge, asked at
    /// every write; a refusal is kept for the rest of the operation, and a write let through admits it (<see cref="Judged"/>).
    /// The fan guard's put-backs pass (<see cref="SafetyLease"/>).</summary>
    private string? JudgeNow()
    {
        if (CurrentLease.Value == SafetyLease) return null;
        if (Judged.Value is { Busy: { } refused }) return refused;
        string? busy = _judgeBusyReason();
        if (Judged.Value != null) Judged.Value = busy == null ? Verdict.Admitted : new Verdict(busy);
        return busy;
    }

    /// <summary>Whether the request can change the card, the profile or the experiment: everything but the reads.</summary>
    private static bool Changes(BrokerRequest req) =>
        req.IsWrite
        || req.Op == BrokerRequest.OpProfile && req.Kind is not ("show" or "preview" or "preview-adopt" or "saved")
        || req.Op == BrokerRequest.OpExperiment && req.Kind is not ("show" or "alive");

    private BrokerResponse DispatchOne(BrokerRequest req, bool top)
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

            bool scopedTrial = req.Op == BrokerRequest.OpExperiment && req.Kind == BrokerRequest.ScopedTrial;
            if (scopedTrial && string.IsNullOrEmpty(req.ExpectedTuningScope)
                || req.ExpectedTuningScope != null && (!scopedTrial || req.ExpectedTuningScope != PolicyScope(target)))
                return Fail("The approved tuning session or recovery generation changed; call set_tuning_policy again", "consent_required");

            // Invalidate before the operation, even if recovery or session replacement fails.
            if (req.Op == BrokerRequest.OpRecover
                || req.Op == BrokerRequest.OpProfile && req.Kind is "crashed" or "after-reset"
                || req.Op == BrokerRequest.OpExperiment && req.Kind is "begin" or "end" or "crash")
                _policyGenerations[target.StableId] = _policyGenerations.GetValueOrDefault(target.StableId) + 1;

            // A recovery a client asks for on its own (the CLI's --recover, a write session given back after a failed step, a
            // 0.3.6 client's stock: "off", then a recovery) takes the card off any trial in force, so the trial leaves the
            // record once it landed, as every other way off the card does (Leave). Not one inside an operation (that
            // operation's Leave is around it), nor the service's own: after a driver reset "crashed" records the trial's
            // crash, and at start the leftovers' recovery may come before a reset of the hang is in the System log, so the
            // trial waits for the idle-gated daily profile. Until 0.3.7 a recovery left the trial on record over a card at stock.
            if (top && ClientAsks.Value && req.Op == BrokerRequest.OpRecover)
                return Leave(target, Experiment.Survived, "the card went back to stock", _ => { var r = Dispatch(req); return (r, r); }).Answer;

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
            if (Volatile.Read(ref _closed)) return Fail(ClosedError, BrokerCode.WindowBusy);
            if (!_writeWindow.Wait(WindowWait))
                return Fail($"another write has held the window for over {WindowWait.TotalSeconds:0} s; nothing was written, try again", BrokerCode.WindowBusy);
            try
            {
                if (JudgeNow() is { } busy) return Fail(JudgeBusy(busy), BrokerCode.JudgeBusy);
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
            // The lock appeared after the first look; the sentinel saw it, nothing was written, and nothing more of the operation will be.
            if (Judged.Value != null) Judged.Value = new Verdict(e.Reason);
            return Fail(JudgeBusy(e.Reason), BrokerCode.JudgeBusy);
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
    public bool Close(TimeSpan wait)
    {
        Volatile.Write(ref _closed, true);
        return _writeWindow.Wait(wait);
    }

    /// <summary>Set by <see cref="Close"/>: a write after it answers at once instead of waiting out <see cref="WindowWait"/>.</summary>
    private bool _closed;

    private const string ClosedError = "the service is handing over to a fresh process after a driver reset; nothing was written, try again in a few seconds";

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
        lock (_settle)
        {
            var crash = CrashLog.SettleReboot(target.Journal, target.Sentinel, _host, uncleanShutdown);
            // What is left of a record from before a clean reboot describes a state the reboot reset: dropped, as recovery
            // drops it, before anything puts its cells "back". Until 0.4.0 the start's apply did: another tool's old value,
            // held as an original from the boot before, went back over a card at stock and was then held as Clocktail's.
            if (target.Journal.Inspect().Reason == RecoveryReason.Rebooted) target.Journal.Adopt();
            return crash;
        }
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
        if (r.Clean) _givenBack[target.StableId] = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial?.At;   // off the card now
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
        Settle(target);   // a record from before the boot is settled before anything here reads or puts back its cells
        var path = DailyProfile.PathBeside(target.Journal.Path);
        var profile = DailyProfile.Load(path);
        switch (req.Kind)
        {
            case "show":
                return Ok(new { success = true, path, profile, card = new { vbios = target.Vbios, driver = target.Driver } });
            case "preview":
            case "preview-adopt":
            {
                // What keep, or a preset or saved profile (Value: its name), would make the daily profile, against the daily
                // profile and the card: nothing is written or saved. The agent shows it before asking. "preview-adopt" is
                // what "adopt" would keep (0.4.0).
                bool adopt = req.Kind == "preview-adopt";
                var (writes, left, _) = req.Value is { Length: > 0 } name && !adopt ? (PresetWrites(target, name), [], []) : KeepWrites(target, profile, adopt);
                var cardNow = CardValues(target, writes);
                return Ok(new
                {
                    success = true,
                    source = req.Value is { Length: > 0 } n && !adopt ? $"'{n}'" : adopt ? "keep_as_daily (adopt)" : "keep_as_daily",
                    changes = DailyProfile.InWriteOrder(writes).Select(w => new
                    {
                        kind = w.Kind, key = w.Key, becomes = w.Value,
                        daily_now = profile?.Writes.FirstOrDefault(d => d.Kind == w.Kind && d.Key == w.Key)?.Value,
                        card_now = cardNow.GetValueOrDefault($"{w.Kind}/{w.Key}"),
                    }).Where(c => c.becomes != c.daily_now),
                    dropped = profile?.Writes.Where(d => !writes.Any(w => w.Kind == d.Kind && w.Key == d.Key)).Select(d => $"{d.Kind}/{d.Key} = {d.Value}"),
                    not_kept = left,
                });
            }
            case "keep":
            case "adopt":
            {
                // "adopt" (0.4.0) keeps what another tool left on the card too (FactoryStock: the curve, the P0 and clock-domain
                // offsets, the voltage demands, the boost), so the service puts it back after every boot and the other tool
                // can be closed.
                bool adopt = req.Kind == "adopt";
                var (writes, _, adopted) = KeepWrites(target, profile, adopt);
                var inForce = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial;
                bool trialWall = inForce?.PowerMw != null;
                // A trial that crashed but is still on the card (its put-back held off) is never what a keep takes from it.
                if (inForce?.Ending == Experiment.Crash)
                    return Fail($"the trial on the card crashed ({inForce.Change}) and waits for the card to be free to go back: a crash is not kept; back_to_daily", BrokerCode.CannotWrite);
                if (writes.Count == 0)
                    return Fail(adopt ? "nothing on the card differs from stock or from the daily profile; nothing to keep" : "nothing this broker holds differs from stock; nothing to keep", BrokerCode.CannotWrite);
                var trialKept = CloseTrial(target, Experiment.Stable, "kept as the daily profile");
                string listed = string.Join("; ", writes.Select(w => $"{w.Kind}/{w.Key}={w.Value}"));
                // What was adopted before stays adopted (a trial over it records it as its original); this keep's own adds to it.
                IReadOnlyList<ProfileWrite> adoptedAll = [.. (profile?.Adopted ?? []).Where(a => !adopted.Any(n => n.Kind == a.Kind && n.Key == a.Key)), .. adopted];
                var kept = new DailyProfile(DailyProfile.InWriteOrder(writes), _host.Now, History: profile?.History, Adopted: adoptedAll.Count > 0 ? adoptedAll : null)
                    .On(target.Vbios, target.Driver)
                    .Noted(_host.Now, req.Value is { } why ? $"kept: {listed} ({why})" : $"kept: {listed}");
                kept.Save(path);
                // A trial's lowered wall or current limit is still on the card: the kept wall goes back on now (and the
                // limits back to their defaults), not at the next start.
                var wall = trialKept?.PowerMw != null || HeldTrialCells(target).Any()
                    ? ApplyProfile(target, kept, path, only: w => w.Kind == PowerCap.Kind) : null;
                Log(target, "Kept as the new daily profile", [
                    req.Value is { } note ? $"Note: {note}" : null,
                    trialKept != null ? $"From trial: {trialKept.Game}, {trialKept.Change} ({Limits(trialKept)})" : null,
                    adopted.Count > 0 ? $"Adopted from another tool (on the card, never written by Clocktail): {string.Join(", ", adopted.Select(w => $"{w.Kind}/{w.Key}"))}" : null,
                    profile != null
                        ? "Taken from the card: what the trials tuned (the curves, offsets, rails, voltage demands, the clock ratio and a clock cap); the power limit stays the old daily profile's; current limits, fixed fan duties, the F8 request, clock floors, point locks and Boost Lock are not kept"
                        : trialWall ? "A trial's power limit, current limits, fixed fan duties, the F8 request, clock floors, point locks and Boost Lock are not kept" : "Current limits, fixed fan duties, the F8 request, clock floors, point locks and Boost Lock are not kept",
                    .. kept.Writes.Select(w => $"New: {w.Kind}/{w.Key} = {w.Value}"),
                    .. (profile?.Writes ?? []).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
                    .. wall != null ? ["Daily power limit put back:", .. Writes(wall)] : Array.Empty<string>(),
                    wall is { Ok: false } ? $"The card did not finish switching to it: {wall.Error}" : null]);
                // Saved is not switched: a trial's wall, limits or fans that did not go back are still on the card, and the
                // answer says so instead of success.
                if (wall is { Ok: false })
                    return new BrokerResponse(false,
                        $"the daily profile is saved, but the card did not finish switching to it: {wall.Error}; back_to_daily (or load_daily) tries again",
                        Element(new { success = false, saved = true, switched = false, path, profile = kept, pending = wall.Result }), wall.Code ?? BrokerCode.Failed);
                return Ok(new { success = true, saved = true, switched = true, path, profile = kept });
            }
            case "off":
                // Suspends the daily profile and writes nothing (the person's CLI: stock at the next start or reset). The card
                // is as it was, so a trial in force stays in force; until 0.3.7 "off" closed it while the card still ran it.
                if (profile == null) return Ok(new { success = true, path, profile });
                profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                profile.Save(path);
                Log(target, "Daily profile suspended", ["The card is at stock from the next start or driver reset, until the daily profile is loaded or kept again"]);
                return Ok(new { success = true, path, profile });
            case "stock":
            {
                // The card to stock now (the agent's stock, an uninstall): the daily profile suspended so nothing puts it back,
                // and what this broker holds back to its original in the same operation, so the trial in force leaves the
                // record once that landed (Leave). Until 0.3.7 stock was "off", which closed the trial, then a recovery of
                // its own: a recovery a stability run held off left the trial on the card and off the record.
                if (profile != null)
                {
                    profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                    profile.Save(path);
                    Log(target, "Daily profile suspended", ["The card is at stock until the daily profile is loaded or kept again"]);
                }
                var r = Leave(target, Experiment.Survived, "the card went back to stock", _ =>
                {
                    var rec = Dispatch(new BrokerRequest(BrokerRequest.OpRecover, target.StableId));
                    return (rec, rec);
                }).Answer;
                return new BrokerResponse(r.Ok, r.Error, Element(new { success = r.Ok, path, profile, recover = r.Result }), r.Code);
            }
            case "factory":
            {
                // The card to NVIDIA's own settings (the agent's stock since 0.4.0): what "stock" does, then every setting
                // another tool left (FactoryStock) written to its default and let go of (ToFactory). "stock" alone, the
                // uninstall's, still gives back only what Clocktail changed: the card as Clocktail found it.
                if (profile != null && !profile.Suspended)
                {
                    profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                    profile.Save(path);
                    Log(target, "Daily profile suspended", ["The card is at stock until the daily profile is loaded or kept again"]);
                }
                var left = Leave(target, Experiment.Survived, "the card went back to stock", _ =>
                {
                    var rec = Dispatch(new BrokerRequest(BrokerRequest.OpRecover, target.StableId));
                    // The trial leaves the record once the give-back landed, whatever the other tool's settings do after it.
                    return (rec.Ok ? ToFactory(target, rec) : rec, rec);
                });
                var r = left.Answer;
                List<string?> Strings(string name) => r.Result is { } res && res.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                    ? [.. a.EnumerateArray().Select(x => x.GetString())] : [];
                // Only ToFactory's answer carries not_cleared: without it the give-back did not land and nothing else was looked at.
                bool surveyed = r.Result is { } sr && sr.TryGetProperty("not_cleared", out _);
                var cleared = Strings("cleared");
                var notCleared = Strings("not_cleared");
                Log(target, r.Ok ? "Card put to stock" : "Card not all put to stock", [
                    !surveyed ? "The give-back did not land: nothing another tool set was looked at"
                    : cleared.Count > 0 ? "Cleared what another tool had set:" : notCleared.Count == 0 ? "Nothing another tool had set was on the card" : null,
                    .. cleared.Select(c => $"- {c}"),
                    .. notCleared.Select(c => $"Not cleared: {c}"),
                    .. Writes(r),
                    profile != null ? "The daily profile is suspended until load_daily" : null]);
                return new BrokerResponse(r.Ok, r.Error, Element(new
                {
                    success = r.Ok, path, profile,
                    items = r.Result is { } a && a.TryGetProperty("items", out var its) ? its : (JsonElement?)null,
                    applied = r.Result is { } b && b.TryGetProperty("applied", out var ap) ? ap : (JsonElement?)null,
                    cleared,
                    not_cleared = r.Result is { } c && c.TryGetProperty("not_cleared", out var nc) ? nc : (JsonElement?)null,
                }), r.Code);
            }
            case "apply":
            {
                // Value: why, for the log ("the broker started", an agent's back_to_daily).
                var r = Leave(target, Experiment.Survived, "the daily profile went back on", _ =>
                {
                    var a = ApplyProfile(target, profile, path);
                    return (a, a);
                }).Answer;
                Log(target, "Daily profile applied",[req.Value is { } why ? $"Reason: {why}" : null, profile is { Suspended: true } ? "Daily profile suspended: nothing written" : null, .. Writes(r)]);
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
                profile = profile.Noted(_host.Now, "loaded by request").On(target.Vbios, target.Driver) with { Suspended = false };
                profile.Save(path);
                var r = Leave(target, Experiment.Survived, "the daily profile went back on", _ =>
                {
                    var a = ApplyProfile(target, profile, path);
                    return (a, a);
                }).Answer;
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
                var loaded = new DailyProfile(DailyProfile.InWriteOrder(preset.Writes), _host.Now, History: profile?.History).On(target.Vbios, target.Driver)
                    .Noted(_host.Now, $"preset '{preset.Name}' loaded");
                var r = Leave(target, Experiment.Survived, $"preset '{preset.Name}' went on", _ => Loading(target, $"preset '{preset.Name}'", loaded, profile, path)).Answer;
                if (!r.Ok) return r;
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
                    _host.Now, profile?.Suspended ?? false, profile?.History, profile != null ? profile.Vbios : target.Vbios, profile != null ? profile.Driver : target.Driver).Noted(_host.Now, $"daily power limit {mw / 1000.0:0.#} W");
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
                // the kept limit are still a safe state. A trial in force now was written after the reset's give-back (the
                // crash record kept it, CrashedAt), and nothing of it is touched: under a wall of its own nothing is written;
                // under none it runs under the daily wall, as its record says, which goes on as the profile's power run puts
                // it (bound to its VBIOS, the firmware's board limit made to follow) without putting the trial's own cells
                // back (its fan duty, its current limits).
                if (profile is null or { Suspended: true }) return Ok(new { success = true, path, profile, applied = Array.Empty<object>() });
                var inForce = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial;
                if (inForce?.PowerMw != null)
                    return Ok(new { success = true, skipped = "a trial began after the crash, under a wall of its own: the agent running it decides", profile });
                var r = ApplyProfile(target, profile, path, only: w => w.Kind == PowerCap.Kind, trialStays: inForce != null);
                Log(target, inForce != null ? "Driver reset: the daily power limit put back under the trial in force" : "Driver reset: power limit put back first", Writes(r));
                return r;
            }
            case "crashed":
            {
                // A crash: a driver reset (the held cells already given back), or the machine going down while it held
                // cells. Recorded at once, before anything waits for an idle GPU: the trial that was on the card when the GPU
                // stopped answering is the one that crashed (a data point), even one taken off the record since (CrashedAt;
                // without the flight recorder's time, the trial in force), and one the agent writes after this is not blamed
                // for it. Nothing is written here; the daily profile goes back on unchanged in "after-reset". No step-down:
                // the person is at the machine and the agent adjusts from there (the user, 2026-10-19).
                uint? uv = req.Uv > 0 ? req.Uv : null;
                List<string> how = [];
                var crash = req.HungAt is { } stopped
                    ? CrashedAt(target, req.Value, uv, req.AnsweredAt ?? stopped, stopped, out how)
                    : CloseTrial(target, Experiment.Crash, req.Value, uv);
                if (profile != null)
                {
                    profile = profile.Noted(_host.Now, crash != null
                        ? $"{req.Value ?? "driver reset"} during an experiment trial ({crash.Change}): a data point; put back unchanged"
                        : $"{req.Value ?? "driver reset"}: put back unchanged");
                    profile.Save(path);
                }
                Log(target, crash != null ? $"Crash · during trial · {crash.Game}" : "Crash · daily profile", [
                    $"Evidence: {req.Value ?? "driver reset"}",
                    req.HungAt is { } hung ? $"The GPU stopped answering at {When(hung)}" : null,
                    .. how,
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
                return Fail($"profile needs show, preview, preview-adopt, keep, adopt, off, stock, factory, apply, resume, load, preset, save, saved, forget, wall, apply-power, crashed or after-reset, not '{req.Kind}'", BrokerCode.BadRequest);
        }
    }

    /// <summary>Live tuning in a game: see <see cref="BrokerRequest.OpExperiment"/> and <see cref="Experiment"/>.</summary>
    private BrokerResponse ExperimentOp(IGpuTarget target, BrokerRequest req)
    {
        string path = Experiment.PathBeside(target.Journal.Path), data = Experiment.DataPathBeside(target.Journal.Path);
        switch (req.Kind)
        {
            case "policy-scope":
            {
                var exp = Experiment.Load(path);
                return Ok(new { success = true, gpu = target.StableId, session = exp?.SessionId,
                    game = exp?.Game, generation = PolicyScope(target) });
            }
            case "show":
                return Ok(new { success = true, path, data, experiment = Experiment.Load(path), points = Experiment.ReadLast(data, 20) });
            case "begin":
            {
                var a = Args(req.Value);
                string game = Text(a, "game") ?? throw new ArgumentException("begin needs a game");
                // A trial still in force from the session before ends as "end" ends it: its settings go back with the daily
                // profile, so the new session's trials run on, and are recorded under, the daily wall and limits it says.
                // The new session does not begin over a card that is not back (as in 0.3.6): one held busy keeps the trial in
                // force; one refused for another reason lets the record go, and the card may still hold part of it.
                DataPoint? closed = null;
                IReadOnlyList<string> writes = [];
                BrokerResponse? back = null;
                if (Experiment.Load(path)?.Trial != null)
                {
                    var leaving = LeaveBack(target, Experiment.Survived, "a new session began", out writes);
                    (closed, back) = (leaving.Point, leaving.Answer);
                    if (!leaving.Back)
                    {
                        Log(target, $"Session not started · {game}", [$"Last trial: {closed!.Change}", leaving.Left
                            ? "Back to the daily profile did not all land: the record let go of the trial, but the card may still hold part of it"
                            : "Back to the daily profile did not all land: the trial stays in force", .. writes]);
                        return new BrokerResponse(false, leaving.Left
                            ? $"the trial in force did not all go back ({back.Error ?? back.Code}); the new session did not begin: read the card (card_settings) before beginning again"
                            : $"the trial in force did not go back, the card held busy ({back.Error ?? back.Code}); it stays in force and the new session did not begin: begin again once the card is free",
                            back.Result, back.Code);
                    }
                }
                // unattended: the person is away (an overnight mapping run), so the session stops writing new trials after
                // max_crashes crashes (default 2) rather than keep hanging the card; nothing steps down, the daily profile stays.
                bool unattended = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("unattended", out var un) && un.ValueKind == JsonValueKind.True;
                int? budget = unattended ? (int)(UInt(a, "max_crashes") ?? 2) : null;
                lock (_experiment)
                {
                    var exp = new Experiment(game, Text(a, "note"), _host.Now, CrashBudget: budget) { SessionId = Guid.NewGuid().ToString("N") };
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
            case BrokerRequest.ScopedTrial:
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
                // writes: any other registry write for the trial, {kind, key, value}, each through its own path and rules (the
                // core P0 offset, NVVDD's offsets, SYS/Video, the boost, a clock lock). The board wall stays power_mw (a
                // trial's wall is never kept) and the curve stays curve.
                var extra = new List<ProfileWrite>();
                var creates = new HashSet<string>();
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("writes", out var ws) && ws.ValueKind == JsonValueKind.Array)
                    foreach (var w in ws.EnumerateArray())
                    {
                        string kind = Text(w, "kind") ?? throw new ArgumentException("a trial write names its kind");
                        string key = Text(w, "key") ?? throw new ArgumentException("a trial write names its key");
                        if (WriteRegistry.Find(kind) == null) throw new ArgumentException($"'{kind}' is not a write path (one of: {string.Join(", ", WriteRegistry.Kinds)})");
                        if (kind == VfWrites.Kind) throw new ArgumentException("the curve goes in 'curve', not in writes");
                        if (kind == PowerCap.Kind && key.Equals(PowerCap.Key(PowerRequestKind.Ordinary), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("the board wall goes in 'power_mw', not in writes");
                        // create: the F8 request may be created (the driver cannot delete one; set_power_cap asks the person first).
                        if (w.TryGetProperty("create", out var cr) && cr.ValueKind == JsonValueKind.True) creates.Add($"{kind}/{key.ToLowerInvariant()}");
                        extra.Add(new ProfileWrite(kind, key, Text(w, "value") ?? throw new ArgumentException("a trial write names its value")));
                    }
                bool keepCurve = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("keep_curve", out var kc) && kc.ValueKind == JsonValueKind.True
                    && memoryKhz == null && !extra.Any(w => w.Kind == ClockOffsets.Kind);
                lock (_experiment)
                {
                    var exp = Experiment.Load(path) ?? throw new InvalidOperationException("no experiment session is open; begin one first");
                    // A trial written while the GPU hangs would be the one the coming reset is blamed on.
                    if (HungReason(target.StableId) is { } hung)
                        return Fail($"the GPU looks hung ({hung}): nothing written; a driver reset usually follows, and the service records it", BrokerCode.GpuHung);
                    if (exp.CrashBudget is { } budget && exp.CrashesSince(data) is var crashes && crashes >= budget)
                        return Fail($"this unattended session has had {crashes} crash{(crashes == 1 ? "" : "es")}, its budget of {budget}: no new trial until a new session begins (the daily profile stays on)", BrokerCode.CrashBudget);
                    // The writer's context (the readings and each point before and after, from the MCP server) goes in as given.
                    var context = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                    var written = new List<BrokerResponse>();
                    // The trial is on the card from its first write: a hang while its writes land is its (CrashedAt), and the
                    // trial before it left the card then.
                    var began = _host.Now;
                    // Everything goes on in the profile's write order (DailyProfile.Rank), the order added within a rank:
                    // the wall, current limits and fans first, a P0 offset (the curve set aside for it), the rails, the
                    // voltage demands before the clock offsets they carry, the curve last.
                    var queue = new List<(int Rank, BrokerRequest Request)>();
                    string Num(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (powerMw is { } mw)
                        queue.Add((0, WriteOf(target, PowerCap.Kind, PowerCap.Key(PowerRequestKind.Ordinary), Num(mw))));
                    // nvvdd_ma / msvdd_ma: a current limit for the trial (a lower one caps the rail's power below the
                    // 400 W the board request stops at); never kept, put back to its original when the trial ends.
                    foreach (var (rail, ma) in new[] { (Rail.Nvvdd, nvvddMa), (Rail.Msvdd, msvddMa) })
                        if (ma is { } v) queue.Add((0, WriteOf(target, CurrentLimitWrites.Kind, CurrentLimitWrites.Key(rail), Num(v))));
                    // The fan curve goes on every policy the firmware lets be edited (curve 0 and 1 on a 5090), as the
                    // daily profile's does; a fixed duty on every fan channel is trial-only, as a current limit is (fan_pct 0:
                    // the fans back to the curve).
                    if (fanCurve != null)
                        foreach (var i in FanPolicyReader.ReadInfo(_hw, target.Session).Editable.Select(p => p.Index))
                            queue.Add((1, WriteOf(target, FanPolicyWrites.Kind, FanPolicyWrites.Key(i), fanCurve)));
                    if (fanPct is { } pct)
                        foreach (var id in FanReader.Snapshot(_hw, target.Session).Channels.Select(c => c.Id))
                            queue.Add((1, WriteOf(target, FanControl.Kind, FanControl.Key(id), (pct == 0 ? FanState.Auto : new FanState(true, pct)).ToString())));
                    if (memoryKhz is { } mem)
                        queue.Add((DailyProfile.Rank(ClockOffsets.Kind), WriteOf(target, ClockOffsets.Kind, ClockOffsets.Key(ClockDomain.Memory), Num(mem))));
                    // msvdd_rel_uv / msvdd_vmin_uv: the MSVDD rail's offsets for the trial, through the rails path (its
                    // rules and post-write MAX check hold); putting the daily profile back puts its offsets back (the
                    // originals when it keeps none), and a keep takes them from the card.
                    if (msvddRel != null || msvddVmin != null)
                    {
                        var slots = new List<string>();
                        if (msvddRel is { } rel) slots.Add($"rel={Num(rel)}");
                        if (msvddVmin is { } vmin) slots.Add($"vmin={Num(vmin)}");
                        queue.Add((DailyProfile.Rank(RailWrites.Kind), WriteOf(target, RailWrites.Kind, RailWrites.Key(Rail.Msvdd), string.Join(",", slots))));
                    }
                    if (xbarVdemand is { } vd)
                        queue.Add((DailyProfile.Rank(ClockVdemand.Kind), WriteOf(target, ClockVdemand.Kind, ClockVdemand.Key(ClockDomainId.Xbar), Num(vd))));
                    if (xbarKhz is { } xk)
                        queue.Add((DailyProfile.Rank(ClockDomainOffsets.Kind), WriteOf(target, ClockDomainOffsets.Kind, ClockDomainOffsets.Key(ClockDomainId.Xbar), Num(xk))));
                    foreach (var w in extra)
                        queue.Add((DailyProfile.Rank(w.Kind), WriteOf(target, w.Kind, w.Key, w.Value) with { AllowCreate = creates.Contains($"{w.Kind}/{w.Key.ToLowerInvariant()}") }));
                    foreach (var (_, request) in queue.OrderBy(q => q.Rank))
                    {
                        if (!written.All(x => x.Ok)) break;
                        // The trial writes its curve right after (unless it keeps the one on the card): a curve this broker does
                        // not hold (adopted, or another tool's the person tunes from) is set aside too, and comes back with it.
                        if (request.Kind == ClockOffsets.Kind && CurveAside(target, request.Key!, request.Value!, curveFollows: !keepCurve) is { } aside)
                        {
                            written.Add(aside);
                            if (!aside.Ok) break;
                        }
                        written.Add(Dispatch(request));
                    }
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
                    var replaced = exp.Trial is { } before && Records(before, Experiment.Survived) ? exp.Point(Experiment.Survived, began, "replaced by the next trial") : null;
                    if (replaced != null) Experiment.Append(data, replaced);
                    if (exp.Trial is { } prior) Gone(target, exp, prior with { Ending = replaced != null ? Experiment.Survived : prior.Ending ?? Experiment.Survived }, began);
                    // A curve trial runs under the wall the trial before it set, until the daily profile goes back on; not under
                    // one a recovery has given back since (a driver reset's, before its crash is recorded): the card is at stock.
                    var under = exp.Trial is { } t0 && !GivenBack(target, t0) ? t0 : null;
                    var trial = new ExperimentTrial(began, curve, change, UInt(a, "at_uv"), UInt(a, "at_mhz"), PowerMw: powerMw ?? under?.PowerMw,
                        NvvddMa: nvvddMa ?? under?.NvvddMa, MsvddMa: msvddMa ?? under?.MsvddMa,
                        MsvddRelUv: msvddRel ?? under?.MsvddRelUv, MsvddVminUv: msvddVmin ?? under?.MsvddVminUv,
                        FanCurve: fanCurve ?? under?.FanCurve, FanPct: fanPct == 0 ? null : fanPct ?? under?.FanPct, MemoryKhz: memoryKhz ?? under?.MemoryKhz,
                        XbarKhz: xbarKhz ?? under?.XbarKhz, XbarVdemandUv: xbarVdemand ?? under?.XbarVdemandUv,
                        Writes: extra.Count == 0 ? under?.Writes
                            : [.. (under?.Writes ?? []).Where(b => !extra.Any(n => n.Kind == b.Kind && n.Key == b.Key)), .. extra]);
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
                    // Ended already (back_to_daily or a crash the card held off): its ending is its data point, and a stable
                    // mark after it would be a second one (a lab step's, or the agent's).
                    if (t.Ending is { } ended)
                        return Fail($"the trial in force has ended ({ended}) and waits for the card to be free to go back; nothing to mark", BrokerCode.CannotWrite);
                    var point = exp.Point(Experiment.Stable, _host.Now, req.Value)!;
                    Experiment.Append(data, point);
                    (exp with { Trial = t with { Marked = true } }).Save(path);
                    Log(target, $"Stable · {exp.Game}", [$"Trial: {point.Change}, ran {point.Minutes} min, {Limits(point)}", point.Note]);
                    return Ok(new { success = true, point });
                }
            case "crash":
            {
                // The game fell to the desktop with no driver reset: nothing the broker can see, so the player says so.
                if (Experiment.Load(path)?.Trial == null) return Fail("no trial is in force; nothing to mark", BrokerCode.CannotWrite);
                var (point, r, left, _) = LeaveBack(target, Experiment.Crash, req.Value ?? "the game crashed (reported by the player)", out var writes);
                Log(target, $"Crash · reported · {point!.Game}", [   // the player, or a stress run that failed
                    $"What happened: {point.Note}",
                    $"Trial: {point.Change}, ran {point.Minutes} min; written at {Mv(point.AtUv)}, {point.AtMhz} MHz; {Limits(point)}",
                    $"Trial curve: {point.Curve}",
                    "Back to the daily profile:", .. writes,
                    left ? null : "It did not all land: the trial stays in force until something takes the card off it"]);
                return r;
            }
            case "alive":
                // An agent's MCP server says it is still there (TrialLease): nothing is written or saved.
                LastAlive = _host.Now;
                return Ok(new { success = true });
            case "quiet":
                // A measurement that has just taken judge.lock, before it starts: a write that looked at the lock before it
                // was there may still be landing, so this is answered only once no operation is in flight (Dispatch runs one
                // at a time), and with the lock held none but the run's own starts after it. The answer is "show"'s: the
                // trial the run measures. Until 0.3.7 a run could start in the middle of another agent's write.
                return Ok(new { success = true, path, data, experiment = Experiment.Load(path) });
            case "release":
                // The agent running the trial is gone (its MCP server's input closed): what only a watching agent may hold
                // goes back, a fixed fan duty (it can hold the fans at 30 % at a 600 W wall) and lowered current limits.
                // With two agents on one card, one leaving hands these back for both: the safe side.
                return HandBack(target, path, data, req.Value ?? "the agent disconnected", fansOnly: false);
            case "fans-back":
                // The service's own fan guard: the card got too hot (or unreadable) under a fixed fan duty. Only the fans
                // go back to the curve; a lowered current limit only lowers power, so it stays.
                return HandBack(target, path, data, req.Value ?? "the fan guard", fansOnly: true);
            case "end":
            {
                var exp = Experiment.Load(path);
                var (point, r, left, _) = LeaveBack(target, Experiment.Survived, "the session ended", out var writes);
                // The session ends with its trial off the card; a trial the card did not let go of keeps it open.
                bool ends = point == null || left;
                if (ends) lock (_experiment) Experiment.Delete(path);
                Log(target, ends ? $"Session ended · {exp?.Game}" : $"Session not ended · {exp?.Game}", [
                    point != null ? $"Last trial: {point.Change}, ran {point.Minutes} min" : null,
                    "Back to the daily profile:", .. writes,
                    ends ? null : "It did not all land: the trial stays in force and the session stays open (end_session again, or back_to_daily)"]);
                return ends ? r : new BrokerResponse(false,
                    $"the trial in force did not go back, the card held busy ({r.Error ?? r.Code}); it stays in force and the session stays open: end_session again once the card is free",
                    r.Result, r.Code);
            }
            default:
                return Fail($"experiment needs show, policy-scope, begin, try, try-scoped, stable, crash, note, alive, quiet, release, fans-back or end, not '{req.Kind}'", BrokerCode.BadRequest);
        }
    }

    /// <summary>Back to the daily profile when one is on, else to stock, so a trial never stays behind; the answer carries
    /// the trial's data point beside the writes, and comes with the put-back's own response (for <see cref="Leave"/>).</summary>
    private (BrokerResponse Answer, BrokerResponse PutBack) Back(IGpuTarget target, DataPoint? point, out IReadOnlyList<string> writes)
    {
        var path = DailyProfile.PathBeside(target.Journal.Path);
        var profile = DailyProfile.Load(path);
        var back = profile is { Suspended: false }
            ? ApplyProfile(target, profile, path)
            : Dispatch(new BrokerRequest(BrokerRequest.OpRecover, target.StableId));
        writes = profile is { Suspended: false } ? [.. Writes(back)] : ["No daily profile in force: back to stock", .. Writes(back)];
        return (new BrokerResponse(back.Ok, back.Error, Element(new { success = back.Ok, point, back = back.Result }), back.Code), back);
    }

    /// <summary>Leaves the trial in force (<see cref="Leave"/>) through <see cref="Back"/>; the writes for the log.</summary>
    private Leaving LeaveBack(IGpuTarget target, string outcome, string? note, out IReadOnlyList<string> writes)
    {
        IReadOnlyList<string> logged = [];
        var leaving = Leave(target, outcome, note, p =>
        {
            var b = Back(target, p, out var w);
            logged = w;
            return b;
        });
        writes = logged;
        return leaving;
    }

    private void Log(IGpuTarget target, string title, IEnumerable<string?> lines) =>
        TuningLog.Append(TuningLog.PathBeside(target.Journal.Path), _host.Now, title, lines);

    private static string Mv(uint? uv) => uv is { } v ? $"NVVDD {v / 1000.0:0.#} mV" : "NVVDD unread";
    private static string Wall(uint? mw) => mw is { } v ? $"{v / 1000.0:0.#} W (set by the trial)" : "the daily profile's";
    private static string Limits(uint? powerMw, uint? nvvddMa, uint? msvddMa, int? msvddRelUv, int? msvddVminUv,
        string? fanCurve, uint? fanPct, int? memoryKhz, int? xbarKhz, int? xbarVdemandUv, IReadOnlyList<ProfileWrite>? writes) =>
        $"power limit {Wall(powerMw)}" + (nvvddMa is { } n ? $", NVVDD current limit {n / 1000.0:0.#} A" : "") + (msvddMa is { } m ? $", MSVDD current limit {m / 1000.0:0.#} A" : "")
        + (msvddRelUv is { } r ? $", MSVDD REL offset {r / 1000.0:0.#} mV" : "") + (msvddVminUv is { } v ? $", MSVDD VMIN offset {v / 1000.0:0.#} mV" : "")
        + (fanCurve is { } fc ? $", fan curve {fc}" : "") + (fanPct is { } fp ? $", fans held at {fp} %" : "")
        + (memoryKhz is { } mk ? $", memory offset {mk / 1000.0:+0.#;-0.#;0} MHz" : "")
        + (xbarKhz is { } xk ? $", XBAR offset {xk / 1000.0:+0.#;-0.#;0} MHz" : "") + (xbarVdemandUv is { } xv ? $", XBAR voltage demand {xv / 1000.0:+0.#;-0.#;0} mV" : "")
        + string.Concat((writes ?? []).Select(w => $", {w.Kind}/{w.Key} {w.Value}"));
    private static string Limits(DataPoint p) => Limits(p.PowerMw, p.NvvddMa, p.MsvddMa, p.MsvddRelUv, p.MsvddVminUv, p.FanCurve, p.FanPct, p.MemoryKhz, p.XbarKhz, p.XbarVdemandUv, p.Writes);
    private static string Limits(ExperimentTrial t) => Limits(t.PowerMw, t.NvvddMa, t.MsvddMa, t.MsvddRelUv, t.MsvddVminUv, t.FanCurve, t.FanPct, t.MemoryKhz, t.XbarKhz, t.XbarVdemandUv, t.Writes);

    /// <summary>A current-limit write: its setter is off until a request enables it, as the V/F points' is.</summary>
    private static BrokerRequest LimitWrite(IGpuTarget target, string key, string value) => WriteOf(target, CurrentLimitWrites.Kind, key, value);

    /// <summary>The setters a kind's write enables for itself: the ones off by default whose rule is complete.</summary>
    private static string[]? Setters(string kind) => kind switch
    {
        VfWrites.Kind => [nameof(Slot.ClockClientClkVfPointsSetControl)],
        CurrentLimitWrites.Kind => [nameof(Slot.PowerPolicySetControl)],
        ClockLimitWrites.Kind => [nameof(Slot.PerfClientLimitsSetStatus)],
        ClockRatio.Kind => [nameof(Slot.ClockClkPropTopRelsSetControl)],
        _ => null,
    };

    /// <summary>The ordinary write of one cell, with the setter it needs enabled.</summary>
    private static BrokerRequest WriteOf(IGpuTarget target, string kind, string key, string value) =>
        new(BrokerRequest.OpWrite, target.StableId, Kind: kind, Key: key, Value: value, EnableSlots: Setters(kind));

    /// <summary>A cell only a trial sets and no daily profile holds (DailyProfile.TrialOnly): a rail's current limit, a
    /// fixed fan duty, the F8 request, a clock floor, V/F point lock or Boost Lock.</summary>
    private static bool TrialOnly(OwnedItem i) => DailyProfile.TrialOnly(i.Kind, i.Key);

    /// <summary>The trial-only cells this broker holds.</summary>
    private static IEnumerable<OwnedItem> HeldTrialCells(IGpuTarget target) => target.Journal.Inspect().Items.Where(TrialOnly);

    /// <summary>What goes back to its original before a profile goes on, in write order. The whole profile going on: every
    /// cell held that the profile does not write itself (whatever a trial set beside it: a curve, a wall, a fan curve,
    /// the memory offset, XBAR, MSVDD), so the card is the profile and stock everywhere else. Part of it (the wall alone,
    /// right after a driver reset or at a keep): only the trial-only cells, a wall and MSVDD's offsets the profile does
    /// not hold, nothing that needs an idle GPU.</summary>
    /// <summary>
    /// What "keep" would save: every cell this broker holds, at its value on the card, but not a trial's current limits or
    /// fixed fan duty, nor a wall a trial lowered; once a profile is kept, only what an agent tunes is taken from the card
    /// (DailyProfile.KeptFromCard) and the wall stays as kept. The second list says what is held and not kept, and why.
    /// </summary>
    private (List<ProfileWrite> Writes, List<string> NotKept, List<ProfileWrite> Adopted) KeepWrites(IGpuTarget target, DailyProfile? profile, bool adopt = false)
    {
        var writes = new List<ProfileWrite>();
        var notKept = new List<string>();
        var adopted = new List<ProfileWrite>();
        var writer = new OwnedWriter(_hw, target.Journal, target.Sentinel, _host);
        // A wall a trial set was lowered to sample the curve lower down: never the daily one, with a profile kept
        // or not (the daily wall is set on its own: "wall").
        bool trialWall = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial?.PowerMw != null;
        var held = target.Journal.Inspect().Items;
        foreach (var item in held)
        {
            if (TrialOnly(item)) { notKept.Add($"{item.Id}: trial-only (a current limit, a fixed fan duty, the F8 request, or a clock floor, point lock or Boost Lock)"); continue; }
            if (trialWall && item.Kind == PowerCap.Kind) { notKept.Add($"{item.Id}: a trial's wall; the daily wall is set with set_daily_power_limit"); continue; }
            var wp = WriteRegistry.Find(item.Kind) ?? throw new InvalidOperationException($"this version cannot keep '{item.Id}'");
            var cell = wp.Prepare(_hw, target.Session, item.Key, item.Original, WriteOptions.Recovery).Cell;
            var now = writer.Look(target.Session, cell) ?? throw new InvalidOperationException($"{cell.Id} could not be read; nothing kept");
            if (now != item.Original) writes.Add(new ProfileWrite(item.Kind, item.Key, now));
        }
        // Adopt: what another tool left on the card and Clocktail neither holds nor keeps (the curve, the P0 and clock-domain
        // offsets, the voltage demands, the boost) is kept as it is now. Its wall, fixed fans and clock locks are not: the
        // daily wall and a cap are set through Clocktail, and fixed fans and the other locks are trial-only. The boost is one
        // setting through two APIs (FactoryStock.Boost): adopted once, by the key this card writes it through, and not at all
        // while Clocktail holds or keeps either.
        if (adopt)
        {
            string? Kept(string kind, string key) => profile is { Suspended: false } p ? p.Writes.FirstOrDefault(w => w.Kind == kind && w.Key == key)?.Value : null;
            var cells = FactoryStock.Survey(_hw, target.Session, (kind, key) => held.FirstOrDefault(i => i.Kind == kind && i.Key == key)?.Original, Kept);
            bool boostOurs = cells.Any(c => c.Kind == VoltBoost.Kind && (c.Original != null || c.Daily != null)) || writes.Any(w => w.Kind == VoltBoost.Kind);
            string? boostKey = FactoryStock.BoostKey(_hw, target.Session);
            foreach (var c in cells)
            {
                if (c.Now is not { } now || now == c.Stock || c.Original != null || c.Daily == now || c.Sibling) continue;
                if (c.Kind == VoltBoost.Kind && (boostOurs || c.Key != boostKey)) continue;
                if (Adoptable(c.Kind)) { writes.Add(new ProfileWrite(c.Kind, c.Key, now)); adopted.Add(new ProfileWrite(c.Kind, c.Key, now)); }
                else notKept.Add($"{c.Id}: {c.Name} {c.Describe(now)}, left by another tool: " + (c.Kind, c.Key) switch
                {
                    (PowerCap.Kind, _) => "the daily wall is set on its own (set_daily_power_limit)",
                    (ClockLimitWrites.Kind, ClockLimitWrites.MaxKey) => "another tool's clock cap is not adopted: set one with set_clock_lock in a trial, and a keep keeps it",
                    _ => "a fixed fan duty, a clock floor, a point lock or Boost Lock is trial-only",
                });
            }
        }
        // Once a profile is kept, what an agent tunes is taken from the card (DailyProfile.KeptFromCard): the curves,
        // the memory offset, MSVDD, XBAR/SYS/Video. The wall stays as kept, so a wall lowered to sample the curve
        // lower down never becomes the daily one. What the profile keeps from the card and the card still holds stays,
        // held or not: a value that went on as no change (one adopted, one another tool had set the same) is not held,
        // and a keep after a trial elsewhere used to drop it from the profile.
        if (profile != null)
        {
            notKept.AddRange(writes.Where(w => !DailyProfile.KeptFromCard(w.Kind, w.Key)).Select(w => $"{w.Kind}/{w.Key}: stays as the daily profile keeps it"));
            var fromCard = profile.Writes.Where(w => DailyProfile.KeptFromCard(w.Kind, w.Key) && !DailyProfile.TrialOnly(w.Kind, w.Key)
                && !writes.Any(x => x.Kind == w.Kind && x.Key == w.Key) && !held.Any(i => i.Kind == w.Kind && i.Key == w.Key)).ToList();
            var cardNow = CardValues(target, fromCard);
            // A lock a profile kept before locks were trial-only (0.2.5) is not carried over.
            writes = [.. profile.Writes.Where(w => !DailyProfile.KeptFromCard(w.Kind, w.Key) && !DailyProfile.TrialOnly(w.Kind, w.Key)),
                      .. fromCard.Where(w => cardNow.GetValueOrDefault($"{w.Kind}/{w.Key}") == w.Value),
                      .. writes.Where(w => DailyProfile.KeptFromCard(w.Kind, w.Key))];
        }
        return (writes, notKept, adopted);
    }

    /// <summary>What "adopt" takes from another tool: what a daily profile keeps from the card anyway, but the clock cap.</summary>
    private static bool Adoptable(string kind) =>
        kind is VfWrites.Kind or ClockOffsets.Kind or ClockDomainOffsets.Kind or ClockVdemand.Kind or VoltBoost.Kind;

    /// <summary>
    /// The card to NVIDIA's own settings after a give-back (<paramref name="recovered"/>): every setting whose default is
    /// known without a record (<see cref="FactoryStock"/>) and that the card holds at another value, another tool's, is
    /// written to that default through the ordinary write and let go of, so stock is the card's baseline from here and
    /// nothing gives the other tool's value back later. A write that does not go on is named and the rest still go, but
    /// a card held busy (a stability run, another operation) stops it there.
    /// </summary>
    private BrokerResponse ToFactory(IGpuTarget target, BrokerResponse recovered)
    {
        var applied = new List<JsonElement>();
        var cleared = new List<string>();
        var notCleared = new List<string>();
        foreach (var c in FactoryStock.Survey(_hw, target.Session, (_, _) => null, (_, _) => null).Where(c => c.Now != c.Stock))
        {
            // The boost's two APIs are one setting, its own path first (FactoryStock.Survey): that write may have cleared both.
            if (c.Kind == VoltBoost.Kind && LookNow(target, c.Kind, c.Key, c.Stock) == c.Stock) continue;
            var r = Dispatch(WriteOf(target, c.Kind, c.Key, c.Stock));
            applied.AddRange(AppliedOf(r));
            if (r.Ok)
            {
                target.Journal.Release(c.Kind, c.Key);
                cleared.Add($"{c.Name}: {c.Describe(c.Now)} → stock ({c.Describe(c.Stock)})");
                continue;
            }
            notCleared.Add($"{c.Name} ({c.Describe(c.Now)}): {WhyNot(r) ?? "not written"}");
            if (r.Code is BrokerCode.JudgeBusy or BrokerCode.WindowBusy) break;
        }
        var items = recovered.Result is { } rec && rec.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array ? its : (JsonElement?)null;
        bool ok = notCleared.Count == 0;
        return new BrokerResponse(ok, ok ? null : $"not everything another tool set went back to stock: {string.Join("; ", notCleared)}",
            Element(new { success = ok, items, applied, cleared, not_cleared = notCleared }), ok ? null : BrokerCode.Failed);
    }

    /// <summary>A cell's value on the card now; null when it cannot be read.</summary>
    private string? LookNow(IGpuTarget target, string kind, string key, string value)
    {
        try { return OwnedWriter.Look(_hw, target.Session, WriteRegistry.Find(kind)!.Prepare(_hw, target.Session, key, value, WriteOptions.Recovery).Cell); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or FormatException) { return null; }
    }

    /// <summary>A preset's writes, or a saved profile's, by name; throws when there is neither, or the preset is another model's.</summary>
    private static List<ProfileWrite> PresetWrites(IGpuTarget target, string name)
    {
        if (Presets.Find(name) is { } preset)
            return preset.Fits(target.Name) ? [.. preset.Writes] : throw new ArgumentException($"'{preset.Name}' was tuned on an {preset.Gpu}; this card is {target.Name}");
        return SavedProfiles.Find(SavedProfiles.PathBeside(target.Journal.Path), name) is var (_, saved)
            ? [.. saved.Writes] : throw new ArgumentException($"no preset or saved profile named '{name}'");
    }

    /// <summary>Each written cell's value on the card now, by "kind/key"; a cell that cannot be read is left out.</summary>
    private Dictionary<string, string> CardValues(IGpuTarget target, IEnumerable<ProfileWrite> writes)
    {
        var writer = new OwnedWriter(_hw, target.Journal, target.Sentinel, _host);
        var values = new Dictionary<string, string>();
        foreach (var w in writes)
            try
            {
                if (WriteRegistry.Find(w.Kind) is not { } wp) continue;
                var cell = wp.Prepare(_hw, target.Session, w.Key, w.Value, WriteOptions.Recovery).Cell;
                if (writer.Look(target.Session, cell) is { } now) values[$"{w.Kind}/{w.Key}"] = now;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or FormatException) { }
        return values;
    }

    /// <summary>"preset" with a saved profile's name: it becomes the daily profile and goes on, as a preset does.</summary>
    private BrokerResponse LoadSaved(IGpuTarget target, string? value, DailyProfile? profile, string path)
    {
        var file = SavedProfiles.PathBeside(target.Journal.Path);
        if (SavedProfiles.Find(file, value) is not var (name, saved))
        {
            string known = string.Join(", ", [.. Presets.All.Select(p => p.Name), .. SavedProfiles.All(file).Select(p => $"'{p.Name}'")]);
            return Fail($"no preset or saved profile named '{value}' (known: {known})", BrokerCode.CannotWrite);
        }
        var loaded = new DailyProfile(DailyProfile.InWriteOrder(saved.Writes), _host.Now, History: profile?.History).On(target.Vbios, target.Driver)
            .Noted(_host.Now, $"saved profile '{name}' loaded");
        var r = Leave(target, Experiment.Survived, $"saved profile '{name}' went on", _ => Loading(target, $"saved profile '{name}'", loaded, profile, path)).Answer;
        if (!r.Ok) return r;
        loaded.Save(path);
        Log(target, $"Saved profile loaded · {name}", [
            .. (profile?.Writes ?? []).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
            .. Writes(r)]);
        return r;
    }

    /// <summary>A preset or saved profile going on, for <see cref="Leave"/>: all of it, or (<see cref="NotLoaded"/>) the
    /// daily profile back as it was; the card is off the trial when either landed.</summary>
    private (BrokerResponse Answer, BrokerResponse PutBack) Loading(IGpuTarget target, string what, DailyProfile loaded, DailyProfile? profile, string path)
    {
        var r = ApplyProfile(target, loaded, path);
        if (r.Ok) return (r, r);
        var answer = NotLoaded(target, what, r, profile, path, out var back);
        return (answer, back);
    }

    /// <summary>A preset or saved profile that did not all go on (a write this card refuses, e.g. a wall above its board
    /// maximum) does not become the daily profile: the daily profile stays as it was and goes back on, so the card is
    /// never left holding part of a profile nothing keeps. The answer carries both runs' writes.</summary>
    private BrokerResponse NotLoaded(IGpuTarget target, string what, BrokerResponse failed, DailyProfile? profile, string path, out BrokerResponse back)
    {
        back = ApplyProfile(target, profile, path);
        Log(target, $"Not loaded · {what}", [
            $"Reason: {failed.Error}",
            .. Writes(failed),
            back.Ok ? "The daily profile stays as it was and went back on" : $"The daily profile stays as it was, but it did not all go back on: {back.Error}",
            .. Writes(back)]);
        string error = $"{failed.Error}; {what} was not loaded: the daily profile stays as it was" +
            (back.Ok ? " and is back on the card" : $", but it did not all go back on ({back.Error}): read the card");
        return new BrokerResponse(false, error, Element(new { success = false, path, profile, applied = (JsonElement[])[.. AppliedOf(failed), .. AppliedOf(back)] }), failed.Code);
    }

    /// <summary>When an agent's MCP server last said it was there ("alive"); the broker's start until one does. "alive" takes
    /// no lock and the lease reads it from its own thread, so it is kept as UTC ticks, read and written whole.</summary>
    public DateTimeOffset LastAlive
    {
        get => new(Interlocked.Read(ref _lastAliveTicks), TimeSpan.Zero);
        private set => Interlocked.Exchange(ref _lastAliveTicks, value.UtcTicks);
    }
    private long _lastAliveTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>The GPUs whose last hand-back put nothing back: the tuning log has the first refusal of a run of them, not
    /// every ask after it (the trial lease asks for minutes, the fan guard every 30 s). Asked under the operation lock.</summary>
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The trial-only cells (a fixed fan duty, and unless <paramref name="fansOnly"/> lowered current limits, a clock floor,
    /// point lock or Boost Lock and the F8 request) back to their originals. The rest of the trial stays on the card and in
    /// force, recorded from here as a trial of its own without what went back, the part before as survived (as
    /// <see cref="Records"/> says: a trial whose ending is on record keeps it, and nothing more is recorded). What went back
    /// is the journal's word after the put-backs, not their answers: a cell still held stays in the trial's record (the
    /// caller asks again), and when nothing went back nothing is recorded but the refusal, once until something does go back.
    /// No curve is written: the GPU may be under load.
    /// </summary>
    private BrokerResponse HandBack(IGpuTarget target, string path, string data, string why, bool fansOnly)
    {
        bool Mine(OwnedItem i) => !fansOnly || i.Kind == FanControl.Kind;
        var held = HeldTrialCells(target).Where(Mine).OrderBy(i => DailyProfile.Rank(i.Kind)).ToList();
        if (held.Count == 0)
        {
            _refused.Remove(target.StableId);
            return Ok(new { success = true, released = Array.Empty<object>() });
        }
        // The fan guard's fans go back under a measurement's judge.lock too (SafetyLease); everything else waits for it.
        string? lease = CurrentLease.Value;
        if (fansOnly) CurrentLease.Value = SafetyLease;
        List<BrokerResponse> back;
        try { back = held.Select(h => Dispatch(PutBack(target, h))).ToList(); }
        finally { CurrentLease.Value = lease; }
        var left = HeldTrialCells(target).Where(Mine).ToList();
        var gone = held.Where(h => !left.Any(l => l.Id == h.Id)).ToList();
        bool ok = left.Count == 0;
        if (gone.Count == 0 && _refused.Add(target.StableId))
            Log(target, fansOnly ? "Fans not handed back" : "Trial-only settings not handed back", [$"Reason: {why}",
                $"Still held: {string.Join(", ", left.Select(l => l.Id))}: asked again until they go (logged when they do)",
                .. back.SelectMany(Writes)]);
        if (gone.Count > 0)
        {
            _refused.Remove(target.StableId);
            string what = $"{Named(gone, left)} handed back{(fansOnly ? " to the curve" : "")}";
            DataPoint? closed = null;
            lock (_experiment)
            {
                if (Experiment.Load(path) is { Trial: { } t } exp)
                {
                    closed = Records(t, Experiment.Survived) ? exp.Point(Experiment.Survived, _host.Now, $"{why}: {what}") : null;
                    if (closed != null) Experiment.Append(data, closed);
                    Gone(target, exp, t with { Ending = closed != null ? Experiment.Survived : t.Ending });   // the part before
                    // An ending on record stays: the rest is still that trial waiting to go back, not a new one to record again.
                    (exp with { Trial = Without(t, gone, left) with { At = _host.Now, Marked = false, Change = $"{t.Change} ({what}: {why})" } }).Save(path);
                }
            }
            Log(target, !ok ? "Trial-only settings not all handed back" : fansOnly ? "Fans handed back to the curve" : "Trial-only settings handed back", [$"Reason: {why}",
                closed != null ? $"The trial ran {closed.Minutes} min with them: recorded as survived; the rest of it stays in force" : null,
                ok ? null : $"Still held: {string.Join(", ", left.Select(l => l.Id))}: the trial keeps them in its record",
                .. back.SelectMany(Writes)]);
        }
        var failed = back.FirstOrDefault(x => !x.Ok);
        return new BrokerResponse(ok, ok ? null : $"not every trial-only setting went back ({failed?.Error ?? $"{string.Join(", ", left.Select(l => l.Id))} still held"}): read the card",
            Element(new { success = ok, released = (JsonElement[])[.. back.SelectMany(AppliedOf)] }), ok ? null : failed?.Code ?? BrokerCode.Failed);
    }

    /// <summary>Trial-only cells in words, for the trial's change and its data point: "current limits and the F8 request";
    /// "some fans" when a kind is still held in part (<paramref name="left"/>).</summary>
    private static string Named(IEnumerable<OwnedItem> cells, IReadOnlyList<OwnedItem> left)
    {
        var names = cells.Select(c => (left.Any(l => l.Kind == c.Kind) ? "some " : "") + c.Kind switch
        {
            FanControl.Kind => "fans",
            CurrentLimitWrites.Kind => "current limits",
            ClockLimitWrites.Kind => "clock locks",
            PowerCap.Kind => "the F8 request",
            _ => c.Id,
        }).Distinct().ToList();
        return names.Count < 2 ? names.FirstOrDefault() ?? "nothing" : $"{string.Join(", ", names[..^1])} and {names[^1]}";
    }

    /// <summary>The trial without the trial-only cells that went back: its fixed fan duty once no fan is held, a rail's
    /// current limit that went back, and the writes (a clock floor, point lock, Boost Lock, the F8 request) of the cells
    /// that did. What is still held stays as it was.</summary>
    private static ExperimentTrial Without(ExperimentTrial t, IReadOnlyList<OwnedItem> gone, IReadOnlyList<OwnedItem> left)
    {
        bool Went(string kind, string key) => gone.Any(g => g.Kind == kind && g.Key == key);
        var writes = t.Writes?.Where(w => !Went(w.Kind, w.Key)).ToList();
        return t with
        {
            FanPct = gone.Any(g => g.Kind == FanControl.Kind) && !left.Any(l => l.Kind == FanControl.Kind) ? null : t.FanPct,
            NvvddMa = Went(CurrentLimitWrites.Kind, CurrentLimitWrites.Key(Rail.Nvvdd)) ? null : t.NvvddMa,
            MsvddMa = Went(CurrentLimitWrites.Kind, CurrentLimitWrites.Key(Rail.Msvdd)) ? null : t.MsvddMa,
            Writes = writes is { Count: > 0 } ? writes : null,
        };
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
                undone.Add(Dispatch(WriteOf(target, kind, key, before)));
            }
        return undone;
    }

    private static IEnumerable<OwnedItem> CellsToPutBack(IGpuTarget target, DailyProfile? profile, bool whole)
    {
        bool Written(OwnedItem i) => profile is { Suspended: false } p && p.Writes.Any(w => w.Kind == i.Kind && w.Key == i.Key);
        // Only a record this broker may act on: one from before the boot (Settle drops it) or another GPU's is not a debt.
        var plan = target.Journal.Inspect();
        if (plan.Reason is not (RecoveryReason.OwnedByThisProcess or RecoveryReason.OwnerDied)) return [];
        return plan.Items
            .Where(i => TrialOnly(i) || !Written(i) && (whole || i.Kind == PowerCap.Kind || i.Kind == RailWrites.Kind && i.Key == RailWrites.Key(Rail.Msvdd)))
            .OrderBy(i => DailyProfile.Rank(i.Kind));
    }

    /// <summary>The write that puts a held cell back to its original, with the setter it needs enabled.</summary>
    private static BrokerRequest PutBack(IGpuTarget target, OwnedItem held) => WriteOf(target, held.Kind, held.Key, held.Original);

    /// <summary>
    /// A P0 offset write (pstates20) resets every V/F point offset, so its path refuses one while any is set. When this
    /// write changes the P0 offset and the curve on the card is one this broker holds, the curve goes to 0 first; the
    /// caller writes it back after it in the same run (the trial's curve, the profile's, or the held original: a P0
    /// write ranks before the curve). Null when nothing needs setting aside. A curve this broker does not hold stays,
    /// and the path's refusal stands: it is not ours to wipe, unless <paramref name="curveFollows"/> says the caller
    /// writes a curve of its own right after (the daily profile with its curve, after a reset left another one on).
    /// </summary>
    private BrokerResponse? CurveAside(IGpuTarget target, string key, string value, bool curveFollows = false)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var khz)) return null;
        var domain = key == ClockOffsets.Key(ClockDomain.Memory) ? ClockDomain.Memory : ClockDomain.Core;
        if (Pstates20Reader.Read(_hw, target.Session).P0(domain)?.DeltaKhz == khz) return null;   // no change: nothing is reset
        var offsets = VfReader.Read(_hw, target.Session).Offsets();
        if (offsets.All(o => o == 0)) return null;
        if (!curveFollows && !target.Journal.Inspect().Items.Any(i => i.Kind == VfWrites.Kind)) return null;
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
        if (r.Result is { } s && s.TryGetProperty("skipped", out var why) && why.ValueKind == JsonValueKind.String) yield return $"Not put on: {why.GetString()}";
        if (r.Result is { } t && t.TryGetProperty("wall", out var wall) && wall.ValueKind == JsonValueKind.String) yield return $"Wall: {wall.GetString()}";
        if (!r.Ok) yield return $"Failed: {r.Error}";
    }

    /// <summary>Closes the trial in force, if any, with this outcome and returns its data point; null when no trial was in
    /// force. Recorded as <see cref="Records"/> says (a driver reset after a crash already on record is not a second one).
    /// The session stays open. Only for a card that keeps what it holds (a keep makes the trial the daily profile) or was
    /// taken off the trial already (a driver reset); one that has yet to be taken off it leaves through <see cref="Leave"/>.</summary>
    private DataPoint? CloseTrial(IGpuTarget target, string outcome, string? note = null, uint? crashUv = null)
    {
        lock (_experiment)
        {
            var path = Experiment.PathBeside(target.Journal.Path);
            if (Experiment.Load(path) is not { Trial: { } trial } exp) return null;
            var point = Records(trial, outcome) ? exp.Point(outcome, _host.Now, note, crashUv) : null;
            if (point != null) Experiment.Append(Experiment.DataPathBeside(target.Journal.Path), point);
            Gone(target, exp, trial with { Ending = point != null ? outcome : trial.Ending ?? outcome });
            (exp with { Trial = null }).Save(path);
            return point ?? exp.Point(outcome, _host.Now, note, crashUv);
        }
    }

    /// <summary>A trial that left the record, with its session's game and when (<see cref="Gone"/>).</summary>
    private sealed record GoneTrial(string Game, ExperimentTrial Trial, DateTimeOffset LeftAt);

    /// <summary>
    /// The trials that left the record lately, per GPU, newest last. A driver reset's crash is recorded seconds after the
    /// hang (once Windows has logged the reset, the driver has settled and the held cells are given back), and anything can
    /// take the trial off the record in between (back_to_daily, end_session, a lab job's end, the next trial, a hand-back),
    /// so the crash looks here for the trial that was on the card when the GPU stopped answering (<see cref="CrashedAt"/>).
    /// Kept <see cref="GoneKeep"/>, longer than the responder waits to give back, and in memory only: a reset that only the
    /// next worker finds has no hang time to look with. Under <see cref="_experiment"/>.
    /// </summary>
    private readonly Dictionary<string, List<GoneTrial>> _gone = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan GoneKeep = TimeSpan.FromMinutes(20);

    /// <summary>Remembers <paramref name="trial"/> (with how it ended) leaving the record (<see cref="_gone"/>): now, or
    /// <paramref name="at"/> when the card began leaving it before (the next trial's first write).</summary>
    private void Gone(IGpuTarget target, Experiment exp, ExperimentTrial trial, DateTimeOffset? at = null)
    {
        if (!_gone.TryGetValue(target.StableId, out var list)) _gone[target.StableId] = list = [];
        list.RemoveAll(g => _host.Now - g.LeftAt > GoneKeep);
        list.Add(new GoneTrial(exp.Game, trial, at ?? _host.Now));
    }

    /// <summary>A time for the tuning log, in local time as its headings are (the host's clock is UTC, the flight recorder's local).</summary>
    private static string When(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss.f", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The trial (its write time, the trial's identity) in force when each GPU's last clean recovery ran
    /// (<see cref="Recover"/>; a driver reset's give-back, before its crash is recorded): the card holds nothing of it. Kept
    /// by identity and not by time, so a clock set back cannot make a trial written after it look given back. Under the
    /// operation lock, as every recovery and every trial write is.</summary>
    private readonly Dictionary<string, DateTimeOffset?> _givenBack = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a recovery took the card off <paramref name="trial"/> (it was in force when one ran).</summary>
    private bool GivenBack(IGpuTarget target, ExperimentTrial trial) => _givenBack.GetValueOrDefault(target.StableId) == trial.At;

    /// <summary>
    /// A driver reset's crash ("crashed") when the flight recorder says when the GPU stopped answering
    /// (<paramref name="stopped"/>; <paramref name="answered"/>: the last look before, at which it still answered): the
    /// trial that was on the card then. That is the trial in force if it was on the card by then (its writes had begun:
    /// they can be what hung the GPU), else the one taken off the record after <paramref name="answered"/>
    /// (<see cref="_gone"/>). The crash is recorded once (<see cref="Records"/>), dated when the GPU stopped answering, so
    /// that its run (At less Minutes to At) is the run before the hang and not the frozen readings after it. A trial in force
    /// written after the GPU stopped answering is not blamed: when the reset's give-back took the card off it (it was in
    /// force when the give-back ran, <see cref="GivenBack"/>) it leaves the record with no data point; one written after
    /// the give-back is on the card and stays in force, for the agent running it. Until 0.3.8 the trial in force when the crash was recorded, seconds after the reset, took
    /// it: one written in between was blamed, and one taken off the record in between was recorded as survived and the
    /// crash filed under the daily profile. <paramref name="lines"/>: what the tuning log says beyond the plain case. Null
    /// when no trial was on the card then.
    /// </summary>
    private DataPoint? CrashedAt(IGpuTarget target, string? note, uint? crashUv, DateTimeOffset answered, DateTimeOffset stopped, out List<string> lines)
    {
        lines = [];
        lock (_experiment)
        {
            string path = Experiment.PathBeside(target.Journal.Path), data = Experiment.DataPathBeside(target.Journal.Path);
            var exp = Experiment.Load(path);
            ExperimentTrial? onCard = null;
            string? game = null;
            int gone = -1;
            bool clear = true;
            if (exp is { Trial: { } t })
            {
                if (t.At <= stopped) (onCard, game) = (t, exp.Game);
                else if (GivenBack(target, t))
                    lines.Add($"Not blamed: the trial in force ({t.Change}) was written at {When(t.At)}, after the GPU stopped answering; the reset's give-back took the card off it, so it leaves the record with no data point");
                else
                {
                    clear = false;
                    lines.Add($"Not blamed: the trial in force ({t.Change}) was written at {When(t.At)}, after the reset's give-back, and is on the card: it stays in force");
                }
            }
            var list = _gone.GetValueOrDefault(target.StableId);
            if (onCard == null && list?.FindLastIndex(g => g.Trial.At <= stopped && g.LeftAt > answered) is int i and >= 0)
            {
                (onCard, game, gone) = (list[i].Trial, list[i].Game, i);
                lines.Add($"The trial on the card when the GPU stopped answering ({onCard.Change}) had left the record at {When(list[i].LeftAt)}" +
                    (onCard.Ending is { } ended && ended != Experiment.Crash ? $", recorded as {ended}: this crash is how it ended" : ""));
            }
            DataPoint? point = null;
            if (onCard != null)
            {
                point = new Experiment(game!, null, onCard.At, onCard).Point(Experiment.Crash, stopped, note, crashUv)!;
                if (Records(onCard, Experiment.Crash)) Experiment.Append(data, point);
                if (gone >= 0) list![gone] = list[gone] with { Trial = onCard with { Ending = Experiment.Crash } };
            }
            if (clear && exp is { Trial: { } inForce })
            {
                Gone(target, exp, gone < 0 && onCard != null ? inForce with { Ending = Experiment.Crash } : inForce);
                (exp with { Trial = null }).Save(path);
            }
            return point;
        }
    }

    /// <summary>
    /// Whether ending <paramref name="trial"/> with <paramref name="outcome"/> writes a data point: a crash unless one is on
    /// record for it already (after a stable mark, or after an ending whose put-back the card held off, the card still ran
    /// the trial when it crashed); any other ending only while nothing is on record for it. So each trial's ending is
    /// recorded once, its crash always, and a driver reset after a mark_crash the card held off is not a second crash.
    /// </summary>
    private static bool Records(ExperimentTrial trial, string outcome) =>
        outcome == Experiment.Crash ? trial.Ending != Experiment.Crash : !trial.Marked && trial.Ending == null;

    /// <summary>How a trial left (<see cref="Leave"/>): its data point (null when no trial was in force), the answer,
    /// whether the record let go of it (<see cref="Left"/>), and whether the card is back: all of it, or all but whole
    /// groups the driver will not take (<see cref="Partial"/>).</summary>
    private readonly record struct Leaving(DataPoint? Point, BrokerResponse Answer, bool Left, bool Back);

    /// <summary>
    /// Ends the trial in force, if any, with this outcome, and has <paramref name="back"/> take the card off it (the daily
    /// profile, stock, a preset): it gives the caller's answer and the put-back's own response, which alone says whether
    /// the card is off the trial (<see cref="Left"/>). The data point is written first: a crash happened whatever the
    /// put-back does. While the card is held busy (a stability run holding it, another operation) the trial stays in force
    /// with its ending on record (<see cref="ExperimentTrial.Ending"/>): a crash before something takes the card off it is
    /// the trial's and not the daily profile's, nothing is recorded twice (<see cref="Records"/>), and whatever does take
    /// the card off it closes it. Until 0.3.7 the trial left the record before the put-back ran, so a put-back a stability
    /// run held off left the card running the trial under a record that said none.
    /// </summary>
    private Leaving Leave(IGpuTarget target, string outcome, string? note,
        Func<DataPoint?, (BrokerResponse Answer, BrokerResponse PutBack)> back, uint? crashUv = null)
    {
        var path = Experiment.PathBeside(target.Journal.Path);
        DataPoint? point = null;
        lock (_experiment)
        {
            if (Experiment.Load(path) is { Trial: { } trial } exp)
            {
                var written = Records(trial, outcome) ? exp.Point(outcome, _host.Now, note, crashUv) : null;
                if (written != null) Experiment.Append(Experiment.DataPathBeside(target.Journal.Path), written);
                // The ending on record: a crash over anything else, otherwise the first (a trial marked stable ends too).
                (exp with { Trial = trial with { Ending = written != null ? outcome : trial.Ending ?? outcome } }).Save(path);
                point = written ?? exp.Point(outcome, _host.Now, note, crashUv);
            }
        }
        var (answer, putBack) = back(point);
        bool landed = putBack.Ok || Partial(putBack), left = Left(putBack);
        if (point != null && left)
        {
            lock (_experiment)
                if (Experiment.Load(path) is { Trial: { } gone } exp)
                {
                    Gone(target, exp, gone);
                    (exp with { Trial = null }).Save(path);
                }
            if (!landed)
                Log(target, "Trial off the record, not all off the card", [$"Trial: {point.Change}", $"Reason: {putBack.Error ?? "the put-back did not finish"}",
                    "Refused for a reason waiting does not cure: the record lets go of the trial as before 0.3.7, but the card may still hold part of it; read the card (card_settings)"]);
        }
        return new Leaving(point, answer, left, landed);
    }

    /// <summary>
    /// Whether the trial may leave the record after what put the card back: it all went on, or all but whole groups the
    /// driver will not take (621.01's MSVDD offsets), which went back to stock with every cell of theirs (<see cref="Partial"/>:
    /// only when every put-back landed). A put-back the card was too busy for (a stability run holding it, another
    /// operation in the window) keeps the trial in force, and asked again once the card is free it lands: under a run that
    /// waits for the operation in flight nothing of it was written (<see cref="Judged"/>); one that does not wait can stop
    /// it part-way, as before 0.3.7. One refused for a reason a wait does not cure (a cell this version cannot give back, an unreadable one, no elevation, a write the driver will not
    /// take) lets the record go as before 0.3.7, or nothing could ever end the trial.
    /// </summary>
    private static bool Left(BrokerResponse r) => r.Ok || Partial(r) || r.Code is not (BrokerCode.JudgeBusy or BrokerCode.WindowBusy);

    /// <summary>ApplyProfile's answer when everything went on but whole groups the driver will not take.</summary>
    private static bool Partial(BrokerResponse r) =>
        r.Result is { ValueKind: JsonValueKind.Object } res && res.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;

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

    /// <summary>
    /// Each write of a profile that is not suspended (those <paramref name="only"/> picks, all by default), in its order,
    /// through the ordinary write op. A write that does not go on (refused, vetoed, or dropped by the driver, as 621.01
    /// drops every rail write) takes the rest of its group with it (<see cref="DailyProfile.Group"/>): the profile goes on
    /// again without that group, which puts any cell of it this broker holds back to its original, and the other groups go
    /// on as kept. The card is then never left with half of a run, the curve set aside for a memory write and never
    /// written back. The answer fails, names each group left at stock and why (not_applied), and carries every write.
    /// <paramref name="trialStays"/>: under a trial that stays in force, nothing held is put back first.
    /// </summary>
    private BrokerResponse ApplyProfile(IGpuTarget target, DailyProfile? profile, string path, Func<ProfileWrite, bool>? only = null, bool trialStays = false)
    {
        var (r, failed) = ApplyOnce(target, profile, path, only, trialStays);
        if (failed == null || profile == null) return r;
        var runs = new List<BrokerResponse> { r };
        var left = new List<(string Group, List<string> Cells, string Why)>();
        var writing = profile;
        while (failed != null)
        {
            string group = DailyProfile.Group(failed.Kind, failed.Key);
            bool In(ProfileWrite w) => DailyProfile.Group(w.Kind, w.Key) == group;
            left.Add((group, [.. writing.Writes.Where(In).Select(w => $"{w.Kind}/{w.Key}")], r.Error ?? $"{failed.Kind}/{failed.Key} did not apply"));
            writing = writing with { Writes = [.. writing.Writes.Where(w => !In(w))] };
            (r, failed) = ApplyOnce(target, writing, path, only, trialStays);
            runs.Add(r);
        }
        // The later runs write again what the first one already put on; only what they changed is worth a line.
        var applied = runs.Take(1).SelectMany(AppliedOf)
            .Concat(runs.Skip(1).SelectMany(AppliedOf).Where(a => !(a.TryGetProperty("outcome", out var o) && o.GetString() == nameof(TxOutcome.NoChange))))
            .ToArray();
        string stays = string.Join("; ", left.Select(l => $"the {l.Group} ({string.Join(", ", l.Cells)}) stays at stock: {l.Why}"));
        string error = r.Ok ? $"the daily profile is on, but {stays}" : $"{stays}; and the rest did not all go on: {r.Error}";
        string? skipped = r.Result is { } res && res.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.String ? sk.GetString() : null;
        string? wall = r.Result is { } res2 && res2.TryGetProperty("wall", out var wl) && wl.ValueKind == JsonValueKind.String ? wl.GetString() : null;
        return new BrokerResponse(false, error, Element(new
        {
            success = false, partial = r.Ok, path, profile, applied,
            not_applied = left.Select(l => new { group = l.Group, cells = l.Cells, why = l.Why }),
            skipped, wall,
        }), r.Ok ? runs[0].Code : r.Code);
    }

    /// <summary>Why a write that ran did not go on: the broker's refusal, else the transaction's veto or detail.</summary>
    private static string? WhyNot(BrokerResponse r)
    {
        if (r.Error != null) return r.Error;
        string? Get(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return AppliedOf(r).Select(a => Get(a, "veto") ?? Get(a, "detail")).LastOrDefault(s => s != null);
    }

    /// <summary>One run of <see cref="ApplyProfile"/>: stops at the first write that fails, and names it when it is one of
    /// the profile's own (a held cell that did not go back is not: nothing to leave out, the run just stops).</summary>
    private (BrokerResponse Response, ProfileWrite? Failed) ApplyOnce(IGpuTarget target, DailyProfile? profile, string path, Func<ProfileWrite, bool>? only, bool trialStays = false)
    {
        var applied = new List<JsonElement>();
        // A profile kept on another VBIOS goes on only when someone loads or keeps one (which binds it to this VBIOS):
        // until then whatever puts it on puts the card back to stock instead, as a suspended profile does.
        string? skipped = profile?.OtherVbios(target.Vbios);
        var writing = skipped != null ? null : profile;
        // A trial's current limits and fixed fans are never part of a profile, nor anything else a trial set unless the
        // profile writes it too: whatever puts the whole profile on puts them back first (not under a trial that stays).
        IEnumerable<OwnedItem> putBack = trialStays ? [] : CellsToPutBack(target, writing, whole: only == null);
        foreach (var held in putBack.ToList())
        {
            if (held.Kind == ClockOffsets.Kind && CurveAside(target, held.Key, held.Original) is { } aside)
            {
                applied.AddRange(AppliedOf(aside));
                if (!aside.Ok) return (new BrokerResponse(false, aside.Error ?? "the curve could not be set aside for a P0 write", Element(new { success = false, path, profile, applied }), aside.Code), null);
            }
            var r = Dispatch(PutBack(target, held));
            applied.AddRange(AppliedOf(r));
            if (!r.Ok) return (new BrokerResponse(false, $"{held.Id} did not go back" + (WhyNot(r) is { } why ? $": {why}" : ""), Element(new { success = false, path, profile, applied }), r.Code), null);
        }
        bool curveFollows = writing is { Suspended: false } && writing.Writes.Where(only ?? (_ => true)).Any(w => w.Kind == VfWrites.Kind);
        if (writing is { Suspended: false })
            // A lock a profile kept before locks were trial-only (0.2.5) stays off: CellsToPutBack released it above.
            foreach (var w in writing.Writes.Where(only ?? (_ => true)).Where(w => !DailyProfile.TrialOnly(w.Kind, w.Key)))
            {
                if (w.Kind == ClockOffsets.Kind && CurveAside(target, w.Key, w.Value, curveFollows) is { } aside)
                {
                    applied.AddRange(AppliedOf(aside));
                    if (!aside.Ok) return (new BrokerResponse(false, $"{w.Kind}/{w.Key} did not apply: the curve could not be set aside for it" + (WhyNot(aside) is { } why ? $" ({why})" : ""), Element(new { success = false, path, profile, applied }), aside.Code), w);
                }
                var r = Dispatch(WriteOf(target, w.Kind, w.Key, w.Value));
                applied.AddRange(AppliedOf(r));
                if (!r.Ok) return (new BrokerResponse(false, $"{w.Kind}/{w.Key} did not apply" + (WhyNot(r) is { } why ? $": {why}" : ""), Element(new { success = false, path, profile, applied }), r.Code), w);
            }
        // The firmware's board limit must follow the request (BoardWall): a request that reads as the daily wall while the
        // card holds another is written again, a step away and back (a changed value is what was seen to take).
        string? wallNote = null;
        if (writing is { Suspended: false } && writing.Writes.Where(only ?? (_ => true)).FirstOrDefault(w => w.Kind == PowerCap.Kind && w.Key == PowerCap.Key(PowerRequestKind.Ordinary)) is { } wall
            && uint.TryParse(wall.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var wallMw))
        {
            BoardPowerInfo? board = null;
            uint? enforced = null;
            try { board = PowerRequestsReader.ReadBoard(_hw, target.Session); enforced = BoardWall.Enforced(_hw, target.Session, board); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException) { }
            if (board != null && enforced is { } held && held != wallMw)
            {
                wallNote = $"the card enforced {held / 1000.0:0.#} W under a {wallMw / 1000.0:0.#} W request: the request was written again";
                uint away = wallMw - 1000 >= board.MinMw ? wallMw - 1000 : wallMw + 1000;
                foreach (var v in new[] { away, wallMw })
                {
                    var r = Dispatch(WriteOf(target, PowerCap.Kind, wall.Key, v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    applied.AddRange(AppliedOf(r));
                    if (!r.Ok) return (new BrokerResponse(false, "the daily wall could not be written again" + (WhyNot(r) is { } why ? $": {why}" : ""), Element(new { success = false, path, profile, applied, wall = wallNote }), r.Code), wall);
                }
            }
        }
        return (Ok(new { success = true, path, profile, applied, skipped, wall = wallNote }), null);
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
