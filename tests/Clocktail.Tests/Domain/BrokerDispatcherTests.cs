using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The broker's dispatcher is the one place a privileged op runs, so these tests pin the guards that make an
/// automatic write safe: the firmware ceiling is the domain rule (no client flag lifts it), only one write
/// window is open at a time, a write stands down while a stability run holds the lock, and a non-elevated
/// process reports that instead of touching the driver. Reads do none of this.
/// </summary>
[Collection("fake-driver")]
public class BrokerDispatcherTests : IDisposable
{
    private const int Board = 0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = 0xFFFFFFFF };

    public BrokerDispatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Target(OwnershipJournal j, ApplySentinel s, GpuSession g) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => g;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    private static byte[] Info()
    {
        var b = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(b, 4, 1u << Board);
        int e = PowerRequestsLayout.InfoEntry(Board);
        W(b, e, 1); W(b, e + 8, 400_000); W(b, e + 0xc, 575_000); W(b, e + 0x10, 600_000);
        return b;
    }

    /// <summary>A dispatcher wired to the power fake (the 5090's live table: FE 575 W, F8 absent).</summary>
    private BrokerDispatcher Power(bool canWrite = true, Func<string?>? judge = null, ApplySentinel? sentinel = null)
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = Info();
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        uint controlWord = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != controlWord) return null;
            var a = (byte[])q.Clone();
            uint cmd = U(q, PowerRequestsLayout.ControlEntry(Board) + 4);
            W(a, PowerRequestsLayout.ControlEntry(Board), _requests.GetValueOrDefault(cmd, 0xFFFFFFFF));
            return a;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            int e = PowerRequestsLayout.ControlEntry(Board);
            _requests[U(req, e + 4)] = U(req, e);
            return state;
        };
        // The raw policy table's slots are wired too; they answer only when a test installs CurrentLimitsFixture.
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo],
            [Slot.ClientPwrPoliciesSetControl, Slot.PowerPolicySetControl]));
        var t = new Target(_journal, sentinel ?? _sentinel, _gpu);
        return new BrokerDispatcher(hw, t, _host, canWrite, judge);
    }

    private static uint? AfterMw(BrokerResponse r) =>
        r.Result!.Value.GetProperty("applied")[0].GetProperty("after_mw") is { ValueKind: JsonValueKind.Number } n ? n.GetUInt32() : null;
    private static string Outcome(BrokerResponse r) => r.Result!.Value.GetProperty("applied")[0].GetProperty("outcome").GetString()!;
    private static string OwnedBy(BrokerResponse r) => r.Result!.Value.GetProperty("applied")[0].GetProperty("ownership").GetString()!;

    [Fact]
    public void Ping_names_the_gpus_without_opening_a_write_window()
    {
        var d = Power();
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPing));
        Assert.True(r.Ok);
        Assert.True(r.Result!.Value.GetProperty("pong").GetBoolean());
        Assert.Equal("gpu-A", r.Result!.Value.GetProperty("gpus")[0].GetString());
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Power_at_the_current_value_changes_nothing_and_holds_nothing()
    {
        var d = Power();
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "ordinary", Mw: 575_000));
        Assert.True(r.Ok);
        Assert.Equal("NoChange", Outcome(r));
        Assert.StartsWith("nothing held", OwnedBy(r));
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Power_below_the_current_value_is_applied_and_held()
    {
        var d = Power();
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.True(r.Ok);
        Assert.Equal("Applied", Outcome(r));
        Assert.Equal(560_000u, AfterMw(r));
        Assert.StartsWith("held: original 575000", OwnedBy(r));
    }

    [Fact]
    public void The_firmware_ceiling_is_the_domain_rule_and_no_request_gets_past_it()
    {
        var d = Power();
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 5_000_000));   // 5000 W, far above the board max
        Assert.False(r.Ok);
        Assert.Equal("Aborted", Outcome(r));
        Assert.Contains("above the driver maximum", r.Result!.Value.GetProperty("applied")[0].GetProperty("detail").GetString());
        Assert.Equal(0, FakeDriver.Sets);                 // nothing reached the driver
        Assert.Equal(575_000u, _requests[0xfe]);          // the request is still at its original
    }

    [Fact]
    public void A_write_stands_down_while_a_stability_run_holds_the_lock()
    {
        var d = Power(judge: () => "pid 1234, 'baseline'");
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.False(r.Ok);
        Assert.Contains("stability run", r.Error);
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.Equal(0, FakeDriver.Sets);                 // the state under test was not touched
    }

    [Fact]
    public void A_read_is_allowed_while_a_stability_run_holds_the_lock()
    {
        var d = Power(judge: () => "pid 1234, 'baseline'");
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpOwnership));
        Assert.True(r.Ok);                                // reads never take the window, so the judge lock does not block them
    }

    [Fact]
    public void Without_elevation_a_write_reports_the_need_and_touches_nothing()
    {
        var d = Power(canWrite: false);
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.False(r.Ok);
        Assert.Contains("-137", r.Error);
        Assert.Equal(BrokerCode.NeedsAdmin, r.Code);
        Assert.Equal(BrokerCode.NeedsAdmin, d.Dispatch(Write("power", "fe", "560000")).Code);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void An_unknown_operation_fails_without_touching_the_driver()
    {
        var d = Power();
        var r = d.Dispatch(new BrokerRequest("frobnicate"));
        Assert.False(r.Ok);
        Assert.Contains("unknown operation", r.Error);
        Assert.Equal(BrokerCode.UnknownOp, r.Code);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_bad_gpu_id_fails_cleanly()
    {
        var d = Power();
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, GpuId: "gpu-Z", Kind: "fe", Mw: 560_000));
        Assert.False(r.Ok);
        Assert.Contains("no GPU with id 'gpu-Z'", r.Error);
        Assert.Equal(BrokerCode.NoGpu, r.Code);
    }

    /// <summary>A refusal before the op ran carries a fixed code a client can switch on; an op that ran and failed carries a result and no code.</summary>
    [Fact]
    public void A_refusal_carries_a_code_and_a_result_that_failed_carries_none()
    {
        var d = Power();
        Assert.Equal(BrokerCode.NotAPath, d.Dispatch(Write("register", "0x1000", "1")).Code);
        Assert.Equal(BrokerCode.BadRequest, d.Dispatch(new BrokerRequest(BrokerRequest.OpWrite, Kind: "power")).Code);
        Assert.Equal(BrokerCode.BadRequest, d.Dispatch(Write("power", "f9", "1")).Code);
        Assert.Equal(BrokerCode.BadRequest, d.Dispatch(Write("power", "fe", "560000", enable: ["NoSuchSlot"])).Code);
        Assert.Equal(BrokerCode.CannotWrite, d.Dispatch(Write("power", "f8", "absent")).Code);
        Assert.Equal(BrokerCode.NoProof, d.Dispatch(new BrokerRequest(BrokerRequest.OpProve, Kind: "power", Key: "fe")).Code);
        var ran = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 5_000_000));
        Assert.False(ran.Ok);
        Assert.Null(ran.Code);
        Assert.NotNull(ran.Result);
        Assert.Null(d.Dispatch(new BrokerRequest(BrokerRequest.OpPing)).Code);
    }

    [Fact]
    public void The_write_window_opens_again_after_a_write_completes()
    {
        var d = Power();
        var first = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.True(first.Ok);
        var second = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 575_000));   // back to original: released
        Assert.True(second.Ok);
        Assert.StartsWith("released", OwnedBy(second));
    }

    [Fact]
    public void Ownership_reads_the_server_side_journal()
    {
        var d = Power();
        d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));   // leaves a held record
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpOwnership));
        Assert.True(r.Ok);
        Assert.Equal("OwnedByThisProcess", r.Result!.Value.GetProperty("reason").GetString());
        Assert.Equal("fe", r.Result!.Value.GetProperty("items")[0].GetProperty("Key").GetString());
        Assert.Equal(0, r.Result!.Value.GetProperty("crashes").GetArrayLength());
        Assert.EndsWith(CrashLog.FileName, r.Result!.Value.GetProperty("crash_log").GetString());
    }

    private static BrokerRequest Write(string kind, string key, string value, string[]? enable = null, bool allowCreate = false) =>
        new(BrokerRequest.OpWrite, Kind: kind, Key: key, Value: value, EnableSlots: enable, AllowCreate: allowCreate);

    [Fact]
    public void The_write_verb_reaches_the_same_cell_as_the_power_verb()
    {
        var d = Power();
        var r = d.Dispatch(Write("power", "fe", "560000"));
        Assert.True(r.Ok, r.Error);
        var a = r.Result!.Value.GetProperty("applied")[0];
        Assert.Equal("power", a.GetProperty("kind").GetString());
        Assert.Equal("fe", a.GetProperty("key").GetString());
        Assert.Equal("Applied", a.GetProperty("outcome").GetString());
        Assert.Equal("575000", a.GetProperty("before").GetString());
        Assert.Equal("560000", a.GetProperty("after").GetString());
        Assert.Equal("mW", a.GetProperty("unit").GetString());
        Assert.StartsWith("held: original 575000", OwnedBy(r));
        Assert.Equal("Held", a.GetProperty("hold").GetString());
        Assert.Equal("575000", a.GetProperty("original").GetString());
        Assert.True(a.GetProperty("state_known").GetBoolean());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("veto").ValueKind);   // no post-write objection: the field is there and empty
        Assert.Equal(560_000u, _requests[0xfe]);

        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 575_000));   // the first path's own verb releases it
        Assert.StartsWith("released", OwnedBy(back));
    }

    [Fact]
    public void A_value_the_path_cannot_write_from_fails_before_the_driver()
    {
        var d = Power();
        var r = d.Dispatch(Write("power", "f8", "absent"));
        Assert.False(r.Ok);
        Assert.Contains("no way to delete", r.Error);
        Assert.Equal(0, FakeDriver.Sets);

        var needsCreate = d.Dispatch(Write("power", "f8", "500000"));
        Assert.False(needsCreate.Ok);
        Assert.Contains("--allow-new-request", needsCreate.Result!.Value.GetProperty("applied")[0].GetProperty("detail").GetString());
        Assert.Equal(0, FakeDriver.Sets);

        var created = d.Dispatch(Write("power", "f8", "500000", allowCreate: true));
        Assert.True(created.Ok, created.Error);
        Assert.Equal(500_000u, _requests[0xf8]);
    }

    [Fact]
    public void An_unknown_kind_or_a_missing_key_fails_before_the_driver()
    {
        var d = Power();
        Assert.Contains("not a write path", d.Dispatch(Write("register", "0x1000", "1")).Error);
        Assert.Contains("names a kind, a key and a value", d.Dispatch(new BrokerRequest(BrokerRequest.OpWrite, Kind: "power")).Error);
        Assert.Contains("not a power request", d.Dispatch(Write("power", "f9", "1")).Error);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_setter_can_be_enabled_for_one_request_only_when_its_rules_are_complete()
    {
        var d = Power();
        Assert.Contains("forbidden", d.Dispatch(Write("power", "fe", "560000", enable: ["RegisterOp"])).Error);
        Assert.Contains("not a slot name", d.Dispatch(Write("power", "fe", "560000", enable: ["NoSuchSlot"])).Error);
        Assert.Equal(0, FakeDriver.Sets);

        var r = d.Dispatch(Write("power", "fe", "560000", enable: [nameof(Slot.ClockClientClkVfPointsSetControl)]));   // decoded, off until requested
        Assert.True(r.Ok, r.Error);
        Assert.Equal("ClockClientClkVfPointsSetControl", r.Result!.Value.GetProperty("applied")[0].GetProperty("enabled_slots")[0].GetString());
    }

    [Fact]
    public void Prove_is_only_for_the_paths_that_define_an_equal_value_proof()
    {
        var d = Power();
        Assert.Contains("defines no equal-value proof", d.Dispatch(new BrokerRequest(BrokerRequest.OpProve, Kind: "power", Key: "fe")).Error);
        Assert.Contains("names a kind and a key", d.Dispatch(new BrokerRequest(BrokerRequest.OpProve, Kind: "boost")).Error);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Ping_lists_the_write_paths_a_client_may_ask_for()
    {
        var d = Power();
        var paths = d.Dispatch(new BrokerRequest(BrokerRequest.OpPing)).Result!.Value.GetProperty("paths");
        Assert.Equal(10, paths.GetArrayLength());
        Assert.Contains(paths.EnumerateArray(), p => p.GetProperty("kind").GetString() == "rails" && p.GetProperty("checks_after_write").GetString()!.Contains("MAX"));
        Assert.Contains(paths.EnumerateArray(), p => p.GetProperty("kind").GetString() == "fan" && p.GetProperty("checks_after_write").ValueKind == JsonValueKind.Null);
    }

    /// <summary>
    /// Looking and then publishing lets two tools each look, see nothing, and both go ahead. The dispatcher looks before
    /// the window opens and the sentinel looks again once it is on disk, so a run whose lock appeared between the two is
    /// honoured before any byte reaches the driver, and neither a sentinel nor a record outlives the refusal.
    /// </summary>
    [Fact]
    public void A_run_whose_lock_appears_after_the_first_look_is_still_honoured_once_the_sentinel_is_on_disk()
    {
        int looks = 0;
        string? Judge() => ++looks >= 2 ? "pid 1234, 'preheat'" : null;   // free at the first look, busy from the second
        var sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"), Judge);
        var d = Power(judge: Judge, sentinel: sentinel);
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.Contains("preheat", r.Error);
        Assert.Equal(2, looks);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.False(sentinel.IsBlocked(out _));
        Assert.Null(_journal.Load());
    }

    /// <summary>Recovery's window is a write window like any other: the same second look, and the record it was about to
    /// give back stays for the next attempt.</summary>
    [Fact]
    public void A_recovery_that_finds_a_run_starting_stands_down_and_keeps_the_record()
    {
        int looks = 0;
        string? Judge() => ++looks >= 2 ? "pid 1234, 'preheat'" : null;
        var sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"), Judge);
        var d = Power(judge: Judge, sentinel: sentinel);
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(PowerCap.Kind, "fe", "560000", _host.Now, null));   // hardware is at 575000: a write is owed
        _host.Pid = 100;
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.Equal(RecoveryReason.OwnerDied, _journal.Inspect().Reason);
        Assert.False(sentinel.IsBlocked(out _));
        Assert.Equal(0, FakeDriver.Sets);
    }

    /// <summary>The marks a crash left are settled before any op reads or drops them, so the first look after a reboot
    /// (the UI's ownership call, or a write) already shows the crash in `crashes` and no stale sentinel.</summary>
    [Fact]
    public void After_a_reboot_the_first_op_whichever_it_is_puts_the_open_window_on_record()
    {
        var d = Power();
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(PowerCap.Kind, "fe", "575000", _host.Now, "ordinary request, mW"));
        _sentinel.Begin("manual", false, "power/fe <- 560000 mW");
        _host.Pid = 100;
        _host.BootId = "boot-B";

        var o = d.Dispatch(new BrokerRequest(BrokerRequest.OpOwnership));
        Assert.True(o.Ok);
        Assert.Equal("Nothing", o.Result!.Value.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, o.Result!.Value.GetProperty("sentinel").ValueKind);
        Assert.EndsWith(CrashLog.FileName, o.Result!.Value.GetProperty("crash_recorded").GetString());
        var crash = Assert.Single(o.Result!.Value.GetProperty("crashes").EnumerateArray());
        Assert.Contains("power/fe <- 560000 mW", crash.GetProperty("sentinel").GetString());
        Assert.Equal("boot-A", crash.GetProperty("boot_id").GetString());
        Assert.Equal(JsonValueKind.Null, d.Dispatch(new BrokerRequest(BrokerRequest.OpOwnership)).Result!.Value.GetProperty("crash_recorded").ValueKind);

        // The same marks left for a write instead: on record, and the write goes ahead with no stale sentinel in its way.
        _host.BootId = "boot-C";
        _journal.Claim(new OwnedItem(PowerCap.Kind, "fe", "575000", _host.Now, null));
        _sentinel.Begin("manual", false, "power/fe <- 500000 mW");
        _host.BootId = "boot-D";
        var w = d.Dispatch(Write("power", "fe", "560000"));
        Assert.True(w.Ok, w.Error);
        Assert.Equal("Applied", Outcome(w));
        var crashes = CrashLog.ReadLast(CrashLog.PathBeside(_sentinel.Path), 5);
        Assert.Equal(2, crashes.Count);
        Assert.Contains("500000", crashes[1].Sentinel);
        Assert.Equal("boot-C", crashes[1].BootId);
    }

    /// <summary>A driver reset keeps the broker's own writes on the card: recovery gives back what this very process
    /// holds, and the reset's recovery puts the held cells on record first.</summary>
    [Fact]
    public void After_a_driver_reset_what_the_broker_holds_is_on_record_and_given_back()
    {
        var d = Power();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.Equal(RecoveryReason.OwnedByThisProcess, _journal.Inspect().Reason);

        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Written", r.Result!.Value.GetProperty("items")[0].GetProperty("action").GetString());
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Equal(RecoveryReason.Nothing, _journal.Inspect().Reason);
        var crash = Assert.Single(CrashLog.ReadLast(CrashLog.PathBeside(_sentinel.Path), 5));
        Assert.StartsWith("driver reset (nvlddmkm 153)", crash.Sentinel);
        Assert.Equal("575000", Assert.Single(crash.Items).Original);
        Assert.EndsWith(CrashLog.FileName, r.Result!.Value.GetProperty("crash_recorded").GetString());

        // A reset with nothing held writes nothing and records nothing.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "again")).Ok);
        Assert.Single(CrashLog.ReadLast(CrashLog.PathBeside(_sentinel.Path), 5));
    }

    /// <summary>A preset is named, never sent: an unknown name or another card model writes nothing and keeps no profile.</summary>
    [Fact]
    public void A_preset_for_another_card_or_an_unknown_name_writes_nothing()
    {
        var d = Power();
        var other = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "founders"));
        Assert.False(other.Ok);
        Assert.Contains("this card is fake", other.Error);
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "nonexistent")).Ok);
        Assert.False(File.Exists(DailyProfile.PathBeside(_journal.Path)));
        Assert.Empty(_journal.Inspect().Items);
    }

    /// <summary>The daily profile is what the broker holds when it is kept, goes back on through the ordinary write, and
    /// after a driver reset goes back on unchanged.</summary>
    [Fact]
    public void A_kept_profile_is_the_held_state_applied_again_and_put_back_unchanged_after_a_reset()
    {
        var d = Power();
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);   // nothing held yet
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", Value: "the agent's pick after RE4"));
        Assert.True(kept.Ok, kept.Error);
        var w = kept.Result!.Value.GetProperty("profile").GetProperty("Writes")[0];
        Assert.Equal("power", w.GetProperty("Kind").GetString());
        Assert.Equal("560000", w.GetProperty("Value").GetString());
        Assert.EndsWith("(the agent's pick after RE4)", kept.Result!.Value.GetProperty("profile").GetProperty("History")[0].GetString());

        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
        var applied = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.Equal(RecoveryReason.OwnedByThisProcess, _journal.Inspect().Reason);

        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "t")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);   // the limit goes back on at once
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153")).Ok);
        var reset = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "after-reset", Value: "nvlddmkm 153"));
        Assert.True(reset.Ok, reset.Error);
        Assert.False(reset.Result!.Value.GetProperty("profile").GetProperty("Suspended").GetBoolean());
        Assert.Equal(560_000u, _requests[0xfe]);   // put back as kept

        // Off suspends it: a suspended profile writes nothing until the person loads it again.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);   // a suspended profile writes nothing
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "load")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);   // until the person loads it again
    }

    private static BrokerRequest Exp(string kind, string? value = null) => new(BrokerRequest.OpExperiment, Kind: kind, Value: value);
    private static string Trial(string curve, string change) => JsonSerializer.Serialize(new { curve, change, at_uv = 950_000u, at_mhz = 2880u });
    private static BrokerRequest Curve(string value) =>
        new(BrokerRequest.OpWrite, Kind: VfWrites.Kind, Key: VfWrites.CoreKey, Value: value, EnableSlots: [nameof(Slot.ClockClientClkVfPointsSetControl)]);

    /// <summary>
    /// Live tuning in a game: a crash while a trial is on the card is the trial's. It becomes a data point and the
    /// daily profile goes back on unchanged; only a crash with no trial in force steps the profile down (the user's
    /// decision, 2026-10-18).
    /// </summary>
    [Fact]
    public void A_crash_during_a_trial_is_a_data_point_and_the_daily_profile_goes_back_unchanged()
    {
        var d = new BrokerDispatcher(new FakeVf().Install(), new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        const string daily = "0*60,30000*67";
        Assert.True(d.Dispatch(Curve(daily)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);

        Assert.False(d.Dispatch(Exp("try", Trial("0*60,45000*67", "+15"))).Ok);   // no session yet
        Assert.True(d.Dispatch(Exp("begin", """{"game":"TLOU2"}""")).Ok);
        var t = d.Dispatch(Exp("try", Trial("0*60,45000*67", "+15 MHz on 60-126")));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(45_000, FakeVf.Offsets()[100]);

        // The driver resets: the broker gives back what is held, records the crash, then puts the profile back on.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153", Uv: 950_000)).Ok);
        var reset = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "after-reset", Value: "nvlddmkm 153"));
        Assert.True(reset.Ok, reset.Error);
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());   // put back as kept, not a notch down
        var p = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal((Experiment.Crash, "TLOU2", "+15 MHz on 60-126", 950_000u), (p.Outcome, p.Game, p.Change, p.CrashUv));
        Assert.Null(Experiment.Load(Experiment.PathBeside(_journal.Path))!.Trial);   // the session stays, the trial is over

        // With no trial in force the profile goes back on unchanged too: no step-down (the user, 2026-10-19).
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "again")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "again")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "after-reset", Value: "again")).Ok);
        Assert.Equal(30_000, FakeVf.Offsets()[100]);
        Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));   // no trial, no data point

        // Every step is in the tuning log, in order.
        var log = File.ReadAllText(TuningLog.PathBeside(_journal.Path));
        Assert.True(log.IndexOf("Session started · TLOU2") < log.IndexOf("Trial · TLOU2"));
        Assert.True(log.IndexOf("Trial · TLOU2") < log.IndexOf("Crash · during trial · TLOU2"));
        Assert.True(log.IndexOf("Crash · during trial · TLOU2") < log.IndexOf("Crash · daily profile"));
        Assert.Contains("Crash voltage from the flight recorder: 950 mV", log);
    }

    /// <summary>The crash is recorded as soon as the cells are given back, not once the GPU idles: a trial the agent
    /// writes in between (the game kept running) is not blamed for it, and the profile step that comes once the GPU is
    /// idle leaves that trial alone. Before, the crashed trial was recorded as "survived" and the next one as the crash.</summary>
    [Fact]
    public void A_trial_written_after_a_reset_but_before_the_GPU_idles_is_not_the_crash()
    {
        var d = new BrokerDispatcher(new FakeVf().Install(), new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        const string daily = "0*60,30000*67";
        Assert.True(d.Dispatch(Curve(daily)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"KCD2"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", Trial("0*60,60000*67", "+30 MHz on 60-126"))).Ok);

        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153", Uv: 970_000)).Ok);
        var crash = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal((Experiment.Crash, "+30 MHz on 60-126", 970_000u), (crash.Outcome, crash.Change, crash.CrashUv));

        // The game is still running; the agent steps back while the GPU is busy.
        Assert.True(d.Dispatch(Exp("try", Trial("0*60,45000*67", "-15 MHz on 60-126"))).Ok);
        var after = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "after-reset", Value: "nvlddmkm 153"));
        Assert.True(after.Ok, after.Error);
        Assert.Contains("a trial began after the crash", after.Result!.Value.GetProperty("skipped").GetString());
        Assert.Equal(45_000, FakeVf.Offsets()[100]);                                           // the new trial stays on
        Assert.Equal("-15 MHz on 60-126", Experiment.Load(Experiment.PathBeside(_journal.Path))!.Trial!.Change);
        Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));      // still only the crash

        // With no trial in force, the same step puts the daily profile back.
        Assert.True(d.Dispatch(Exp("stable", "held")).Ok);
        Assert.True(d.Dispatch(Exp("end")).Ok);
        Assert.Equal(30_000, FakeVf.Offsets()[100]);
    }

    /// <summary>A game that falls to the desktop leaves no driver reset for the broker to see: the player reports it, and
    /// it is the trial's crash all the same, with the daily profile back on at once.</summary>
    [Fact]
    public void A_crash_the_player_reports_is_the_trials_and_the_daily_profile_goes_back()
    {
        var d = new BrokerDispatcher(new FakeVf().Install(), new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        Assert.True(d.Dispatch(Curve("0*60,30000*67")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"RE4"}""")).Ok);
        Assert.False(d.Dispatch(Exp("crash", "nothing on yet")).Ok);                // no trial in force

        Assert.True(d.Dispatch(Exp("try", Trial("0*60,45000*67", "+15"))).Ok);
        var r = d.Dispatch(Exp("crash", "fell to the desktop entering the village"));
        Assert.True(r.Ok, r.Error);
        var p = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal((Experiment.Crash, "fell to the desktop entering the village"), (p.Outcome, p.Note));
        Assert.Equal(30_000, FakeVf.Offsets()[100]);                               // the daily profile, unchanged
        Assert.NotNull(Experiment.Load(Experiment.PathBeside(_journal.Path)));     // the session stays open
    }

    /// <summary>A trial may lower the wall to sample the curve lower down; the data point carries it, and keeping a
    /// profile never keeps it: once a profile is kept only its curve comes from the card (the user, 2026-10-19).</summary>
    [Fact]
    public void A_trial_wall_is_on_the_data_point_but_never_kept_as_the_daily_profile()
    {
        var d = Power();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Alan Wake 2"}""")).Ok);

        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 560 → 450 W", power_mw = 450_000u, keep_curve = true })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(450_000u, _requests[0xfe]);
        Assert.True(d.Dispatch(Exp("stable", "15 min at 450 W")).Ok);
        var p = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal(450_000u, p.PowerMw);

        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", Value: "try to keep the low wall"));
        Assert.True(kept.Ok, kept.Error);
        Assert.Equal("560000", kept.Result!.Value.GetProperty("profile").GetProperty("Writes")[0].GetProperty("Value").GetString());
        Assert.Equal(560_000u, _requests[0xfe]);   // the kept wall is back on at once
        Assert.Contains("the power limit stays the old daily profile's", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));

        // A wall set outside a session is not kept either.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 500_000)).Ok);
        kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.Equal("560000", kept.Result!.Value.GetProperty("profile").GetProperty("Writes")[0].GetProperty("Value").GetString());
    }

    /// <summary>With no daily profile kept (a fresh install), a trial's wall is not kept either: keeping it alone is
    /// nothing to keep, and ending the session puts stock back.</summary>
    [Fact]
    public void With_no_profile_a_trial_wall_is_not_kept_and_goes_back_to_stock()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Alan Wake 2"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 575 → 450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        Assert.Equal(450_000u, _requests[0xfe]);
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.Null(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path)));
        Assert.True(d.Dispatch(Exp("end")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    /// <summary>The daily wall is set on its own: it goes on now and into the profile (one is started when none is kept);
    /// a limit the board refuses changes nothing; while a trial runs under its own wall, the daily one waits for its end.</summary>
    [Fact]
    public void The_daily_wall_is_set_on_its_own_and_waits_for_a_trial_wall()
    {
        var d = Power();
        static BrokerRequest Wall(uint mw) => new(BrokerRequest.OpProfile, Kind: "wall", Value: mw.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string Kept() => Assert.Single(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!.Writes, w => w.Kind == PowerCap.Kind).Value;

        var w = d.Dispatch(Wall(600_000));
        Assert.True(w.Ok, w.Error);
        Assert.Equal(600_000u, _requests[0xfe]);
        Assert.Equal("600000", Kept());
        Assert.False(d.Dispatch(Wall(650_000)).Ok);
        Assert.Equal("600000", Kept());
        Assert.Equal(600_000u, _requests[0xfe]);

        Assert.True(d.Dispatch(Exp("begin", """{"game":"Cyberpunk"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 600 → 450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(Wall(590_000)).Ok);
        Assert.Equal(450_000u, _requests[0xfe]);   // the trial's wall stays
        Assert.Equal("590000", Kept());
        Assert.True(d.Dispatch(Exp("end")).Ok);
        Assert.Equal(590_000u, _requests[0xfe]);
        Assert.Contains("Daily power limit · 590 W", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>Several daily profiles, kept by name (the user, 2026-10-20): save the daily profile as "quiet", switch to
    /// another, and load "quiet" back by name as a preset is loaded; a preset's name is not a saved profile's.</summary>
    [Fact]
    public void A_daily_profile_saved_under_a_name_loads_back_by_that_name()
    {
        var d = Power();
        static BrokerRequest P(string kind, string? value = null) => new(BrokerRequest.OpProfile, Kind: kind, Value: value);
        static BrokerRequest Wall(uint mw) => P("wall", mw.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string Kept() => Assert.Single(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!.Writes, w => w.Kind == PowerCap.Kind).Value;

        Assert.False(d.Dispatch(P("save", "quiet")).Ok);                          // nothing kept yet
        Assert.True(d.Dispatch(Wall(450_000)).Ok);
        var saved = d.Dispatch(P("save", " Quiet "));
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal("Quiet", saved.Result!.Value.GetProperty("name").GetString());
        Assert.False(saved.Result!.Value.GetProperty("replaced").GetBoolean());
        Assert.False(d.Dispatch(P("save", "founders")).Ok);                       // a preset's name
        Assert.False(d.Dispatch(P("save", "  ")).Ok);                             // no name
        Assert.False(d.Dispatch(P("save", "two\nlines")).Ok);
        const string Odd = "CS2 / late night: <quiet> 安静 \U0001F319";   // any name at all, never a file name
        Assert.True(d.Dispatch(P("save", Odd)).Ok);

        Assert.True(d.Dispatch(Wall(600_000)).Ok);                                // the daily profile moves on
        Assert.Equal("600000", Kept());
        var list = d.Dispatch(P("saved"));
        Assert.Equal(["Quiet", Odd], list.Result!.Value.GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        var loaded = d.Dispatch(P("preset", "QUIET"));
        Assert.True(loaded.Ok, loaded.Error);
        Assert.Equal("450000", Kept());
        Assert.Equal(450_000u, _requests[0xfe]);                                   // on the card now
        Assert.Contains("Saved profile loaded · Quiet", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));

        var missing = d.Dispatch(P("preset", "loud"));
        Assert.False(missing.Ok);
        Assert.Contains("quiet", missing.Error);                                   // the names it knows
        Assert.True(d.Dispatch(P("preset", Odd)).Ok);                             // the odd one loads by its name too
        Assert.Equal("450000", Kept());
        Assert.True(d.Dispatch(P("forget", Odd)).Ok);
        Assert.True(d.Dispatch(P("forget", "quiet")).Ok);
        Assert.False(d.Dispatch(P("forget", "quiet")).Ok);
        Assert.False(d.Dispatch(P("preset", "quiet")).Ok);
        Assert.Equal("450000", Kept());                                            // deleting a saved one leaves the daily profile
    }

    /// <summary>A saved profile (or preset) this card cannot take, here a wall above its 600 W board maximum, does not
    /// replace the daily profile: the daily one stays and goes back on, and the answer carries both runs' writes.</summary>
    [Fact]
    public void A_profile_the_card_refuses_leaves_the_daily_profile_kept_and_on_the_card()
    {
        var d = Power();
        static BrokerRequest P(string kind, string? value = null) => new(BrokerRequest.OpProfile, Kind: kind, Value: value);
        string profilePath = DailyProfile.PathBeside(_journal.Path);
        Assert.True(d.Dispatch(P("wall", "450000")).Ok);
        Assert.Equal(450_000u, _requests[0xfe]);
        SavedProfiles.Save(SavedProfiles.PathBeside(_journal.Path), "too big",
            new DailyProfile([new ProfileWrite(PowerCap.Kind, PowerCap.Key(PowerRequestKind.Ordinary), "700000")], _host.Now));

        var r = d.Dispatch(P("preset", "too big"));
        Assert.False(r.Ok);
        Assert.Contains("was not loaded: the daily profile stays as it was and is back on the card", r.Error);
        Assert.Equal("450000", Assert.Single(DailyProfile.Load(profilePath)!.Writes).Value);
        Assert.Equal(450_000u, _requests[0xfe]);
        Assert.Contains("Not loaded · saved profile 'too big'", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>A trial may lower a rail's current limit, where the board request stops at 400 W. The data point
    /// carries it, whatever puts the daily profile back puts the limit back first, and it is never kept.</summary>
    [Fact]
    public void A_trial_current_limit_is_on_the_data_point_goes_back_with_the_daily_profile_and_is_never_kept()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Cyberpunk"}""")).Ok);

        string Limit(uint ma) => JsonSerializer.Serialize(new { curve = "0*127", change = $"NVVDD 600 → {ma / 1000} A", nvvdd_ma = ma, keep_curve = true });
        var t = d.Dispatch(Exp("try", Limit(350_000)));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));
        Assert.True(d.Dispatch(Exp("stable", "10 min at 350 A")).Ok);
        Assert.Equal(350_000u, Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10)).NvvddMa);

        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", Value: "try to keep the limit"));
        Assert.True(kept.Ok, kept.Error);
        Assert.DoesNotContain(kept.Result!.Value.GetProperty("profile").GetProperty("Writes").EnumerateArray(), w => w.GetProperty("Kind").GetString() == CurrentLimitWrites.Kind);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));   // back to the default at once
        Assert.DoesNotContain(_journal.Inspect().Items, i => i.Kind == CurrentLimitWrites.Kind);

        // A limit beyond the default is refused, and the trial before stays in force.
        Assert.False(d.Dispatch(Exp("try", Limit(650_000))).Ok);
        Assert.True(d.Dispatch(Exp("try", Limit(300_000))).Ok);
        var end = d.Dispatch(Exp("end"));
        Assert.True(end.Ok, end.Error);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(560_000u, _requests[0xfe]);
        var log = File.ReadAllText(TuningLog.PathBeside(_journal.Path));
        Assert.Contains("NVVDD current limit 350 A", log);
        Assert.Contains("ocp/nvvdd: 300000 → 600000", log);
    }

    /// <summary>A new session while a trial is in force ends that trial as end_session does: its wall goes back to the daily
    /// one, so the new session's data points are recorded under the wall they ran at, and a first keep cannot keep the
    /// old trial's lowered wall as the daily one.</summary>
    [Fact]
    public void A_new_session_puts_the_trial_in_force_back_first()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 575 → 450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        Assert.Equal(450_000u, _requests[0xfe]);

        var b = d.Dispatch(Exp("begin", """{"game":"B"}"""));
        Assert.True(b.Ok, b.Error);
        Assert.Equal(575_000u, _requests[0xfe]);                    // no daily profile yet: back to stock
        Assert.Empty(_journal.Inspect().Items);
        Assert.Equal(Clocktail.Mcp.WriteOutcome.Applied, Clocktail.Mcp.WriteOutcome.Of(b.Ok, b.Code, b.Result));
        var a = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal(450_000u, a.PowerMw);                           // A's trial, recorded under its own wall
        Assert.Equal("B", Experiment.Load(Experiment.PathBeside(_journal.Path))!.Game);

        // With nothing held, the next session begins with nothing to put back.
        Assert.True(d.Dispatch(Exp("begin", """{"game":"C"}""")).Ok);
        Assert.Contains("Last trial: wall 575 → 450 W", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>A trial of several items whose later item is refused does not leave the earlier ones on the card: they go
    /// back, the trial before stays in force, and the answer says nothing changed. Before, the agent was told "written:
    /// none" while the lowered wall stayed on.</summary>
    [Fact]
    public void A_trial_that_fails_part_way_puts_back_what_it_wrote_first()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Cyberpunk"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 560 → 500 W", power_mw = 500_000u, keep_curve = true }))).Ok);

        // The wall lands first, then the current limit is refused (above its default).
        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W and 650 A", power_mw = 450_000u, nvvdd_ma = 650_000u, keep_curve = true })));
        Assert.False(t.Ok);
        Assert.Contains("put back, so the card is as it was", t.Error);
        Assert.Equal(500_000u, _requests[0xfe]);                    // the previous trial's wall, not 450 W
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(Clocktail.Mcp.WriteOutcome.None, Clocktail.Mcp.WriteOutcome.Of(t.Ok, t.Code, t.Result));
        var exp = Experiment.Load(Experiment.PathBeside(_journal.Path))!;
        Assert.Equal("wall 560 → 500 W", exp.Trial!.Change);            // the trial before stays in force
        Assert.DoesNotContain(_journal.Inspect().Items, i => i.Kind == CurrentLimitWrites.Kind);
        Assert.Contains("The items written before it were put back", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>A trial may undervolt MSVDD through its REL offset. The data point carries the offset, and with no
    /// offsets in the daily profile putting it back puts the stock offset back first.</summary>
    [Fact]
    public void A_trial_msvdd_offset_is_on_the_data_point_and_goes_back_to_stock_when_the_profile_keeps_none()
    {
        var d = Rails();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Steel Nomad"}""")).Ok);

        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "MSVDD REL -50 → -75 mV", msvdd_rel_uv = -75_000, keep_curve = true })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(-75_000, MsvddRel());
        Assert.True(d.Dispatch(Exp("stable", "a run at -75")).Ok);
        Assert.Equal(-75_000, Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10)).MsvddRelUv);

        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(RailsFixture.MsvddRelOffset, MsvddRel());
        Assert.DoesNotContain(_journal.Inspect().Items, i => i.Kind == RailWrites.Kind);
        Assert.Contains("MSVDD REL offset -75 mV", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>The MSVDD offsets are kept from the card, as the curves are (the user, 2026-10-20: REL 0 is free speed).
    /// A trial's offset then goes back to the kept one in a single write, and a later keep takes the card's again.</summary>
    [Fact]
    public void The_msvdd_offsets_are_kept_from_the_card_and_a_trial_goes_back_to_them()
    {
        var d = Rails();
        string msvdd = RailWrites.Key(Rail.Msvdd);
        string? Kept(BrokerResponse r) => r.Result!.Value.GetProperty("profile").GetProperty("Writes").EnumerateArray()
            .Where(w => w.GetProperty("Kind").GetString() == RailWrites.Kind && w.GetProperty("Key").GetString() == msvdd)
            .Select(w => w.GetProperty("Value").GetString()).SingleOrDefault();

        Assert.True(d.Dispatch(Write(RailWrites.Kind, msvdd, "rel=0")).Ok);
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", Value: "REL 0"));
        Assert.True(kept.Ok, kept.Error);
        Assert.Equal(0, RailWrites.Decode(Kept(kept)!).RelUv);
        Assert.Contains("New: rails/msvdd", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));

        Assert.True(d.Dispatch(Exp("begin", """{"game":"Steel Nomad"}""")).Ok);
        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "MSVDD REL 0 → -12.5 mV", msvdd_rel_uv = -12_500, keep_curve = true })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(-12_500, MsvddRel());
        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(0, MsvddRel());
        // Straight to the kept offset, never through stock.
        Assert.Single(back.Result!.Value.GetProperty("applied").EnumerateArray());
        Assert.Equal(RailsFixture.MsvddRelOffset, RailWrites.Decode(Assert.Single(_journal.Inspect().Items).Original).RelUv);

        // A keep with a profile already takes the card's offset again, here a trial's, and puts nothing back.
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "MSVDD REL 0 → -12.5 mV", msvdd_rel_uv = -12_500, keep_curve = true }))).Ok);
        kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.True(kept.Ok, kept.Error);
        Assert.Equal(-12_500, RailWrites.Decode(Kept(kept)!).RelUv);
        Assert.Equal(-12_500, MsvddRel());
    }

    private static int MsvddRel() =>
        RailWrites.OffsetsIn(FakeDriver.State[Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word], Rail.Msvdd)!.Value.RelUv;

    [Fact]
    public void Each_trial_ends_as_a_data_point_and_ending_the_session_puts_the_daily_profile_back()
    {
        var d = new BrokerDispatcher(new FakeVf().Install(), new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        Assert.True(d.Dispatch(Curve("0*60,30000*67")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Cyberpunk"}""")).Ok);

        Assert.True(d.Dispatch(Exp("try", Trial("0*60,45000*67", "first"))).Ok);
        Assert.True(d.Dispatch(Exp("try", Trial("0*60,60000*67", "second"))).Ok);   // the first survived until replaced
        Assert.True(d.Dispatch(Exp("stable", "20 min, no artefacts")).Ok);
        var end = d.Dispatch(Exp("end"));
        Assert.True(end.Ok, end.Error);

        var points = Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10);
        Assert.Equal([("first", Experiment.Survived), ("second", Experiment.Stable)], points.Select(x => (x.Change, x.Outcome)));
        Assert.Equal("20 min, no artefacts", points[1].Note);
        Assert.Equal(30_000, FakeVf.Offsets()[100]);                               // the daily profile is back on
        Assert.Null(Experiment.Load(Experiment.PathBeside(_journal.Path)));
    }

    /// <summary>
    /// The live drill of 2026-10-20: a memory offset over a curve in force was refused, because any Pstates20 write
    /// resets every V/F point offset. A curve the broker holds is set aside to 0 for it and written back after, in the
    /// trial and when the daily profile goes back on.
    /// </summary>
    [Fact]
    public void A_memory_offset_over_a_held_curve_sets_the_curve_aside_and_writes_it_back()
    {
        new FakeVf().Install();
        // A memory clock beside the core one, and a P0 write that resets the curve, as the card does.
        var table = FakeDriver.State[FakeVf.PstatesWord];
        int mem = Clocktail.Domain.Clocks.Pstates20Layout.Clock(0, 1);
        W(table, 0xc, 2);
        W(table, mem, (uint)Clocktail.Domain.Clocks.ClockDomain.Memory); W(table, mem + 8, 1);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(mem + 0x10), -1_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(mem + 0x14), 3_000_000);
        var vfApplier = FakeDriver.RequestApplier!;
        int pstateWrites = 0;
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) != FakeVf.PstatesWord) return vfApplier(state, req);
            pstateWrites++;
            var next = (byte[])state.Clone();
            int entry = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(0x1c)) == (uint)Clocktail.Domain.Clocks.ClockDomain.Memory ? mem : Clocktail.Domain.Clocks.Pstates20Layout.Clock(0, 0);
            req.AsSpan(0x28, 4).CopyTo(next.AsSpan(entry + 0xc));
            var control = FakeDriver.State[FakeVf.ControlWord];
            for (int i = 0; i < 127; i++) control.AsSpan(VfLayout.OffsetField(i), 4).Clear();
            return next;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.GetPstates20],
            [Slot.ClockClientClkVfPointsSetControl, Slot.SetPstates20]), WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl));
        int MemoryKhz() => BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.State[FakeVf.PstatesWord].AsSpan(mem + 0xc));

        var d = new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        const string daily = "0*60,30000*67";
        Assert.True(d.Dispatch(Curve(daily)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);

        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = daily, change = "memory 0 → +179 MHz", memory_khz = 179_000, keep_curve = true })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(179_000, MemoryKhz());
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());   // written back after the memory write reset it

        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(0, MemoryKhz());
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());
        Assert.Equal(2, pstateWrites);

        // A curve this broker does not hold is not its to wipe: the memory write is refused, as before.
        _journal.Release(VfWrites.Kind, VfWrites.CoreKey);
        t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = daily, change = "memory 0 → +179 MHz", memory_khz = 179_000, keep_curve = true })));
        Assert.False(t.Ok);
        Assert.Equal(0, MemoryKhz());
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());
    }

    [Fact]
    public void Recover_says_per_item_whether_the_hardware_state_is_known()
    {
        var d = Power(canWrite: false);
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(PowerCap.Kind, "fe", "560000", _host.Now, null));   // hardware is at 575000: differs, needs a write
        _host.Pid = 100;
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover));
        Assert.False(r.Ok);
        var item = r.Result!.Value.GetProperty("items")[0];
        Assert.Equal("Refused", item.GetProperty("action").GetString());
        Assert.True(item.GetProperty("state_known").GetBoolean());   // looked at and known, only not writable from here
        Assert.True(r.Result!.Value.GetProperty("needs_write").GetBoolean());
        Assert.True(r.Result!.Value.GetProperty("sentinel_cleared").GetBoolean());
        Assert.False(r.Result!.Value.GetProperty("journal_cleared").GetBoolean());
    }

    /// <summary>A dispatcher on the 5090-shaped rails with NVVDD's REL at 1.2 V stock, above the 1.15 V ceiling; the
    /// status follows the control offsets so a write moves what the next read sees.</summary>
    private BrokerDispatcher Rails()
    {
        RailsFixture.SeedDriver();
        uint statusWord = Slots.Info(Slot.VoltVoltRailsGetStatus).Shape!.Value.Word, controlWord = Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != statusWord) return null;
            var s = RailsFixture.Status();
            var o = RailWrites.OffsetsIn(FakeDriver.State[controlWord], Rail.Nvvdd)!.Value;
            int b = RailsLayout.StatusBase;
            uint rel = (uint)(1_200_000 + o.RelUv), alt = (uint)(RailsFixture.NvvddAlt + o.AltUv), ov = (uint)(RailsFixture.NvvddOv + o.OvUv);
            W(s, b + 4, rel); W(s, b + 8, alt); W(s, b + 12, ov); W(s, b + 16, Math.Min(rel, Math.Min(alt, ov))); W(s, b + 20, (uint)(RailsFixture.NvvddVmin + o.VminUv));
            return s;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.VoltVoltRailsSetControl]));
        return new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
    }

    /// <summary>
    /// The write verb hands the path the journal's original for the cell, so a user can put a lowered slot back to a
    /// stock value above the ceiling (the hardware held it before this program did anything) and the record is
    /// released; the request itself never supplies an origin.
    /// </summary>
    [Fact]
    public void The_write_verb_hands_the_path_the_journal_origin_so_a_user_restore_lands_and_releases()
    {
        var d = Rails();
        var down = d.Dispatch(Write("rails", "nvvdd", "rel=-100000"));
        Assert.True(down.Ok, down.Error);
        Assert.Equal("Applied", Outcome(down));
        Assert.StartsWith("held: original rel=0,alt=0,ov=0,vmin=0", OwnedBy(down));
        Assert.Equal(JsonValueKind.Null, down.Result!.Value.GetProperty("applied")[0].GetProperty("origin").ValueKind);

        var back = d.Dispatch(Write("rails", "NVVDD", "rel=0"));     // 1.2 V again: above the ceiling, but where the hardware stood
        Assert.True(back.Ok, back.Error);
        Assert.Equal("Applied", Outcome(back));
        Assert.StartsWith("released", OwnedBy(back));
        Assert.Equal("rel=0,alt=0,ov=0,vmin=0", back.Result!.Value.GetProperty("applied")[0].GetProperty("origin").GetString());
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.Equal(2, FakeDriver.Sets);
    }

    [Fact]
    public void A_state_file_that_cannot_be_written_is_a_failed_answer_not_an_exception()
    {
        Directory.CreateDirectory(Path.Combine(_dir, Experiment.FileName));   // a folder where the session file goes
        var r = Power().Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: "{\"game\":\"RE4\"}"));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.Failed, r.Code);
    }

    /// <summary>Waking up from sleep: the profile again, writing only what sleep took, and never under an agent's trial.</summary>
    [Fact]
    public void Waking_up_puts_back_only_what_sleep_took_and_leaves_a_trial_alone()
    {
        var d = Power();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);

        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "resume", Value: "Kernel-Power 107"));
        Assert.True(kept.Ok, kept.Error);
        Assert.Equal("NoChange", kept.Result!.Value.GetProperty("applied")[0].GetProperty("outcome").GetString());   // sleep kept it

        _requests[0xfe] = 575_000;   // sleep took it: the card back at its stock wall
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "resume", Value: "Kernel-Power 107")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);

        _requests[0xfe] = 575_000;
        new Experiment("RE4", null, DateTimeOffset.Now, new ExperimentTrial(DateTimeOffset.Now, "0*127", "+15 MHz", null, null))
            .Save(Experiment.PathBeside(_journal.Path));
        var trial = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "resume", Value: "Kernel-Power 107"));
        Assert.True(trial.Ok, trial.Error);
        Assert.Contains("a trial is in force", trial.Result!.Value.GetProperty("skipped").GetString());
        Assert.Equal(575_000u, _requests[0xfe]);   // nothing written under the trial
    }
}
