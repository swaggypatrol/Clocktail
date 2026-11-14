using System.Buffers.Binary;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// A host whose protected approval store cannot be opened (<see cref="UnavailableProfileApprovals"/>) still serves: it
/// approves nothing and applies no daily profile, says why to whoever asks, and leaves the ways that need no approval
/// (trials, back to stock) working. The alternative, a host that does not start, turned one folder made by another
/// account into a service that never ran.
/// </summary>
[Collection("fake-driver")]
public class ApprovalsUnavailableTests : IDisposable
{
    private const int Board = 0;
    private const string Why = "approval directory is not admin-owned and protected";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = 0xFFFFFFFF };
    private int _asked;

    public ApprovalsUnavailableTests()
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

    /// <summary>A dispatcher on the power fake (FE 575 W, the board 400 to 600 W) whose approval store is unavailable.</summary>
    private BrokerDispatcher Degraded()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(info, 4, 1u << Board);
        int ie = PowerRequestsLayout.InfoEntry(Board);
        W(info, ie, 1); W(info, ie + 8, 400_000); W(info, ie + 0xc, 575_000); W(info, ie + 0x10, 600_000);
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = info;
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
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo],
            [Slot.ClientPwrPoliciesSetControl, Slot.PowerPolicySetControl]));
        return new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true, profileApprovals: new UnavailableProfileApprovals(Why))
        {
            AuthorizeProfileChange = _ => { _asked++; return true; },
        };
    }

    private string ProfilePath => DailyProfile.PathBeside(_journal.Path);

    [Theory]
    [InlineData("keep")]
    [InlineData("adopt")]
    [InlineData("preset")]
    [InlineData("wall")]
    [InlineData("load")]
    [InlineData("save")]
    public void What_would_grant_an_approval_is_refused_with_the_reason_and_the_person_is_not_asked(string kind)
    {
        var d = Degraded();
        new DailyProfile([new ProfileWrite("power", "fe", "560000")], _host.Now).Save(ProfilePath);

        var r = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: kind, Value: kind == "wall" ? "560000" : "games", ExpectedProfileFingerprint: "any"));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.CannotWrite, r.Code);
        Assert.Contains("profile approvals are unavailable", r.Error);
        Assert.Contains(Why, r.Error);
        Assert.Equal(0, _asked);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void Stock_still_asks_suspends_the_profile_and_gives_the_card_back()
    {
        var d = Degraded();
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);   // a write of the session, held
        new DailyProfile([new ProfileWrite("power", "fe", "560000")], _host.Now).Save(ProfilePath);

        var r = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, _asked);
        Assert.Equal(575_000u, _requests[0xfe]);                 // back at the card's own
        Assert.True(DailyProfile.Load(ProfilePath)!.Suspended);  // and nothing puts the profile back
        Assert.Empty(_journal.Inspect().Items);
    }

    [Fact]
    public void A_profile_on_disk_is_never_put_on()
    {
        var d = Degraded();
        new DailyProfile([new ProfileWrite("power", "fe", "450000")], _host.Now).Save(ProfilePath);

        var asked = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.False(asked.Ok);
        Assert.True(asked.Result!.Value.GetProperty("approval_required").GetBoolean());
        var atStart = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "start", Value: "the broker started"));   // the service's own
        Assert.False(atStart.Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void A_trial_write_needs_no_approval_and_goes_on()
    {
        var d = Degraded();
        var r = d.FromClient(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(560_000u, _requests[0xfe]);
    }

    [Fact]
    public void Ping_and_show_say_that_approvals_are_unavailable_and_why()
    {
        var d = Degraded();
        new DailyProfile([new ProfileWrite("power", "fe", "560000")], _host.Now).Save(ProfilePath);

        var ping = d.FromClient(new BrokerRequest(BrokerRequest.OpPing)).Result!.Value.GetProperty("profile_approvals");
        Assert.False(ping.GetProperty("available").GetBoolean());
        Assert.Equal(Why, ping.GetProperty("reason").GetString());

        var show = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "show")).Result!.Value;
        Assert.False(show.GetProperty("approved").GetBoolean());
        Assert.Equal(Why, show.GetProperty("approvals_unavailable").GetString());
    }

    [Fact]
    public void A_working_store_reports_itself_available()
    {
        FakeDriver.Reset();
        var d = new BrokerDispatcher(new NvClient(FakeDriver.ApiFor([], [])), new Target(_journal, _sentinel, _gpu), _host, canWrite: true, profileApprovals: new MemoryProfileApprovals());
        var ping = d.FromClient(new BrokerRequest(BrokerRequest.OpPing)).Result!.Value.GetProperty("profile_approvals");
        Assert.True(ping.GetProperty("available").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, ping.GetProperty("reason").ValueKind);
    }
}
