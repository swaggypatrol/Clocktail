using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The 5090's boost situation on the fake driver: both rails kind 3, the independent getter answers 0 %, the legacy
/// byte reads 0. <see cref="Model"/> decides what a boost write does to the rails, so the same tests can describe a
/// driver where the two paths alias, where only one acts, or where nothing happens.
/// </summary>
public enum Model { Alias, IndependentActs, LegacyActs, Ignores }

[Collection("fake-driver")]
public class VoltBoostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private static readonly GpuSession Gpu = new(1, "fake");

    public VoltBoostTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly uint IndepWord = Slots.Info(Slot.ClientVoltRailsGetControl).Shape!.Value.Word;
    private static readonly uint ControlWord = Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word;
    private static readonly uint StatusWord = Slots.Info(Slot.VoltVoltRailsGetStatus).Shape!.Value.Word;
    private const int NvvddMax = RailsLayout.StatusBase + 16;
    private const uint UvPerPercent = 1_000;

    private static void Kind3(byte[] info, byte[] control)
    {
        for (int bit = 0; bit < 2; bit++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(RailsLayout.InfoBase + bit * RailsLayout.InfoEntry), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(control.AsSpan(RailsLayout.ControlBase + bit * RailsLayout.ControlEntry), 3);
        }
    }

    private VoltBoost Setup(Model model, uint baseMax = RailsFixture.NvvddMax)
    {
        RailsFixture.SeedDriver();
        Kind3(FakeDriver.State[Slots.Info(Slot.VoltVoltRailsGetInfo).Shape!.Value.Word], FakeDriver.State[ControlWord]);
        BinaryPrimitives.WriteUInt32LittleEndian(FakeDriver.State[StatusWord].AsSpan(NvvddMax), baseMax);
        FakeDriver.Seed(Slot.ClientVoltRailsGetControl);

        FakeDriver.RequestApplier = (state, req) =>
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(req);
            uint pct = word == IndepWord ? BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(4)) : word == ControlWord ? req[RailsLayout.ControlBoostByte] : uint.MaxValue;
            if (pct == uint.MaxValue) return req;
            bool acts = model switch
            {
                Model.Alias => true,
                Model.IndependentActs => word == IndepWord,
                Model.LegacyActs => word == ControlWord,
                _ => false,
            };
            if (model == Model.Ignores) return state;
            if (model == Model.Alias)
            {
                // One variable, two views: writing either moves both.
                BinaryPrimitives.WriteUInt32LittleEndian(FakeDriver.State[IndepWord].AsSpan(4), pct);
                FakeDriver.State[ControlWord][RailsLayout.ControlBoostByte] = (byte)pct;
            }
            if (acts) BinaryPrimitives.WriteUInt32LittleEndian(FakeDriver.State[StatusWord].AsSpan(NvvddMax), baseMax + pct * UvPerPercent);
            return model == Model.Alias ? (byte[])FakeDriver.State[word].Clone() : req;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo, Slot.ClientVoltRailsGetControl],
            [Slot.VoltVoltRailsSetControl, Slot.ClientVoltRailsSetControl]));
        _hw = hw;
        return new VoltBoost(hw, _journal, _sentinel, _host);
    }

    private NvClient _hw = null!;

    [Fact]
    public void On_kind_3_rails_the_original_s_rule_picks_the_legacy_byte_even_though_the_independent_getter_answers()
    {
        Setup(Model.Alias);
        var o = BoostReader.Observe(_hw, Gpu);
        Assert.Equal(0u, o.IndependentPercent);
        Assert.Equal(3u, o.NvvddKind);
        Assert.Equal(BoostPath.Legacy, o.DefaultRule(railsWritable: true));
    }

    [Theory]
    [InlineData(Model.Alias, BoostPath.Independent, BoostVerdict.Alias)]
    [InlineData(Model.Alias, BoostPath.Legacy, BoostVerdict.Alias)]
    [InlineData(Model.IndependentActs, BoostPath.Independent, BoostVerdict.Effective)]
    [InlineData(Model.LegacyActs, BoostPath.Independent, BoostVerdict.StoredNoEffect)]
    [InlineData(Model.LegacyActs, BoostPath.Legacy, BoostVerdict.Effective)]
    public void One_small_step_tells_the_driver_models_apart(Model model, BoostPath path, BoostVerdict verdict)
    {
        var vb = Setup(model);
        var r = vb.Apply(Gpu, path, 1);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(verdict, r.Verdict);
    }

    [Fact]
    public void A_driver_that_ignores_the_write_fails_the_transaction_and_owes_nothing()
    {
        var vb = Setup(Model.Ignores);
        var r = vb.Apply(Gpu, BoostPath.Independent, 1);
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.NotNeeded, r.Tx.Restore);
        Assert.StartsWith("released", r.Ownership);
        Assert.Null(r.Verdict);
    }

    [Fact]
    public void A_boost_that_lifts_MAX_past_1_15_V_stays_applied_and_shows_where_MAX_went()
    {
        // NVVDD MAX 1.140 V; +20 % lifts it by 20 mV to 1.160 V, above the 1.15 V standard ceiling.
        var vb = Setup(Model.IndependentActs, baseMax: 1_140_000);
        var r = vb.Apply(Gpu, BoostPath.Independent, 20);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.True(r.Succeeded);
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(FakeDriver.State[IndepWord].AsSpan(4)));
        Assert.Equal(1_160_000u, BinaryPrimitives.ReadUInt32LittleEndian(FakeDriver.State[StatusWord].AsSpan(NvvddMax)));
        Assert.Equal(1_160_000u, r.After!.Rails.Single(x => x.Rail == Rail.Nvvdd).MaxUv);
        Assert.NotNull(_journal.Load());
    }

    [Fact]
    public void Back_to_zero_releases_and_more_than_100_is_refused()
    {
        var vb = Setup(Model.IndependentActs);
        Assert.StartsWith("held", vb.Apply(Gpu, BoostPath.Independent, 5).Ownership);
        Assert.StartsWith("released", vb.Apply(Gpu, BoostPath.Independent, 0).Ownership);
        Assert.Equal(TxOutcome.Aborted, vb.Apply(Gpu, BoostPath.Legacy, 101).Tx.Outcome);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void The_legacy_path_writes_only_byte_8_of_the_rail_control()
    {
        var vb = Setup(Model.LegacyActs);
        var before = (byte[])FakeDriver.State[ControlWord].Clone();
        var r = vb.Apply(Gpu, BoostPath.Legacy, 3);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        var after = FakeDriver.State[ControlWord];
        Assert.Equal(RailsLayout.ControlBoostByte, Transaction.FirstDifference(before, after, []));
        Assert.Null(Transaction.FirstDifference(before.AsSpan(9), after.AsSpan(9), []));
    }

    [Fact]
    public void The_equal_value_proof_calls_the_setter_and_finds_nothing_changed()
    {
        Setup(Model.IndependentActs);
        var p = BoostProof.EqualValue(_hw, Gpu, _sentinel, BoostPath.Independent);
        Assert.True(p.Written);
        Assert.True(p.Proven, p.FirstDifference);
        Assert.Equal(1, FakeDriver.Sets);
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void The_equal_value_proof_reports_a_driver_that_moves_something()
    {
        Setup(Model.IndependentActs);
        FakeDriver.OnSet = () => FakeDriver.State[StatusWord][RailsLayout.StatusBase + 0x20] ^= 1;
        var p = BoostProof.EqualValue(_hw, Gpu, _sentinel, BoostPath.Independent);
        Assert.False(p.Proven);
        Assert.Contains("rails status", p.FirstDifference);
        Assert.True(_sentinel.IsBlocked(out _));   // the window stays marked: something moved that nobody owns
    }

    [Fact]
    public void A_boost_write_that_nudges_a_rail_offset_is_reported_not_hidden()
    {
        var vb = Setup(Model.IndependentActs);
        FakeDriver.OnSet = () => FakeDriver.State[ControlWord][RailsLayout.ControlBase + 4] ^= 1;   // NVVDD REL offset
        var r = vb.Apply(Gpu, BoostPath.Independent, 1);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Contains("outside the boost byte", r.SideEffect);
        Assert.False(r.Succeeded);
    }

    [Theory]
    [InlineData("independent")]
    [InlineData("legacy")]
    public void The_registry_entry_writes_and_recovers_through_the_bare_writer(string key)
    {
        Setup(Model.Alias);
        var writer = new OwnedWriter(_hw, _journal, _sentinel, _host);
        var (cell, plan) = VoltBoost.Prepare(_hw, Gpu, key, "2");
        Assert.Equal($"boost/{key}", cell.Id);
        Assert.Equal(TxOutcome.Applied, writer.Apply(Gpu, cell, plan!).Tx.Outcome);
        var item = _journal.Load()!.Items.Single();
        var (rc, rp) = VoltBoost.Prepare(_hw, Gpu, item.Key, item.Original);
        Assert.StartsWith("released", writer.Apply(Gpu, rc, rp!).Ownership);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void The_registry_entry_has_no_plan_outside_0_to_100()
    {
        Setup(Model.Alias);
        Assert.Null(VoltBoost.Prepare(_hw, Gpu, "legacy", "101").Plan);
        Assert.Null(VoltBoost.Prepare(_hw, Gpu, "independent", "-1").Plan);
        Assert.Throws<ArgumentException>(() => VoltBoost.Prepare(_hw, Gpu, "both", "0"));
    }
}
