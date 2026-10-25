using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
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
        public string? Vbios { get; init; }
        public string? Driver { get; init; }
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
    private BrokerDispatcher Power(bool canWrite = true, Func<string?>? judge = null, ApplySentinel? sentinel = null, string? vbios = null, string? driver = null)
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
        var t = new Target(_journal, sentinel ?? _sentinel, _gpu) { Vbios = vbios, Driver = driver };
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
        Assert.Equal(13, paths.GetArrayLength());
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

    /// <summary>S14: after a VBIOS flash the daily profile (offsets measured on the old firmware's curve) does not go on by
    /// itself: a start, a driver reset and back_to_daily leave the card at stock and say why, until the person loads it,
    /// which binds it to the new VBIOS.</summary>
    [Fact]
    public void A_profile_kept_on_another_vbios_does_not_go_on_by_itself()
    {
        var d = Power(vbios: "98.02.2E.00.0A", driver: "617.42");
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.Equal("98.02.2E.00.0A", kept.Result!.Value.GetProperty("profile").GetProperty("Vbios").GetString());
        Assert.Equal("617.42", kept.Result!.Value.GetProperty("profile").GetProperty("Driver").GetString());
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);

        var flashed = Power(vbios: "98.02.30.00.01", driver: "617.42");
        var show = flashed.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "show"));
        Assert.Equal("98.02.30.00.01", show.Result!.Value.GetProperty("card").GetProperty("vbios").GetString());
        foreach (var kind in new[] { "apply", "apply-power", "after-reset" })
        {
            var r = flashed.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: kind, Value: "the broker started"));
            Assert.True(r.Ok, r.Error);
            Assert.Contains("kept on VBIOS 98.02.2E.00.0A", r.Result!.Value.GetProperty("skipped").GetString());
            Assert.Equal(575_000u, _requests[0xfe]);   // stock
        }

        var load = flashed.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "load"));
        Assert.True(load.Ok, load.Error);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.Equal("98.02.30.00.01", load.Result!.Value.GetProperty("profile").GetProperty("Vbios").GetString());
        Assert.True(flashed.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);
        var apply = flashed.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.Equal(JsonValueKind.Null, apply.Result!.Value.GetProperty("skipped").ValueKind);
        Assert.Equal(560_000u, _requests[0xfe]);   // bound to this VBIOS now: it goes on at every start again
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

        // A limit beyond the driver's range is refused, and the trial before stays in force.
        Assert.False(d.Dispatch(Exp("try", Limit(5_001_001))).Ok);
        Assert.True(d.Dispatch(Exp("try", Limit(300_000))).Ok);
        var end = d.Dispatch(Exp("end"));
        Assert.True(end.Ok, end.Error);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(560_000u, _requests[0xfe]);
        var log = File.ReadAllText(TuningLog.PathBeside(_journal.Path));
        Assert.Contains("NVVDD current limit 350 A", log);
        Assert.Contains("ocp/nvvdd: 300000 → 600000", log);
    }

    /// <summary>When the agent goes away (its MCP server's input closes) the relay asks for "release": what only a
    /// watching agent may hold goes back, here a lowered current limit, while the rest of the trial (its wall) stays in
    /// force, recorded from then on as a trial of its own. Before, nothing ended it until a reboot or a driver event.</summary>
    [Fact]
    public void An_agent_that_leaves_hands_back_the_trial_only_settings_and_nothing_else()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"sweep"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));

        var r = d.Dispatch(Exp("release", "the agent disconnected"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));     // the limit back to its default
        Assert.Equal(450_000u, _requests[0xfe]);                    // the trial's wall stays
        Assert.DoesNotContain(_journal.Inspect().Items, i => i.Kind == CurrentLimitWrites.Kind);
        var trial = Experiment.Load(Experiment.PathBeside(_journal.Path))!.Trial!;
        Assert.Equal((450_000u, (uint?)null), (trial.PowerMw!.Value, trial.NvvddMa));
        Assert.Contains("handed back: the agent disconnected", trial.Change);
        var before = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal((Experiment.Survived, 350_000u), (before.Outcome, before.NvvddMa!.Value));   // the part that ran with it

        // Nothing trial-only held: nothing written, nothing recorded.
        Assert.True(d.Dispatch(Exp("release", "again")).Ok);
        Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Contains("Trial-only settings handed back", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>A release the card could not take (here a stability run holding it) changes nothing: the limit stays on the
    /// card, the trial keeps it in its record, and no data point is written. Before 0.3.4 the trial was recorded as handed
    /// back whether or not it was. The tuning log has the refusal once, not every ask after it; asked again once the card is
    /// free (the trial lease does), it goes back and is recorded.</summary>
    [Fact]
    public void A_hand_back_the_card_could_not_take_records_nothing_until_it_lands()
    {
        string? busy = null;
        var d = Power(judge: () => busy);
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"sweep"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        string trialPath = Experiment.PathBeside(_journal.Path), dataPath = Experiment.DataPathBeside(_journal.Path);
        var before = Experiment.Load(trialPath)!.Trial!;

        busy = "clocktail-lab (pid 4242)";
        var r = d.Dispatch(Exp("release", "no agent has been heard from for 5 min"));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));             // still on the card
        Assert.Contains(_journal.Inspect().Items, i => i.Kind == CurrentLimitWrites.Kind);
        Assert.Equal(before, Experiment.Load(trialPath)!.Trial);              // and in the trial's record, as it was
        Assert.Empty(Experiment.ReadLast(dataPath, 10));
        Assert.False(d.Dispatch(Exp("release", "no agent has been heard from for 5 min")).Ok);   // asked again, still held
        string tuning = File.ReadAllText(TuningLog.PathBeside(_journal.Path));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(tuning, "Trial-only settings not handed back"));
        Assert.DoesNotContain("Trial-only settings handed back", tuning);

        busy = null;
        var again = d.Dispatch(Exp("release", "no agent has been heard from for 6 min"));
        Assert.True(again.Ok, again.Error);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        var trial = Experiment.Load(trialPath)!.Trial!;
        Assert.Equal((450_000u, (uint?)null), (trial.PowerMw!.Value, trial.NvvddMa));
        Assert.Contains("current limits handed back: no agent has been heard from for 6 min", trial.Change);
        Assert.Equal(350_000u, Assert.Single(Experiment.ReadLast(dataPath, 10)).NvvddMa!.Value);
        Assert.Contains("Trial-only settings handed back", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>Handing back takes what went back out of the trial's record, the trial-only writes too (here the F8 request;
    /// a clock floor, point lock and Boost Lock are the same kind of write): the trial is recorded from then on as it runs.
    /// Before 0.3.4 only the fan duty and the current limits were taken out, so every later data point still named an F8
    /// request or a Boost Lock the card no longer held.</summary>
    [Fact]
    public void Handing_back_takes_the_trial_only_writes_out_of_the_trials_record()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);
        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "F8 absent → 500 W", keep_curve = true,
            writes = new[] { new { kind = "power", key = "f8", value = "500000", create = true } } })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(500_000u, _requests[0xf8]);

        var r = d.Dispatch(Exp("release", "the agent disconnected"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(600_000u, _requests[0xf8]);                            // back to the board maximum
        var trial = Experiment.Load(Experiment.PathBeside(_journal.Path))!.Trial!;
        Assert.Null(trial.Writes);
        Assert.Contains("the F8 request handed back: the agent disconnected", trial.Change);
        var before = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
        Assert.Equal("f8", Assert.Single(before.Writes!).Key);              // the part that ran with it
    }

    /// <summary>A dispatcher with a trial in force (a 450 W wall) whose writes a stability run can hold off: the card a
    /// put-back cannot reach while <paramref name="busy"/> says so.</summary>
    private BrokerDispatcher TrialAt450(Func<string?> busy)
    {
        var d = Power(judge: busy);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 575 → 450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        Assert.Equal(450_000u, _requests[0xfe]);
        return d;
    }

    private ExperimentTrial? TrialNow() => Experiment.Load(Experiment.PathBeside(_journal.Path))?.Trial;
    private List<string> Outcomes() => [.. Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20).Select(p => p.Outcome)];

    /// <summary>back_to_daily the card cannot take (here a stability run holding it) leaves the trial in force, its ending on
    /// record: its survived point is on record once, a crash before the card is off it is the trial's (not the daily profile's), and the
    /// back_to_daily that lands closes it without a second point. Until 0.3.7 the trial left the record first, so the card
    /// ran the 450 W wall under a record that said no trial, and the crash would have been the daily profile's.</summary>
    [Fact]
    public void A_trial_leaves_the_record_only_once_the_card_has_left_it()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);

        busy = "clocktail-lab (pid 4242)";
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.False(r.Ok);
        Assert.Equal(450_000u, _requests[0xfe]);                            // still on the card
        Assert.Equal(("wall 575 → 450 W", Experiment.Survived, false), (TrialNow()!.Change, TrialNow()!.Ending, TrialNow()!.Marked));   // ended, not marked stable
        Assert.Equal([Experiment.Survived], Outcomes());

        var crash = d.Dispatch(Exp("crash", "the game closed to the desktop"));   // still held off: the card is on the trial
        Assert.False(crash.Ok);
        Assert.Equal([Experiment.Survived, Experiment.Crash], Outcomes());
        Assert.Equal("wall 575 → 450 W", Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20)[^1].Change);
        Assert.NotNull(TrialNow());

        busy = null;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Null(TrialNow());
        Assert.Equal([Experiment.Survived, Experiment.Crash], Outcomes());   // nothing written twice
    }

    /// <summary>A new session over a trial the card did not let go of does not begin, and the trial stays in force, so
    /// asking again puts it back first. Until 0.3.7 the first ask took the trial off the record, and the second began the
    /// new session over a card still at the old trial's 450 W.</summary>
    [Fact]
    public void A_new_session_waits_until_the_trial_before_it_is_off_the_card()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);

        busy = "clocktail-lab (pid 4242)";
        Assert.False(d.Dispatch(Exp("begin", """{"game":"B"}""")).Ok);
        Assert.Equal("A", Experiment.Load(Experiment.PathBeside(_journal.Path))!.Game);
        Assert.False(d.Dispatch(Exp("begin", """{"game":"B"}""")).Ok);    // asked again while it is still held: still not
        Assert.Equal(450_000u, _requests[0xfe]);

        busy = null;
        var b = d.Dispatch(Exp("begin", """{"game":"B"}"""));
        Assert.True(b.Ok, b.Error);
        Assert.Equal(575_000u, _requests[0xfe]);                            // the old trial off the card first
        Assert.Equal(("B", (ExperimentTrial?)null), (Experiment.Load(Experiment.PathBeside(_journal.Path))!.Game, TrialNow()));
        Assert.Equal([Experiment.Survived], Outcomes());
    }

    /// <summary>end_session over a trial the card did not let go of keeps the session open with the trial in force; ending
    /// it again once the card is free ends it.</summary>
    [Fact]
    public void A_session_ends_only_once_its_trial_is_off_the_card()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);

        busy = "clocktail-lab (pid 4242)";
        var r = d.Dispatch(Exp("end"));
        Assert.False(r.Ok);
        Assert.Contains("the session stays open", r.Error);
        Assert.NotNull(TrialNow());

        busy = null;
        Assert.True(d.Dispatch(Exp("end")).Ok);
        Assert.False(File.Exists(Experiment.PathBeside(_journal.Path)));
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Equal([Experiment.Survived], Outcomes());
    }

    /// <summary>"stock" suspends the profile and puts the card back to stock in one operation, so the trial leaves the
    /// record only once that landed. Until 0.3.7 stock was "off", which closed the trial, then a recovery of its own.
    /// "off" alone (the person's CLI: stock at the next start) writes nothing, so the trial stays in force with the card.</summary>
    [Fact]
    public void Stock_takes_the_card_off_the_trial_before_the_trial_leaves_the_record()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "560000")).Ok);   // a daily profile to suspend

        int sets = FakeDriver.Sets;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off")).Ok);
        Assert.Equal(sets, FakeDriver.Sets);                                  // nothing written
        Assert.NotNull(TrialNow());                                           // the card still runs the trial
        Assert.True(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!.Suspended);

        busy = "clocktail-lab (pid 4242)";
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        Assert.NotNull(TrialNow());
        Assert.Equal(450_000u, _requests[0xfe]);

        busy = null;
        var stock = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock"));
        Assert.True(stock.Ok, stock.Error);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Null(TrialNow());
        Assert.Empty(_journal.Inspect().Items);
        Assert.Equal(JsonValueKind.Object, stock.Result!.Value.GetProperty("recover").ValueKind);
        sets = FakeDriver.Sets;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);   // a recovery after it: nothing left to do
        Assert.Equal(sets, FakeDriver.Sets);
    }

    /// <summary>mark_crash asked again while the card is still held (the answer says to try again once it is free) does not
    /// record the crash twice: an unattended session's crash budget counts each crash once. A different ending after it
    /// (back_to_daily) records nothing more either: the crash stays the trial's ending.</summary>
    [Fact]
    public void A_crash_asked_again_while_the_card_is_held_is_recorded_once()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);

        busy = "clocktail-lab (pid 4242)";
        Assert.False(d.Dispatch(Exp("crash", "the game closed to the desktop")).Ok);
        Assert.False(d.Dispatch(Exp("crash", "the game closed to the desktop")).Ok);   // asked again
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily")).Ok);
        Assert.Equal([Experiment.Crash], Outcomes());
        Assert.Equal(Experiment.Crash, TrialNow()!.Ending);

        busy = null;
        Assert.True(d.Dispatch(Exp("crash", "the game closed to the desktop")).Ok);
        Assert.Null(TrialNow());
        Assert.Equal([Experiment.Crash], Outcomes());
    }

    /// <summary>A put-back refused for a reason no wait cures (here a cell this version cannot give back) lets the record go,
    /// as before 0.3.7, saying the card may still hold part of the trial: otherwise nothing could ever end it.</summary>
    [Fact]
    public void A_put_back_that_can_never_land_lets_the_record_go_and_says_so()
    {
        var d = TrialAt450(() => null);
        _journal.Claim(new OwnedItem("nosuchkind", "x", "1", _host.Now));     // held, and no write path can give it back
        var r = d.Dispatch(Exp("end"));
        Assert.False(r.Ok);
        Assert.False(File.Exists(Experiment.PathBeside(_journal.Path)));    // the session ended all the same
        Assert.Contains("Trial off the record, not all off the card", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>The fan guard's fans back to the curve pass a measurement's judge.lock (the service's own safety action, a
    /// card too hot under a fixed fan duty); every other write still waits for it.</summary>
    [Fact]
    public void The_fan_guards_safety_writes_pass_a_measurement_and_nothing_else_does()
    {
        var d = Power(judge: () => "stress_test (pid 4242)");
        Assert.Equal(BrokerCode.JudgeBusy, d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 450_000)).Code);
        BrokerDispatcher.CurrentLease.Value = BrokerDispatcher.SafetyLease;
        try { Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 450_000)).Ok); }
        finally { BrokerDispatcher.CurrentLease.Value = null; }
        Assert.Equal(450_000u, _requests[0xfe]);
    }

    /// <summary>The fan guard's request and its lease are the service's own, and so are its recoveries after a driver reset:
    /// a client sending any of them is refused, and a request carrying the lease does not pass the lock with it in-process
    /// either.</summary>
    [Fact]
    public void A_client_cannot_pass_a_measurement_with_the_service_s_safety_lease()
    {
        var d = Power(judge: () => "stress_test (pid 4242)");
        Assert.Equal(BrokerCode.BadRequest, d.FromClient(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 450_000, Lease: BrokerDispatcher.SafetyLease)).Code);
        Assert.Equal(BrokerCode.BadRequest, d.FromClient(Exp("fans-back", "a client")).Code);
        Assert.Equal(BrokerCode.BadRequest, d.FromClient(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset)).Code);
        Assert.Equal(BrokerCode.BadRequest, d.FromClient(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverResetAgain)).Code);
        Assert.Equal(BrokerCode.BadRequest, d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "a client")).Code);
        Assert.Equal(BrokerCode.JudgeBusy, d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 450_000, Lease: BrokerDispatcher.SafetyLease)).Code);
        Assert.Equal(BrokerCode.JudgeBusy, d.FromClient(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 450_000)).Code);   // an ordinary one meets the judge
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Equal(0, FakeDriver.Sets);
    }

    /// <summary>A trial whose ending is on record (back_to_daily, mark_crash: the card held off) is not marked stable after
    /// it, by a lab step or the agent, and one that crashed is never kept. Before, a sweep's step after the agent's
    /// back_to_daily wrote survived then stable for one trial, and keep could make a crash the daily profile.</summary>
    [Fact]
    public void A_trial_that_has_ended_is_neither_marked_stable_nor_kept()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);
        busy = "clocktail-lab (pid 4242)";
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily")).Ok);
        Assert.Equal(BrokerCode.CannotWrite, d.Dispatch(Exp("stable", "tune_memory: +500 MHz")).Code);
        Assert.Equal([Experiment.Survived], Outcomes());

        Assert.False(d.Dispatch(Exp("crash", "the game closed to the desktop")).Ok);   // still on the card: the trial's crash
        var keep = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.Equal(BrokerCode.CannotWrite, keep.Code);
        Assert.Contains("a crash is not kept", keep.Error);
        Assert.Null(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path)));
        Assert.Equal([Experiment.Survived, Experiment.Crash], Outcomes());
    }

    /// <summary>A hand-back (here the trial lease's release once the agent has gone) keeps an ending on record: the rest of
    /// the trial is still that trial waiting to go back, so putting it back records nothing more. Before, the hand-back
    /// reopened it, and the back_to_daily after it recorded a survived point after the crash.</summary>
    [Fact]
    public void A_hand_back_keeps_the_trial_s_ending_and_nothing_more_is_recorded()
    {
        string? busy = null;
        var d = Power(judge: () => busy);
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"sweep"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);

        busy = "clocktail-lab (pid 4242)";
        Assert.False(d.Dispatch(Exp("crash", "the game closed to the desktop")).Ok);
        busy = null;
        Assert.True(d.Dispatch(Exp("release", "the agent disconnected")).Ok);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(Experiment.Crash, TrialNow()!.Ending);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily")).Ok);
        Assert.Null(TrialNow());
        Assert.Equal([Experiment.Crash], Outcomes());
    }

    /// <summary>A driver reset after a mark_crash the card held off is that same crash, recorded once (an unattended
    /// session's budget counts it once); the reset took the card off the trial, so it leaves the record.</summary>
    [Fact]
    public void A_driver_reset_after_a_held_mark_crash_is_not_a_second_crash()
    {
        string? busy = null;
        var d = TrialAt450(() => busy);
        busy = "stress_test (pid 4242)";
        Assert.False(d.Dispatch(Exp("crash", "stress_test failed: 3 errors")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153")).Ok);
        Assert.Null(TrialNow());
        Assert.Equal([Experiment.Crash], Outcomes());
    }

    /// <summary>A dispatcher judged as the service is (<see cref="BrokerDispatcher.Judge"/>, its sentinel's second look too) by
    /// a run that takes judge.lock once the next write of the test's has reached the driver (<paramref name="lockedAfter"/>).</summary>
    private BrokerDispatcher JudgedByARun(Func<int?> lockedAfter, bool waits)
    {
        var judge = BrokerDispatcher.Judge(() => lockedAfter() is { } s && FakeDriver.Sets > s
            ? new JudgeLock.Info(4242, "stress-test-1", "stress_test", Waits: waits) : null);
        return Power(judge: judge, sentinel: new ApplySentinel(Path.Combine(_dir, "apply.lock"), judge));
    }

    /// <summary>
    /// The judge's word holds for a whole operation: a run that takes judge.lock while a put-back is landing and waits for it
    /// ("quiet", Clocktail's runs since 0.3.7) lets it finish. Before 0.3.7 the next write was refused, so the NVVDD limit
    /// went back while the 450 W wall stayed, and the trial was kept in force as if none had. The lock appears once the
    /// put-back's first write has reached the driver: back_to_daily over a daily profile (each write a request of its own,
    /// each judged), and stock (one recovery, every cell under its one window).
    /// </summary>
    [Theory]
    [InlineData("apply")]
    [InlineData("stock")]
    public void A_run_that_waits_lets_a_put_back_under_way_finish(string kind)
    {
        int? lockedAfter = null;
        var d = JudgedByARun(() => lockedAfter, waits: true);
        CurrentLimitsFixture.Install();
        if (kind == "apply") Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "560000")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);

        lockedAfter = FakeDriver.Sets;
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: kind, Value: "back_to_daily"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(kind == "apply" ? 560_000u : 575_000u, _requests[0xfe]);
        Assert.Null(TrialNow());
        // The run's lock is there: the next operation meets it.
        Assert.Equal(BrokerCode.JudgeBusy, d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 500_000)).Code);
    }

    /// <summary>A run that does not wait for the operation in flight (StabilityLab, which looks for apply.lock before it
    /// starts; a Clocktail run from before 0.3.7) is never written under: every write after it took the lock stands down,
    /// as before 0.3.7, and the put-back stops where it is, the trial kept in force to be asked again.</summary>
    [Fact]
    public void A_run_that_does_not_wait_is_never_written_under()
    {
        int? lockedAfter = null;
        var d = JudgedByARun(() => lockedAfter, waits: false);
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "560000")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);

        lockedAfter = FakeDriver.Sets;
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.Equal(lockedAfter + 1, FakeDriver.Sets);                     // the write already let through, and nothing after it
        Assert.Equal(450_000u, _requests[0xfe]);
        Assert.NotNull(TrialNow());
    }

    /// <summary>The service's judge: free with no run; the run's reason otherwise, but for the run's own writes (its lease)
    /// and the fan guard's.</summary>
    [Fact]
    public void The_judge_lets_through_a_run_s_own_writes_and_the_fan_guard_s()
    {
        JudgeLock.Info? run = null;
        var judge = BrokerDispatcher.Judge(() => run);
        Assert.Null(judge());
        run = new JudgeLock.Info(4242, "lab-1", "clocktail-lab lab-1", Waits: true);
        Assert.Equal("pid 4242 'clocktail-lab lab-1'", judge());              // outside any operation a waiting run blocks too
        try
        {
            BrokerDispatcher.CurrentLease.Value = "lab-1";
            Assert.Null(judge());
            BrokerDispatcher.CurrentLease.Value = "lab-2";
            Assert.NotNull(judge());
            BrokerDispatcher.CurrentLease.Value = BrokerDispatcher.SafetyLease;
            Assert.Null(judge());
        }
        finally { BrokerDispatcher.CurrentLease.Value = null; }
    }

    /// <summary>An operation the judge refused at its first write writes nothing after it, even if the lock has gone by the
    /// next: before, the daily wall was refused, its group dropped, and the wall the broker held went back to stock, a
    /// "partial" daily profile that let a trial off the record.</summary>
    [Fact]
    public void An_operation_refused_at_its_first_write_writes_nothing_after_it()
    {
        bool armed = false;
        int asks = 0;
        var d = Power(judge: () => armed && asks++ == 0 ? "stress_test (pid 4242)" : null);   // busy at the first look only
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "560000")).Ok);
        int sets = FakeDriver.Sets;

        armed = true;
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "the broker started"));
        Assert.Equal(BrokerCode.JudgeBusy, r.Code);
        Assert.False(r.Result is { } res && res.TryGetProperty("partial", out var p) && p.GetBoolean());
        Assert.Equal(sets, FakeDriver.Sets);
        Assert.Equal(560_000u, _requests[0xfe]);
    }

    /// <summary>A recovery a client asks for takes the card off the trial, which then leaves the record: a 0.3.6 client's stock
    /// is "off" (writing nothing) then a recovery, and the CLI's --recover is one. The service's own leave the trial alone:
    /// after a driver reset (first try or retry) the "crashed" step records its crash, and at start the leftovers' recovery
    /// may come before the hang's reset is in the System log, so a trial waits for the idle-gated daily profile.</summary>
    [Fact]
    public void A_client_s_recovery_takes_the_trial_off_the_record_and_the_service_s_own_do_not()
    {
        var d = TrialAt450(() => null);
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off")).Ok);
        Assert.NotNull(TrialNow());
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Null(TrialNow());
        Assert.Equal([Experiment.Survived], Outcomes());

        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 575 → 450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);           // the leftovers at start
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverResetAgain, Value: "nvlddmkm 153")).Ok);
        Assert.NotNull(TrialNow());
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153")).Ok);
        Assert.Null(TrialNow());
        Assert.Equal([Experiment.Survived, Experiment.Crash], Outcomes());
    }

    /// <summary>A new session does not begin over a trial that did not all go back for a reason waiting does not cure (as
    /// in 0.3.6): the record lets it go, and the log says the card may still hold part of it.</summary>
    [Fact]
    public void A_new_session_does_not_begin_over_a_put_back_that_failed()
    {
        var d = TrialAt450(() => null);
        _journal.Claim(new OwnedItem("nosuchkind", "x", "1", _host.Now));     // held, and no write path can give it back
        var r = d.Dispatch(Exp("begin", """{"game":"B"}"""));
        Assert.False(r.Ok);
        Assert.Contains("the new session did not begin", r.Error);
        Assert.Null(TrialNow());
        Assert.Equal("A", Experiment.Load(Experiment.PathBeside(_journal.Path))!.Game);
        Assert.Contains("Trial off the record, not all off the card", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>621.01: a put-back that leaves only MSVDD's group at stock is back through end_session (as through mark_crash
    /// and a new session) just as through back_to_daily: the trial leaves the record and nothing says the card may still
    /// hold it. Before, those three looked for "partial" in the wrong place and logged that it might.</summary>
    [Fact]
    public void A_partial_put_back_on_621_01_is_back_through_end_session_too()
    {
        var d = CurveAndDroppedRails();
        const string daily = "0*60,30000*67";
        new DailyProfile([
            new ProfileWrite(RailWrites.Kind, RailWrites.Key(Rail.Msvdd), "rel=0,alt=0,ov=0,vmin=0"),
            new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, daily)], _host.Now).Save(DailyProfile.PathBeside(_journal.Path));
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*60,45000*67", change = "+15 MHz" }))).Ok);

        var r = d.Dispatch(Exp("end"));
        Assert.False(r.Ok);                                                  // the uncore stays at stock, and the answer says so
        Assert.False(File.Exists(Experiment.PathBeside(_journal.Path)));    // the session ended: the card is off the trial
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());
        Assert.Equal([Experiment.Survived], Outcomes());
        Assert.DoesNotContain("not all off the card", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    private static BrokerRequest Crashed(DateTimeOffset stopped, uint uv = 0) => new(BrokerRequest.OpProfile, Kind: "crashed",
        Value: "nvlddmkm 153", Uv: uv, HungAt: stopped, AnsweredAt: stopped.AddMilliseconds(-250));

    /// <summary>The responder's answer to a reset whose GPU stopped answering at <paramref name="stopped"/>: what is held
    /// given back, then the crash recorded (DriverResetResponder.RespondFor).</summary>
    private void Reset(BrokerDispatcher d, DateTimeOffset stopped, uint uv = 0)
    {
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        Assert.True(d.Dispatch(Crashed(stopped, uv)).Ok);
    }

    /// <summary>
    /// A driver reset's crash is recorded seconds after the hang (the reset logged, the driver settled, the cells given
    /// back), and it goes to the trial that was on the card when the GPU stopped answering, whatever took that trial off the
    /// record in between: back_to_daily (a lab job's end), end_session, stock, a client's recovery, the next trial, a
    /// hand-back. Once: after a mark_crash in between nothing more is recorded. Until 0.3.8 the trial in force at the record
    /// took it: none here, so the crash was the daily profile's (after a "try", the next trial's).
    /// </summary>
    [Theory]
    [InlineData("back_to_daily")]
    [InlineData("end_session")]
    [InlineData("stock")]
    [InlineData("recover")]
    [InlineData("try")]
    [InlineData("release")]
    [InlineData("mark_crash")]
    public void A_reset_s_crash_goes_to_the_trial_on_the_card_when_the_GPU_stopped_answering(string between)
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        var written = _host.Now;
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        var stopped = written.AddMinutes(3);
        _host.Now = stopped.AddSeconds(4);                         // the reset is logged; the service has yet to record it
        var r = between switch
        {
            "back_to_daily" => d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "tune_memory finished")),
            "end_session" => d.FromClient(Exp("end")),
            "stock" => d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock")),
            "recover" => d.FromClient(new BrokerRequest(BrokerRequest.OpRecover)),
            "try" => d.FromClient(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 500 W", power_mw = 500_000u, keep_curve = true }))),
            "release" => d.FromClient(Exp("release", "the agent disconnected")),
            _ => d.FromClient(Exp("crash", "the screen went black")),
        };
        Assert.True(r.Ok, r.Error);
        _host.Now = stopped.AddSeconds(8);
        Reset(d, stopped.ToOffset(TimeSpan.FromHours(-4)), 990_000);   // the flight recorder's clock carries the local offset

        var crash = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20), p => p.Outcome == Experiment.Crash);
        Assert.Equal(("A", "450 W, NVVDD 350 A"), (crash.Game, crash.Change));
        // Dated when the GPU stopped answering: its run (At less Minutes to At) is the trial's, not the frozen readings after it.
        if (between != "mark_crash") Assert.Equal((stopped, 3.0, 990_000u), (crash.At, crash.Minutes, crash.CrashUv!.Value));
        Assert.Null(TrialNow());                                   // a trial written after the hang is not blamed, and goes too
        var log = File.ReadAllText(TuningLog.PathBeside(_journal.Path));
        Assert.Contains("Crash · during trial · A", log);
        Assert.DoesNotContain("Crash · daily profile", log);
        Assert.Contains(FormattableString.Invariant($"The GPU stopped answering at {stopped.ToLocalTime():HH:mm:ss.f}"), log);   // in local time, as the log's headings
    }

    /// <summary>A trial taken off the card before the GPU stopped answering is not blamed, nor one written after: the crash
    /// is the daily profile's, and the later trial, which the reset's give-back took off the card, leaves the record with
    /// no data point.</summary>
    [Fact]
    public void A_trial_off_the_card_before_the_hang_or_written_after_it_is_not_blamed()
    {
        var d = TrialAt450(() => null);
        var written = _host.Now;
        _host.Now = written.AddMinutes(1);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily")).Ok);
        var stopped = written.AddMinutes(3);
        _host.Now = stopped.AddSeconds(4);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 500 W", power_mw = 500_000u, keep_curve = true }))).Ok);
        _host.Now = stopped.AddSeconds(8);
        Reset(d, stopped);
        Assert.Equal([Experiment.Survived], Outcomes());
        Assert.Null(TrialNow());
        var log = File.ReadAllText(TuningLog.PathBeside(_journal.Path));
        Assert.Contains("Crash · daily profile", log);
        Assert.Contains("Not blamed: the trial in force (wall 500 W)", log);
    }

    /// <summary>The trial in force when the GPU stopped answering takes the crash, as having run until then.</summary>
    [Fact]
    public void The_trial_in_force_when_the_GPU_stopped_answering_takes_the_crash()
    {
        var d = TrialAt450(() => null);
        var stopped = _host.Now.AddMinutes(2);
        _host.Now = stopped.AddSeconds(30);
        Reset(d, stopped, 975_000);
        var crash = Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20));
        Assert.Equal((Experiment.Crash, 2.0, 975_000u), (crash.Outcome, crash.Minutes, crash.CrashUv!.Value));
        Assert.Null(TrialNow());
    }

    /// <summary>A trial written after the reset's give-back, before the crash is recorded (the two are operations of their
    /// own, and a client's waits between them), is on the card: it is not blamed and stays in force, the daily wall that
    /// "apply-power" puts back after a reset does not go over it, and it runs under nothing of the trial before it, which
    /// the give-back took off the card (not its NVVDD limit). The crash is the trial it replaced.</summary>
    [Fact]
    public void A_trial_written_after_the_give_back_stays_in_force()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "560000")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W, NVVDD 350 A", power_mw = 450_000u, nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        var stopped = _host.Now.AddMinutes(2);
        _host.Now = stopped.AddSeconds(30);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        _host.Now = stopped.AddSeconds(31);
        Assert.True(d.FromClient(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 500 W", power_mw = 500_000u, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(Crashed(stopped)).Ok);
        Assert.Equal(("wall 500 W", (uint?)null), (TrialNow()!.Change, TrialNow()!.NvvddMa));
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power")).Ok);
        Assert.Equal(500_000u, _requests[0xfe]);
        Assert.Equal("450 W, NVVDD 350 A", Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20), p => p.Outcome == Experiment.Crash).Change);
        Assert.Contains("after the reset's give-back, and is on the card: it stays in force", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>A trial written after the GPU stopped answering and before the give-back is off the card once the give-back
    /// has run, whatever a client wrote after that (a write of its own, outside any trial): it leaves the record.</summary>
    [Fact]
    public void A_trial_the_give_back_took_off_leaves_the_record_whatever_was_written_after()
    {
        var d = TrialAt450(() => null);
        var stopped = _host.Now.AddMinutes(2);
        _host.Now = stopped.AddSeconds(4);
        Assert.True(d.FromClient(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 500 W", power_mw = 500_000u, keep_curve = true }))).Ok);
        _host.Now = stopped.AddSeconds(30);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        _host.Now = stopped.AddSeconds(31);
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 540_000)).Ok);
        Assert.True(d.Dispatch(Crashed(stopped)).Ok);
        Assert.Null(TrialNow());
        Assert.Equal(540_000u, _requests[0xfe]);
        Assert.Equal("wall 575 → 450 W", Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20), p => p.Outcome == Experiment.Crash).Change);
    }

    /// <summary>A trial kept after the reset's give-back that sets no wall of its own runs under the daily wall, as its record
    /// says: "apply-power" writes that alone and touches nothing else of the trial (its NVVDD limit stays). Skipping it left
    /// the card at the driver's 575 W.</summary>
    [Fact]
    public void A_kept_trial_without_a_wall_of_its_own_gets_the_daily_one_and_keeps_the_rest()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "560000")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        var stopped = _host.Now.AddMinutes(2);
        _host.Now = stopped.AddSeconds(30);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);                                  // the driver's wall, after the give-back
        _host.Now = stopped.AddSeconds(31);
        Assert.True(d.FromClient(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "NVVDD 350 A", nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(Crashed(stopped)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(("NVVDD 350 A", (uint?)null), (TrialNow()!.Change, TrialNow()!.PowerMw));
    }

    /// <summary>A daily profile kept on another VBIOS does not go on by itself (S14), under a kept trial either: after the
    /// reset the card stays at the driver's wall, and the trial's own NVVDD limit stays.</summary>
    [Fact]
    public void A_profile_kept_on_another_VBIOS_does_not_go_on_under_a_kept_trial()
    {
        var d = Power(vbios: "98.02.30.00.01");
        CurrentLimitsFixture.Install();
        new DailyProfile([new ProfileWrite(PowerCap.Kind, PowerCap.Key(PowerRequestKind.Ordinary), "560000")], _host.Now)
            .On("98.02.2E.00.01", null).Save(DailyProfile.PathBeside(_journal.Path));
        Assert.True(d.Dispatch(Exp("begin", """{"game":"A"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        var stopped = _host.Now.AddMinutes(2);
        _host.Now = stopped.AddSeconds(30);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        _host.Now = stopped.AddSeconds(31);
        Assert.True(d.FromClient(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "NVVDD 350 A", nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(Crashed(stopped)).Ok);
        d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power"));
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal("NVVDD 350 A", TrialNow()!.Change);
    }

    /// <summary>Whether the give-back took a trial off the card is which trial was in force when it ran, not a comparison of
    /// clock times: a clock set back after the give-back does not make a trial written after it look given back.</summary>
    [Fact]
    public void A_clock_set_back_does_not_make_a_later_trial_look_given_back()
    {
        var d = TrialAt450(() => null);
        var stopped = _host.Now.AddMinutes(2);
        _host.Now = stopped.AddSeconds(30);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153")).Ok);
        _host.Now = stopped.AddSeconds(20);                                         // time sync stepped the clock back 10 s
        Assert.True(d.FromClient(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 500 W", power_mw = 500_000u, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(Crashed(stopped)).Ok);
        Assert.Equal("wall 500 W", TrialNow()!.Change);
        Assert.Equal(500_000u, _requests[0xfe]);
    }

    /// <summary>A hang that begins while a trial's writes are still landing is that trial's: it is on the card from its
    /// first write. Stamped once its writes had all landed, it was "written after the GPU stopped answering", and the trial
    /// before it took the crash.</summary>
    [Fact]
    public void A_hang_while_a_trial_s_writes_land_is_that_trial_s()
    {
        var d = TrialAt450(() => null);
        var apply = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, req) => { _host.Now += TimeSpan.FromSeconds(1); return apply(state, req); };   // each write takes a second
        _host.Now = _host.Now.AddMinutes(2);
        var began = _host.Now;
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 500 W", power_mw = 500_000u, keep_curve = true }))).Ok);
        Assert.Equal(began, TrialNow()!.At);
        _host.Now = began.AddSeconds(30);
        Reset(d, began.AddSeconds(0.5));
        Assert.Equal([Experiment.Survived, Experiment.Crash], Outcomes());
        Assert.Equal("wall 500 W", Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 20)[^1].Change);
    }

    /// <summary>S-4: no trial is written while the GPU looks hung; the trial before stays in force.</summary>
    [Fact]
    public void No_trial_is_written_while_the_GPU_looks_hung()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"x"}""")).Ok);
        d.HungReason = _ => "its readings have been frozen at 3180 MHz / 1000 mV since 17:59:21";
        int sets = FakeDriver.Sets;
        var r = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W", power_mw = 450_000u, keep_curve = true })));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.GpuHung, r.Code);
        Assert.Equal(sets, FakeDriver.Sets);
        d.HungReason = _ => null;
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
    }

    /// <summary>"alive" records when an agent was last heard, for the trial lease, and writes nothing.</summary>
    [Fact]
    public void Alive_records_the_agent_and_writes_nothing()
    {
        var d = Power();
        int sets = FakeDriver.Sets;
        Assert.True(d.Dispatch(Exp("alive")).Ok);
        Assert.Equal(_host.Now, d.LastAlive);
        Assert.Equal(sets, FakeDriver.Sets);
        Assert.False(File.Exists(Experiment.PathBeside(_journal.Path)));
    }

    /// <summary>LastAlive keeps the instant whatever offset the clock reports it in (it is stored as UTC ticks).</summary>
    [Fact]
    public void Alive_keeps_the_instant_across_offsets()
    {
        var d = Power();
        _host.Now = new DateTimeOffset(2026, 10, 23, 21, 30, 15, TimeSpan.FromHours(8)).AddTicks(1234);
        Assert.True(d.Dispatch(Exp("alive")).Ok);
        Assert.Equal(_host.Now.UtcTicks, d.LastAlive.UtcTicks);
    }

    /// <summary>After Close a write answers WindowBusy at once, not after WindowWait, and touches nothing; reads still answer.</summary>
    [Fact]
    public void A_write_after_close_is_refused_at_once()
    {
        var d = Power();
        Assert.True(d.Close(TimeSpan.Zero));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.WindowBusy, r.Code);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPing)).Ok);
    }

    /// <summary>An unattended session stops writing trials once it has spent its crash budget; a session the person is at
    /// has none, and a new session starts a new budget.</summary>
    [Fact]
    public void An_unattended_session_stops_trials_after_its_crash_budget()
    {
        var d = Power();
        string Wall(uint w) => JsonSerializer.Serialize(new { curve = "0*127", change = $"{w} W", power_mw = w * 1000, keep_curve = true });
        Assert.True(d.Dispatch(Exp("begin", """{"game":"overnight","unattended":true,"max_crashes":1}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", Wall(500))).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153")).Ok);
        var refused = d.Dispatch(Exp("try", Wall(480)));
        Assert.False(refused.Ok);
        Assert.Equal(BrokerCode.CrashBudget, refused.Code);

        Assert.True(d.Dispatch(Exp("begin", """{"game":"attended"}""")).Ok);   // no budget when the person is there
        Assert.True(d.Dispatch(Exp("try", Wall(480))).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "nvlddmkm 153")).Ok);
        Assert.True(d.Dispatch(Exp("try", Wall(460))).Ok);
    }

    /// <summary>preview: what keep would save, against the daily profile and the card, with what is held and not kept;
    /// nothing is written or saved.</summary>
    [Fact]
    public void A_preview_lists_what_keep_would_save_and_writes_nothing()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"x"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "NVVDD 350 A", nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        int sets = FakeDriver.Sets;

        var p = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preview"));
        Assert.True(p.Ok, p.Error);
        var change = Assert.Single(p.Result!.Value.GetProperty("changes").EnumerateArray());
        Assert.Equal(("power", "560000", "560000"), (change.GetProperty("kind").GetString(), change.GetProperty("becomes").GetString(), change.GetProperty("card_now").GetString()));
        Assert.Contains("trial-only", p.Result!.Value.GetProperty("not_kept")[0].GetString());
        Assert.Equal(sets, FakeDriver.Sets);
        Assert.False(File.Exists(DailyProfile.PathBeside(_journal.Path)));
        Assert.False(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preview", Value: "founders")).Ok);   // another model
    }

    /// <summary>The fan guard's "fans-back" hands back a fixed fan duty only: a lowered current limit lowers power, so it
    /// stays, and with no fan duty held nothing is written or recorded.</summary>
    [Fact]
    public void Fans_back_leaves_a_current_limit_alone()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"sweep"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "NVVDD 350 A", nvvdd_ma = 350_000u, keep_curve = true }))).Ok);
        var r = d.Dispatch(Exp("fans-back", "the fan guard: 87 °C"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, r.Result!.Value.GetProperty("released").GetArrayLength());
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));
        Assert.Empty(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10));
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

    /// <summary>set_power_cap's trial creates the F8 request only when the write says create; it is trial-only, so
    /// back_to_daily puts it back to the board maximum (it cannot be deleted) and keep never takes it.</summary>
    [Fact]
    public void A_trial_f8_request_is_created_on_purpose_and_goes_back_to_the_board_maximum()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);
        string Try(bool create) => JsonSerializer.Serialize(new { curve = "0*127", change = "F8 absent → 500 W", keep_curve = true,
            writes = new[] { new { kind = "power", key = "f8", value = "500000", create } } });
        Assert.False(d.Dispatch(Exp("try", Try(create: false))).Ok);
        Assert.Equal(0xFFFFFFFFu, _requests.GetValueOrDefault(0xf8u, 0xFFFFFFFF));
        var t = d.Dispatch(Exp("try", Try(create: true)));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(500_000u, _requests[0xf8]);

        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.False(kept.Ok);   // nothing but a trial-only cell is held: nothing to keep
        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(600_000u, _requests[0xf8]);
        Assert.Empty(_journal.Inspect().Items);
    }

    /// <summary>The 575 W regime (2026-10-21): the FE request read 600 W while the board power policy held 575, and the
    /// service, seeing the request already at the daily wall, wrote nothing. Putting the profile on now checks the policy
    /// and writes the request again, a step away and back; with the two agreeing it writes nothing more.</summary>
    [Fact]
    public void A_daily_wall_the_firmware_does_not_hold_is_written_again()
    {
        var d = Power();
        CurrentLimitsFixture.Install();
        uint policyWord = CurrentLimitsFixture.Word(Slot.PowerPolicyGetControl);
        int at = PolicyTableLayout.ControlLimit(8);
        var applier = FakeDriver.RequestApplier!;
        int feWrites = 0;
        FakeDriver.RequestApplier = (state, req) =>
        {
            var next = applier(state, req);
            if (BitConverter.ToUInt32(req) == Slots.Info(Slot.ClientPwrPoliciesSetControl).Shape!.Value.Word)
            {
                feWrites++;   // the firmware follows a request write
                W(FakeDriver.State[policyWord], at, _requests[0xfe]);
            }
            return next;
        };
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 600_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        W(FakeDriver.State[policyWord], at, 575_000);   // the firmware back on the VBIOS default; the request still reads 600
        feWrites = 0;

        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "the broker started"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, feWrites);
        Assert.Equal(600_000u, _requests[0xfe]);
        Assert.Equal(600_000u, U(FakeDriver.State[policyWord], at));
        Assert.Contains("the card enforced 575 W under a 600 W request", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));

        feWrites = 0;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "again")).Ok);
        Assert.Equal(0, feWrites);   // the two agree: nothing more is written
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

        // The wall lands first, then the current limit is refused (past the driver's 5001 A).
        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "450 W and 5001.001 A", power_mw = 450_000u, nvvdd_ma = 5_001_001u, keep_curve = true })));
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

    /// <summary>A trial may carry any other registry write in "writes" (here NVVDD's REL offset, under the fixture's 1.15 V ceiling). It goes through the
    /// path's rules, is on the data point, and goes back with the daily profile; the wall and the curve keep their own
    /// fields.</summary>
    [Fact]
    public void A_trial_carries_any_registry_write_and_it_goes_back_with_the_daily_profile()
    {
        var d = Rails();
        uint controlWord = Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word;
        int NvvddRel() => RailWrites.OffsetsIn(FakeDriver.State[controlWord], Rail.Nvvdd)!.Value.RelUv;
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Steel Nomad"}""")).Ok);

        string Try(object writes) => JsonSerializer.Serialize(new { curve = "0*127", change = "NVVDD REL -100 mV", keep_curve = true, writes });
        var t = d.Dispatch(Exp("try", Try(new[] { new { kind = "rails", key = "nvvdd", value = "rel=-100000" } })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(-100_000, NvvddRel());
        Assert.True(d.Dispatch(Exp("stable", "a run at -100")).Ok);
        var w = Assert.Single(Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10)).Writes!);
        Assert.Equal(("rails", "nvvdd", "rel=-100000"), (w.Kind, w.Key, w.Value));
        Assert.Contains("rails/nvvdd rel=-100000", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));

        Assert.False(d.Dispatch(Exp("try", Try(new[] { new { kind = "power", key = "fe", value = "450000" } }))).Ok);   // power_mw
        Assert.False(d.Dispatch(Exp("try", Try(new[] { new { kind = "bogus", key = "x", value = "1" } }))).Ok);
        Assert.Equal(-100_000, NvvddRel());   // a refused trial wrote nothing

        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(0, NvvddRel());
        Assert.Empty(_journal.Inspect().Items);

        // A boost (set_voltage_boost) through the same list: the legacy byte at the rails' control +8, kept from the card.
        Assert.True(d.Dispatch(Exp("try", Try(new[] { new { kind = "boost", key = "legacy", value = "10" } }))).Ok);
        Assert.Equal(10, FakeDriver.State[controlWord][8]);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily")).Ok);
        Assert.Equal(0, FakeDriver.State[controlWord][8]);

        // adjust_nvvdd's offsets are kept from the card, as MSVDD's are.
        Assert.True(d.Dispatch(Exp("try", Try(new[] { new { kind = "rails", key = "nvvdd", value = "rel=-100000,alt=0,ov=0,vmin=0" } }))).Ok);
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.True(kept.Ok, kept.Error);
        Assert.Contains(kept.Result!.Value.GetProperty("profile").GetProperty("Writes").EnumerateArray(),
            x => x.GetProperty("Kind").GetString() == "rails" && x.GetProperty("Key").GetString() == "nvvdd");
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

        // A curve this broker does not hold (another tool's, or one adopted) is set aside too, since 0.4.0: the trial writes
        // its curve right after the memory offset, so nothing of it is lost. Until then the memory write was refused.
        _journal.Release(VfWrites.Kind, VfWrites.CoreKey);
        t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = daily, change = "memory 0 → +179 MHz", memory_khz = 179_000, keep_curve = true })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(179_000, MemoryKhz());
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());
    }

    /// <summary>The core P0 offset as a trial write (set_core_offset): the held curve is set aside for it and written back,
    /// keep takes it from the card even with a profile kept, and back_to_daily puts the kept one back.</summary>
    [Fact]
    public void A_core_offset_trial_writes_the_curve_back_and_keep_takes_it_from_the_card()
    {
        new FakeVf().Install();
        int core = Clocktail.Domain.Clocks.Pstates20Layout.Clock(0, 0);
        var vfApplier = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) != FakeVf.PstatesWord) return vfApplier(state, req);
            var next = (byte[])state.Clone();
            req.AsSpan(0x28, 4).CopyTo(next.AsSpan(core + 0xc));
            var control = FakeDriver.State[FakeVf.ControlWord];
            for (int i = 0; i < 127; i++) control.AsSpan(VfLayout.OffsetField(i), 4).Clear();   // a P0 write resets the points
            return next;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.GetPstates20],
            [Slot.ClockClientClkVfPointsSetControl, Slot.SetPstates20]), WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl));
        int CoreKhz() => BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.State[FakeVf.PstatesWord].AsSpan(core + 0xc));
        var d = new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        const string daily = "0*60,30000*67";
        Assert.True(d.Dispatch(Curve(daily)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);

        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = daily, change = "core 0 → +15 MHz", keep_curve = true,
            writes = new[] { new { kind = Clocktail.Domain.Clocks.ClockOffsets.Kind, key = "core", value = "15000" } } })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(15_000, CoreKhz());
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());   // written back after the P0 write reset it

        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.True(kept.Ok, kept.Error);
        Assert.Contains(kept.Result!.Value.GetProperty("profile").GetProperty("Writes").EnumerateArray(),
            w => w.GetProperty("Kind").GetString() == Clocktail.Domain.Clocks.ClockOffsets.Kind && w.GetProperty("Key").GetString() == "core" && w.GetProperty("Value").GetString() == "15000");
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(0, CoreKhz());
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.Equal(15_000, CoreKhz());
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());
    }

    /// <summary>SYS's offset and voltage demand in one trial (adjust_clock_domain): the demand goes on before the clock it
    /// carries whatever order the writes came in, and both go back with the daily profile.</summary>
    [Fact]
    public void A_sys_trial_writes_its_demand_before_its_offset_and_both_go_back()
    {
        ClockDomainsFixture.Seed();
        var info = RailsFixture.Info();
        foreach (int bit in new[] { 0, 1 }) W(info, RailsLayout.InfoBase + bit * RailsLayout.InfoEntry, 3);   // the 5090's rails are kind 3
        foreach (var b in new[] { info, RailsFixture.Control(), RailsFixture.Status(), RailsFixture.Devices() }) FakeDriver.State[U(b, 0)] = b;
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl, Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.ClockClkDomainsSetControl]));
        var d = new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        int sys = ClockDomainsParser.Find(ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology()), (uint)Clocktail.Domain.Clocks.ClockDomainId.Sys).Index;
        int Demand() => BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.Current(Slot.ClockClkDomainsGetControl).AsSpan(ClockDomainsLayout.OffsetAt(sys, 0xf) + 8));
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);

        var t = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "SYS +150 MHz, +10 mV", keep_curve = true,
            writes = new[] { new { kind = "clockdomain", key = "sys", value = "150000" }, new { kind = "vdemand", key = "sys", value = "10000" } } })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(150_000, ClockDomainsFixture.OffsetKhz(sys));
        Assert.Equal(10_000, Demand());
        var order = t.Result!.Value.GetProperty("writes").EnumerateArray().Select(w => w.GetProperty("applied")[0].GetProperty("kind").GetString()).ToList();
        Assert.Equal(["vdemand", "clockdomain"], order);

        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal((0, 0), (ClockDomainsFixture.OffsetKhz(sys), Demand()));
        Assert.Empty(_journal.Inspect().Items);
    }

    /// <summary>
    /// The ST08 drill (2026-10-21): the curve's original was +15 MHz on every point (left by another tool before
    /// Clocktail's first write). Recovery gave that curve back first and then refused the memory offset (a P0 write over
    /// a curve not ours), and after a device restart the daily profile's memory offset was refused the same way, so the
    /// profile did not go back on. Now recovery gives the P0 offsets back first, the curve after them, and the profile
    /// sets a curve it does not hold aside when it writes its own right after.
    /// </summary>
    [Fact]
    public void A_curve_original_that_is_not_zero_strands_neither_recovery_nor_the_profile()
    {
        new FakeVf().Install();
        var table = FakeDriver.State[FakeVf.PstatesWord];
        int mem = Clocktail.Domain.Clocks.Pstates20Layout.Clock(0, 1);
        W(table, 0xc, 2);
        W(table, mem, (uint)Clocktail.Domain.Clocks.ClockDomain.Memory); W(table, mem + 8, 1);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(mem + 0x10), -1_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(mem + 0x14), 3_000_000);
        var vfApplier = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) != FakeVf.PstatesWord) return vfApplier(state, req);
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

        const string other = "15000*127", daily = "0*60,30000*67";
        Assert.True(d.Dispatch(Curve(other)).Ok);
        _journal.Release(VfWrites.Kind, VfWrites.CoreKey);                 // another tool's curve, on before Clocktail's first write
        Assert.True(d.Dispatch(Curve(daily)).Ok);                          // the journal's original for the curve: +15 everywhere
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = daily, change = "memory +358", memory_khz = 358_000, keep_curve = true }))).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);

        var rec = d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover));
        Assert.True(rec.Ok, rec.Error);
        Assert.Equal(0, MemoryKhz());
        Assert.Equal(VfOffsets.Decode(other), FakeVf.Offsets());           // every original back, the memory offset too
        Assert.Empty(_journal.Inspect().Items);

        var apply = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "the broker started"));
        Assert.True(apply.Ok, apply.Error);
        Assert.Equal(358_000, MemoryKhz());
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

    /// <summary>The V/F curve and both rails, with a rail setter that answers OK and changes nothing: how driver 621.01
    /// answered every rail write, NVVDD and MSVDD, up and down (2026-10-22).</summary>
    private BrokerDispatcher CurveAndDroppedRails()
    {
        new FakeVf().Install();
        foreach (var bytes in new[] { RailsFixture.Info(), RailsFixture.Control(), RailsFixture.Status(), RailsFixture.Devices() })
            FakeDriver.State[BinaryPrimitives.ReadUInt32LittleEndian(bytes)] = bytes;
        uint railsControl = Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word;
        var vf = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, req) => BitConverter.ToUInt32(req) == railsControl ? state : vf(state, req);
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.GetPstates20,
             Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.ClockClientClkVfPointsSetControl, Slot.VoltVoltRailsSetControl]), WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl));
        return new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
    }

    /// <summary>
    /// The update to 621.01 (2026-10-22): the daily profile stopped at MSVDD, which the driver dropped, after the memory
    /// write had set the curve aside, so the card ran a flat curve with neither the uncore nor the daily curve on. Now the
    /// uncore stays at stock, the rest goes on, and the answer says which group did not and why.
    /// </summary>
    [Fact]
    public void A_cell_the_driver_drops_leaves_its_group_at_stock_and_the_rest_of_the_profile_goes_on()
    {
        var d = CurveAndDroppedRails();
        const string daily = "0*60,30000*67";
        new DailyProfile([
            new ProfileWrite(RailWrites.Kind, RailWrites.Key(Rail.Msvdd), "rel=0,alt=0,ov=0,vmin=0"),
            new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, daily)], _host.Now).Save(DailyProfile.PathBeside(_journal.Path));

        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "the broker started"));
        Assert.False(r.Ok);
        Assert.Equal(VfOffsets.Decode(daily), FakeVf.Offsets());           // the curve went on
        Assert.Equal(RailsFixture.MsvddRelOffset, MsvddRel());              // MSVDD stayed at stock
        Assert.Equal(VfWrites.Kind, Assert.Single(_journal.Inspect().Items).Kind);

        var res = r.Result!.Value;
        Assert.True(res.GetProperty("partial").GetBoolean());
        var left = Assert.Single(res.GetProperty("not_applied").EnumerateArray());
        Assert.Equal("uncore", left.GetProperty("group").GetString());
        Assert.StartsWith("the daily profile is on, but the uncore (rails/msvdd) stays at stock: rails/msvdd did not apply: readback differs", r.Error);
        Assert.StartsWith("partly applied, ", DriverResetResponder.ProfileSummary(r));
        Assert.Contains("Failed: the daily profile is on, but the uncore", File.ReadAllText(TuningLog.PathBeside(_journal.Path)));
    }

    /// <summary>NVVDD's offsets go with the curve (a REL cut is part of it): when they do not go on, neither does the curve,
    /// and a curve the broker holds goes back to stock rather than run without the cut it was tuned under.</summary>
    [Fact]
    public void A_dropped_nvvdd_offset_takes_the_curve_along_and_a_held_curve_goes_back_to_stock()
    {
        var d = CurveAndDroppedRails();
        Assert.True(d.Dispatch(Curve("0*60,45000*67")).Ok);
        new DailyProfile([
            new ProfileWrite(RailWrites.Kind, RailWrites.Key(Rail.Nvvdd), "rel=-12500,alt=0,ov=0,vmin=0"),
            new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, "0*60,30000*67")], _host.Now).Save(DailyProfile.PathBeside(_journal.Path));

        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.False(r.Ok);
        Assert.All(FakeVf.Offsets(), o => Assert.Equal(0, o));
        Assert.Empty(_journal.Inspect().Items);
        var left = Assert.Single(r.Result!.Value.GetProperty("not_applied").EnumerateArray());
        Assert.Equal("core", left.GetProperty("group").GetString());
        Assert.Equal(["rails/nvvdd", "vf/core"], left.GetProperty("cells").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void The_profile_groups_follow_the_method()
    {
        Assert.Equal("uncore", DailyProfile.Group(RailWrites.Kind, RailWrites.Key(Rail.Msvdd)));
        Assert.Equal("core", DailyProfile.Group(RailWrites.Kind, RailWrites.Key(Rail.Nvvdd)));
        Assert.Equal("memory", DailyProfile.Group(ClockOffsets.Kind, ClockOffsets.Key(ClockDomain.Memory)));
        Assert.Equal("core", DailyProfile.Group(ClockOffsets.Kind, ClockOffsets.Key(ClockDomain.Core)));
        Assert.Equal("uncore", DailyProfile.Group(ClockVdemand.Kind, ClockVdemand.Key(ClockDomainId.Xbar)));
        Assert.Equal("core", DailyProfile.Group(ClockVdemand.Kind, ClockVdemand.Key(ClockDomainId.Graphics)));
        Assert.Equal("uncore", DailyProfile.Group(ClockDomainOffsets.Kind, ClockDomainOffsets.Key(ClockDomainId.Xbar)));
        Assert.Equal("uncore", DailyProfile.Group(ClockRatio.Kind, ClockRatio.Key));
        Assert.Equal("power", DailyProfile.Group(PowerCap.Kind, PowerCap.Key(PowerRequestKind.Ordinary)));
        // The boost on its own: a driver that will not take it leaves the curve and the uncore on.
        Assert.Equal("boost", DailyProfile.Group(Clocktail.Domain.Boost.VoltBoost.Kind, "legacy"));
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

    /// <summary>One whole operation at a time: a profile change that needs no write ("off") waits while a "wall" is in its
    /// write, instead of saving the profile under it and having the wall's save, made from the profile it read before,
    /// undo it. Before, the suspension was lost and both answered success.</summary>
    [Fact]
    public void A_profile_change_waits_for_a_wall_in_its_write_instead_of_being_overwritten()
    {
        var d = Power();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "590000")).Ok);
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = FakeDriver.RequestApplier!;
        int calls = 0;
        FakeDriver.RequestApplier = (state, req) =>
        {
            var s = inner(state, req);
            if (Interlocked.Increment(ref calls) == 1) { inWrite.Set(); release.Wait(5000); }
            return s;
        };
        BrokerResponse? wall = null, off = null;
        var a = new Thread(() => wall = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "600000")));
        a.Start();
        Assert.True(inWrite.Wait(5000));
        var b = new Thread(() => off = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off")));
        b.Start();
        Assert.False(b.Join(300));          // "off" waits for the wall's whole operation, not only its write
        release.Set();
        Assert.True(a.Join(10_000) && b.Join(10_000));
        Assert.True(wall!.Ok, wall.Error);
        Assert.True(off!.Ok, off.Error);
        var kept = DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;
        Assert.True(kept.Suspended);        // "off" came last and stuck
        Assert.Equal("600000", Assert.Single(kept.Writes, w => w.Kind == PowerCap.Kind).Value);
    }

    /// <summary>"quiet", which a measurement asks once it holds judge.lock, is answered only once the operation in flight has
    /// finished: a write that looked at the lock before it was taken can still be landing, and until 0.3.7 a run could
    /// start in the middle of it. The answer is the experiment, for the trial the run measures.</summary>
    [Fact]
    public void Quiet_is_answered_once_the_write_in_flight_has_landed()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"drill"}""")).Ok);
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = FakeDriver.RequestApplier!;
        int calls = 0;
        FakeDriver.RequestApplier = (state, req) =>
        {
            var s = inner(state, req);
            if (Interlocked.Increment(ref calls) == 1) { inWrite.Set(); release.Wait(5000); }
            return s;
        };
        BrokerResponse? trial = null, quiet = null;
        var a = new Thread(() => trial = d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 575 → 450 W", power_mw = 450_000u, keep_curve = true }))));
        a.Start();
        Assert.True(inWrite.Wait(5000));
        var b = new Thread(() => quiet = d.Dispatch(Exp("quiet")));
        b.Start();
        Assert.False(b.Join(300));          // the trial's write is still landing
        release.Set();
        Assert.True(a.Join(10_000) && b.Join(10_000));
        Assert.True(trial!.Ok, trial.Error);
        Assert.True(quiet!.Ok, quiet.Error);
        Assert.Equal("wall 575 → 450 W", quiet.Result!.Value.GetProperty("experiment").GetProperty("Trial").GetProperty("Change").GetString());
    }

    /// <summary>A measurement holds judge.lock: every other write waits, and the holder's own (its lease on the request,
    /// the nested write of a profile op too) goes through.</summary>
    [Fact]
    public void A_measurement_lease_lets_only_its_holders_writes_through()
    {
        var d = Power(judge: () => BrokerDispatcher.CurrentLease.Value == "run-1" ? null : "pid 4321 'clocktail-lab'");
        static BrokerRequest Wall(uint mw, string? lease = null) =>
            new(BrokerRequest.OpProfile, Kind: "wall", Value: mw.ToString(System.Globalization.CultureInfo.InvariantCulture), Lease: lease);
        var other = d.Dispatch(Wall(590_000));
        Assert.False(other.Ok);
        Assert.Equal(BrokerCode.JudgeBusy, other.Code);
        Assert.Equal(575_000u, _requests[0xfe]);
        var own = d.Dispatch(Wall(590_000, "run-1"));
        Assert.True(own.Ok, own.Error);
        Assert.Equal(590_000u, _requests[0xfe]);
        Assert.Null(BrokerDispatcher.CurrentLease.Value);   // given back with the request
        Assert.Equal(BrokerCode.JudgeBusy, d.Dispatch(Wall(600_000, "run-2")).Code);
    }

    /// <summary>Saved is not switched: when the trial's wall does not come off, keep answers that, with the profile saved,
    /// instead of success.</summary>
    [Fact]
    public void Keep_says_when_the_card_did_not_finish_switching_to_the_profile()
    {
        var d = Power();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "600000")).Ok);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"Cyberpunk"}""")).Ok);
        Assert.True(d.Dispatch(Exp("try", JsonSerializer.Serialize(new { curve = "0*127", change = "wall 600 → 450 W", power_mw = 450_000u, keep_curve = true }))).Ok);
        Assert.Equal(450_000u, _requests[0xfe]);
        FakeDriver.RequestApplier = (state, _) => state;   // the board stops taking the wall
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.False(r.Ok);
        Assert.Contains("saved, but the card did not finish switching", r.Error);
        Assert.True(r.Result!.Value.GetProperty("saved").GetBoolean());
        Assert.False(r.Result!.Value.GetProperty("switched").GetBoolean());
        Assert.NotNull(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path)));
        Assert.Equal(450_000u, _requests[0xfe]);           // still the trial's: what the answer says is pending
    }

    [Fact]
    public void Policy_scope_reads_are_write_free_and_session_ids_do_not_depend_on_time()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"game"}""")).Ok);
        string Scope() => d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").GetString()!;
        string first = Scope();
        Assert.Equal(first, Scope());
        Assert.Equal(0, FakeDriver.Sets);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"game"}""")).Ok); // fake host time is unchanged
        Assert.NotEqual(first, Scope());
        Assert.Equal(0, FakeDriver.Sets);
        var stale = d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: BrokerRequest.ScopedTrial,
            Value: """{"curve":"0*127","power_mw":450000,"keep_curve":true}""", ExpectedTuningScope: first));
        Assert.False(stale.Ok);
        Assert.Equal("consent_required", stale.Code);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Theory]
    [InlineData("crashed")]
    [InlineData("after-reset")]
    [InlineData("recover")]
    [InlineData("crash")]
    [InlineData("end")]
    [InlineData("driver")]
    public void Policy_scope_expires_before_a_relaxed_trial_can_write(string change)
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"game"}""")).Ok);
        var first = d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").GetString();
        if (change == "driver") _gpu.Rebind(2);
        else if (change == "recover") d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover));
        else if (change is "crashed" or "after-reset") d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: change));
        else d.Dispatch(Exp(change));
        var stale = d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: BrokerRequest.ScopedTrial,
            Value: """{"curve":"0*127","power_mw":450000,"keep_curve":true}""", ExpectedTuningScope: first));
        Assert.Equal("consent_required", stale.Code);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Valid_scope_does_not_bypass_device_bounds_or_crash_budget()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"game","unattended":true,"max_crashes":1}""")).Ok);
        var scope = d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").GetString();
        var tooLow = d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: BrokerRequest.ScopedTrial,
            Value: """{"curve":"0*127","power_mw":250000,"keep_curve":true}""", ExpectedTuningScope: scope));
        Assert.False(tooLow.Ok); // fake 5090 minimum remains 400 W at the lower layer
        Assert.Equal(0, FakeDriver.Sets);
        Assert.True(d.Dispatch(Exp("try", """{"curve":"0*127","power_mw":450000,"keep_curve":true}""")).Ok);
        Assert.True(d.Dispatch(Exp("crash")).Ok);
        scope = d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").GetString();
        int sets = FakeDriver.Sets;
        var exhausted = d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: BrokerRequest.ScopedTrial,
            Value: """{"curve":"0*127","power_mw":450000,"keep_curve":true}""", ExpectedTuningScope: scope));
        Assert.Equal(BrokerCode.CrashBudget, exhausted.Code);
        Assert.Equal(sets, FakeDriver.Sets);
    }

    [Fact]
    public void Older_sessions_without_an_identity_and_closed_brokers_cannot_supply_a_scope()
    {
        var d = Power();
        new Experiment("old", null, _host.Now).Save(Experiment.PathBeside(_journal.Path));
        Assert.Equal(JsonValueKind.Null, d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").ValueKind);
        Assert.True(d.Dispatch(Exp("begin", """{"game":"new"}""")).Ok);
        Assert.NotEqual(JsonValueKind.Null, d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").ValueKind);
        Assert.True(d.Close(TimeSpan.FromSeconds(1)));
        Assert.Equal(JsonValueKind.Null, d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").ValueKind);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Scoped_trial_requires_a_scope_and_plain_trial_cannot_carry_one()
    {
        var d = Power();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"game"}""")).Ok);
        var scope = d.Dispatch(Exp("policy-scope")).Result!.Value.GetProperty("generation").GetString();
        string payload = """{"curve":"0*127","power_mw":450000,"keep_curve":true}""";
        Assert.Equal("consent_required", d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment,
            Kind: BrokerRequest.ScopedTrial, Value: payload)).Code);
        Assert.Equal("consent_required", d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment,
            Kind: "try", Value: payload, ExpectedTuningScope: scope)).Code);
        Assert.Equal(0, FakeDriver.Sets);
        var accepted = d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment,
            Kind: BrokerRequest.ScopedTrial, Value: payload, ExpectedTuningScope: scope));
        Assert.True(accepted.Ok, accepted.Error);
        Assert.Equal(450_000u, _requests[0xfe]);
    }
}
