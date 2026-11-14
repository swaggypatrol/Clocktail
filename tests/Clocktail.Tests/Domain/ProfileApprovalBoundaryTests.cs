using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class ProfileApprovalBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-approval-" + Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly MemoryProfileApprovals _approvals = new();
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = uint.MaxValue };
    private readonly OwnershipJournal _journal;
    private string ProfilePath => DailyProfile.PathBeside(_journal.Path);
    private sealed class Target(OwnershipJournal journal, ApplySentinel sentinel) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session { get; } = new(1, "fake");
        public OwnershipJournal Journal => journal;
        public ApplySentinel Sentinel => sentinel;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == StableId ? this : null;
    }
    public ProfileApprovalBoundaryTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
    }
    public void Dispose() => Directory.Delete(_dir, true);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private BrokerDispatcher Dispatcher(IProfileApprovals? approvals = null, Func<string?>? judge = null)
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(info, 4, 1);
        int e = PowerRequestsLayout.InfoEntry(0);
        W(info, e, 1); W(info, e + 8, 400_000); W(info, e + 12, 575_000); W(info, e + 16, 600_000);
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = info;
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        uint control = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != control) return null;
            var a = (byte[])q.Clone();
            int entry = PowerRequestsLayout.ControlEntry(0);
            W(a, entry, _requests.GetValueOrDefault(U(q, entry + 4), uint.MaxValue));
            return a;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            int entry = PowerRequestsLayout.ControlEntry(0);
            _requests[U(req, entry + 4)] = U(req, entry);
            return state;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo],
            [Slot.ClientPwrPoliciesSetControl, Slot.PowerPolicySetControl]));
        return new BrokerDispatcher(hw, new Target(_journal, new ApplySentinel(Path.Combine(_dir, "apply.lock"))), _host, true,
            judgeBusyReason: judge, profileApprovals: approvals ?? _approvals);
    }

    private void SeedApproved()
    {
        var profile = new DailyProfile([new("power", "fe", "560000")], _host.Now);
        profile.Save(ProfilePath);
        _approvals.Remember("gpu-A", ProfileApproval.Daily, profile);
    }

    private BrokerRequest Request(BrokerDispatcher d, string kind)
    {
        string? value = kind switch { "preset" or "forget" => "base", "save" => "new", "wall" => "550000", _ => null };
        string? fingerprint = null;
        if (kind is "keep" or "adopt" or "preset")
        {
            var preview = d.FromClient(new(BrokerRequest.OpProfile, Kind: kind == "adopt" ? "preview-adopt" : "preview",
                Value: kind == "preset" ? "base" : null));
            Assert.True(preview.Ok, preview.Error);
            fingerprint = preview.Result!.Value.GetProperty("values_fingerprint").GetString();
        }
        return new(BrokerRequest.OpProfile, Kind: kind, Value: value, ExpectedProfileFingerprint: fingerprint);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("start")]
    [InlineData("resume")]
    [InlineData("apply-power")]
    [InlineData("after-reset")]
    [InlineData("finish-job")]
    public void Editing_daily_file_then_apply_is_not_approval(string kind)
    {
        var d = Dispatcher();
        new DailyProfile([new("power", "fe", "450000")], _host.Now).Save(ProfilePath);
        int prompts = 0;
        d.AuthorizeProfileChange = _ => { prompts++; return true; };
        var request = new BrokerRequest(BrokerRequest.OpProfile, Kind: kind, Lease: "lab");
        var response = kind is "apply" or "finish-job" ? d.FromClient(request) : d.Dispatch(request);
        Assert.False(response.Ok);
        Assert.Contains("approv", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Equal(0, prompts);
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("adopt")]
    [InlineData("preset")]
    [InlineData("stock")]
    [InlineData("factory")]
    [InlineData("off")]
    [InlineData("load")]
    [InlineData("wall")]
    [InlineData("save")]
    [InlineData("forget")]
    public async Task Pending_confirmation_does_not_block_reset_apply_or_candidate_write(string kind)
    {
        var d = Dispatcher();
        SeedApproved();
        Assert.True(d.Dispatch(new(BrokerRequest.OpProfile, Kind: "save", Value: "base")).Ok);
        var request = Request(d, kind);
        using var opened = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        d.AuthorizeProfileChange = _ => { opened.Set(); answer.Wait(TimeSpan.FromSeconds(5)); return false; };
        var asking = Task.Run(() => d.FromClient(request));
        Task<BrokerResponse>? reset = null, apply = null, candidate = null;
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(2)));
            reset = Task.Run(() => d.Dispatch(new(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset)));
            apply = Task.Run(() => d.FromClient(new(BrokerRequest.OpProfile, Kind: "apply")));
            candidate = Task.Run(() => d.FromClient(new(BrokerRequest.OpPower, Kind: "fe", Mw: 550_000, Lease: "lab-job")));
            var results = await Task.WhenAll(reset, apply, candidate).WaitAsync(TimeSpan.FromSeconds(1));
            foreach (var result in results) Assert.True(result.Ok, result.Error);
        }
        finally
        {
            answer.Set();
            await asking;
            if (reset != null && apply != null && candidate != null) await Task.WhenAll(reset, apply, candidate);
        }
    }

    [Fact]
    public async Task Only_one_confirmation_can_wait_and_denial_has_a_cooldown()
    {
        var d = Dispatcher();
        using var opened = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        int prompts = 0;
        d.AuthorizeProfileChange = _ => { Interlocked.Increment(ref prompts); opened.Set(); answer.Wait(TimeSpan.FromSeconds(5)); return false; };
        var first = Task.Run(() => d.FromClient(new(BrokerRequest.OpProfile, Kind: "stock")));
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(2)));
            var others = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
                Task.Run(() => d.FromClient(new(BrokerRequest.OpProfile, Kind: "stock"))))).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.All(others, result => { Assert.False(result.Ok); Assert.Contains("pending", result.Error); });
            Assert.Equal(1, prompts);
        }
        finally { answer.Set(); await first; }
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        Assert.Equal(1, prompts);
        // Recovery is exempt from both admission and cooldown.
        Assert.True(d.FromClient(new(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        _host.Now += BrokerDispatcher.ConfirmationCooldown;
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        Assert.Equal(2, prompts);
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("adopt")]
    [InlineData("preset")]
    [InlineData("stock")]
    [InlineData("factory")]
    [InlineData("off")]
    [InlineData("load")]
    [InlineData("wall")]
    [InlineData("save")]
    [InlineData("forget")]
    public void Every_kind_rechecks_snapshot_after_consent_and_refuses_stale_commit(string kind)
    {
        var d = Dispatcher();
        SeedApproved();
        Assert.True(d.Dispatch(new(BrokerRequest.OpProfile, Kind: "save", Value: "base")).Ok);
        var request = Request(d, kind);
        var tampered = new DailyProfile([new("power", "fe", "450000")], _host.Now);
        d.AuthorizeProfileChange = _ => { tampered.Save(ProfilePath); return true; };
        int sets = FakeDriver.Sets;
        var result = d.FromClient(request);
        Assert.False(result.Ok, $"stale {kind} must not commit");
        Assert.Equal(sets, FakeDriver.Sets);
        Assert.Equal("450000", DailyProfile.Load(ProfilePath)!.Writes.Single().Value);
        Assert.False(_approvals.Matches("gpu-A", ProfileApproval.Daily, tampered));
        Assert.NotNull(SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "base"));
        Assert.Null(SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "new"));
    }

    [Fact]
    public void Migration_requires_confirmation_and_receipts_survive_a_dispatcher_restart()
    {
        var d = Dispatcher();
        var legacy = new DailyProfile([new("power", "fe", "550000")], _host.Now);
        legacy.Save(ProfilePath);
        Assert.False(d.Dispatch(new(BrokerRequest.OpProfile, Kind: "start")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
        d.AuthorizeProfileChange = _ => false;
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "load")).Ok);
        Assert.False(_approvals.Matches("gpu-A", ProfileApproval.Daily, legacy));
        _host.Now += BrokerDispatcher.ConfirmationCooldown;
        d.AuthorizeProfileChange = _ => true;
        Assert.True(d.FromClient(new(BrokerRequest.OpProfile, Kind: "load")).Ok);
        Assert.Equal(550_000u, _requests[0xfe]);
        var restarted = Dispatcher(); // shares host-owned receipt storage, not the dispatcher's authorization state
        _requests[0xfe] = 575_000;
        Assert.True(restarted.Dispatch(new(BrokerRequest.OpProfile, Kind: "start")).Ok);
        Assert.Equal(550_000u, _requests[0xfe]);
    }

    [Fact]
    public void Tampered_active_profile_is_rejected_and_existing_owned_wall_is_returned()
    {
        var d = Dispatcher();
        Assert.True(d.Dispatch(new(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        var tampered = DailyProfile.Load(ProfilePath)! with { Writes = [new("power", "fe", "450000")] };
        tampered.Save(ProfilePath);
        var result = d.FromClient(new(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.False(result.Ok);
        Assert.True(result.Result!.Value.GetProperty("stock_restored").GetBoolean());
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Empty(_journal.Inspect().Items);
    }

    [Fact]
    public void Stock_revokes_an_active_profile_even_if_its_old_file_is_replayed()
    {
        var d = Dispatcher();
        SeedApproved();
        string active = File.ReadAllText(ProfilePath);
        d.AuthorizeProfileChange = _ => true;
        Assert.True(d.FromClient(new(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        File.WriteAllText(ProfilePath, active);
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void Edited_saved_profile_is_not_trusted_but_can_be_explicitly_reapproved()
    {
        var d = Dispatcher();
        SeedApproved();
        Assert.True(d.Dispatch(new(BrokerRequest.OpProfile, Kind: "save", Value: "base")).Ok);
        var edited = new DailyProfile([new("power", "fe", "450000")], _host.Now);
        SavedProfiles.Save(SavedProfiles.PathBeside(_journal.Path), "base", edited);
        Assert.False(_approvals.Matches("gpu-A", ProfileApproval.Saved("base"), edited));
        int prompts = 0;
        d.AuthorizeProfileChange = _ => { prompts++; return false; };
        Assert.False(d.FromClient(Request(d, "preset")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
        _host.Now += BrokerDispatcher.ConfirmationCooldown;
        d.AuthorizeProfileChange = _ => { prompts++; return true; };
        Assert.True(d.FromClient(Request(d, "preset")).Ok);
        Assert.Equal(2, prompts);
        Assert.Equal(450_000u, _requests[0xfe]);
        Assert.True(_approvals.Matches("gpu-A", ProfileApproval.Saved("base"), edited));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("stock")]
    [InlineData("factory")]
    public void Disabling_a_missing_profile_revokes_its_receipt_so_deleted_JSON_cannot_be_replayed(string kind)
    {
        var d = Dispatcher();
        SeedApproved();
        var original = DailyProfile.Load(ProfilePath)!;
        File.Delete(ProfilePath);
        d.AuthorizeProfileChange = _ => true;
        d.FromClient(new(BrokerRequest.OpProfile, Kind: kind));
        Assert.False(_approvals.Matches("gpu-A", ProfileApproval.Daily, original));
        original.Save(ProfilePath);
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void Closing_the_dispatcher_during_confirmation_cannot_commit_a_late_yes()
    {
        var d = Dispatcher();
        SeedApproved();
        string before = File.ReadAllText(ProfilePath);
        d.AuthorizeProfileChange = _ => { Assert.True(d.Close(TimeSpan.Zero)); return true; };
        var result = d.FromClient(new(BrokerRequest.OpProfile, Kind: "wall", Value: "450000"));
        Assert.False(result.Ok);
        Assert.Equal(BrokerCode.WindowBusy, result.Code);
        Assert.Equal(before, File.ReadAllText(ProfilePath));
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void A_measurement_lease_acquired_during_confirmation_is_respected_on_commit()
    {
        bool measuring = false;
        var d = Dispatcher(judge: () => measuring ? "a new lab measurement" : null);
        SeedApproved();
        string before = File.ReadAllText(ProfilePath);
        d.AuthorizeProfileChange = _ => { measuring = true; return true; };
        var result = d.FromClient(new(BrokerRequest.OpProfile, Kind: "wall", Value: "450000"));
        Assert.False(result.Ok);
        Assert.Equal(BrokerCode.JudgeBusy, result.Code);
        Assert.Equal(before, File.ReadAllText(ProfilePath));
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void A_trial_started_during_confirmation_is_not_overwritten_by_a_late_stock_yes()
    {
        var d = Dispatcher();
        SeedApproved();
        d.AuthorizeProfileChange = _ =>
        {
            Assert.True(d.FromClient(new(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"new lab job"}""")).Ok);
            Assert.True(d.FromClient(new(BrokerRequest.OpExperiment, Kind: "try", Lease: "new-job",
                Value: """{"curve":"0*127","change":"new trial","power_mw":450000,"keep_curve":true}""")).Ok);
            return true;
        };
        var result = d.FromClient(new(BrokerRequest.OpProfile, Kind: "stock"));
        Assert.False(result.Ok);
        Assert.Equal(450_000u, _requests[0xfe]);
        Assert.False(DailyProfile.Load(ProfilePath)!.Suspended);
        Assert.Equal("new trial", Experiment.Load(Experiment.PathBeside(_journal.Path))!.Trial!.Change);
    }

    private sealed class UnavailableApprovals : IProfileApprovals
    {
        public bool Matches(string gpu, string slot, DailyProfile profile) => throw new IOException("store offline");
        public void Remember(string gpu, string slot, DailyProfile profile) => throw new IOException("store offline");
        public void Forget(string gpu, string slot) => throw new IOException("store offline");
    }

    [Fact]
    public void Approval_storage_failure_cannot_apply_a_profile_or_claim_success()
    {
        var d = Dispatcher(new UnavailableApprovals());
        new DailyProfile([new("power", "fe", "450000")], _host.Now).Save(ProfilePath);
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        d.AuthorizeProfileChange = _ => true;
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "load")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void A_wall_or_save_confirmation_does_not_launder_an_unapproved_profile()
    {
        var d = Dispatcher();
        new DailyProfile([new("power", "fe", "450000")], _host.Now).Save(ProfilePath);
        d.AuthorizeProfileChange = _ => throw new Exception("must ask to approve the full profile first");
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "wall", Value: "550000")).Ok);
        Assert.False(d.FromClient(new(BrokerRequest.OpProfile, Kind: "save", Value: "hidden")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }
}
