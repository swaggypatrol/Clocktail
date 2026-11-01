using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The rail write path against a fake driver that behaves like the RTX 5090 did live:
/// evaluated = base + offset, MAX = min(REL, ALT, OV), domain 445–1280 mV in 5 mV steps.
/// </summary>
[Collection("fake-driver")]
public class RailWritesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    /// <summary>The driver's own bases per rail: REL, ALT/OP, OV, VMIN (µV).</summary>
    private static readonly uint[][] Bases = [[1_095_000, 1_115_000, 1_200_000, 800_000], [1_095_000, 1_115_000, 1_200_000, 825_000]];
    /// <summary>Added to the derived MAX of every rail: a driver whose MAX is not what the offsets predict.</summary>
    private uint _maxBias;
    /// <summary>MSVDD's REL/ALT/OV bases follow NVVDD's evaluated values: the coupling the 5090's numbers allow.</summary>
    private bool _coupled;
    private static readonly uint StatusWord = Slots.Info(Slot.VoltVoltRailsGetStatus).Shape!.Value.Word;
    private static readonly uint ControlWord = Slots.Info(Slot.VoltVoltRailsGetControl).Shape!.Value.Word;

    public RailWritesTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    private static int Field(Rail r, RailSlot s) => RailsLayout.ControlBase + (int)r * RailsLayout.ControlEntry + 4 + 4 * (int)s;

    private (RailWrites rails, NvClient hw) Setup()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.VoltVoltRailsGetInfo);
        W(info, 4, 3u);
        for (int bit = 0; bit < 2; bit++)
        {
            int ib = RailsLayout.InfoBase + bit * RailsLayout.InfoEntry;
            byte domain = (byte)(bit == 0 ? 0 : 2);
            W(info, ib, 3u); W(info, ib + 4, bit == 0 ? 1u : 3u); info[ib + 8] = domain; W(info, ib + 0x18, 1u << domain);
        }
        var control = NvClient.NewBuffer(Slot.VoltVoltRailsGetControl);
        W(control, 4, 3u);
        W(control, RailsLayout.ControlBase, 3u);
        W(control, RailsLayout.ControlBase + RailsLayout.ControlEntry, 3u);
        W(control, Field(Rail.Msvdd, RailSlot.Rel), -50_000);
        var devices = NvClient.NewBuffer(Slot.VoltVoltDevicesGetInfo);
        foreach (var (i, type) in new[] { (0, 1), (2, 3) })
        {
            int d = RailsLayout.DevicesBase + i * RailsLayout.DevicesEntry;
            devices[d + 4] = (byte)type; devices[d + 6] = 1;
            W(devices, d + 8, 445_000u); W(devices, d + 12, 1_280_000u); W(devices, d + 16, 5_000u);
        }
        FakeDriver.State[BitConverter.ToUInt32(info)] = info;
        FakeDriver.State[ControlWord] = control;
        FakeDriver.State[BitConverter.ToUInt32(devices)] = devices;
        FakeDriver.Seed(Slot.VoltVoltRailsGetStatus);
        FakeDriver.QueryResponder = q => BitConverter.ToUInt32(q) == StatusWord ? Status(FakeDriver.State[ControlWord]) : null;

        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo],
            [Slot.VoltVoltRailsSetControl]));
        return (new RailWrites(hw, _journal, _sentinel, _host), hw);
    }

    /// <summary>Status derived from the control structure the way the 5090 reported it.</summary>
    private byte[] Status(byte[] control)
    {
        var s = NvClient.NewBuffer(Slot.VoltVoltRailsGetStatus);
        W(s, 4, 3u);
        var nv = RailWrites.OffsetsIn(control, Rail.Nvvdd)!.Value;
        for (int n = 0; n < 2; n++)
        {
            var o = RailWrites.OffsetsIn(control, (Rail)n)!.Value;
            var b = n == 1 && _coupled
                ? [Bases[0][0] + nv.RelUv, Bases[0][1] + nv.AltUv, Bases[0][2] + nv.OvUv, Bases[1][3]]
                : Bases[n].Select(x => (long)x).ToArray();
            uint rel = (uint)(b[0] + o.RelUv), alt = (uint)(b[1] + o.AltUv), ov = (uint)(b[2] + o.OvUv), vmin = (uint)(b[3] + o.VminUv);
            int sb = RailsLayout.StatusBase + n * RailsLayout.StatusEntry;
            W(s, sb + 4, rel); W(s, sb + 8, alt); W(s, sb + 12, ov); W(s, sb + 16, Math.Min(rel, Math.Min(alt, ov)) + _maxBias); W(s, sb + 20, vmin);
        }
        return s;
    }

    private static RailRules.Request Rel(int uv) => new(null, uv, null, null);
    private static int ControlField(Rail r, RailSlot s) => BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.State[ControlWord].AsSpan(Field(r, s)));

    [Fact]
    public void The_canonical_encoding_round_trips_and_reads_from_the_control_structure_alone()
    {
        var o = new RailOffsets(-50_000, 0, 5_000, -10_000);
        Assert.Equal("rel=-50000,alt=0,ov=5000,vmin=-10000", RailWrites.Encode(o));
        Assert.Equal(o, RailWrites.Decode(RailWrites.Encode(o)));
        Setup();
        Assert.Equal("rel=-50000,alt=0,ov=0,vmin=0", RailWrites.Cell(Rail.Msvdd, 3).StateIn(FakeDriver.State[ControlWord]));
        var nvvddOnly = (byte[])FakeDriver.State[ControlWord].Clone(); W(nvvddOnly, 4, 1u);
        Assert.Null(RailWrites.Cell(Rail.Msvdd, 1).StateIn(nvvddOnly));
    }

    [Fact]
    public void Equal_value_is_no_change_with_no_set_call_and_nothing_held()
    {
        var (rails, _) = Setup();
        var r = rails.Apply(_gpu, Rail.Msvdd, Rel(-50_000));
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.True(r.SentinelCleared);
        Assert.False(_sentinel.IsBlocked(out _));
        Assert.StartsWith("nothing held", r.Ownership);
        Assert.Equal(r.LimitsBefore, r.LimitsAfter);
    }

    [Fact]
    public void Smallest_step_down_lands_moves_MAX_and_is_released_when_put_back()
    {
        var (rails, _) = Setup();
        var down = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.True(down.Succeeded, down.Tx.ToString());
        Assert.Equal(TxOutcome.Applied, down.Tx.Outcome);
        Assert.Equal(-5_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        Assert.Equal(1_090_000u, down.LimitsAfter!.Value.MaxUv);
        Assert.Empty(down.Surprises);
        Assert.Null(down.Violation);
        Assert.StartsWith("held: original rel=0,alt=0,ov=0,vmin=0", down.Ownership);
        Assert.Contains(down.Tx.Log, l => l.StartsWith("journal: rails/nvvdd claimed"));
        Assert.Equal(-50_000, ControlField(Rail.Msvdd, RailSlot.Rel));   // the other rail is untouched

        var back = rails.Apply(_gpu, Rail.Nvvdd, Rel(0));
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Offsets_that_moved_between_the_status_read_and_the_write_snapshot_abort_the_plan()
    {
        var (rails, _) = Setup();
        int controlReads = 0;
        FakeDriver.QueryResponder = q =>
        {
            uint word = BitConverter.ToUInt32(q);
            if (word == StatusWord) return Status(FakeDriver.State[ControlWord]);
            // A concurrent tool lowers MSVDD between the reader's control read and the transaction's.
            if (word == ControlWord && ++controlReads == 2) W(FakeDriver.State[ControlWord], Field(Rail.Msvdd, RailSlot.Rel), -60_000);
            return null;
        };
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("msvdd offsets changed between the status read and the write snapshot", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_side_effect_on_the_other_rail_is_reported_because_the_restore_only_puts_back_what_this_write_changed()
    {
        var (rails, _) = Setup();
        FakeDriver.Mode = FakeDriver.SetMode.TouchNeighbour;
        FakeDriver.Field = Field(Rail.Msvdd, RailSlot.Rel);
        FakeDriver.Neighbour = -RailsLayout.ControlEntry;   // lands on NVVDD's REL
        FakeDriver.MisbehaveOnce = true;
        var r = rails.Apply(_gpu, Rail.Msvdd, Rel(-55_000));
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Equal(-50_000, ControlField(Rail.Msvdd, RailSlot.Rel));
        Assert.StartsWith("released", r.Ownership);          // MSVDD itself is back and owes nothing
        Assert.Equal(1, ControlField(Rail.Nvvdd, RailSlot.Rel));   // the side effect stays: field-wise restore
        Assert.Contains("nvvdd rel=0,alt=0,ov=0,vmin=0 → rel=1,alt=0,ov=0,vmin=0", r.Violation);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Another_rail_s_limits_moving_after_a_clean_write_is_a_finding()
    {
        var (rails, _) = Setup();
        // A driver that couples MSVDD's REL base to NVVDD's REL offset.
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != StatusWord) return null;
            var s = Status(FakeDriver.State[ControlWord]);
            int sb = RailsLayout.StatusBase + RailsLayout.StatusEntry;
            W(s, sb + 4, (uint)(1_045_000 + ControlField(Rail.Nvvdd, RailSlot.Rel)));
            return s;
        };
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.True(r.Succeeded);
        Assert.Contains(r.Surprises, n => n.StartsWith("msvdd limits moved although it was not written"));
    }

    [Fact]
    public void The_legacy_boost_byte_is_not_this_path_s_to_touch()
    {
        var (rails, _) = Setup();
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.State[ControlWord][RailsLayout.ControlBoostByte]);
        var edits = Transaction.FirstDifference(r.Tx.Before, r.Tx.After!, []);
        Assert.Equal(Field(Rail.Nvvdd, RailSlot.Rel), edits);
    }

    [Fact]
    public void Give_back_writes_the_recorded_original_from_a_fresh_snapshot()
    {
        var (rails, hw) = Setup();
        Assert.Equal(TxOutcome.Applied, rails.Apply(_gpu, Rail.Msvdd, Rel(-60_000)).Tx.Outcome);
        var original = _journal.Load()!.Items.Single().Original;
        Assert.Equal("rel=-50000,alt=0,ov=0,vmin=0", original);

        var (cell, plan) = RailWrites.GiveBack(hw, _gpu, Rail.Msvdd, original);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Released, w.Hold);
        Assert.Equal(-50_000, ControlField(Rail.Msvdd, RailSlot.Rel));
    }

    [Fact]
    public void Prepare_takes_a_partial_value_for_a_write_and_the_full_original_for_recovery()
    {
        Assert.Equal(new RailRules.Request(null, -5_000, null, null), RailWrites.ParseRequest("rel=-5000"));
        Assert.Equal(new RailRules.Request(0, -50_000, 0, 0), RailWrites.ParseRequest("rel=-50000,alt=0,ov=0,vmin=0"));
        Assert.Throws<FormatException>(() => RailWrites.ParseRequest("max=5000"));
        Assert.Throws<FormatException>(() => RailWrites.ParseRequest(""));

        var (rails, hw) = Setup();
        Assert.Throws<ArgumentException>(() => RailWrites.Prepare(hw, _gpu, "fbvdd", "rel=0"));
        var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", "rel=-5000");
        Assert.Equal("rails/nvvdd", cell.Id);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(-5_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        var (cell2, plan2) = RailWrites.Prepare(hw, _gpu, "nvvdd", _journal.Load()!.Items.Single().Original);
        Assert.Equal(Hold.Released, new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell2, plan2).Hold);
    }

    private static RailRules.Request Ov(int uv) => new(null, null, null, uv);

    [Fact]
    public void A_lowered_OV_can_be_put_back_to_stock()
    {
        var (rails, _) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Ov(-5_000)).Succeeded);
        var back = rails.Apply(_gpu, Rail.Nvvdd, Ov(0));   // 1.2 V again: the journal's original, a return
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Null(back.Violation);
        Assert.StartsWith("released", back.Ownership);
        Assert.Equal(0, ControlField(Rail.Nvvdd, RailSlot.Ov));
    }

    [Fact]
    public void Recovery_gives_a_lowered_OV_back()
    {
        var (rails, hw) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Ov(-5_000)).Succeeded);
        var (cell, plan) = RailWrites.GiveBack(hw, _gpu, Rail.Nvvdd, _journal.Load()!.Items.Single().Original);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Released, w.Hold);
        Assert.Empty(_journal.Load()?.Items ?? []);
    }

    [Fact]
    public void Without_the_tracking_signature_NVVDD_is_judged_on_its_own()
    {
        var (rails, _) = Setup();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000)).Succeeded);   // NVVDD REL eval now 1.090 ≠ MSVDD REL base 1.095
        Assert.True(rails.Apply(_gpu, Rail.Msvdd, new RailRules.Request(null, 50_000, 35_000, null)).Succeeded);
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, 55_000, 35_000, null)).Succeeded);
    }

    [Fact]
    public void Every_write_can_be_undone_through_Prepare_with_the_journal_original()
    {
        var (rails, hw) = Setup();
        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        foreach (var value in new[] { "ov=-5000", "rel=-5000,ov=-305000", "alt=-290000", "vmin=-5000", "rel=50000" })
        {
            var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", value);
            Assert.Equal(TxOutcome.Applied, writer.Apply(_gpu, cell, plan).Tx.Outcome);
            var original = _journal.Load()!.Items.Single().Original;
            (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", original, original);
            var back = writer.Apply(_gpu, cell, plan);
            Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
            Assert.Empty(_journal.Load()?.Items ?? []);
        }
    }

    [Fact]
    public void The_whitelist_accepts_the_read_modify_write_buffer_and_refuses_a_foreign_mask()
    {
        Setup();
        var buf = (byte[])FakeDriver.State[ControlWord].Clone();
        Assert.Null(WritePolicySet.Default.Check(Slot.VoltVoltRailsSetControl, buf));
        W(buf, 4, 2u);
        Assert.NotNull(WritePolicySet.Default.Check(Slot.VoltVoltRailsSetControl, buf));
    }

    // No software envelope: anything inside the domain the firmware reports (445..1280 mV here) reaches the driver,
    // past the old 1.15 V standard ceiling too, and goes back exactly through the journal's original.
    [Theory]
    [InlineData("rel=60000")]
    [InlineData("rel=140000")]
    [InlineData("ov=5000")]
    [InlineData("rel=-3000")]
    [InlineData("vmin=400000")]
    [InlineData("alt=-320000")]
    [InlineData("rel=100000,alt=100000,ov=80000,vmin=0")]
    public void Requests_inside_the_reported_domain_are_not_vetoed_by_tuning_policy(string value)
    {
        var (_, hw) = Setup();
        var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", value);
        var r = new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.True(FakeDriver.Sets > 0);
        var original = _journal.Load()!.Items.Single().Original;
        (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", original, original);
        Assert.Equal(TxOutcome.Applied, new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan).Tx.Outcome);
        Assert.Empty(_journal.Load()?.Items ?? []);
    }

    [Fact]
    public void Derived_voltage_and_inferred_coupling_do_not_override_driver_acceptance()
    {
        var (rails, _) = Setup();
        _coupled = true;
        _maxBias = 100_000;
        Assert.True(rails.Apply(_gpu, Rail.Msvdd, new RailRules.Request(null, 50_000, 35_000, null)).Succeeded);
        var r = rails.Apply(_gpu, Rail.Nvvdd, new RailRules.Request(null, 100_000, 100_000, null));
        Assert.True(r.Succeeded);
        Assert.Null(r.Violation);
        Assert.Equal(100_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
    }

    /// <summary>The firmware-reported domain maximum binds as its minimum does: a slot evaluating past 1280 mV here
    /// is refused before the driver.</summary>
    [Theory]
    [InlineData("ov=85000")]                 // 1.285 V
    [InlineData("rel=190000")]               // 1.285 V
    [InlineData("vmin=485000")]              // 1.285 V
    [InlineData("rel=-655000")]              // 440 mV, under the domain minimum
    public void A_slot_past_the_reported_domain_is_refused_before_the_driver(string value)
    {
        var (_, hw) = Setup();
        var (cell, plan) = RailWrites.Prepare(hw, _gpu, "nvvdd", value);
        var r = new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("the reported device", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
    }

    /// <summary>Off the 5 mV grid is rounded before the write, so it lands and reads back; the rounding is a finding.
    /// The original goes back exactly, off the grid or not: the hardware held it.</summary>
    [Fact]
    public void An_offset_off_the_step_grid_is_rounded_before_the_write_and_the_original_goes_back_exactly()
    {
        var (rails, hw) = Setup();
        var r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-3_000));
        Assert.True(r.Succeeded, r.Tx.ToString());
        Assert.Equal(-5_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        Assert.Contains("REL: -3 mV asked, -5 mV written (whole 5 mV steps)", r.Surprises);
        Assert.Equal(1, FakeDriver.Sets);                                     // one write, no write-then-roll-back
        Assert.StartsWith("released", rails.Apply(_gpu, Rail.Nvvdd, Rel(0)).Ownership);

        W(FakeDriver.State[ControlWord], Field(Rail.Nvvdd, RailSlot.Rel), 3_000);   // another tool left it off the grid
        r = rails.Apply(_gpu, Rail.Nvvdd, Rel(-5_000));                      // -8 mV from where it is: 2 steps
        Assert.True(r.Succeeded);
        Assert.Equal(-7_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
        var original = _journal.Load()!.Items.Single().Original;
        Assert.Equal("rel=3000,alt=0,ov=0,vmin=0", original);
        var (cell, plan) = RailWrites.GiveBack(hw, _gpu, Rail.Nvvdd, original);
        Assert.Equal(Hold.Released, new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell, plan).Hold);
        Assert.Equal(3_000, ControlField(Rail.Nvvdd, RailSlot.Rel));
    }

    [Fact]
    public void An_origin_is_for_recovery_not_a_ceiling_on_new_requests()
    {
        var (rails, _) = Setup();
        W(FakeDriver.State[ControlWord], Field(Rail.Nvvdd, RailSlot.Rel), 100_000);
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(75_000)).Succeeded);
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(105_000)).Succeeded);
        var back = rails.Apply(_gpu, Rail.Nvvdd, Rel(100_000));
        Assert.True(back.Succeeded);
        Assert.StartsWith("released", back.Ownership);
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(75_000)).Succeeded);
        _journal.Clear();
        Assert.True(rails.Apply(_gpu, Rail.Nvvdd, Rel(100_000)).Succeeded);
    }


}
