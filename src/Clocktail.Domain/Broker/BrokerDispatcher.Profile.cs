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
    private BrokerResponse Profile(IGpuTarget target, BrokerRequest req)
    {
        if (ClientAsks.Value && req.Kind is "crashed" or "crashed-held" or "start" or "resume" or "apply-power" or "after-reset")
            return Fail("that request is the service's own (its answer to a driver reset or service lifecycle); a client cannot send it", BrokerCode.BadRequest);
        Settle(target);   // a record from before the boot is settled before anything here reads or puts back its cells
        var path = DailyProfile.PathBeside(target.Journal.Path);
        var profile = DailyProfile.Load(path);
        switch (req.Kind)
        {
            case "show":
                return Ok(new { success = true, path, profile, approved = profile != null && Approved(target, profile), approvals_unavailable = _profileApprovals.Unavailable, card = new { vbios = target.Vbios, driver = target.Driver } });
            case "preview":
            case "preview-adopt":
            {
                // What keep, or a saved profile (Value: its name), would make the daily profile, against the daily
                // profile and the card: nothing is written or saved. The agent shows it before asking. "preview-adopt" is
                // what "adopt" would keep (0.4.0).
                bool adopt = req.Kind == "preview-adopt";
                var (writes, left, _) = req.Value is { Length: > 0 } name && !adopt ? (SavedWrites(target, name), [], []) : KeepWrites(target, profile, adopt);
                var cardNow = CardValues(target, writes);
                string source = req.Value is { Length: > 0 } savedName && !adopt ? $"'{savedName}'" : adopt ? "keep_as_daily (adopt)" : "keep_as_daily";
                return Ok(new
                {
                    success = true,
                    source,
                    values_fingerprint = ProfileSnapshot.Fingerprint(source, writes),
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
                if (ClientAsks.Value && AuthorizeProfileChange == null)
                    return Fail($"profile mutation '{req.Kind}' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
                if (ClientAsks.Value && string.IsNullOrWhiteSpace(req.ExpectedProfileFingerprint))
                    return Fail($"keeping a daily profile requires the preview's expected profile fingerprint ({BrokerRequest.OpProfile} preview{(adopt ? "-adopt" : "")}); preview and confirm again", BrokerCode.BadRequest);
                string fpSource = adopt ? "keep_as_daily (adopt)" : "keep_as_daily";
                string preToken = DailyStateToken(profile);
                var (writes, _, adopted) = KeepWrites(target, profile, adopt);
                string preFp = ProfileSnapshot.Fingerprint(fpSource, writes);
                if (req.ExpectedProfileFingerprint is { } expected && expected != preFp)
                    return Fail("the profile values changed after the preview: nothing kept; preview and confirm again", BrokerCode.CannotWrite);
                var inForce = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial;
                bool trialWall = inForce?.PowerMw != null;
                // A trial that crashed but is still on the card (its put-back held off) is never what a keep takes from it.
                if (inForce?.Ending == Experiment.Crash)
                    return Fail($"the trial on the card crashed ({inForce.Change}) and waits for the card to be free to go back: a crash is not kept; back_to_daily", BrokerCode.CannotWrite);
                if (writes.Count == 0)
                    return Fail(adopt ? "nothing on the card differs from stock or from the daily profile; nothing to keep" : "nothing this broker holds differs from stock; nothing to keep", BrokerCode.CannotWrite);
                if (AuthorizeClientProfileChange(target, req.Kind!, $"Keep {writes.Count} setting(s) as daily profile", writes, req.Value) is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                inForce = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial;
                if (inForce?.Ending == Experiment.Crash)
                    return Fail($"the trial on the card crashed ({inForce.Change}) while waiting for confirmation: nothing kept; back_to_daily", BrokerCode.CannotWrite);
                (writes, _, adopted) = KeepWrites(target, profile, adopt);
                if (writes.Count == 0
                    || DailyStateToken(profile) != preToken
                    || ProfileSnapshot.Fingerprint(fpSource, writes) != preFp)
                    return Fail("the card or profile changed while waiting for confirmation: nothing kept; preview and confirm again", BrokerCode.CannotWrite);
                trialWall = inForce?.PowerMw != null;
                string listed = string.Join("; ", writes.Select(w => $"{w.Kind}/{w.Key}={w.Value}"));
                var trialKept = CloseTrial(target, Experiment.Stable, out _, "kept as the daily profile");
                // What was adopted before stays adopted (a trial over it records it as its original); this keep's own adds to it.
                IReadOnlyList<ProfileWrite> adoptedAll = [.. (profile?.Adopted ?? []).Where(a => !adopted.Any(n => n.Kind == a.Kind && n.Key == a.Key)), .. adopted];
                var kept = new DailyProfile(DailyProfile.InWriteOrder(writes), _host.Now, History: profile?.History, Adopted: adoptedAll.Count > 0 ? adoptedAll : null)
                    .On(target.Vbios, target.Driver)
                    .Noted(_host.Now, req.Value is { } why ? $"kept: {listed} ({why})" : $"kept: {listed}");
                SaveApprovedDaily(target, kept, path);
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
            {
                // Suspends the daily profile and writes nothing (the person's CLI: stock at the next start or reset). The card
                // is as it was, so a trial in force stays in force; until 0.3.7 "off" closed it while the card still ran it.
                string preToken = DailyStateToken(profile);
                if (AuthorizeClientProfileChange(target, "off",
                    profile == null ? "Suspend daily profile (none currently kept)" : "Suspend the daily profile (card goes to stock on next start or reset)",
                    profile?.Writes, req.Value) is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                if (ClientAsks.Value && DailyStateToken(profile) != preToken)
                    return Fail("the daily profile changed while waiting for confirmation: nothing suspended; try again", BrokerCode.CannotWrite);
                if (profile == null)
                {
                    _profileApprovals.Forget(target.StableId, ProfileApproval.Daily);
                    return Ok(new { success = true, path, profile });
                }
                profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                SaveApprovedDaily(target, profile, path);
                Log(target, "Daily profile suspended", ["The card is at stock from the next start or driver reset, until the daily profile is loaded or kept again"]);
                return Ok(new { success = true, path, profile });
            }
            case "stock":
            {
                // The card to stock now (the agent's stock, an uninstall): the daily profile suspended so nothing puts it back,
                // and what this broker holds back to its original in the same operation, so the trial in force leaves the
                // record once that landed (Leave). Until 0.3.7 stock was "off", which closed the trial, then a recovery of
                // its own: a recovery a stability run held off left the trial on the card and off the record.
                string preToken = DailyStateToken(profile);
                if (AuthorizeClientProfileChange(target, "stock", "Suspend the daily profile and restore Clocktail-owned GPU settings to stock", profile?.Writes, req.Value) is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                if (ClientAsks.Value && DailyStateToken(profile) != preToken)
                    return Fail("the daily profile changed while waiting for confirmation: nothing changed; try again", BrokerCode.CannotWrite);
                if (profile != null)
                {
                    profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                    SaveApprovedDaily(target, profile, path);
                    Log(target, "Daily profile suspended", ["The card is at stock until the daily profile is loaded or kept again"]);
                }
                else _profileApprovals.Forget(target.StableId, ProfileApproval.Daily);
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
                string preToken = DailyStateToken(profile);
                if (AuthorizeClientProfileChange(target, "factory", "Suspend the daily profile and clear GPU settings (including other tools') to factory defaults", profile?.Writes, req.Value) is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                if (ClientAsks.Value && DailyStateToken(profile) != preToken)
                    return Fail("the daily profile changed while waiting for confirmation: nothing changed; try again", BrokerCode.CannotWrite);
                if (profile != null)
                {
                    profile = profile.Noted(_host.Now, "suspended by request") with { Suspended = true };
                    SaveApprovedDaily(target, profile, path);
                    Log(target, "Daily profile suspended", ["The card is at stock until the daily profile is loaded or kept again"]);
                }
                else _profileApprovals.Forget(target.StableId, ProfileApproval.Daily);
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
            case "start":
            {
                // The service's daily profile at start, once the GPU is idle: "apply", for the trial in force when this
                // service began (it ran on a card a reboot or the restart's recovery has given back) or none. A trial
                // written since is the agent's, written over that card: it stays, and the agent running it decides, as
                // after a reset. Until 0.8.1 the start's "apply" ended it as survived and put the daily profile over it.
                if (Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial is { } since
                    && Identity(since) != _trialAtStart.GetValueOrDefault(target.StableId))
                {
                    Log(target, "At start: a trial written since the service started is in force, nothing written", [req.Value is { } w ? $"Reason: {w}" : null, $"Trial: {since.Change}"]);
                    return Ok(new { success = true, skipped = "a trial began after the service started: the agent running it decides", profile });
                }
                return ApplyDailyCase(target, profile, path, req.Value);
            }
            case "finish-job":
            {
                // Cleanup restores the already-approved daily profile; it must not wait for a person
                // while trial-only settings are still on the card. It never replaces/suspends the daily profile.
                if (string.IsNullOrWhiteSpace(req.Lease)) return Fail("job cleanup needs the job's identity", BrokerCode.BadRequest);
                var trial = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial;
                if (trial != null && trial.JobId != req.Lease)
                    return Ok(new { success = true, skipped = "another trial is in force: left unchanged", trial_id = trial.Id });
                return ApplyDailyCase(target, profile, path, req.Value);
            }
            case "apply":
            {
                // Value: why, for the log ("the broker started", an agent's back_to_daily).
                // back_to_daily and editor undo restore existing consent, not grant new consent.
                return ApplyDailyCase(target, profile, path, req.Value);
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
                if (ClientAsks.Value && AuthorizeProfileChange == null)
                    return Fail("profile mutation 'load' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
                if (profile == null) return Fail("no daily profile is kept; nothing to load", BrokerCode.CannotWrite);
                string preToken = DailyStateToken(profile);
                if (AuthorizeClientProfileChange(target, "load", $"Load daily profile ({profile.Writes.Count} setting(s))", profile.Writes, req.Value) is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                if (profile == null || (ClientAsks.Value && DailyStateToken(profile) != preToken))
                    return Fail("the daily profile changed while waiting for confirmation: nothing loaded; try again", BrokerCode.CannotWrite);
                profile = profile.Noted(_host.Now, "loaded by request").On(target.Vbios, target.Driver) with { Suspended = false };
                SaveApprovedDaily(target, profile, path);
                var r = Leave(target, Experiment.Survived, "the daily profile went back on", _ =>
                {
                    var a = ApplyProfile(target, profile, path);
                    return (a, a);
                }).Answer;
                Log(target, "Daily profile loaded", Writes(r));
                return r;
            }
            case "preset":
                // Value: a saved profile's name (the op keeps its old name; no profile ships with Clocktail). It replaces
                // the daily profile (the old one's writes go into the log) and goes on now.
                return LoadSaved(target, req.Value, profile, path, req.ExpectedProfileFingerprint);
            case "save":
            {
                // Value: the name, any name. The daily profile as it is kept now, saved under it; one already saved under
                // the name (whatever its case) is replaced.
                if (ClientAsks.Value && AuthorizeProfileChange == null)
                    return Fail("profile mutation 'save' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
                var (name, bad) = SavedProfiles.Normalize(req.Value);
                if (name == null) return Fail(bad!, BrokerCode.BadRequest);
                if (profile == null) return Fail("no daily profile is kept yet: approve and keep this GPU's settings with keep_as_daily first, then save them", BrokerCode.CannotWrite);
                if (!Approved(target, profile)) return Fail("the daily profile is not approved; load_daily and confirm it before saving a preset", BrokerCode.CannotWrite);
                string preToken = DailyStateToken(profile);
                if (AuthorizeClientProfileChange(target, "save", $"Save current daily profile as '{name}'", profile.Writes, name) is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                if (profile == null || (ClientAsks.Value && DailyStateToken(profile) != preToken))
                    return Fail("the daily profile changed while waiting for confirmation: nothing saved; try again", BrokerCode.CannotWrite);
                var file = SavedProfiles.PathBeside(target.Journal.Path);
                var saved = new DailyProfile(profile.Writes, _host.Now, Adopted: RelevantAdopted(profile.Adopted, profile.Writes))
                    .Noted(_host.Now, $"saved as '{name}' from the daily profile kept {profile.KeptAt:yyyy-MM-dd HH:mm}");
                bool replaced = SavedProfiles.Save(file, name, saved);
                _profileApprovals.Remember(target.StableId, ProfileApproval.Saved(name), saved);
                Log(target, $"Profile saved · {name}", [replaced ? "Replaced the profile saved under this name" : null, .. profile.Writes.Select(w => $"{w.Kind}/{w.Key} = {w.Value}")]);
                return Ok(new { success = true, name, path = file, replaced, profile = saved });
            }
            case "saved":
                return Ok(new
                {
                    success = true,
                    profiles = SavedProfiles.All(SavedProfiles.PathBeside(target.Journal.Path)).Select(p => new { name = p.Name, saved_at = p.Profile.KeptAt, writes = p.Profile.Writes, approved = Approved(target, p.Profile, ProfileApproval.Saved(p.Name)) }),
                });
            case "forget":
            {
                // Value: a saved profile's name. The daily profile is never deleted here.
                if (ClientAsks.Value && AuthorizeProfileChange == null)
                    return Fail("profile mutation 'forget' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
                var file = SavedProfiles.PathBeside(target.Journal.Path);
                if (SavedProfiles.Find(file, req.Value) is not var (savedName, savedProfile))
                    return Fail($"no profile is saved as '{req.Value}'", BrokerCode.CannotWrite);
                string preSavedFp = ProfileSnapshot.Fingerprint($"'{savedName}'", savedProfile.Writes);
                if (AuthorizeClientProfileChange(target, "forget", $"Delete saved profile '{savedName}'", savedProfile.Writes, savedName) is { } denied)
                    return denied;
                if (SavedProfiles.Find(file, req.Value) is not var (_, currentSaved)
                    || ProfileSnapshot.Fingerprint($"'{savedName}'", currentSaved.Writes) != preSavedFp)
                    return Fail("the saved profile changed while waiting for confirmation: nothing deleted; try again", BrokerCode.CannotWrite);
                _profileApprovals.Forget(target.StableId, ProfileApproval.Saved(savedName));
                var gone = SavedProfiles.Delete(file, req.Value);
                if (gone == null) return Fail($"no profile is saved as '{req.Value}'", BrokerCode.CannotWrite);
                Log(target, $"Saved profile deleted · {gone.Value.Name}", [.. gone.Value.Profile.Writes.Select(w => $"Was: {w.Kind}/{w.Key} = {w.Value}")]);
                return Ok(new { success = true, name = gone.Value.Name, deleted = gone.Value.Profile });
            }
            case "wall":
            {
                // The daily power limit, set on its own (a trial's wall is never kept): it replaces the profile's, or starts
                // one. It goes on now, unless the card is at stock (suspended) or a trial runs under its own wall, which
                // stays until the trial ends.
                if (ClientAsks.Value && AuthorizeProfileChange == null)
                    return Fail("profile mutation 'wall' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
                if (!uint.TryParse(req.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mw) || mw == 0)
                    return Fail("wall needs the daily power limit in mW", BrokerCode.BadRequest);
                if (profile != null && !Approved(target, profile))
                    return Fail("the daily profile is not approved; load_daily and confirm all its settings before changing only its power limit", BrokerCode.CannotWrite);
                string key = PowerCap.Key(PowerRequestKind.Ordinary), value = mw.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string preToken = DailyStateToken(profile);
                bool preTrialWall = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial?.PowerMw != null;
                if (AuthorizeClientProfileChange(target, "wall", $"Set daily power limit to {mw / 1000.0:0.#} W", [new ProfileWrite(PowerCap.Kind, key, value)], $"{mw} mW") is { } denied)
                    return denied;
                profile = DailyProfile.Load(path);
                bool trialWall = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial?.PowerMw != null;
                if (ClientAsks.Value && (DailyStateToken(profile) != preToken || trialWall != preTrialWall))
                    return Fail("the daily profile or trial state changed while waiting for confirmation: daily power limit was not changed; try again", BrokerCode.CannotWrite);
                BrokerResponse? now = null;
                if (profile is not { Suspended: true } && !trialWall)
                {
                    now = Dispatch(new BrokerRequest(BrokerRequest.OpWrite, target.StableId, Kind: PowerCap.Kind, Key: key, Value: value));
                    if (!now.Ok) return now;   // refused (out of the board's range): the profile stays as it was
                }
                var walled = new DailyProfile(DailyProfile.InWriteOrder([.. (profile?.Writes ?? []).Where(w => w.Kind != PowerCap.Kind || w.Key != key), new ProfileWrite(PowerCap.Kind, key, value)]),
                    _host.Now, profile?.Suspended ?? false, profile?.History, profile != null ? profile.Vbios : target.Vbios, profile != null ? profile.Driver : target.Driver, Adopted: profile?.Adopted)
                    .Noted(_host.Now, $"daily power limit {mw / 1000.0:0.#} W");
                SaveApprovedDaily(target, walled, path);
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
                bool appended;
                var crash = req.HungAt is { } stopped
                    ? CrashedAt(target, req.Value, uv, req.AnsweredAt ?? stopped, stopped, out how, out appended)
                    : CloseTrial(target, Experiment.Crash, out appended, req.Value, uv);
                if (profile != null)
                {
                    profile = profile.Noted(_host.Now, crash != null
                        ? $"{req.Value ?? "driver reset"} during an experiment trial ({crash.Change}): a data point; put back unchanged"
                        : $"{req.Value ?? "driver reset"}: put back unchanged");
                    profile.Save(path);
                }
                if (_crashHeld.Remove(target.StableId, out var heldAt) && crash != null && !appended && crash.TrialAt == heldAt)
                {
                    // This reset's crash was recorded and logged while the give-back waited ("crashed-held"): no second
                    // point, and the tuning log says only that the card is off the trial now.
                    Log(target, $"Crash · trial off the card · {crash.Game}", [
                        $"The crash of the trial ({crash.Change}) was on record already: the give-back has landed, and the trial is off the card and the record"]);
                    return Ok(new { success = true, path, profile, point = crash, on_record_already = true, applied = Array.Empty<object>() });
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
            case "crashed-held":
            {
                // The same crash while its give-back has not landed (a stability run holding the card, a cell that would
                // not go back): the card may still hold the trial, so it stays in force with its crash on record, as a
                // put-back held off leaves it (Leave): nothing marks it stable or keeps it, nothing records it twice, and
                // "crashed" closes it once the give-back lands. Without a trial nothing is recorded yet: "crashed" will.
                uint? uv = req.Uv > 0 ? req.Uv : null;
                List<string> how = [];
                var crash = req.HungAt is { } stopped
                    ? CrashedAt(target, req.Value, uv, req.AnsweredAt ?? stopped, stopped, out how, out _, held: true)
                    : CrashHeld(target, req.Value, uv);
                if (crash == null) return Ok(new { success = true, path, profile, point = (DataPoint?)null, applied = Array.Empty<object>() });
                _crashHeld[target.StableId] = crash.TrialAt;
                Log(target, $"Crash · during trial · {crash.Game}", [
                    $"Evidence: {req.Value ?? "driver reset"}",
                    req.HungAt is { } hung ? $"The GPU stopped answering at {When(hung)}" : null,
                    .. how,
                    req.Uv > 0 ? $"Crash voltage from the flight recorder: {req.Uv / 1000.0:0.#} mV" : "The flight recorder found no crash point",
                    $"Trial: {crash.Change}, ran {crash.Minutes} min; written at {Mv(crash.AtUv)}, {crash.AtMhz} MHz; {Limits(crash)}",
                    $"Trial curve: {crash.Curve}",
                    "The give-back has not landed yet: the crash is on record, and the trial stays in force until the card is off it"]);
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
                return Fail($"profile needs show, preview, preview-adopt, keep, adopt, off, stock, factory, apply, start, resume, load, preset, save, saved, forget, wall, apply-power, crashed, crashed-held or after-reset, not '{req.Kind}'", BrokerCode.BadRequest);
        }
    }

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

    /// <summary>A saved profile's writes, by name; throws when there is none by that name.</summary>
    private static List<ProfileWrite> SavedWrites(IGpuTarget target, string name) =>
        SavedProfiles.Find(SavedProfiles.PathBeside(target.Journal.Path), name) is var (_, saved)
            ? [.. saved.Writes] : throw new ArgumentException($"no saved profile named '{name}'");

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

    private BrokerResponse ApplyDailyCase(IGpuTarget target, DailyProfile? profile, string path, string? reason)
    {
        var r = Leave(target, Experiment.Survived, "the daily profile went back on", _ =>
        {
            var a = ApplyProfile(target, profile, path);
            return (a, a);
        }).Answer;
        Log(target, "Daily profile applied", [reason != null ? $"Reason: {reason}" : null, profile is { Suspended: true } ? "Daily profile suspended: nothing written" : null, .. Writes(r)]);
        return r;
    }

    /// <summary>"preset" with a saved profile's name: it becomes the daily profile and goes on.</summary>
    private BrokerResponse LoadSaved(IGpuTarget target, string? value, DailyProfile? profile, string path, string? expectedFingerprint)
    {
        if (ClientAsks.Value && AuthorizeProfileChange == null)
            return Fail("profile mutation 'preset' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
        if (ClientAsks.Value && string.IsNullOrWhiteSpace(expectedFingerprint))
            return Fail("loading a saved profile requires the preview's expected profile fingerprint (profile preview); preview and confirm again", BrokerCode.BadRequest);
        var file = SavedProfiles.PathBeside(target.Journal.Path);
        if (SavedProfiles.Find(file, value) is not var (name, saved))
        {
            var known = SavedProfiles.All(file).Select(p => $"'{p.Name}'").ToList();
            return Fail($"no saved profile named '{value}' ({(known.Count > 0 ? $"saved: {string.Join(", ", known)}" : "none is saved yet: save_profile keeps the daily profile under a name")})", BrokerCode.CannotWrite);
        }
        string preSavedFp = ProfileSnapshot.Fingerprint($"'{value}'", saved.Writes);
        string preToken = DailyStateToken(profile);
        if (expectedFingerprint != null && expectedFingerprint != preSavedFp)
            return Fail("the saved profile changed after the preview: nothing loaded; preview and confirm again", BrokerCode.CannotWrite);
        if (AuthorizeClientProfileChange(target, "preset", $"Load saved profile '{name}' as the daily profile", saved.Writes, name) is { } denied)
            return denied;
        profile = DailyProfile.Load(path);
        if (SavedProfiles.Find(file, value) is not var (nameNow, savedNow)
            || ProfileSnapshot.Fingerprint($"'{value}'", savedNow.Writes) != preSavedFp
            || (ClientAsks.Value && DailyStateToken(profile) != preToken))
            return Fail("the saved or daily profile changed while waiting for confirmation: nothing loaded; preview and confirm again", BrokerCode.CannotWrite);
        name = nameNow;
        saved = savedNow;
        var loaded = new DailyProfile(DailyProfile.InWriteOrder(saved.Writes), _host.Now, History: profile?.History, Adopted: MergeAdopted(saved.Writes, saved.Adopted, profile?.Adopted))
            .On(target.Vbios, target.Driver)
            .Noted(_host.Now, $"saved profile '{name}' loaded");
        var r = Leave(target, Experiment.Survived, $"saved profile '{name}' went on", _ => Loading(target, $"saved profile '{name}'", loaded, profile, path)).Answer;
        if (!r.Ok) return r;
        SaveApprovedDaily(target, loaded, path);
        _profileApprovals.Remember(target.StableId, ProfileApproval.Saved(name), saved);
        Log(target, $"Saved profile loaded · {name}", [
            .. (profile?.Writes ?? []).Select(w => $"Old: {w.Kind}/{w.Key} = {w.Value}"),
            .. Writes(r)]);
        return r;
    }

    /// <summary>A saved profile going on, for <see cref="Leave"/>: all of it, or (<see cref="NotLoaded"/>) the
    /// daily profile back as it was; the card is off the trial when either landed.</summary>
    private (BrokerResponse Answer, BrokerResponse PutBack) Loading(IGpuTarget target, string what, DailyProfile loaded, DailyProfile? profile, string path)
    {
        var r = ApplyProfile(target, loaded, path, confirmedCandidate: true);
        if (r.Ok) return (r, r);
        var answer = NotLoaded(target, what, r, profile, path, out var back);
        return (answer, back);
    }

    /// <summary>A saved profile that did not all go on (a write this card refuses, e.g. a wall above its board
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

    /// <summary>
    /// Each write of a profile that is not suspended (those <paramref name="only"/> picks, all by default), in its order,
    /// through the ordinary write op. A write that does not go on (refused, vetoed, or dropped by the driver, as 621.01
    /// drops every rail write) takes the rest of its group with it (<see cref="DailyProfile.Group"/>): the profile goes on
    /// again without that group, which puts any cell of it this broker holds back to its original, and the other groups go
    /// on as kept. The card is then never left with half of a run, the curve set aside for a memory write and never
    /// written back. The answer fails, names each group left at stock and why (not_applied), and carries every write.
    /// A write the card was held busy for (<see cref="HeldBusy"/>) is no such group: the run stops there, as the put-back's
    /// own refusal does. <paramref name="trialStays"/>: under a trial that stays in force, nothing held is put back first.
    /// </summary>
    private BrokerResponse ApplyProfile(IGpuTarget target, DailyProfile? profile, string path, Func<ProfileWrite, bool>? only = null, bool trialStays = false, bool confirmedCandidate = false)
    {
        if (profile != null && !confirmedCandidate && !Approved(target, profile))
            return UnapprovedDaily(target, path);
        var (r, failed) = ApplyOnce(target, profile, path, only, trialStays);
        // A write the card was held busy for (a stability run, another operation) is not one the driver will not take:
        // nothing more of the operation is written, so its group is not left out, and the refusal is the answer.
        if (failed == null || profile == null || HeldBusy(r)) return r;
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
            if (HeldBusy(r)) failed = null;   // the rest waits for the card: "did not all go on", not left at stock
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
}
