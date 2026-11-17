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
        if (r.Clean) _givenBack[target.StableId] = Identity(Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial);   // off the card now
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

    /// <summary>When an agent's MCP server last said it was there ("alive"); the broker's start until one does. "alive" takes
    /// no lock and the lease reads it from its own thread, so it is kept as UTC ticks, read and written whole.</summary>
    public DateTimeOffset LastAlive
    {
        get => new(Interlocked.Read(ref _lastAliveTicks), TimeSpan.Zero);
        private set => Interlocked.Exchange(ref _lastAliveTicks, value.UtcTicks);
    }
    private long _lastAliveTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>The GPUs whose last hand-back put nothing back: the tuning log has the first refusal of a run of them, not
    /// every ask after it (the trial lease asks for minutes). Asked under the operation lock.</summary>
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The trial-only cells (a fixed fan duty, lowered current limits, a clock floor, point lock or Boost Lock and the F8
    /// request) back to their originals. The rest of the trial stays on the card and in
    /// force, recorded from here as a trial of its own without what went back, the part before as survived (as
    /// <see cref="Records"/> says: a trial whose ending is on record keeps it, and nothing more is recorded). What went back
    /// is the journal's word after the put-backs, not their answers: a cell still held stays in the trial's record (the
    /// caller asks again), and when nothing went back nothing is recorded but the refusal, once until something does go back.
    /// No curve is written: the GPU may be under load.
    /// </summary>
    private BrokerResponse HandBack(IGpuTarget target, string path, string data, string why)
    {
        var held = HeldTrialCells(target).OrderBy(i => DailyProfile.Rank(i.Kind)).ToList();
        if (held.Count == 0)
        {
            _refused.Remove(target.StableId);
            return Ok(new { success = true, released = Array.Empty<object>() });
        }
        var back = held.Select(h => Dispatch(PutBack(target, h))).ToList();
        var left = HeldTrialCells(target).ToList();
        var gone = held.Where(h => !left.Any(l => l.Id == h.Id)).ToList();
        bool ok = left.Count == 0;
        if (gone.Count == 0 && _refused.Add(target.StableId))
            Log(target, "Trial-only settings not handed back", [$"Reason: {why}",
                $"Still held: {string.Join(", ", left.Select(l => l.Id))}: asked again until they go (logged when they do)",
                .. back.SelectMany(Writes)]);
        if (gone.Count > 0)
        {
            _refused.Remove(target.StableId);
            string what = $"{Named(gone, left)} handed back";
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
            Log(target, !ok ? "Trial-only settings not all handed back" : "Trial-only settings handed back", [$"Reason: {why}",
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

    /// <summary>Restore the earliest value of each landed cell. Restore P0 before the curve it can reset.</summary>
    private List<BrokerResponse> PutBackLanded(IGpuTarget target, IReadOnlyList<BrokerResponse> written,
        VfCurveSnapshot? curveBefore, Pstates20Snapshot? p0Before)
    {
        var cells = new Dictionary<(string Kind, string Key), string>();
        foreach (var item in written.Where(w => w.Ok).SelectMany(AppliedOf))
        {
            string? Get(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Get("outcome") == nameof(TxOutcome.Applied) && Get("kind") is { } kind &&
                Get("key") is { } key && Get("before") is { } before && before != Get("after"))
                cells.TryAdd((kind, key), before);
        }

        var undone = new List<BrokerResponse>();
        bool p0Changed = cells.Keys.Any(cell => cell.Kind == ClockOffsets.Kind);
        var curveKey = (VfWrites.Kind, VfWrites.CoreKey);
        if (p0Changed && curveBefore != null)
            cells[curveKey] = VfOffsets.Encode(curveBefore.Offsets());
        var order = cells.Reverse().OrderBy(c => p0Changed && c.Key.Kind == VfWrites.Kind ? 1 : 0);
        foreach (var (cell, before) in order)
        {
            if (cell.Kind == ClockOffsets.Kind && curveBefore != null && CurveAside(target, cell.Key, before, curveFollows: true) is { } aside)
            {
                undone.Add(aside);
                if (!aside.Ok) return undone;
            }
            var restored = Dispatch(WriteOf(target, cell.Kind, cell.Key, before));
            undone.Add(restored);
            if (!restored.Ok) return undone;
        }
        if (p0Changed && curveBefore != null && p0Before != null)
        {
            try
            {
                var curveNow = VfReader.Read(_hw, target.Session);
                if (!curveBefore.Offsets().SequenceEqual(curveNow.Offsets()) || VfEffectiveCurve.Mismatch(curveBefore, curveNow) != null ||
                    Transaction.FirstDifference(p0Before.Raw, Pstates20Reader.Read(_hw, target.Session).Raw, Pstates20Layout.StatusRanges) != null)
                    throw new InvalidOperationException("the previous P0 controls and effective curve did not return");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
            {
                target.Sentinel.Begin("trial rollback unverified", takeOver: true, e.Message);
                undone.Add(new BrokerResponse(false, e.Message + "; run recovery before tuning",
                    Element(new { applied = new[] { new { kind = VfWrites.Kind, key = VfWrites.CoreKey,
                        outcome = nameof(TxOutcome.Failed), restore = nameof(RestoreState.RestoreFailed), state_known = false } } }), BrokerCode.Failed));
            }
        }
        return undone;
    }

    /// <summary>What goes back to its original before a profile goes on, in write order. The whole profile going on: every
    /// cell held that the profile does not write itself (whatever a trial set beside it: a curve, a wall, a fan curve,
    /// the memory offset, XBAR, MSVDD), so the card is the profile and stock everywhere else. Part of it (the wall alone,
    /// right after a driver reset or at a keep): only the trial-only cells, a wall and MSVDD's offsets the profile does
    /// not hold, nothing that needs an idle GPU.</summary>
    private static IEnumerable<OwnedItem> CellsToPutBack(IGpuTarget target, DailyProfile? profile, bool whole, bool implicitCore)
    {
        // A P0-only Core daily owns its generated curve even without a vf/core entry in the file.
        bool Written(OwnedItem i) => (implicitCore && i.Kind == VfWrites.Kind) ||
            profile is { Suspended: false } p && p.Writes.Any(w => w.Kind == i.Kind && w.Key == i.Key);
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
        if (_hw.Api.Resolve(Slot.ClockClientClkVfPointsGetInfo) == 0) return null;
        if (!int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var khz)) return null;
        if (!Enum.TryParse<ClockDomain>(key, true, out var domain) || !Enum.IsDefined(domain)) return null;
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

    /// <summary>The trial (its <see cref="Identity"/>) in force when each GPU's last clean recovery ran
    /// (<see cref="Recover"/>; a driver reset's give-back, before its crash is recorded): the card holds nothing of it. Kept
    /// by identity and not by time, so a clock set back cannot make a trial written after it look given back, nor one
    /// written in the same tick. Under the operation lock, as every recovery and every trial write is.</summary>
    private readonly Dictionary<string, string?> _givenBack = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The trial (its write time) whose crash each GPU's "crashed-held" logged, until the "crashed" after it: that
    /// one logs only that the card is off the trial, not the crash again. In memory: a reset the next worker answers is
    /// logged in full. Under the operation lock, as both requests are.</summary>
    private readonly Dictionary<string, DateTimeOffset?> _crashHeld = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a recovery took the card off <paramref name="trial"/> (it was in force when one ran).</summary>
    private bool GivenBack(IGpuTarget target, ExperimentTrial trial) =>
        _givenBack.GetValueOrDefault(target.StableId) is { } back && back == Identity(trial);

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
    /// when no trial was on the card then. <paramref name="held"/>: the give-back has not landed ("crashed-held"), so the
    /// card may still hold the trial in force: nothing leaves the record, and the trial in force that crashed stays in force
    /// with its ending on record (<see cref="ExperimentTrial.Ending"/>), as <see cref="Leave"/> keeps one held off.
    /// <paramref name="appended"/>: whether the point was written now, not on record already.
    /// </summary>
    private DataPoint? CrashedAt(IGpuTarget target, string? note, uint? crashUv, DateTimeOffset answered, DateTimeOffset stopped, out List<string> lines, out bool appended, bool held = false)
    {
        lines = [];
        appended = false;
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
                appended = Records(onCard, Experiment.Crash);
                if (appended) Experiment.Append(data, point);
                if (gone >= 0) list![gone] = list[gone] with { Trial = onCard with { Ending = Experiment.Crash } };
            }
            if (held)
            {
                if (gone < 0 && onCard != null) (exp! with { Trial = onCard with { Ending = Experiment.Crash } }).Save(path);
            }
            else if (clear && exp is { Trial: { } inForce })
            {
                Gone(target, exp, gone < 0 && onCard != null ? inForce with { Ending = Experiment.Crash } : inForce);
                (exp with { Trial = null }).Save(path);
            }
            return point;
        }
    }
}
