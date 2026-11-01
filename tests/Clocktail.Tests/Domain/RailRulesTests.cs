using Clocktail.Domain;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class RailRulesTests
{
    private static RailRecord Nvvdd(bool devices = true) => RailsFixture.Snapshot(devices).Get(Rail.Nvvdd)!;
    private static RailRecord Msvdd() => RailsFixture.Snapshot().Get(Rail.Msvdd)!;

    private static Mutation.Apply Ok(Mutation m)
    {
        if (m is Mutation.Abort a) Assert.Fail(a.Reason);
        return Assert.IsType<Mutation.Apply>(m);
    }
    private static string Refused(Mutation m) => Assert.IsType<Mutation.Abort>(m).Reason;

    [Fact]
    public void The_bounds_are_the_reported_domain_and_nothing_narrower()
    {
        Assert.Equal(RailsFixture.DomainMax, RailRules.Ceiling(Nvvdd()));       // domain max, no 1.15 V standard ceiling
        Assert.Equal(uint.MaxValue, RailRules.Ceiling(Nvvdd(false)));           // no domain table: what the driver can hold
        Assert.Equal(RailsFixture.DomainMin, RailRules.Floor(Nvvdd()));
        Assert.Equal(0u, RailRules.Floor(Nvvdd(false)));
        Assert.Equal(RailsFixture.DomainStep, RailRules.Step(Nvvdd()));
        Assert.Equal(0u, RailRules.Step(Nvvdd(false)));
    }

    [Fact]
    public void Unchanged_request_yields_no_edits()
    {
        var m = Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, null)));
        Assert.Empty(m.Edits);
        m = Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, RailsFixture.MsvddRelOffset, null, null)));
        Assert.Empty(m.Edits);
    }

    [Fact]
    public void A_raise_within_the_ceiling_becomes_edits_at_the_right_offsets()
    {
        var rail = Nvvdd();
        var m = Ok(RailRules.Plan(rail, new RailRules.Request(null, 50_000, 50_000, null)));   // 1.07 → 1.12 V
        Assert.Equal(2, m.Edits.Count);
        Assert.Contains(m.Edits, e => e.Offset == rail.ControlFieldOffset(RailSlot.Rel) && BitConverter.ToInt32(e.Value) == 50_000);
        Assert.Contains(m.Edits, e => e.Offset == rail.ControlFieldOffset(RailSlot.Alt) && BitConverter.ToInt32(e.Value) == 50_000);
    }

    [Fact]
    public void A_raise_goes_up_to_the_reported_domain_maximum_and_no_further()
    {
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, 100_000, 100_000, null)));    // 1.17 V: past the old 1.15 V ceiling
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, 125_000, 125_000, null)));    // 1.195 V, the last step under 1.2 V
        Assert.Contains("REL would evaluate to 1201.25 mV, above the reported device maximum of 1200 mV",
            Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, 131_250, null, null))));   // one step more
    }

    [Fact]
    public void Every_slot_is_held_to_the_domain_both_ways_ov_included()
    {
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, 100_000)));             // OV 1.2 V
        Assert.Contains("OV would evaluate to 1300 mV, above", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, 200_000))));
        Assert.Contains("below", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, null, null, -600_000))));
        Assert.Contains("VMIN would evaluate", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(600_000, null, null, null))));   // 1.3 V
    }

    [Fact]
    public void A_request_off_the_step_grid_is_rounded_to_the_nearest_step_before_it_is_written()
    {
        var rail = Nvvdd();
        var m = Ok(RailRules.Plan(rail, new RailRules.Request(null, 10_000, null, null)));     // 1.6 steps of 6.25 mV
        Assert.Equal(12_500, BitConverter.ToInt32(Assert.Single(m.Edits).Value));
        m = Ok(RailRules.Plan(rail, new RailRules.Request(null, 9_000, null, null)));          // 1.44 steps
        Assert.Equal(6_250, BitConverter.ToInt32(Assert.Single(m.Edits).Value));
        Assert.Empty(Ok(RailRules.Plan(rail, new RailRules.Request(null, 3_000, null, null))).Edits);   // under half a step: no change
        // Counted from where the slot is now: MSVDD REL sits at -50 mV, +60 mV asked is 9.6 steps, 10 written.
        m = Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, 10_000, null, null)));
        Assert.Equal(12_500, BitConverter.ToInt32(Assert.Single(m.Edits).Value));
        // No domain table, no grid: written as asked.
        m = Ok(RailRules.Plan(Nvvdd(false), new RailRules.Request(null, 10_000, null, null)));
        Assert.Equal(10_000, BitConverter.ToInt32(Assert.Single(m.Edits).Value));
    }

    [Fact]
    public void Going_back_to_the_origin_is_exact_and_not_judged()
    {
        var rail = Nvvdd();
        var origin = new RailOffsets(RelUv: 3_000, AltUv: 0, OvUv: 200_000, VminUv: 0);   // off the grid, OV past the domain max
        var m = Ok(RailRules.Plan(rail, new RailRules.Request(0, 3_000, 0, 200_000), origin));
        Assert.Contains(m.Edits, e => e.Offset == rail.ControlFieldOffset(RailSlot.Rel) && BitConverter.ToInt32(e.Value) == 3_000);
        Assert.Contains(m.Edits, e => e.Offset == rail.ControlFieldOffset(RailSlot.Ov) && BitConverter.ToInt32(e.Value) == 200_000);
    }

    [Fact]
    public void A_decrease_is_not_ceiling_checked_but_is_floor_checked()
    {
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(null, -100_000, -100_000, null)));
        Assert.Contains("below the reported device minimum", Refused(RailRules.Plan(Nvvdd(), new RailRules.Request(null, -500_000, null, null))));
    }

    [Fact]
    public void Cross_slot_voltage_consistency_is_decided_by_the_driver()
    {
        // VMIN eval 0.7 V + 0.4 V = 1.1 V > REL 1.07 V
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(400_000, null, null, null)));
        Ok(RailRules.Plan(Nvvdd(), new RailRules.Request(300_000, null, null, null)));   // 1.0 V
    }

    [Fact]
    public void An_absent_alt_cannot_be_changed_but_does_not_gate_rel()
    {
        Assert.Contains("ALT/OP", Refused(RailRules.Plan(Msvdd(), new RailRules.Request(null, null, 10_000, null))));
        Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, 10_000, null, null)));
        Ok(RailRules.Plan(Msvdd(), new RailRules.Request(null, -25_000, null, null)));   // the usual 5090 move
    }

    [Fact]
    public void Unwritable_rails_abort_with_their_reason()
    {
        var rail = RailsFixture.Snapshot(setAvailable: false).Get(Rail.Nvvdd)!;
        Assert.Contains("not writable", Refused(RailRules.Plan(rail, new RailRules.Request(null, 0, null, null))));
    }


    [Fact]
    public void Rules_and_transaction_compose_end_to_end_on_the_fake_driver()
    {
        RailsFixture.SeedDriver();
        var api = FakeDriver.ApiFor([Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo], [Slot.VoltVoltRailsSetControl]);
        var hw = new NvClient(api);
        var gpu = new GpuSession(1, "fake");
        var snap = RailsReader.Read(hw, gpu);
        var r = new Transaction(hw).Run(gpu, new TransactionSpec(Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsSetControl),
            _ => RailRules.Plan(snap.Get(Rail.Msvdd)!, new RailRules.Request(null, -25_000, null, null)));
        Assert.Equal(TxOutcome.Applied, r.Outcome);
        Assert.Equal(-25_000, RailsReader.Read(hw, gpu).Get(Rail.Msvdd)!.Offsets.RelUv);
    }
}
