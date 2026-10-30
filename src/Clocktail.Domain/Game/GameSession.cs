using System.Text.Json;
using System.Globalization;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Game;

public sealed record GameSnapshot(GpuTarget Target, string Curve, uint PowerMw, uint MinPowerMw, int TopMhz,
    ulong? VramBytes, IReadOnlyList<ProfileWrite> Cells,
    // Independent regional status (effective minus base), never inferred from P0 or point controls.
    IReadOnlyList<long>? EffectiveCoreOffsetsKhz = null)
{
    public static string Id(ProfileWrite w) => w.Kind + "/" + w.Key;
    public bool Same(GameSnapshot other) => Target == other.Target && Curve == other.Curve && PowerMw == other.PowerMw && MinPowerMw == other.MinPowerMw
        && Cells.OrderBy(Id).SequenceEqual(other.Cells.OrderBy(Id))
        && (EffectiveCoreOffsetsKhz == null ? other.EffectiveCoreOffsetsKhz == null
            : other.EffectiveCoreOffsetsKhz != null && EffectiveCoreOffsetsKhz.SequenceEqual(other.EffectiveCoreOffsetsKhz));
}

/// <summary>Implemented only inside the card host. Verification precedes every snapshot; tests supply fake transports.</summary>
public interface IGameAccess
{
    GpuTarget Identity { get; }
    void Verify(); // fresh PnP/NVML/NVAPI identity, context-free CUDA and Vulkan device proof
    void BeforeStart() { }
    GameSnapshot Read();
}

public sealed record GameCommand(string? Pci = null, DateTimeOffset? SessionAt = null, DateTimeOffset? TrialAt = null,
    IReadOnlyList<string>? Levers = null, string? Lever = null, uint Value = 0, string? Mode = null, string? Note = null,
    int? ProcessId = null, DateTimeOffset? ProcessStartedAt = null);

public sealed record GameCompletion(string Run, string StableId, GameSnapshot Snapshot, string Mode, bool CrashRecorded,
    DateTimeOffset? SessionAt, DateTimeOffset? TrialAt, bool TrialMarked);

public sealed record GameRecord(string Run, string StableId, GameSnapshot Baseline, GameSnapshot Expected,
    IReadOnlyList<ProfileWrite> Daily, Experiment Session, IReadOnlyList<string> Levers,
    IReadOnlyList<string> Attempted, DateTimeOffset? TrialAt, bool Pending = false,
    IReadOnlyList<ProfileWrite>? PendingWrites = null, bool TrialMarked = false,
    int? ProcessId = null, DateTimeOffset? ProcessStartedAt = null, string? PendingChange = null,
    IReadOnlyList<ProfileWrite>? Originals = null, bool Restoring = false);

/// <summary>
/// A durable, card-host-owned measurement transaction. Every request runs under the broker's operation gate and the
/// caller's actual judge.lock. The client supplies no curve, power ceiling or restoration values.
/// </summary>
public sealed class GameSession(string stableId, string path, IGameAccess access,
    Func<BrokerRequest, BrokerResponse> perform, Func<Experiment?> session, Action<Experiment> saveSession,
    Func<DailyProfile?> daily, Func<string, string, string?> original, Func<string, bool> holdsLease, Func<DateTimeOffset, bool>? crashRecorded = null, Func<DateTimeOffset, bool>? resetPending = null, Func<int, DateTimeOffset, bool>? processAlive = null, Func<string, string, DateTimeOffset?>? crashByChange = null)
{
    public const int Protocol = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string PathBeside(string journal) => Path.Combine(Path.GetDirectoryName(journal)!, "game-recovery.json");
    private GameRecord? Load()
    {
        var bytes = AtomicFile.Read(path, 4 * 1024 * 1024);
        if (bytes == null)
        {
            if (File.Exists(path) || File.Exists(AtomicFile.BackupPath(path))) throw new InvalidDataException("the game recovery record is unreadable; it was preserved");
            return null;
        }
        try
        {
            var state = JsonSerializer.Deserialize<GameRecord>(bytes, Json) ?? throw new InvalidDataException("empty game recovery record");
            if (string.IsNullOrWhiteSpace(state.Run) || string.IsNullOrWhiteSpace(state.StableId)
                || state.Baseline?.Cells is not { Count: > 0 } || state.Expected?.Cells is not { Count: > 0 }
                || state.Session == null || state.Daily == null || state.Attempted == null || state.Levers == null)
                throw new InvalidDataException("incomplete game recovery record; it was preserved");
            return state;
        }
        catch (JsonException e) { throw new InvalidDataException("invalid game recovery record; it was preserved", e); }
    }
    private void Save(GameRecord state) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(state, Json));
    private static BrokerResponse Ok(object value) => new(true, Result: JsonSerializer.SerializeToElement(value));
    private static BrokerResponse No(string why) => new(false, why, Code: BrokerCode.CannotWrite);
    private GameSnapshot Read()
    {
        access.Verify();
        var first = access.Read();
        first.Target.Validate();
        if (!first.Same(access.Read())) throw new InvalidOperationException("settings moved during the card-host snapshot; nothing is judged");
        return first;
    }
    private static bool Active(Experiment? e, GameRecord s) => e?.StartedAt == s.Session.StartedAt && e.Trial?.At == s.TrialAt && e.Trial?.Ending == null && (e.Trial?.Marked ?? false) == s.TrialMarked;

    public BrokerResponse Call(string kind, string? run, GameCommand command)
    {
        if (kind == "info") return Ok(new { protocol = Protocol, target = access.Identity });
        if (run == null || !holdsLease(run)) return No("the game operation requires the caller's live judge.lock");
        var state = Load();
        if (state != null && (state.StableId != stableId || state.Baseline.Target.Pci != access.Identity.Pci))
            return No("the recovery record belongs to another GPU; nothing written");
        if (state is { Pending: true, PendingChange: { } change })
        {
            var candidateTrial = session()?.Trial;
            DateTimeOffset? candidate = candidateTrial?.Change == change && candidateTrial.Curve == state.Baseline.Curve ? candidateTrial.At : crashByChange?.Invoke(change, state.Baseline.Curve);
            if (candidate != null && candidate != state.TrialAt)
            {
                state = state with { TrialAt = candidate, TrialMarked = candidateTrial?.At == candidate && candidateTrial.Marked };
                Save(state); // recover the exact trial identity after a worker died between its write and acknowledgement
            }
        }
        if (kind == "verify")
        {
            if (state != null && RecoveryRefusal(state, allowPendingReset: true) is { } refused) return No(refused);
            if (state == null) Read();
            return Ok(new { verified = true });
        }
        if (kind == "begin")
        {
            if (command.Pci != access.Identity.Pci) return No("the game target does not match the card host");
            if (Receipt()?.Run == run) return No("this run has already completed; use a new run identity");
            if (state != null)
            {
                if (state.Run == run) return No("this run already has a baseline; do not replace its recovery record");
                var recovered = RestoreDaily(state, run, "interrupted game test");
                if (!recovered.Ok) return recovered;
            }
            var e = session();
            if (e == null || e.StartedAt != command.SessionAt || e.Trial?.At != command.TrialAt || e.Trial?.Ending != null)
                return No("the requested session/trial changed or ended before the lease was acquired");
            var levers = command.Levers ?? [];
            if (levers.Any(l => !GameCatalog.AllLevers.Contains(l))) return No("unknown game lever");
            var fresh = Read();
            access.BeforeStart();
            if (e.Trial != null && e.Trial.Curve != fresh.Curve) return No("the curve on the card is not the trial's curve");
            var profile = daily();
            if (e.Trial == null && profile is { Suspended: false } && profile.Writes.Any(w =>
                fresh.Cells.FirstOrDefault(c => GameSnapshot.Id(c) == GameSnapshot.Id(w)) is not { } actual || actual.Value != w.Value))
                return No("the daily profile and the card disagree; reconcile the card before testing");
            if (e.Trial == null && HasImplicitCore(profile) && !MatchesImplicitCore(profile, fresh))
                return No("the daily effective Core curve could not be verified; reconcile the card before testing");
            if (levers.Contains(GameCatalog.Boost) && fresh.Cells.Any(c => c.Kind == "clocklimit" && c.Key is "min" or "voltage" && c.Value != "0"))
                return No("an existing clock floor/point lock must not be replaced by the game test");
            var wanted = fresh.Cells.Select(c => new ProfileWrite(c.Kind, c.Key,
                profile is { Suspended: false } && profile.Writes.FirstOrDefault(w => GameSnapshot.Id(w) == GameSnapshot.Id(c)) is { } kept
                    ? kept.Value : original(c.Kind, c.Key) ?? c.Value)).ToList();
            var originals = fresh.Cells.Select(c => c with { Value = original(c.Kind, c.Key) ?? c.Value }).ToList();
            state = new(run, stableId, fresh, fresh, wanted, e, levers, [], e.Trial?.At, TrialMarked: e.Trial?.Marked ?? false, Originals: originals);
            Save(state);
            return Ok(new { protocol = Protocol, baseline = fresh });
        }
        if (kind == "finish" && Receipt() is { } receipt && receipt.Run == run)
            return Replay(receipt, state);
        if (state == null) return No("no game baseline or matching verified completion is recorded");
        if (kind != "recover" && state.Run != run) return No("another run owns the game recovery record");
        if ((kind is "recover" or "finish") && !GameGone(state))
            return No("the recorded game process is still alive or cannot be checked; no restoration under its load");
        if (kind == "recover") return RestoreDaily(state, run, "interrupted game recovery");
        if (state.TrialAt is { } pendingAt && resetPending?.Invoke(pendingAt) == true && crashRecorded?.Invoke(pendingAt) != true
            && !(kind == "finish" && command.Mode == "reset"))
            return No("a target reset awaits its crash record; no write or manual crash report replaced that evidence");
        if (kind == "finish")
        {
            if (command.Mode is not ("baseline" or "failed" or "reset")) return No("unknown game completion mode");
            if (command.Mode == "reset")
            {
                if (session()?.Trial != null || state.TrialAt is { } at && crashRecorded?.Invoke(at) != true)
                    return No("reset recording/recovery is pending for the measured trial; its record remains intact");
                return VerifyDaily(state, state.TrialAt != null);
            }
            if (command.Mode == "failed")
            {
                if (!Active(session(), state)) return No("the measured trial changed; no different trial was blamed");
                if (!Read().Same(state.Expected)) return No("the card no longer holds the measured settings; no stale trial was blamed");
                var failed = state.TrialAt != null
                    ? perform(new(BrokerRequest.OpExperiment, stableId, Kind: "crash", Value: command.Note, Lease: run))
                    : perform(new(BrokerRequest.OpProfile, stableId, Kind: "apply", Value: "game test failed", Lease: run));
                if (!failed.Ok) return failed;
                return VerifyDaily(state, state.TrialAt != null);
            }
            if (state.Pending) return RestoreDaily(state, run, "an interrupted game write");
            var currentSession = session();
            if (state.Restoring && currentSession?.StartedAt == state.Session.StartedAt && currentSession.Trial?.At == state.Session.Trial?.At
                && currentSession.Trial?.Ending == null && (currentSession.Trial?.Marked ?? false) == (state.Session.Trial?.Marked ?? false))
            {
                var restored = Read();
                if (restored.Same(state.Baseline)) return Complete(state, restored, "baseline", false); // metadata was saved before a worker died
            }
            if (!Active(session(), state)) return No("the measured session/trial ended; the starting trial was not recreated");
            var now = Read();
            if (!now.Same(state.Expected) && !PartialRestore(state, now))
            {
                bool fanSafetyRelease = currentSession?.Trial?.FanPct == null && now.Target == state.Expected.Target
                    && now.Curve == state.Expected.Curve && now.PowerMw == state.Expected.PowerMw && now.MinPowerMw == state.Expected.MinPowerMw
                    && state.Expected.Cells.Any(c => c.Kind == "fan" && c.Value != "auto") && now.Cells.Count == state.Expected.Cells.Count
                    && now.Cells.All(c => state.Expected.Cells.Contains(c) || c.Kind == "fan" && c.Value == "auto");
                return fanSafetyRelease ? RestoreDaily(state, run, "the service's fan safety release interrupted the test")
                    : No("the card changed outside this job; no stale baseline was applied");
            }
            state = state with { Restoring = true };
            Save(state); // before restoring any cell or session metadata
            foreach (var cell in state.Baseline.Cells.Where(c => state.Attempted.Contains(GameSnapshot.Id(c))))
            {
                var r = perform(new(BrokerRequest.OpWrite, stableId, Kind: cell.Kind, Key: cell.Key, Value: cell.Value, Lease: run));
                if (!r.Ok) return r;
                // Persist partial progress, so a pipe break or worker loss does not discard the restore obligations.
                state = state with { Expected = Read() };
                Save(state);
            }
            var final = Read();
            if (!final.Same(state.Baseline)) return No("starting settings did not read back; recovery record preserved");
            if (!Active(session(), state)) return No("the trial ended during restoration; it was not recreated");
            saveSession(state.Session);
            return Complete(state, final, "baseline", false);
        }
        if (!Active(session(), state)) return No("the measured session/trial changed or ended");
        if (state.Pending) return No("a write is uncertain; finish/recover before continuing");
        var current = Read();
        if (!current.Same(state.Expected)) return No("the card changed outside this run; nothing written or credited");
        if (kind == "check") return Ok(new { verified = true });
        if (kind == "launch")
        {
            if (state.ProcessId != null || command.ProcessId is not > 0 || command.ProcessStartedAt == null)
                return No("invalid or duplicate game process registration");
            if (processAlive?.Invoke(command.ProcessId.Value, command.ProcessStartedAt.Value) != true)
                return No("the suspended game process could not be confirmed alive");
            Save(state with { ProcessId = command.ProcessId, ProcessStartedAt = command.ProcessStartedAt });
            return Ok(new { registered = true });
        }
        if (kind != "lever" || command.Lever == null || !state.Levers.Contains(command.Lever)) return No("this lever was not authorized by the run");
        string operation = $"game_test {state.Run} op-{Guid.NewGuid():N}: {command.Lever} {command.Value}";
        var args = new Dictionary<string, object?> { ["curve"] = state.Baseline.Curve, ["keep_curve"] = true, ["change"] = operation, ["context"] = new[] { command.Note } };
        var affected = new List<string>();
        switch (command.Lever)
        {
            case GameCatalog.Power:
                if (command.Value < current.MinPowerMw || command.Value > state.Baseline.PowerMw) return No("power is outside the locked baseline's range");
                args["power_mw"] = command.Value; affected.Add("power/fe"); break;
            case GameCatalog.Fans:
                if (command.Value != 0 && command.Value is < 30 or > 100) return No("fan duty is outside the game test's safe range");
                args["fan_pct"] = command.Value; affected.AddRange(current.Cells.Where(c => c.Kind == "fan").Select(GameSnapshot.Id)); break;
            case GameCatalog.Boost:
                if (command.Value > 1) return No("Boost Lock is 0 or 1");
                args["writes"] = new[] { new { kind = "clocklimit", key = "voltage", value = command.Value == 0 ? "0" : "1500000" } };
                affected.Add("clocklimit/voltage"); break;
            default: return No("unknown lever");
        }
        if (affected.Count == 0 || affected.Any(id => !current.Cells.Any(c => GameSnapshot.Id(c) == id))) return No("the controlled setting could not be read");
        string wantedValue = command.Lever switch
        {
            GameCatalog.Power => command.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GameCatalog.Fans => command.Value == 0 ? "auto" : "manual:" + command.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => command.Value == 0 ? "0" : "1500000",
        };
        state = state with { Pending = true, PendingChange = operation, Attempted = state.Attempted.Union(affected).ToList(),
            PendingWrites = current.Cells.Where(c => affected.Contains(GameSnapshot.Id(c))).Select(c => c with { Value = wantedValue }).ToList() };
        Save(state); // BEFORE the first write
        var answer = perform(new(BrokerRequest.OpExperiment, stableId, Kind: "try", Value: JsonSerializer.Serialize(args), Lease: run));
        if (!answer.Ok) return answer;
        var after = Read();
        if (affected.Any(id => after.Cells.FirstOrDefault(c => GameSnapshot.Id(c) == id)?.Value != wantedValue))
            return No("the lever acknowledged success but independent readback differs; recovery record preserved");
        var untouched = current.Cells.Where(c => !affected.Contains(GameSnapshot.Id(c))).ToList();
        if (untouched.Any(c => !after.Cells.Contains(c))) return No("a lever changed an unrelated setting; recovery record preserved");
        var trial = session()?.Trial ?? throw new InvalidDataException("a lever landed without its trial record");
        if (trial.Change != operation) return No("the acknowledged trial is not this operation's; recovery record preserved");
        state = state with { Expected = after, TrialAt = trial.At, Pending = false, PendingWrites = null, PendingChange = null, TrialMarked = trial.Marked };
        Save(state);
        return Ok(new { verified = true, trial });
    }

    private static bool PartialRestore(GameRecord state, GameSnapshot actual) => state.Restoring
        && actual.Target == state.Expected.Target && actual.Curve == state.Expected.Curve && actual.MinPowerMw == state.Expected.MinPowerMw
        && (state.Attempted.Contains("power/fe") || actual.PowerMw == state.Expected.PowerMw)
        && actual.Cells.Count == state.Expected.Cells.Count && actual.Cells.All(c => state.Expected.Cells.Contains(c)
            || state.Attempted.Contains(GameSnapshot.Id(c)) && state.Baseline.Cells.Contains(c));

    private string? RecoveryRefusal(GameRecord state, bool allowPendingReset = false)
    {
        if (!GameGone(state)) return "the recorded game process is not confirmed gone; recovery must wait";
        var e = session();
        if (e?.Trial is { } t && t.At != state.TrialAt
            && !(state.Restoring && e.StartedAt == state.Session.StartedAt && t.At == state.Session.Trial?.At))
            return "a different trial is active; recovery did not overwrite it";
        if (!allowPendingReset && state.TrialAt is { } at && resetPending?.Invoke(at) == true && crashRecorded?.Invoke(at) != true)
            return "a target reset awaits its crash record; recovery preserved that evidence";
        var current = Read();
        var permitted = state.Expected.Cells.Concat(state.Baseline.Cells).Concat(state.Daily).Concat(state.PendingWrites ?? []).Concat(state.Originals ?? []).ToList();
        return state.Expected.Cells.Any(c => !current.Cells.Any(n => GameSnapshot.Id(n) == GameSnapshot.Id(c))) || current.Cells.Any(c => !permitted.Contains(c)
                && !(c.Kind == "vf" && c.Key == "core" && MatchesImplicitCore(daily(), current)))
            ? "settings changed outside the recorded transaction; no recovery overwrite was attempted" : null;
    }

    private BrokerResponse RestoreDaily(GameRecord state, string run, string reason)
    {
        if (RecoveryRefusal(state) is { } refused) return No(refused);
        var r = perform(new(BrokerRequest.OpProfile, stableId, Kind: "apply", Value: reason, Lease: run));
        return r.Ok ? VerifyDaily(state) : r;
    }
    private BrokerResponse VerifyDaily(GameRecord state, bool recorded = false)
    {
        var actual = Read();
        var profile = daily();
        var expected = (profile is { Suspended: false } ? state.Daily : state.Originals ?? state.Daily).ToDictionary(GameSnapshot.Id);
        if (profile is { Suspended: false }) foreach (var cell in profile.Writes) expected[GameSnapshot.Id(cell)] = cell;
        if (HasImplicitCore(profile))
        {
            if (!MatchesImplicitCore(profile, actual)) return No("daily effective Core curve did not read back; recovery record preserved");
            // This is generated by the saved P0 target, not the journal's pre-tuning curve.
            expected.Remove("vf/core");
        }
        if (expected.Values.Any(c => !actual.Cells.Contains(c))) return No("daily settings did not read back; recovery record preserved");
        return Complete(state, actual, "daily", recorded);
    }

    private static bool HasImplicitCore(DailyProfile? profile) => profile is { Suspended: false }
        && !profile.Writes.Any(w => w.Kind == "vf")
        && profile.Writes.Any(w => w.Kind == "pstates20" && w.Key == "core");

    private static bool MatchesImplicitCore(DailyProfile? profile, GameSnapshot snapshot)
    {
        if (!HasImplicitCore(profile)) return false;
        var core = profile!.Writes.Single(w => w.Kind == "pstates20" && w.Key == "core");
        if (!int.TryParse(core.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int target)
            || !snapshot.Cells.Contains(core) || snapshot.EffectiveCoreOffsetsKhz is not { Count: > 0 } offsets
            || !snapshot.Cells.Contains(new ProfileWrite("vf", "core", snapshot.Curve))) return false;
        // Legacy status has no independent base and supplies no proof. Require every editable point,
        // with the same sub-MHz tolerance as the broker's coupled P0 restoration.
        return offsets.Count == VfOffsets.Decode(snapshot.Curve).Length
            && offsets.All(value => value > (long)target - 1_000 && value < (long)target + 1_000);
    }

    private bool GameGone(GameRecord state) => state.ProcessId is not int pid
        || state.ProcessStartedAt is { } began && processAlive?.Invoke(pid, began) == false;

    private GameCompletion? Receipt()
    {
        var bytes = AtomicFile.Read(path + ".completed", 4 * 1024 * 1024);
        if (bytes == null) return null;
        try { return JsonSerializer.Deserialize<GameCompletion>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    private BrokerResponse Complete(GameRecord state, GameSnapshot snapshot, string mode, bool recorded)
    {
        var current = session();
        var receipt = new GameCompletion(state.Run, stableId, snapshot, mode, recorded, current?.StartedAt, current?.Trial?.At, current?.Trial?.Marked ?? false);
        AtomicFile.Write(path + ".completed", JsonSerializer.SerializeToUtf8Bytes(receipt, Json)); // persist BEFORE removing obligations
        Delete();
        return CompletionReply(receipt);
    }

    private BrokerResponse Replay(GameCompletion receipt, GameRecord? pending)
    {
        var current = session();
        if (pending != null && (pending.Run != receipt.Run || !GameGone(pending)) || receipt.StableId != stableId
            || receipt.Snapshot?.Cells is not { Count: > 0 } || receipt.Snapshot?.Target?.Pci != access.Identity.Pci
            || current?.StartedAt != receipt.SessionAt || current?.Trial?.At != receipt.TrialAt
            || (current?.Trial?.Marked ?? false) != receipt.TrialMarked || current?.Trial?.Ending != null
            || !Read().Same(receipt.Snapshot))
            return No("the completion receipt no longer matches the card/session; no stale success was returned");
        if (pending != null) Delete(); // interrupted only while deleting an already-verified recovery record
        return CompletionReply(receipt);
    }

    private static BrokerResponse CompletionReply(GameCompletion receipt) =>
        Ok(new { restored = true, verified = true, mode = receipt.Mode, crash_recorded = receipt.CrashRecorded });

    private void Delete()
    {
        File.Delete(path);
        File.Delete(AtomicFile.BackupPath(path));
    }
}
