using System.Text.Json;
using System.Text.Json.Nodes;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Game;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>The actual durable card-host transaction with fake hardware/transport; no vendor DLL is loaded.</summary>
public class GameSessionTests
{
    private sealed class World : IGameAccess, IDisposable
    {
        public readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ct-game-session-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Dir, "game-recovery.json");
        public GpuTarget Identity { get; set; } = new("0000:07:03.0", "RTX 5090", 0x2b8510de, 0x205710de, 12, 0, @"PCI\VEN_10DE&DEV_2B85&SUBSYS_205710DE\ONE", 1, 1);
        public readonly Dictionary<string, string> Cells = new()
        {
            ["vf/core"] = "0*127", ["power/fe"] = "600000", ["fan/0"] = "manual:67", ["clocklimit/min"] = "0",
            ["clocklimit/max"] = "0", ["clocklimit/voltage"] = "0", ["pstates20/memory"] = "200000",
        };
        public Dictionary<string, string> DailyCells;
        public Experiment? Session;
        public DailyProfile? Daily;
        public bool Held = true, RejectIdentity, LoseWorkerAfterWrite, LieOnWrite, LieOnRestore, ChangeUnrelated, GameAlive, ResetSeen,
            LoseWorkerAfterRestore, LoseWorkerAfterMetadata;
        public int Reads, CoverageReads, Writes, Proofs;
        public DateTimeOffset? RecordedTrial;
        public string? RecordedChange;
        public readonly List<BrokerRequest> Requests = [];
        private readonly DateTimeOffset _start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public World()
        {
            Directory.CreateDirectory(Dir);
            DailyCells = new(Cells);
            DailyCells["fan/0"] = "auto";
            Session = new("offline", null, _start, new ExperimentTrial(_start.AddMinutes(1), "0*127", "original curve", null, null, FanPct: 67));
            Daily = new(ToWrites(DailyCells), _start);
        }
        private static List<ProfileWrite> ToWrites(Dictionary<string, string> values) => values.Select(c =>
        {
            var key = c.Key.Split('/'); return new ProfileWrite(key[0], key[1], c.Value);
        }).ToList();
        public void Verify() { Proofs++; Identity.Validate(); if (RejectIdentity) throw new NotSupportedException("identity disagreement"); }
        public GameSnapshot Read(bool measureCoverage = false)
        {
            Reads++;
            if (measureCoverage) CoverageReads++;
            return new(Identity, Cells["vf/core"], uint.Parse(Cells["power/fe"]), 400_000, measureCoverage ? 3120 : null, 32UL << 30,
                ToWrites(Cells), measureCoverage ? 1_020_000u : null, measureCoverage ? 1_015_000u : null);
        }
        public GameSession Service() => new("card", Path, this, Perform, () => Session, e =>
            {
                Session = e;
                if (LoseWorkerAfterMetadata) throw new IOException("worker ended after restoring session metadata");
            }, () => Daily,
            (kind, key) => DailyCells.GetValueOrDefault(kind + "/" + key), _ => Held, at => RecordedTrial == at, _ => ResetSeen, (_, _) => GameAlive, (change, _) => RecordedChange == change ? RecordedTrial : null);
        public BrokerResponse Begin(string run = "run") => Service().Call("begin", run, new(Pci: Identity.Pci, SessionAt: Session?.StartedAt,
            TrialAt: Session?.Trial?.At, Levers: GameCatalog.AllLevers));
        public BrokerResponse Lever(string kind, uint value) => Service().Call("lever", "run", new(Lever: kind, Value: value));
        public BrokerResponse Finish(string mode = "baseline", string run = "run") => Service().Call("finish", run, new(Mode: mode));
        private BrokerResponse Perform(BrokerRequest request)
        {
            Requests.Add(request);
            Writes++;
            // The recovery obligation must already be durable before any possible write or lost reply.
            Assert.True(File.Exists(Path));
            if (request.Op == BrokerRequest.OpExperiment && request.Kind == "try")
            {
                var record = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(Path))!;
                Assert.True(record.Pending);
                using var json = JsonDocument.Parse(request.Value!);
                var args = json.RootElement;
                if (!LieOnWrite)
                {
                    if (args.TryGetProperty("power_mw", out var p)) Cells["power/fe"] = p.GetUInt32().ToString();
                    if (args.TryGetProperty("fan_pct", out var f)) Cells["fan/0"] = f.GetUInt32() == 0 ? "auto" : "manual:" + f.GetUInt32();
                    if (args.TryGetProperty("writes", out var ws)) Cells["clocklimit/voltage"] = ws[0].GetProperty("value").GetString()!;
                }
                if (ChangeUnrelated) Cells["pstates20/memory"] = "999000";
                Session = Session! with { Trial = Session.Trial! with { At = Session.Trial!.At.AddSeconds(1), Change = args.GetProperty("change").GetString()!, Marked = false } };
                if (LoseWorkerAfterWrite) throw new IOException("worker ended after the write");
            }
            else if (request.Op == BrokerRequest.OpWrite)
            {
                if (!LieOnRestore) Cells[request.Kind + "/" + request.Key] = request.Value!;
                if (LoseWorkerAfterRestore) throw new IOException("worker ended after a partial restoration");
            }
            else if (request.Op == BrokerRequest.OpProfile || request.Op == BrokerRequest.OpExperiment && request.Kind == "crash")
            {
                if (request.Kind == "crash") RecordedTrial = Session?.Trial?.At;
                if (!LieOnRestore) { Cells.Clear(); foreach (var c in DailyCells) Cells.Add(c.Key, c.Value); }
                Session = Session! with { Trial = null };
            }
            return new(true);
        }
        public void Dispose() { Directory.Delete(Dir, true); }
    }

    [Fact]
    public void Baseline_is_fresh_under_the_lease_not_supplied_by_the_client()
    {
        using var w = new World();
        w.Cells["power/fe"] = "475000"; // changed since the MCP queued its job
        Assert.True(w.Begin().Ok);
        var state = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(w.Path))!;
        Assert.Equal(475_000u, state.Baseline.PowerMw);
        Assert.Equal(1_020_000u, state.Baseline.NvvddMaxUv);
        Assert.Equal(1_015_000u, state.Baseline.TopUv);
        Assert.False(w.Lever("power", 500_000).Ok);
        Assert.Equal(0, w.Writes);
        Assert.True(w.Proofs > 0);
    }

    [Fact]
    public void Recovery_still_restores_legacy_snapshots_without_voltage_reference()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("power", 400_000).Ok);

        var json = JsonNode.Parse(File.ReadAllText(w.Path))!.AsObject();
        foreach (string name in new[] { "Baseline", "Expected" })
        {
            var snapshot = json[name]!.AsObject();
            snapshot["TopMhz"] = 3562;
            snapshot.Remove("NvvddMaxUv");
            snapshot.Remove("TopUv");
        }
        File.WriteAllText(w.Path, json.ToJsonString());

        Assert.True(w.Finish().Ok);
        Assert.Equal(1, w.CoverageReads);
        Assert.Equal("600000", w.Cells["power/fe"]);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void No_actual_lease_or_a_wrong_PCI_target_means_no_snapshot_or_write()
    {
        using var w = new World { Held = false };
        Assert.False(w.Begin().Ok);
        Assert.Equal(0, w.Reads);
        w.Held = true;
        Assert.False(w.Service().Call("begin", "run", new(Pci: "0000:08:00.0")).Ok);
        Assert.Equal(0, w.Reads);
        Assert.Equal(0, w.Writes);
    }

    [Fact]
    public void Identity_disagreement_is_refused_before_reading_a_curve()
    {
        using var w = new World { RejectIdentity = true };
        Assert.Throws<NotSupportedException>(() => w.Begin());
        Assert.Equal(0, w.Reads);
        Assert.Equal(0, w.Writes);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void Existing_locks_and_the_actual_curve_are_verified_before_the_game()
    {
        using var w = new World();
        w.Cells["clocklimit/min"] = "2000000";
        Assert.False(w.Begin().Ok);
        w.Cells["clocklimit/min"] = "0";
        w.Cells["vf/core"] = "15000*127";
        Assert.False(w.Begin().Ok);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void A_daily_profile_that_is_not_really_on_the_card_is_refused()
    {
        using var w = new World();
        w.Session = w.Session! with { Trial = null };
        Assert.False(w.Begin().Ok); // daily fan auto, actual fixed 67
        w.Cells["fan/0"] = "auto";
        Assert.True(w.Begin().Ok);
    }

    [Fact]
    public void Successful_levers_and_restoration_are_independently_read_back()
    {
        using var w = new World();
        var original = w.Session!.Trial!;
        var initial = new Dictionary<string, string>(w.Cells);
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("power", 400_000).Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        Assert.True(w.Lever("boost", 1).Ok);
        Assert.True(w.Finish().Ok);
        Assert.Equal(initial.OrderBy(c => c.Key), w.Cells.OrderBy(c => c.Key));
        Assert.Equal(original.At, w.Session!.Trial!.At);
        Assert.False(File.Exists(w.Path));
        Assert.DoesNotContain(w.Requests, r => r.Op == BrokerRequest.OpWrite && r.Kind == "vf");
    }

    [Fact]
    public void Losing_a_reply_does_not_lose_the_service_owned_baseline()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        _ = w.Lever("fans", 100); // the client never receives/credits this reply
        Assert.True(w.Service().Call("finish", "run", new(Mode: "baseline")).Ok);
        Assert.Equal("manual:67", w.Cells["fan/0"]);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void A_worker_ending_mid_write_keeps_a_durable_record_and_recovers_to_verified_daily()
    {
        using var w = new World { LoseWorkerAfterWrite = true };
        Assert.True(w.Begin().Ok);
        Assert.Throws<IOException>(() => w.Lever("fans", 100));
        var record = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(w.Path))!;
        Assert.True(record.Pending);
        Assert.Equal("manual:100", record.PendingWrites!.Single().Value);
        Assert.True(w.Service().Call("recover", "startup-recovery", new()).Ok);
        Assert.Equal("auto", w.Cells["fan/0"]);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void A_reset_after_an_unacknowledged_write_recovers_the_exact_candidate_trial_identity()
    {
        using var w = new World { LoseWorkerAfterWrite = true };
        Assert.True(w.Begin().Ok);
        Assert.Throws<IOException>(() => w.Lever("fans", 100));
        w.RecordedTrial = w.Session!.Trial!.At;
        w.RecordedChange = w.Session.Trial.Change;
        w.ResetSeen = true;
        w.Session = w.Session with { Trial = null };
        w.Cells.Clear(); foreach (var c in w.DailyCells) w.Cells.Add(c.Key, c.Value);
        Assert.True(w.Finish("reset").Ok);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void A_false_success_from_the_writer_is_not_verified_success()
    {
        using var w = new World { LieOnWrite = true };
        Assert.True(w.Begin().Ok);
        Assert.False(w.Lever("power", 400_000).Ok);
        Assert.True(File.Exists(w.Path));
    }

    [Fact]
    public void Failed_restoration_keeps_the_record_and_can_be_retried()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        w.LieOnRestore = true;
        Assert.False(w.Finish().Ok);
        Assert.True(File.Exists(w.Path));
        w.LieOnRestore = false;
        Assert.True(w.Finish().Ok);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void A_worker_ending_during_partial_restoration_can_resume_without_a_stale_overwrite()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        Assert.True(w.Lever("power", 400_000).Ok);
        w.LoseWorkerAfterRestore = true;
        Assert.Throws<IOException>(() => w.Finish());
        Assert.True(JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(w.Path))!.Restoring);
        w.LoseWorkerAfterRestore = false;
        Assert.True(w.Finish().Ok);
        Assert.Equal("manual:67", w.Cells["fan/0"]);
        Assert.Equal("600000", w.Cells["power/fe"]);
    }

    [Fact]
    public void A_worker_ending_between_metadata_restore_and_receipt_does_not_strand_the_record()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        w.LoseWorkerAfterMetadata = true;
        Assert.Throws<IOException>(() => w.Finish());
        Assert.True(File.Exists(w.Path));
        w.LoseWorkerAfterMetadata = false;
        Assert.True(w.Finish().Ok);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void An_external_change_is_not_credited_or_overwritten_during_recovery()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        w.Cells["vf/core"] = "90000*127";
        int writes = w.Writes;
        Assert.False(w.Service().Call("check", "run", new()).Ok);
        Assert.False(w.Finish().Ok);
        Assert.False(w.Service().Call("recover", "startup", new()).Ok);
        Assert.Equal(writes, w.Writes);
        Assert.True(File.Exists(w.Path));
    }

    [Fact]
    public void A_service_fan_safety_release_goes_to_verified_daily_instead_of_restoring_fixed_fans()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        w.Cells["fan/0"] = "auto";
        w.Session = w.Session! with { Trial = w.Session.Trial! with { FanPct = null } };
        var result = w.Finish();
        Assert.True(result.Ok);
        Assert.Equal("daily", result.Result!.Value.GetProperty("mode").GetString());
        Assert.Equal("auto", w.Cells["fan/0"]);
        Assert.Null(w.Session.Trial);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void A_lever_touching_an_unrelated_setting_stops_and_preserves_evidence()
    {
        using var w = new World { ChangeUnrelated = true };
        Assert.True(w.Begin().Ok);
        Assert.False(w.Lever("power", 400_000).Ok);
        Assert.True(File.Exists(w.Path));
        Assert.False(w.Service().Call("recover", "startup", new()).Ok);
    }

    [Fact]
    public void Another_session_or_ended_trial_is_not_reanimated()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("power", 400_000).Ok);
        w.Session = w.Session! with { Trial = w.Session.Trial! with { Ending = "survived" } };
        Assert.False(w.Finish().Ok);
        Assert.NotNull(w.Session.Trial.Ending);
        Assert.True(File.Exists(w.Path));
    }

    [Fact]
    public void Reset_completion_requires_the_exact_trial_crash_and_actual_daily_readback()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("power", 400_000).Ok);
        var measured = w.Session!.Trial!.At;
        w.Session = w.Session with { Trial = null };
        Assert.False(w.Finish("reset").Ok);
        w.RecordedTrial = measured.AddSeconds(-1);
        Assert.False(w.Finish("reset").Ok);
        w.RecordedTrial = measured;
        Assert.False(w.Finish("reset").Ok); // not daily on hardware yet
        w.Cells.Clear(); foreach (var c in w.DailyCells) w.Cells.Add(c.Key, c.Value);
        Assert.True(w.Finish("reset").Ok);
        Assert.False(File.Exists(w.Path));
    }

    [Fact]
    public void Missing_records_do_not_masquerade_as_verified_restoration()
    {
        using var w = new World();
        Assert.False(w.Finish().Ok);
        Assert.Equal(0, w.Reads);
        Assert.Equal(0, w.Writes);
    }

    [Fact]
    public void A_lost_completion_reply_is_replayed_only_after_fresh_verification()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        _ = w.Finish(); // successful restoration, lost pipe reply
        Assert.True(w.Finish().Ok);
        Assert.Equal("baseline", w.Finish().Result!.Value.GetProperty("mode").GetString());
        w.Cells["power/fe"] = "450000";
        Assert.False(w.Finish().Ok);
    }

    [Fact]
    public void An_old_completion_does_not_delete_another_runs_pending_baseline()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Finish().Ok);
        Assert.True(w.Begin("another-run").Ok);
        Assert.False(w.Finish().Ok);
        Assert.True(File.Exists(w.Path));
    }

    [Fact]
    public void A_late_reset_cannot_be_relabelled_by_a_manual_crash_or_baseline_restore()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.True(w.Lever("fans", 100).Ok);
        w.ResetSeen = true;
        int writes = w.Writes;
        Assert.False(w.Finish("failed").Ok);
        Assert.False(w.Finish("baseline").Ok);
        Assert.False(w.Service().Call("recover", "startup", new()).Ok);
        Assert.Equal(writes, w.Writes);
        Assert.True(File.Exists(w.Path));
    }

    [Fact]
    public void No_finish_or_startup_recovery_runs_under_a_registered_live_game()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        w.GameAlive = true;
        Assert.True(w.Service().Call("launch", "run", new(ProcessId: 123, ProcessStartedAt: DateTimeOffset.UtcNow)).Ok);
        int writes = w.Writes;
        Assert.False(w.Finish().Ok);
        Assert.False(w.Service().Call("verify", "startup", new()).Ok);
        Assert.False(w.Service().Call("recover", "startup", new()).Ok);
        Assert.Equal(writes, w.Writes);
        w.GameAlive = false;
        Assert.True(w.Finish().Ok);
    }

    [Fact]
    public void A_missing_suspended_process_is_never_authorized_to_resume()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        Assert.False(w.Service().Call("launch", "run", new(ProcessId: 123, ProcessStartedAt: DateTimeOffset.UtcNow)).Ok);
    }

    [Fact]
    public void Corrupt_recovery_evidence_is_preserved_and_refused()
    {
        using var w = new World();
        File.WriteAllText(AtomicFile.BackupPath(w.Path), "{broken");
        Assert.Throws<InvalidDataException>(() => w.Begin());
        Assert.True(File.Exists(AtomicFile.BackupPath(w.Path)));
        Assert.Equal(0, w.Writes);
    }

    [Fact]
    public void Stale_session_requests_are_rejected_before_any_driver_snapshot()
    {
        using var w = new World();
        var request = new GameCommand(Pci: w.Identity.Pci, SessionAt: w.Session!.StartedAt, TrialAt: w.Session.Trial!.At);
        w.Session = w.Session with { StartedAt = w.Session.StartedAt.AddMinutes(1) };
        Assert.False(w.Service().Call("begin", "run", request).Ok);
        Assert.Equal(0, w.Reads);
    }

    [Fact]
    public void A_changed_curve_cannot_be_blamed_on_the_old_trial_at_crash_report_time()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        w.Cells["vf/core"] = "15000*127";
        Assert.False(w.Finish("failed").Ok);
        Assert.Null(w.RecordedTrial);
        Assert.Equal(0, w.Writes);
    }

    [Fact]
    public void A_record_for_another_card_is_never_restored()
    {
        using var w = new World();
        Assert.True(w.Begin().Ok);
        var state = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(w.Path))!;
        AtomicFile.Write(w.Path, JsonSerializer.SerializeToUtf8Bytes(state with { StableId = "other-card" }));
        Assert.False(w.Service().Call("recover", "startup", new()).Ok);
        Assert.Equal(0, w.Writes);
    }

    [Fact]
    public void Selection_is_by_exact_PCI_not_model_or_lowest_bus_and_never_falls_back()
    {
        using var w = new World();
        var second = w.Identity with { Pci = "0000:01:00.0" };
        Assert.Throws<NotSupportedException>(() => GpuManager.Choose([w.Identity, second], null));
        Assert.Equal(w.Identity, GpuManager.Choose([second, w.Identity], w.Identity.Pci));
        Assert.Throws<NotSupportedException>(() => GpuManager.Choose([w.Identity], "0000:04:00.0"));
        Assert.Throws<NotSupportedException>(() => GpuManager.Choose([w.Identity with { Major = 8, Minor = 9 }], null));
    }

    [Fact]
    public void PnP_matching_includes_device_subsystem_slot_and_vendor()
    {
        using var w = new World();
        var adapter = new DisplayAdapter(w.Identity.InstanceId, PciAddress.Parse(w.Identity.Pci));
        Assert.Equal(adapter, GpuTarget.Match([adapter], w.Identity.Pci, w.Identity.DeviceId, w.Identity.SubSystemId));
        Assert.Throws<NotSupportedException>(() => GpuTarget.Match([adapter], "0000:07:00.0", w.Identity.DeviceId, w.Identity.SubSystemId));
        Assert.Throws<NotSupportedException>(() => GpuTarget.Match([adapter], w.Identity.Pci, w.Identity.DeviceId, 0));
        Assert.Throws<NotSupportedException>(() => GpuTarget.Match([adapter, adapter], w.Identity.Pci, w.Identity.DeviceId, w.Identity.SubSystemId));
    }

    [Fact]
    public void Recovery_ignores_only_what_is_provably_another_vendors_reset()
    {
        using var w = new World();
        var selected = new DisplayAdapter(w.Identity.InstanceId, PciAddress.Parse(w.Identity.Pci));
        var integrated = new DisplayAdapter(@"PCI\VEN_8086&DEV_1234\IGPU", PciAddress.Parse("0000:00:02.0"));
        string Display(string data) => $"<Event><System><Provider Name='Display'/><EventID>4101</EventID></System><EventData><Data>{data}</Data></EventData></Event>";
        Assert.True(ResetAttribution.ShouldRecover("<Event><System><Provider Name='nvlddmkm'/><EventID>153</EventID></System></Event>", w.Identity, [selected]));
        Assert.True(ResetAttribution.ShouldRecover(Display("nvlddmkm"), w.Identity, [selected, integrated]));
        Assert.False(ResetAttribution.ShouldRecover(Display("igfxn"), w.Identity, [selected, integrated]));
        // doubtful or unreadable: answer it, because giving back only ever lowers what the card runs
        Assert.True(ResetAttribution.ShouldRecover(Display(" "), w.Identity, [selected, integrated]));
        Assert.True(ResetAttribution.ShouldRecover("<not xml", w.Identity, [selected]));
    }

    [Fact]
    public void A_generic_display_reset_without_driver_identity_is_ambiguous_with_an_iGPU()
    {
        using var w = new World();
        var selected = new DisplayAdapter(w.Identity.InstanceId, PciAddress.Parse(w.Identity.Pci));
        var integrated = new DisplayAdapter(@"PCI\VEN_8086&DEV_1234\IGPU", PciAddress.Parse("0000:00:02.0"));
        string header = "<Event><System><Provider Name='Display'/><EventID>4101</EventID></System>";
        Assert.Equal(ResetOwner.Ambiguous, ResetAttribution.Match(header + "<EventData><Data> </Data></EventData></Event>", w.Identity, [selected, integrated]));
        Assert.Equal(ResetOwner.Ambiguous, ResetAttribution.Match(header + "</Event>", w.Identity, [selected, integrated]));
        Assert.Equal(ResetOwner.Target, ResetAttribution.Match(header + "<EventData><Data>nvlddmkm</Data></EventData></Event>", w.Identity, [selected, integrated]));
    }

    [Theory]
    [InlineData("nvlddmkm", "153", "", ResetOwner.Target)]
    [InlineData("Display", "4101", "nvlddmkm", ResetOwner.Target)]
    [InlineData("Display", "4101", "igfxn", ResetOwner.Other)]
    [InlineData("Display", "4101", "amdwddmg", ResetOwner.Other)]
    public void Driver_resets_are_attributed_not_broadcast(string provider, string id, string driver, ResetOwner expected)
    {
        using var w = new World();
        var adapter = new DisplayAdapter(w.Identity.InstanceId, PciAddress.Parse(w.Identity.Pci));
        string xml = $"<Event><System><Provider Name='{provider}'/><EventID>{id}</EventID></System><EventData><Data>{driver}</Data></EventData></Event>";
        Assert.Equal(expected, ResetAttribution.Match(xml, w.Identity, [adapter]));
        if (expected == ResetOwner.Target)
            Assert.Equal(ResetOwner.Ambiguous, ResetAttribution.Match(xml, w.Identity, [adapter, adapter with { Pci = PciAddress.Parse("0000:09:00.0") }]));
    }
}
