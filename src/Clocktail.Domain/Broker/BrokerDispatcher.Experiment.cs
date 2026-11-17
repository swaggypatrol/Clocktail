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

public sealed partial class BrokerDispatcher
{
    /// <summary>Live tuning in a game: see <see cref="BrokerRequest.OpExperiment"/> and <see cref="Experiment"/>.</summary>
    private BrokerResponse ExperimentOp(IGpuTarget target, BrokerRequest req)
    {
        string path = Experiment.PathBeside(target.Journal.Path), data = Experiment.DataPathBeside(target.Journal.Path);
        switch (req.Kind)
        {
            case "show":
                return Ok(new { success = true, path, data, experiment = Experiment.Load(path), points = Experiment.ReadLast(data, 20), vf_history = HistoryStatusOrNull() });
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
                    var exp = new Experiment(game, Text(a, "note"), _host.Now, CrashBudget: budget);
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
                // writes: any other registry write for the trial, {kind, key, value}, each through its own path and rules (the
                // core P0 offset, NVVDD's offsets, SYS/Video, the boost, a clock lock). The board wall stays power_mw (a
                // trial's wall is never kept) and the curve stays curve.
                var extra = new List<ProfileWrite>();
                var creates = new HashSet<string>();
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("writes", out var ws) && ws.ValueKind == JsonValueKind.Array)
                    foreach (var w in ws.EnumerateArray())
                    {
                        string rawKind = Text(w, "kind") ?? throw new ArgumentException("a trial write names its kind");
                        string rawKey = Text(w, "key") ?? throw new ArgumentException("a trial write names its key");
                        var wp = WriteRegistry.Find(rawKind) ?? throw new ArgumentException($"'{rawKind}' is not a write path (one of: {string.Join(", ", WriteRegistry.Kinds)})");
                        string kind = wp.Kind;
                        string key = WriteKeys.Canonicalize(kind, rawKey);
                        if (kind == VfWrites.Kind) throw new ArgumentException("the curve goes in 'curve', not in writes");
                        if (kind == PowerCap.Kind && key == PowerCap.Key(PowerRequestKind.Ordinary))
                            throw new ArgumentException("the board wall goes in 'power_mw', not in writes");
                        // create: the F8 request may be created (the driver cannot delete one; set_power_cap asks the person first).
                        if (w.TryGetProperty("create", out var cr) && cr.ValueKind == JsonValueKind.True) creates.Add($"{kind}/{key}");
                        extra.Add(new ProfileWrite(kind, key, Text(w, "value") ?? throw new ArgumentException("a trial write names its value")));
                    }
                bool keepCurve = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("keep_curve", out var kc) && kc.ValueKind == JsonValueKind.True;
                lock (_experiment)
                {
                    var exp = Experiment.Load(path) ?? throw new InvalidOperationException("no experiment session is open; begin one first");
                    if (req.Lease is { Length: > 0 } job && exp.Trial is { } existing && existing.JobId != job)
                        return Fail("another trial is in force: the job cannot replace it", BrokerCode.CannotWrite);
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
                        queue.Add((DailyProfile.Rank(w.Kind), WriteOf(target, w.Kind, w.Key, w.Value) with { AllowCreate = creates.Contains($"{w.Kind}/{w.Key}") }));
                    // Capture the coupled controls before any trial writes so a later failure can restore both.
                    var curveBefore = queue.Any(q => q.Request.Kind == ClockOffsets.Kind) &&
                        _hw.Api.Resolve(Slot.ClockClientClkVfPointsGetInfo) != 0
                        ? VfReader.Read(_hw, target.Session) : null;
                    var p0Before = curveBefore == null ? null : Pstates20Reader.Read(_hw, target.Session);
                    foreach (var (_, request) in queue.OrderBy(q => q.Rank))
                    {
                        if (!written.All(x => x.Ok)) break;
                        if (request.Kind == ClockOffsets.Kind && keepCurve && curveBefore != null)
                        {
                            var result = Dispatch(request with { P0Curve = P0CurvePolicy.KeepDisplacement });
                            written.Add(result);
                            if (result.Ok) curve = result.Result!.Value.GetProperty("curve").GetString()!;
                            continue;
                        }
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
                        if (UnverifiedWrite(failed) || target.Sentinel.IsBlocked(out _))
                        {
                            string error = $"{failed.Error ?? "a trial write did not verify"}; run recovery before further trials";
                            Log(target, $"Trial write outcome could not be verified · {exp.Game}", [$"Change: {change}", .. context,
                                .. written.SelectMany(Writes), "Run recovery before further trials"]);
                            return new BrokerResponse(false, error, Element(new
                            {
                                success = false,
                                applied = written.SelectMany(AppliedOf),
                            }), failed.Code);
                        }
                        // The items before the one that failed landed: they go back to what they were, last first (the
                        // order they went on in respects every write's constraint, so the reverse does too: a curve set
                        // aside for a memory write comes back after the memory offset), and the trial before stays in force.
                        var undone = PutBackLanded(target, written, curveBefore, p0Before);
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
                            : [.. (under?.Writes ?? []).Where(b => !extra.Any(n => n.Kind == b.Kind && n.Key == b.Key)), .. extra],
                        Id: Guid.NewGuid().ToString("n"), JobId: req.Lease);
                    (exp with { Trial = trial }).Save(path);
                    Log(target, $"Trial · {exp.Game}", [
                        $"Change: {change}",
                        $"Working point at write: {Mv(trial.AtUv)}, {trial.AtMhz} MHz",
                        $"Limits: {Limits(trial)}",
                        .. context,
                        replaced != null ? $"The previous trial ({replaced.Change}) ran {replaced.Minutes} min without a crash: recorded as survived" : null,
                        keepCurve && curveBefore != null ? "Effective V/F curve verified after the P0 write" :
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
                    // Key: the identity of the trial a run measured (tune_memory's step), checked as "crash" checks it, so a
                    // trial written after the run let go of the card does not take its result. Without one (mark_stable), the
                    // trial in force. Until 0.8.3 "stable" took no identity and a sweep's pass could land on the next trial.
                    if (req.Key is { Length: > 0 } measured && t.Id != measured)
                        return Fail("the trial in force is not the one the run measured (another trial was written since): nothing marked", BrokerCode.CannotWrite);
                    // Ended already (back_to_daily or a crash the card held off): its ending is its data point, and a stable
                    // mark after it would be a second one (a lab step's, or the agent's).
                    if (t.Ending is { } ended)
                        return Fail($"the trial in force has ended ({ended}) and waits for the card to be free to go back; nothing to mark", BrokerCode.CannotWrite);
                    // Given back by the service's own recovery (a restart's leftovers, before the daily profile goes on once
                    // the GPU is idle; a driver reset's, before its crash is recorded): the card is not running it.
                    if (GivenBack(target, t))
                        return Fail("the service's own recovery (after a restart or a driver reset) gave the trial in force back to stock, so the card is not running it; nothing to mark: write the trial again to test it", BrokerCode.CannotWrite);
                    var point = exp.Point(Experiment.Stable, _host.Now, req.Value)!;
                    Experiment.Append(data, point);
                    (exp with { Trial = t with { Marked = true } }).Save(path);
                    Log(target, $"Stable · {exp.Game}", [$"Trial: {point.Change}, ran {point.Minutes} min, {Limits(point)}", point.Note]);
                    return Ok(new { success = true, point });
                }
            case "crash":
            {
                // The game fell to the desktop with no driver reset: nothing the broker can see, so the player says so.
                // Key: the identity of the trial a run measured (stress_test, lab_test), checked here under the operation
                // lock, so a trial written after the run cannot take its crash. Without one (mark_crash), the trial in force.
                var inForce = Experiment.Load(path)?.Trial;
                if (inForce == null) return Fail("no trial is in force; nothing to mark", BrokerCode.CannotWrite);
                if (req.Key is { Length: > 0 } measured && inForce.Id != measured)
                    return Fail("the trial in force is not the one the run measured (another trial was written since): nothing recorded", BrokerCode.CannotWrite);
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
                // goes back, a fixed fan duty (it can hold the fans low at a high wall with nobody watching) and lowered
                // current limits. With two agents on one card, one leaving hands these back for both: the safe side.
                return HandBack(target, path, data, req.Value ?? "the agent disconnected");
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
                return Fail($"experiment needs show, begin, try, stable, crash, note, alive, quiet, release or end, not '{req.Kind}'", BrokerCode.BadRequest);
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

    /// <summary>Closes the trial in force, if any, with this outcome and returns its data point; null when no trial was in
    /// force. Recorded as <see cref="Records"/> says (a driver reset after a crash already on record is not a second one):
    /// <paramref name="appended"/> says whether the point was written now. The session stays open. Only for a card that keeps what it holds (a keep makes the trial the daily profile) or was
    /// taken off the trial already (a driver reset); one that has yet to be taken off it leaves through <see cref="Leave"/>.</summary>
    private DataPoint? CloseTrial(IGpuTarget target, string outcome, out bool appended, string? note = null, uint? crashUv = null)
    {
        appended = false;
        lock (_experiment)
        {
            var path = Experiment.PathBeside(target.Journal.Path);
            if (Experiment.Load(path) is not { Trial: { } trial } exp) return null;
            var point = Records(trial, outcome) ? exp.Point(outcome, _host.Now, note, crashUv) : null;
            if (point != null) Experiment.Append(Experiment.DataPathBeside(target.Journal.Path), point);
            appended = point != null;
            Gone(target, exp, trial with { Ending = point != null ? outcome : trial.Ending ?? outcome });
            (exp with { Trial = null }).Save(path);
            return point ?? exp.Point(outcome, _host.Now, note, crashUv);
        }
    }

    /// <summary>A crash of the trial in force whose give-back has not landed ("crashed-held" without the flight recorder's
    /// time): its data point as <see cref="Records"/> says, and the trial stays in force with the crash as its ending. Null
    /// when no trial is in force.</summary>
    private DataPoint? CrashHeld(IGpuTarget target, string? note, uint? crashUv)
    {
        lock (_experiment)
        {
            var path = Experiment.PathBeside(target.Journal.Path);
            if (Experiment.Load(path) is not { Trial: { } trial } exp) return null;
            var point = Records(trial, Experiment.Crash) ? exp.Point(Experiment.Crash, _host.Now, note, crashUv) : null;
            if (point != null) Experiment.Append(Experiment.DataPathBeside(target.Journal.Path), point);
            (exp with { Trial = trial with { Ending = Experiment.Crash } }).Save(path);
            return point ?? exp.Point(Experiment.Crash, _host.Now, note, crashUv);
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
    /// profile, stock, a saved profile): it gives the caller's answer and the put-back's own response, which alone says whether
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

    /// <summary>Refused because the card was held busy (a stability run holding it, another operation in the window): asked
    /// again once it is free, it lands.</summary>
    private static bool HeldBusy(BrokerResponse r) => r.Code is BrokerCode.JudgeBusy or BrokerCode.WindowBusy;

    /// <summary>ApplyProfile's answer when everything went on but whole groups the driver will not take.</summary>
    private static bool Partial(BrokerResponse r) =>
        r.Result is { ValueKind: JsonValueKind.Object } res && res.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
}
