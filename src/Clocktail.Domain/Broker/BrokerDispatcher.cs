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
    Clocktail.Domain.Game.IGameAccess? GameAccess => null;
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
/// <item>the limits are the hardware's own, as the write path's rules enforce them (the board's power range, a rail's
/// reported voltage domain, the driver's clock range, the fan's firmware range) in the plan before the write, with a
/// path's post-write proof inside the transaction; this dispatcher exposes no knob that widens them;</item>
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
public sealed record ProfileConsentRequest(
    string Kind,
    string GpuId,
    string GpuName,
    string Summary,
    IReadOnlyList<ProfileWrite>? Writes = null,
    string? Value = null);

public sealed partial class BrokerDispatcher
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
    public BrokerDispatcher(NvClient hw, IGpuTargets gpus, IHost host, bool canWrite, Func<string?>? judgeBusyReason = null, Fps.IFrameCapture? frames = null, IProfileApprovals? profileApprovals = null)
    {
        _hw = hw;
        _gpus = gpus;
        _host = host;
        _canWrite = canWrite;
        _judgeBusyReason = judgeBusyReason ?? (() => null);
        _frames = frames;
        _profileApprovals = profileApprovals ?? new MemoryProfileApprovals();
        foreach (var id in gpus.Ids)
            if (gpus.Resolve(id) is { } t)
                try { _trialAtStart[id] = Identity(Experiment.Load(Experiment.PathBeside(t.Journal.Path))?.Trial); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The trial in force when this dispatcher began, per GPU (<see cref="Identity"/>; none: no trial, or the file
    /// could not be read): the one the start's daily profile ends ("start"). A trial written after it is the agent's.</summary>
    private readonly Dictionary<string, string?> _trialAtStart = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A trial's identity: its <see cref="ExperimentTrial.Id"/>, or for one written without, when it was written.</summary>
    private static string? Identity(ExperimentTrial? t) => t == null ? null : t.Id ?? t.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private readonly Fps.IFrameCapture? _frames;
    public Func<string, bool> GameLeaseHeld { get; set; } = _ => false;

    /// <summary>Authorization hook for client-requested profile mutations (<c>keep</c>, <c>adopt</c>, <c>stock</c>,
    /// <c>factory</c>, <c>wall</c>, <c>preset</c>, <c>off</c>, <c>load</c>,
    /// <c>save</c>, <c>forget</c>). Invoked only while <see cref="FromClient"/> is active; internal recovery, startup,
    /// wake, and reset calls never invoke it. Client profile mutations fail closed when unset.</summary>
    public Func<ProfileConsentRequest, bool>? AuthorizeProfileChange { get; set; }

    private static string DailyStateToken(DailyProfile? profile) => profile == null
        ? "none"
        : ProfileApproval.Fingerprint("state", ProfileApproval.Daily, profile);

    private static bool SameCell(ProfileWrite a, ProfileWrite b) =>
        string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProfileWrite>? RelevantAdopted(IReadOnlyList<ProfileWrite>? adopted, IReadOnlyList<ProfileWrite> writes)
    {
        if (adopted is not { Count: > 0 } || writes.Count == 0) return null;
        var kept = adopted.Where(a => writes.Any(w => SameCell(w, a))).ToList();
        return kept.Count > 0 ? kept : null;
    }

    private static IReadOnlyList<ProfileWrite>? MergeAdopted(IReadOnlyList<ProfileWrite> writes, IReadOnlyList<ProfileWrite>? primary, IReadOnlyList<ProfileWrite>? fallback)
    {
        var merged = new List<ProfileWrite>();
        if (RelevantAdopted(primary, writes) is { } first) merged.AddRange(first);
        if (RelevantAdopted(fallback, writes) is { } second)
            merged.AddRange(second.Where(a => !merged.Any(existing => SameCell(existing, a))));
        return merged.Count > 0 ? merged : null;
    }

    /// <summary>Why a GPU looks hung right now (the broker's flight recorder; <see cref="FlightRecorder.HungNow"/>), or null.</summary>
    public Func<string, string?> HungReason { get; set; } = _ => null;

    /// <summary>How the broker's always-on V/F history is doing (written, queued, dropped, a write failing), for ping and
    /// experiment "show"; null where no recorder runs (tests, an older broker answers without the field).</summary>
    public Func<object?> HistoryStatus { get; set; } = () => null;

    private object? HistoryStatusOrNull()
    {
        try { return HistoryStatus(); } catch (Exception) { return null; }
    }

    /// <summary>
    /// One whole operation at a time: a profile op or a trial reads the profile and the experiment, writes the card and
    /// saves them, and the write window alone covers only the write in the middle (two "wall"s could leave the card at one
    /// and the profile at the other, both answering success). Reentrant, so an operation's own writes, which come back
    /// through <see cref="Dispatch"/> on the same thread, go straight through; taken before the write window, never inside it.
    /// </summary>
    private readonly object _operation = new();

    /// <summary>The measurement lease of the request in flight (<see cref="BrokerRequest.Lease"/>), for the judge: the
    /// holder's own writes, nested ones too, pass the lock it holds.</summary>
    public static readonly AsyncLocal<string?> CurrentLease = new();

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
    /// holds the card), and so does the rest of an operation let through before the run took the lock, when the run waits
    /// for it (<see cref="Judged"/>). Anything else waits: the run's reason.
    /// </summary>
    public static Func<string?> Judge(Func<JudgeLock.Info?> live) => () =>
    {
        if (live() is not { } run) return null;
        string? lease = CurrentLease.Value;
        if (run.RunId != null && run.RunId == lease) return null;
        if (run.Waits && Judged.Value is { Sure: true }) return null;
        return JudgeLock.Reason(run);
    };

    /// <summary>Set while a client's request is in flight (<see cref="FromClient"/>).</summary>
    private static readonly AsyncLocal<bool> ClientAsks = new();

    /// <summary>
    /// A client's request: over the pipe, or the CLI's in-process. The service's own requests are not a client's: the
    /// recoveries after a driver reset leave the trial to the "crashed" step after them, and "crashed" is the service's
    /// record of a reset it saw (a client's crash is the experiment's "crash"), so a client sending either is refused.
    /// A recovery a client asks for takes the card off the trial in force, which then leaves the record (<see cref="DispatchOne"/>).
    /// </summary>
    public BrokerResponse FromClient(BrokerRequest req)
    {
        if (req.Op == BrokerRequest.OpRecover && req.Kind is BrokerRequest.RecoverAfterDriverReset or BrokerRequest.RecoverAfterDriverResetAgain
            || req.Op == BrokerRequest.OpProfile && req.Kind is "crashed" or "crashed-held" or "start" or "resume" or "apply-power" or "after-reset")
            return Fail("that request is the service's own (its answer to a driver reset or service lifecycle); a client cannot send it", BrokerCode.BadRequest);
        bool prior = ClientAsks.Value;
        ClientAsks.Value = true;
        try { return DispatchClient(req); }
        finally { ClientAsks.Value = prior; }
    }

    public BrokerResponse Dispatch(BrokerRequest req)
    {
        string? prior = CurrentLease.Value;
        var judged = Judged.Value;
        if (req.Lease is { } lease) CurrentLease.Value = lease;
        bool entered = false, top = false;
        try
        {
            if (Changes(req))
            {
                if (Volatile.Read(ref _closed)) return Fail(ClosedError, BrokerCode.WindowBusy);
                top = !Monitor.IsEntered(_operation);   // not one of an operation's own requests, which come back through here
                if (!Monitor.TryEnter(_operation, WindowWait))
                    return Fail($"another operation has held the card for over {WindowWait.TotalSeconds:0} s; nothing was done, try again", BrokerCode.WindowBusy);
                entered = true;
                if (Volatile.Read(ref _closed)) return Fail(ClosedError, BrokerCode.WindowBusy);
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
    /// every write; a refusal is kept for the rest of the operation, and a write let through admits it (<see cref="Judged"/>).</summary>
    private string? JudgeNow()
    {
        if (Judged.Value is { Busy: { } refused }) return refused;
        string? busy = _judgeBusyReason();
        if (Judged.Value != null) Judged.Value = busy == null ? Verdict.Admitted : new Verdict(busy);
        return busy;
    }

    /// <summary>Whether the request can change the card, the profile or the experiment: everything but the reads.</summary>
    private static bool Changes(BrokerRequest req) =>
        req.IsWrite
        || req.Op == BrokerRequest.OpGame
        || req.Op == BrokerRequest.OpProfile && req.Kind is not ("show" or "preview" or "preview-adopt" or "saved")
        || req.Op == BrokerRequest.OpExperiment && req.Kind is not ("show" or "alive");

    private BrokerResponse DispatchOne(BrokerRequest req, bool top)
    {
        try
        {
            if (req.Op == BrokerRequest.OpPing)
                return Ok(new
                {
                    pong = true, pid = _host.Pid, can_write = _canWrite, gpus = _gpus.Ids, game_protocol = Game.GameSession.Protocol,
                    paths = WriteRegistry.All.Select(p => new { kind = p.Kind, keys = p.Keys, values = p.Values, checks_after_write = p.AfterWrite, equal_value_proof = p.ProveEqual != null }),
                    vf_history = HistoryStatusOrNull(),
                    profile_approvals = new { available = _profileApprovals.Unavailable == null, reason = _profileApprovals.Unavailable },
                });

            var target = _gpus.Resolve(req.GpuId);
            if (target == null)
                return Fail(req.GpuId == null ? "no GPU is available" : $"no GPU with id '{req.GpuId}'", BrokerCode.NoGpu);

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
                    BrokerRequest.OpGame => GameOperation(target, req),
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

    private static string JudgeBusy(string busy) => $"a stability run is in progress ({busy}); the write window is held, try again when it finishes";

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

    /// <summary>The general write: set kind/key to value through the registry. A limit the driver reveals only after
    /// the write is the cell's own post-write check; a veto comes back as a failed, restored transaction.</summary>
    private BrokerResponse Write(IGpuTarget target, BrokerRequest req)
    {
        if (!_canWrite) return Fail(NeedsAdmin, BrokerCode.NeedsAdmin);
        var path = WriteRegistry.Find(req.Kind);
        if (path == null) return Fail($"'{req.Kind}' is not a write path (one of: {string.Join(", ", WriteRegistry.Kinds)})", BrokerCode.NotAPath);
        if (req.Key == null || req.Value == null) return Fail("a write names a kind, a key and a value", BrokerCode.BadRequest);
        if (path.Kind == ClockOffsets.Kind && req.P0Curve != P0CurvePolicy.Ordinary) return WriteP0WithCurve(target, req);
        string key = WriteKeys.Canonicalize(path.Kind, req.Key);
        var hw = Client(req.EnableSlots);
        var writer = new OwnedWriter(hw, target.Journal, target.Sentinel, _host);

        // The origin a path may judge the request against is the journal's, never the request's: what the hardware
        // held before this program first touched the cell (a lowered stock value may be put back, even above the
        // ceiling). Asked before planning, with the same adoption rule the write itself applies.
        var original = writer.AdoptedOriginal(path.Kind, key);
        var p = path.Prepare(hw, target.Session, key, req.Value, new WriteOptions(req.AllowCreate, Original: original, CurveFollows: path.Kind == ClockOffsets.Kind));
        if (p.Plan == null) return Fail(p.CannotWrite ?? $"{p.Cell.Id} cannot be written from '{req.Value}'", BrokerCode.CannotWrite);

        // Both public P0 routes capture native side effects through the same ownership path.
        var r = path.Kind == ClockOffsets.Kind
            ? new ClockOffsets(hw, target.Journal, target.Sentinel, _host).ApplyOwned(target.Session,
                Enum.Parse<ClockDomain>(key, true), int.Parse(req.Value, System.Globalization.CultureInfo.InvariantCulture), req.TakeOver)
            : writer.Apply(target.Session, p.Cell, p.Plan, req.TakeOver, req.Value);
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
                    effective_curve_verified = path.Kind == ClockOffsets.Kind ? (bool?)false : null,
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
                    effective_curve_verified = false,
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
                return Ok(_frames.Stop(Text(Args(req.Value), "path")));   // no path (an older client): whatever runs
            default:
                return Fail($"unknown fps request '{req.Kind}' (start, stop)", BrokerCode.BadRequest);
        }
    }

    private static BrokerResponse Fail(string error, string code) => new(false, error, Code: code);
}
