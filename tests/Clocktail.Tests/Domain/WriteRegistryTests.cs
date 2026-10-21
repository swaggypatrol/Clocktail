using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The write-path registry is the one list of what can be written, and recovery and the broker both go through
/// it. These tests pin its contract: every journal kind resolves to an entry, Prepare serves a write and a
/// recovery with the same function, a value the path cannot write from is refused before the driver, and a
/// limit that is only visible after the write (a Verify) gives the cell back inside one sentinel window.
/// </summary>
[Collection("fake-driver")]
public class WriteRegistryTests : IDisposable
{
    private const int Board = 0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = 0xFFFFFFFF };

    public WriteRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static uint Word(Slot s) => Slots.Info(s).Shape!.Value.Word;

    private OwnedWriter Writer(NvClient hw) => new(hw, _journal, _sentinel, _host);

    /// <summary>The 5090's live power table on the fake: FE 575 W present, F8 absent, board max 600 W.</summary>
    private NvClient PowerFake()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(info, 4, 1u << Board);
        int e = PowerRequestsLayout.InfoEntry(Board);
        W(info, e, 1); W(info, e + 8, 400_000); W(info, e + 0xc, 575_000); W(info, e + 0x10, 600_000);
        FakeDriver.State[Word(Slot.ClientPwrPoliciesGetInfo)] = info;
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        uint controlWord = Word(Slot.ClientPwrPoliciesGetControl);
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
            int c = PowerRequestsLayout.ControlEntry(Board);
            _requests[U(req, c + 4)] = U(req, c);
            return state;
        };
        return new NvClient(FakeDriver.ApiFor([Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl], [Slot.ClientPwrPoliciesSetControl]));
    }

    /// <summary>Two fan channels (ids 1 and 2), firmware range [30, 100], both in auto; mode carries a driver bit 0x10.</summary>
    private static NvClient FanFake()
    {
        FakeDriver.Reset();
        var status = NvClient.NewBuffer(Slot.ClientFanCoolersGetStatus);
        status[FanLayout.StatusCount] = 2;
        var control = NvClient.NewBuffer(Slot.ClientFanCoolersGetControl);
        control[FanLayout.ControlCount] = 2;
        for (int i = 0; i < 2; i++)
        {
            int s = FanLayout.StatusEntry(i);
            W(status, s, (uint)i + 1); W(status, s + 4, 1200); W(status, s + 8, 30); W(status, s + 0xc, 100); W(status, s + 0x10, 36);
            W(control, FanLayout.ControlEntry(i), (uint)i + 1); W(control, FanLayout.ControlEntry(i) + FanLayout.ControlLevel, 36); W(control, FanLayout.ControlEntry(i) + FanLayout.ControlMode, 0x10);
        }
        FakeDriver.State[Word(Slot.ClientFanCoolersGetStatus)] = status;
        FakeDriver.State[Word(Slot.ClientFanCoolersGetControl)] = control;
        return new NvClient(FakeDriver.ApiFor([Slot.ClientFanCoolersGetStatus, Slot.ClientFanCoolersGetControl], [Slot.ClientFanCoolersSetControl]));
    }

    [Fact]
    public void Every_kind_the_journal_can_hold_has_exactly_one_entry()
    {
        Assert.Equal(["pstates20", "power", "rails", "clockdomain", "vdemand", "boost", "fan", "fancurve", "clocklimit", "ocp", "vf"], WriteRegistry.Kinds);
        Assert.Equal(WriteRegistry.Kinds.Count(), WriteRegistry.Kinds.Distinct().Count());
        Assert.NotNull(WriteRegistry.Find("POWER"));
        Assert.Null(WriteRegistry.Find("register"));
        Assert.Null(WriteRegistry.Find(null));
        // What is only visible after the write: the two derived rail limits, and the V/F curve's own validation.
        Assert.Equal(["rails", "boost", "vf"], WriteRegistry.All.Where(p => p.AfterWrite != null).Select(p => p.Kind));
        Assert.Equal(["boost"], WriteRegistry.All.Where(p => p.ProveEqual != null).Select(p => p.Kind));
    }

    [Fact]
    public void Pstates20_prepares_the_cell_without_reading_and_refuses_a_value_that_is_not_kilohertz()
    {
        FakeDriver.Reset();
        var hw = new NvClient(FakeDriver.ApiFor([Slot.GetPstates20], [Slot.SetPstates20]));
        var path = WriteRegistry.Find("pstates20")!;
        var p = path.Prepare(hw, _gpu, "core", "15000", WriteOptions.None);
        Assert.Equal("pstates20/core", p.Cell.Id);
        Assert.NotNull(p.Plan);
        var bad = path.Prepare(hw, _gpu, "memory", "fast", WriteOptions.None);
        Assert.Null(bad.Plan);
        Assert.Contains("kilohertz", bad.CannotWrite);
        Assert.Throws<ArgumentException>(() => path.Prepare(hw, _gpu, "xbar", "0", WriteOptions.None));
        Assert.Equal(0, FakeDriver.Gets);
    }

    /// <summary>
    /// The global core offset and the V/F point offsets spend one firmware budget (every point runs at
    /// base + global + point, and the driver states one range for the core clock), so whichever moves second
    /// checks the other: the V/F path already bounds global + point, and here the core path does the same against
    /// the points as they stand. A driver with no V/F structure has no points to spend the budget: the global's own
    /// range is the whole limit. Offsets that exist but cannot be read leave only the globals between 0 and the one
    /// in force provable, so no write is made blind and the way back stays open. Recovery is held to the same
    /// budget: the original global was a state the firmware held, but not necessarily with the points that are
    /// there now.
    /// </summary>
    [Fact]
    public void A_core_offset_is_bounded_by_the_budget_the_V_F_points_already_spend()
    {
        var f = new FakeVf();
        var hw = f.Install();
        var path = WriteRegistry.Find("pstates20")!;
        BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.ControlWord].AsSpan(VfLayout.OffsetField(126)), 900_000);
        var table = FakeDriver.State[FakeVf.PstatesWord];

        // A curve in force: any Pstates20 write would reset it (live 2026-10-18), so every change is refused before
        // the budget is even asked; an equal value writes nothing and is not.
        var over = path.Prepare(hw, _gpu, "core", "200000", WriteOptions.None).Plan!(table);
        Assert.Contains("resets every one of them to 0", Assert.IsType<Mutation.Abort>(over).Reason);
        Assert.Contains("resets every one of them to 0", Assert.IsType<Mutation.Abort>(path.Prepare(hw, _gpu, "core", "100000", WriteOptions.None).Plan!(table)).Reason);
        Assert.IsType<Mutation.Abort>(path.Prepare(hw, _gpu, "core", "200000", WriteOptions.Recovery).Plan!(table));
        Assert.IsType<Mutation.Apply>(path.Prepare(hw, _gpu, "core", "0", WriteOptions.None).Plan!(table));
        // A flat curve: the table is written in request mode.
        BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.ControlWord].AsSpan(VfLayout.OffsetField(126)), 0);
        Assert.IsType<Mutation.Request>(path.Prepare(hw, _gpu, "core", "100000", WriteOptions.None).Plan!(table));
        // The driver's own range on the global still comes first, with its own words.
        Assert.Contains("exceeds the driver range", Assert.IsType<Mutation.Abort>(path.Prepare(hw, _gpu, "core", "1500000", WriteOptions.None).Plan!(table)).Reason);

        // The offsets exist but cannot be read: away from 0 is refused, toward it (a way back) is not.
        FakeDriver.FailNextGets = 1;
        var blind = Assert.IsType<Mutation.Abort>(path.Prepare(hw, _gpu, "core", "100000", WriteOptions.None).Plan!(table));
        Assert.Contains("the V/F point offsets cannot be read", blind.Reason);
        f.GlobalKhz = 200_000;
        FakeDriver.FailNextGets = 1;
        Assert.IsType<Mutation.Request>(path.Prepare(hw, _gpu, "core", "100000", WriteOptions.None).Plan!(f.Pstates()));

        // No V/F structure on this driver: nothing else spends the budget, and nothing is read for it.
        FakeDriver.Reset();
        var bare = new NvClient(FakeDriver.ApiFor([Slot.GetPstates20], [Slot.SetPstates20]));
        Assert.NotNull(path.Prepare(bare, _gpu, "core", "200000", WriteOptions.None).Plan);
        Assert.Equal(0, FakeDriver.Gets);
    }

    [Fact]
    public void Power_write_and_recovery_are_the_same_prepare_with_different_values()
    {
        var hw = PowerFake();
        var path = WriteRegistry.Find(PowerCap.Kind)!;
        var p = path.Prepare(hw, _gpu, "fe", "560000", WriteOptions.None);
        var w = Writer(hw).Apply(_gpu, p.Cell, p.Plan!);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Held, w.Hold);
        Assert.Equal(560_000u, _requests[0xfe]);

        var item = _journal.Load()!.Items.Single();
        Assert.Equal("575000", item.Original);
        var back = path.Prepare(hw, _gpu, item.Key, item.Original, WriteOptions.Recovery);
        var r = Writer(hw).Write(_gpu, back.Cell, back.Plan!);
        Assert.Equal(Hold.Released, r.Hold);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
    }

    [Fact]
    public void An_absent_power_original_has_no_plan_and_says_why()
    {
        var hw = PowerFake();
        var p = WriteRegistry.Find("power")!.Prepare(hw, _gpu, "f8", PowerCap.Absent, WriteOptions.Recovery);
        Assert.Equal("power/f8", p.Cell.Id);
        Assert.Null(p.Plan);
        Assert.Contains("no way to delete", p.CannotWrite);
        Assert.Throws<ArgumentException>(() => WriteRegistry.Find("power")!.Prepare(hw, _gpu, "f9", "1", WriteOptions.None));
    }

    [Fact]
    public void Creating_a_request_that_does_not_exist_is_an_option_a_write_must_pass_and_recovery_always_passes()
    {
        var hw = PowerFake();
        var path = WriteRegistry.Find("power")!;
        var refused = path.Prepare(hw, _gpu, "f8", "500000", WriteOptions.None);
        var r = Writer(hw).Apply(_gpu, refused.Cell, refused.Plan!);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("--allow-new-request", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);

        var allowed = path.Prepare(hw, _gpu, "f8", "500000", new WriteOptions(AllowCreate: true));
        var a = Writer(hw).Apply(_gpu, allowed.Cell, allowed.Plan!);
        Assert.Equal(TxOutcome.Applied, a.Tx.Outcome);
        Assert.Equal("600000", a.Original);   // absent F8 reads as the board maximum, what a give-back writes
    }

    /// <summary>
    /// A limit the driver reveals only after the write is the cell's post-write check, judged inside the transaction:
    /// a veto restores the edited bytes with the transaction's own restore (no plan, no rules), the record is released
    /// because the cell reads back at its original, and the sentinel never outlives the call. The fan cell has no such
    /// limit, so the check is attached here to show the mechanism on a path whose bytes are easy to read.
    /// </summary>
    [Fact]
    public void A_post_write_veto_restores_the_bytes_inside_the_transaction_and_releases_the_record()
    {
        var hw = FanFake();
        var path = WriteRegistry.Find(FanControl.Kind)!;
        var p = path.Prepare(hw, _gpu, "1", "manual:50", WriteOptions.None);
        bool sentinelHeldDuringCheck = false;
        string? seenByTheCheck = null;
        var cell = p.Cell with
        {
            Spec = p.Cell.Spec with
            {
                CheckAfterWrite = (_, _) =>
                {
                    sentinelHeldDuringCheck = _sentinel.IsBlocked(out _);
                    seenByTheCheck = p.Cell.StateIn(FakeDriver.Current(Slot.ClientFanCoolersGetControl));
                    return "pretend: a derived value is above its firmware limit";
                },
            },
        };
        var w = Writer(hw).Apply(_gpu, cell, p.Plan!);

        Assert.True(sentinelHeldDuringCheck);
        Assert.Equal("manual:50", seenByTheCheck);          // the check judged the landed write
        Assert.Equal(TxOutcome.Failed, w.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, w.Tx.Restore);
        Assert.StartsWith("pretend: a derived value is above its firmware limit; previous values restored", w.Tx.Detail);
        Assert.Equal("pretend: a derived value is above its firmware limit", w.Tx.Veto);
        Assert.Equal("auto", w.After);
        Assert.Equal(Hold.Released, w.Hold);
        Assert.True(w.SentinelCleared);
        Assert.False(_sentinel.IsBlocked(out _));
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.Equal(2, FakeDriver.Sets);
        Assert.Equal("auto", FanParser.ControlFor(FakeDriver.Current(Slot.ClientFanCoolersGetControl), 1)!.State.ToString());
    }

    /// <summary>The 5090-shaped rails plus an all-zero independent boost structure, as VoltBoostTests seeds them.</summary>
    private static NvClient RailsAndBoostFake()
    {
        RailsFixture.SeedDriver();
        FakeDriver.Seed(Slot.ClientVoltRailsGetControl);
        return new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo, Slot.ClientVoltRailsGetControl],
            [Slot.VoltVoltRailsSetControl, Slot.ClientVoltRailsSetControl]));
    }

    [Fact]
    public void The_derived_limit_check_rides_on_the_cell_for_a_write_and_is_left_off_for_recovery()
    {
        var hw = RailsAndBoostFake();
        var rails = WriteRegistry.Find(RailWrites.Kind)!;
        var boost = WriteRegistry.Find(VoltBoost.Kind)!;
        Assert.NotNull(rails.Prepare(hw, _gpu, "nvvdd", "rel=-5000", WriteOptions.None).Cell.Spec.CheckAfterWrite);
        Assert.NotNull(boost.Prepare(hw, _gpu, "independent", "10", WriteOptions.None).Cell.Spec.CheckAfterWrite);
        // Recovery returns to a state the driver itself held; the check that bounds requests is not applied to it.
        Assert.Null(rails.Prepare(hw, _gpu, "nvvdd", "rel=0,alt=0,ov=0,vmin=0", WriteOptions.Recovery).Cell.Spec.CheckAfterWrite);
        Assert.Null(boost.Prepare(hw, _gpu, "independent", "0", WriteOptions.Recovery).Cell.Spec.CheckAfterWrite);
        // A value with no plan has nothing to check.
        var refused = boost.Prepare(hw, _gpu, "legacy", "200", WriteOptions.None);
        Assert.Null(refused.Plan);
        Assert.Null(refused.Cell.Spec.CheckAfterWrite);
    }

    [Fact]
    public void The_rail_and_boost_checks_name_a_MAX_above_the_standard_ceiling()
    {
        var hw = RailsAndBoostFake();
        var rails = WriteRegistry.Find(RailWrites.Kind)!.Prepare(hw, _gpu, "nvvdd", "rel=-5000", WriteOptions.None).Cell.Spec.CheckAfterWrite!;
        var boost = WriteRegistry.Find(VoltBoost.Kind)!.Prepare(hw, _gpu, "independent", "10", WriteOptions.None).Cell.Spec.CheckAfterWrite!;
        Assert.Null(rails(hw, _gpu));    // NVVDD MAX 1.100 V, ceiling min(1.2 V domain max, 1.15 V)
        Assert.Null(boost(hw, _gpu));

        W(FakeDriver.State[Word(Slot.VoltVoltRailsGetStatus)], RailsLayout.StatusBase + 16, 1_160_000);
        Assert.Contains("nvvdd", rails(hw, _gpu));
        Assert.Contains("1150000", boost(hw, _gpu));
    }

    /// <summary>
    /// The checks are relative to the state the write was planned on: a MAX another tool left above the ceiling is
    /// not a reason to undo a write that did not raise it, and lowering it is allowed; only a rise past both the
    /// ceiling and where it stood is a violation.
    /// </summary>
    [Fact]
    public void The_checks_judge_a_MAX_against_the_ceiling_or_where_it_stood_before_the_write_whichever_is_higher()
    {
        var hw = RailsAndBoostFake();
        var status = FakeDriver.State[Word(Slot.VoltVoltRailsGetStatus)];
        W(status, RailsLayout.StatusBase + 16, 1_160_000);          // found above the ceiling before the write
        var rails = WriteRegistry.Find(RailWrites.Kind)!.Prepare(hw, _gpu, "msvdd", "rel=-5000", WriteOptions.None).Cell.Spec.CheckAfterWrite!;
        var boost = WriteRegistry.Find(VoltBoost.Kind)!.Prepare(hw, _gpu, "independent", "10", WriteOptions.None).Cell.Spec.CheckAfterWrite!;
        Assert.Null(rails(hw, _gpu));                                // unchanged: not this write's doing
        Assert.Null(boost(hw, _gpu));
        W(status, RailsLayout.StatusBase + 16, 1_155_000);           // lowered, still above the ceiling: allowed
        Assert.Null(rails(hw, _gpu));
        Assert.Null(boost(hw, _gpu));
        W(status, RailsLayout.StatusBase + 16, 1_165_000);           // raised: the violation names the rail and where it was
        Assert.Contains("nvvdd", rails(hw, _gpu));
        Assert.Contains("was 1160000", boost(hw, _gpu));
    }

    /// <summary>The 5090-shaped rails with NVVDD's REL at 1.2 V stock, above the 1.15 V ceiling, and a status that follows the
    /// control offsets (REL, ALT, OV = base + offset; MAX = their minimum), so a write moves what the next read sees.</summary>
    private static NvClient RailsWithStockRelAboveTheCeiling()
    {
        RailsFixture.SeedDriver();
        uint statusWord = Word(Slot.VoltVoltRailsGetStatus), controlWord = Word(Slot.VoltVoltRailsGetControl);
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
        return new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.VoltVoltRailsSetControl]));
    }

    /// <summary>
    /// The rules bound excursions, not positions: a slot lowered from a stock value above the ceiling may go back to
    /// that stock value, because the hardware held it before this program did anything. The registry learns that
    /// origin from the journal (<see cref="WriteOptions.Original"/>), so a user's restore and recovery both land and
    /// release the record, while the same request with no origin is refused by the ceiling rule.
    /// </summary>
    [Fact]
    public void A_lowered_slot_returns_to_a_stock_value_above_the_ceiling_only_with_the_journal_origin()
    {
        var hw = RailsWithStockRelAboveTheCeiling();
        var path = WriteRegistry.Find(RailWrites.Kind)!;
        var down = path.Prepare(hw, _gpu, "nvvdd", "rel=-100000", WriteOptions.None);
        var w = Writer(hw).Apply(_gpu, down.Cell, down.Plan!);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Held, w.Hold);
        var item = _journal.Load()!.Items.Single();
        Assert.Equal("rel=0,alt=0,ov=0,vmin=0", item.Original);

        // No origin: REL back to 1.2 V is a raise past the ceiling, refused before the driver.
        var blind = path.Prepare(hw, _gpu, "nvvdd", "rel=0", WriteOptions.None);
        var refused = Writer(hw).Apply(_gpu, blind.Cell, blind.Plan!);
        Assert.Equal(TxOutcome.Aborted, refused.Tx.Outcome);
        Assert.Contains("REL would rise to 1200 mV, above the firmware-reported ceiling of 1150 mV", refused.Tx.Detail);
        Assert.Equal(1, FakeDriver.Sets);

        // The journal's origin: the same request is a return, lands, and releases the record.
        var back = path.Prepare(hw, _gpu, "NVVDD", "rel=0", new WriteOptions(Original: item.Original));
        var r = Writer(hw).Apply(_gpu, back.Cell, back.Plan!);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(Hold.Released, r.Hold);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.Equal(1_200_000u, RailsReader.Read(hw, _gpu).Get(Rail.Nvvdd)!.Limits.RelUv);

        // Recovery is the same path with the original as both value and origin.
        var again = path.Prepare(hw, _gpu, "nvvdd", "rel=-100000", WriteOptions.None);
        Writer(hw).Apply(_gpu, again.Cell, again.Plan!);
        var rec = path.Prepare(hw, _gpu, "nvvdd", _journal.Load()!.Items.Single().Original, WriteOptions.Recovery);
        Assert.Null(rec.Cell.Spec.CheckAfterWrite);
        var g = Writer(hw).Write(_gpu, rec.Cell, rec.Plan!);
        Assert.Equal(TxOutcome.Applied, g.Tx.Outcome);
        Assert.Equal(Hold.Released, g.Hold);
    }
}
