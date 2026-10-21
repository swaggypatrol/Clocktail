using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class PowerCapTests : IDisposable
{
    private const int Board = 0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    /// <summary>The fake driver's own request table: command → value, as the 5090 reported it live (FE 575 W, F8 absent).</summary>
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = 0xFFFFFFFF };

    public PowerCapTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

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

    private (PowerCap cap, NvClient hw) Setup(bool enabled = true)
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
            return state;   // the stored structure is irrelevant: answers come from the table
        };
        var policies = enabled ? WritePolicySet.Default : WritePolicySet.Default.WithDisabled(Slot.ClientPwrPoliciesSetControl, "switched off for this test");
        var hw = new NvClient(FakeDriver.ApiFor([Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl], [Slot.ClientPwrPoliciesSetControl]), policies);
        return (new PowerCap(hw, _journal, _sentinel, _host), hw);
    }

    [Fact]
    public void Rules_follow_the_gate_and_the_board_range()
    {
        var board = PowerRequestsParser.ParseInfo(Info());
        Assert.Null(PowerRequestRules.Validate(board, PowerRequestKind.Ordinary, 500_000));
        Assert.Contains("below the driver minimum", PowerRequestRules.Validate(board, PowerRequestKind.Ordinary, 350_000));
        Assert.Null(PowerRequestRules.Validate(board, PowerRequestKind.Additional, 350_000));   // F8 has no lower bound
        Assert.Contains("above the driver maximum", PowerRequestRules.Validate(board, PowerRequestKind.Additional, 600_001));
        Assert.Contains("absent", PowerRequestRules.Validate(board, PowerRequestKind.Additional, 0));
        Assert.Contains("absent", PowerRequestRules.Validate(board, PowerRequestKind.Ordinary, 0xFFFFFFFF));
    }

    [Fact]
    public void The_set_request_passes_the_default_whitelist_and_a_switched_off_setter_refuses_it()
    {
        var req = PowerRequestRules.BuildSetRequest(2, PowerRequestKind.Additional, 550_000);
        var on = WritePolicySet.Default;   // on since the live drill of 2026-10-17
        Assert.Null(on.Check(Slot.ClientPwrPoliciesSetControl, req));
        Assert.Contains("disabled", on.WithDisabled(Slot.ClientPwrPoliciesSetControl, "off").Check(Slot.ClientPwrPoliciesSetControl, req));

        var twoBits = (byte[])req.Clone(); W(twoBits, 4, 0b101);
        Assert.Contains("exactly one", on.Check(Slot.ClientPwrPoliciesSetControl, twoBits));
        var badCmd = (byte[])req.Clone(); W(badCmd, PowerRequestsLayout.ControlEntry(2) + 4, 0x13);
        Assert.Contains("must be 0xf8 or 0xfe", on.Check(Slot.ClientPwrPoliciesSetControl, badCmd));
        var zero = (byte[])req.Clone(); W(zero, PowerRequestsLayout.ControlEntry(2), 0);
        Assert.Contains("must not be 0", on.Check(Slot.ClientPwrPoliciesSetControl, zero));
        var stray = (byte[])req.Clone(); stray[0x100] = 1;
        Assert.Contains("+0x100 must be zero", on.Check(Slot.ClientPwrPoliciesSetControl, stray));
    }

    [Fact]
    public void Equal_value_write_is_no_change_and_claims_nothing()
    {
        var (cap, _) = Setup();
        var r = cap.Apply(_gpu, PowerRequestKind.Ordinary, 575_000);
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
        Assert.True(r.SentinelCleared);
        Assert.StartsWith("nothing held", r.Ownership);
    }

    [Fact]
    public void The_power_original_is_claimed_from_the_query_the_write_was_planned_on()
    {
        var (cap, _) = Setup();
        string? originalAtWrite = null;
        int getsAtWrite = -1;
        FakeDriver.OnSet = () =>
        {
            if (getsAtWrite >= 0) return;
            getsAtWrite = FakeDriver.Gets;
            originalAtWrite = _journal.Load()?.Items.Single().Original;
        };
        var r = cap.Apply(_gpu, PowerRequestKind.Ordinary, 560_000);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal("575000", originalAtWrite);
        Assert.Equal(2, getsAtWrite);   // the board INFO, then the one FE query that both the plan and the record use
    }

    [Fact]
    public void Lowering_FE_then_putting_it_back_claims_and_releases()
    {
        var (cap, _) = Setup();
        var down = cap.Apply(_gpu, PowerRequestKind.Ordinary, 500_000);
        Assert.Equal(TxOutcome.Applied, down.Tx.Outcome);
        Assert.Equal(575_000u, down.BeforeMw);
        Assert.Equal(500_000u, down.AfterMw);
        Assert.Equal(500_000u, _requests[0xfe]);
        var owned = Assert.Single(_journal.Load()!.Items);
        Assert.Equal("575000", owned.Original);
        Assert.StartsWith("held", down.Ownership);
        Assert.False(_sentinel.IsBlocked(out _));

        var back = cap.Apply(_gpu, PowerRequestKind.Ordinary, 575_000);
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Null(_journal.Load());
        Assert.StartsWith("released", back.Ownership);
    }

    [Fact]
    public void Creating_a_request_that_does_not_exist_needs_a_deliberate_flag()
    {
        var (cap, _) = Setup();
        var r = cap.Apply(_gpu, PowerRequestKind.Additional, 550_000);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("no way to delete", r.Tx.Detail);
        Assert.Contains("--allow-new-request", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(0xFFFFFFFFu, _requests[0xf8]);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Creating_F8_records_the_board_maximum_as_the_original_and_gives_back_to_it()
    {
        var (cap, _) = Setup();
        var r = cap.Apply(_gpu, PowerRequestKind.Additional, 550_000, allowCreate: true);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(600_000u, r.BeforeMw);   // absent reads as the board maximum
        Assert.Equal(550_000u, r.AfterMw);
        Assert.Equal("600000", Assert.Single(_journal.Load()!.Items).Original);   // absent: the closest it can go back to is non-limiting

        // A driver that normalizes the value: the previous value existed, so it is restored, and the original is still owed.
        FakeDriver.RequestApplier = (state, req) => { uint v = U(req, PowerRequestsLayout.ControlEntry(Board)); _requests[0xf8] = v > 550_000 && v < 600_000 ? 545_000 : v; return state; };
        var clamped = cap.Apply(_gpu, PowerRequestKind.Additional, 560_000);
        Assert.Equal(TxOutcome.Failed, clamped.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, clamped.Tx.Restore);
        Assert.StartsWith("held", clamped.Ownership);

        var back = cap.Apply(_gpu, PowerRequestKind.Additional, 600_000);   // the give-back: the board maximum, released
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
    }

    /// <summary>F8 is read back until it shows twice in a row (re/power-thermal.md §6): a request that lands on the third
    /// read is Applied, not Failed and restored.</summary>
    [Fact]
    public void An_f8_request_that_lands_late_is_read_back_until_it_shows()
    {
        var (cap, _) = Setup();
        uint controlWord = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
        uint? pending = null;
        int reads = 0;
        var answer = FakeDriver.QueryResponder!;
        FakeDriver.RequestApplier = (state, req) => { pending = U(req, PowerRequestsLayout.ControlEntry(Board)); reads = 0; return state; };
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) == controlWord && pending is { } p && ++reads >= 3) { _requests[0xf8] = p; pending = null; }
            return answer(q);
        };
        var r = cap.Apply(_gpu, PowerRequestKind.Additional, 550_000, allowCreate: true);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(550_000u, _requests[0xf8]);
        Assert.Equal(1, FakeDriver.Sets);   // no restore was written
    }

    [Fact]
    public void A_refused_write_with_an_untouched_readback_leaves_everything_clean()
    {
        var (cap, _) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.RefuseUntouched;
        FakeDriver.RequestApplier = (state, req) => state;   // refused means the request table does not change either
        var r = cap.Apply(_gpu, PowerRequestKind.Ordinary, 500_000);
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.NotNeeded, r.Tx.Restore);
        Assert.Null(_journal.Load());
        Assert.True(r.SentinelCleared);
    }

    [Fact]
    public void The_disabled_policy_stops_the_write_before_the_driver()
    {
        var (cap, _) = Setup(enabled: false);
        var r = cap.Apply(_gpu, PowerRequestKind.Ordinary, 500_000);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("disabled", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());   // the claim was released: the request never left the original value
    }

    [Fact]
    public void Recovery_gives_the_power_originals_back_and_a_created_f8_goes_to_the_board_maximum()
    {
        var (cap, hw) = Setup();
        cap.Apply(_gpu, PowerRequestKind.Ordinary, 500_000);
        cap.Apply(_gpu, PowerRequestKind.Additional, 550_000, allowCreate: true);
        _host.Alive.Remove(100); _host.Pid = 7;   // the owner "died"; a new process recovers
        var rec = new Recovery(hw, _journal, _sentinel, _host).Run(_gpu, canWrite: true);
        Assert.Equal(RecoveryReason.OwnerDied, rec.Reason);
        var fe = rec.Items.Single(i => i.Key == "fe");
        Assert.Equal(RecoveryAction.Written, fe.Action);
        Assert.Equal(575_000u, _requests[0xfe]);
        var f8 = rec.Items.Single(i => i.Key == "f8");
        Assert.Equal(RecoveryAction.Written, f8.Action);
        Assert.Equal(600_000u, _requests[0xf8]);   // it cannot be deleted: non-limiting is as close to absent as it goes
        Assert.True(rec.Clean);
    }
}
