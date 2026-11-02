using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>A clock domain's voltage demand (--xbar-vdemand), on the 5090-shaped clock-domain fixture.</summary>
[Collection("fake-driver")]
public class ClockVdemandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private static readonly GpuSession Gpu = new(1, "fake");

    /// <summary>The 5090's rails: NVVDD kind 3 type 1, MSVDD kind 3 type 3.</summary>
    private static (uint Kind, uint Type)? Rails5090(int slot) => slot switch { 0 => (3u, 1u), 1 => (3u, 3u), _ => null };

    public ClockVdemandTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static ClockTopologyEntry Topo(ClockDomainId d) => ClockDomainsParser.Find(ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology()), (uint)d);

    private OwnedWriteResult Apply(ClockDomainId d, int uv)
    {
        var t = Topo(d);
        int slot = ClockVdemand.Slot(t, Rails5090);
        return new OwnedWriter(ClockDomainsFixture.Client(), _journal, _sentinel, _host).Apply(Gpu, ClockVdemand.Cell(d, t, slot), raw => ClockVdemand.Plan(t, slot, raw.ToArray(), uv));
    }

    private static int Demand(int index, int slot) =>
        BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.Current(Slot.ClockClkDomainsGetControl).AsSpan(ClockDomainsLayout.OffsetAt(index, 0xf) + 4 + 4 * slot));

    /// <summary>The probe on the 5090: graphics demands from NVVDD, XBAR/SYS/Video from MSVDD; a domain with
    /// no capability byte, or a rail that is not the expected one, has none.</summary>
    [Fact]
    public void The_probe_picks_the_rail_each_domain_demands_from()
    {
        Assert.Equal(0, ClockVdemand.Slot(Topo(ClockDomainId.Graphics), Rails5090));
        Assert.Equal(1, ClockVdemand.Slot(Topo(ClockDomainId.Xbar), Rails5090));
        Assert.Equal(1, ClockVdemand.Slot(Topo(ClockDomainId.Video), Rails5090));
        var none = ClockDomainsParser.ParseTopology(ClockDomainsFixture.Topology()).Single(t => t.DomainId == 0x14) with { Caps = 0 };
        Assert.Throws<InvalidDataException>(() => ClockVdemand.Slot(none, Rails5090));
        Assert.Throws<InvalidDataException>(() => ClockVdemand.Slot(Topo(ClockDomainId.Xbar), s => s == 1 ? (3u, 1u) : (3u, 1u)));
        Assert.Throws<InvalidDataException>(() => ClockVdemand.Slot(Topo(ClockDomainId.Xbar), s => s == 0 ? (3u, 1u) : null));
    }

    /// <summary>The core's demand (graphics, entry 0) lands in NVVDD's slot of its entry, and 0 releases it.</summary>
    [Fact]
    public void The_core_demand_lands_in_nvvdd_slot_of_the_graphics_entry()
    {
        ClockDomainsFixture.Seed();
        Assert.True(ClockVdemand.Writable(ClockDomainId.Graphics));
        var r = Apply(ClockDomainId.Graphics, -10_000);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(-10_000, Demand(0, 0));
        Assert.Equal(0, Demand(0, 1));
        Assert.Equal("0", _journal.Load()!.Items.Single(i => i.Kind == ClockVdemand.Kind && i.Key == "graphics").Original);
        Assert.StartsWith("released", Apply(ClockDomainId.Graphics, 0).Ownership);
    }

    /// <summary>+20 mV on XBAR lands in MSVDD's slot of XBAR's entry and nothing else moves; the XBAR clock offset
    /// written afterwards keeps it, and 0 puts it back and releases the record.</summary>
    [Fact]
    public void Xbar_demand_lands_in_its_msvdd_slot_and_the_clock_offset_keeps_it()
    {
        ClockDomainsFixture.Seed();
        var before = (byte[])FakeDriver.Current(Slot.ClockClkDomainsGetControl).Clone();
        var r = Apply(ClockDomainId.Xbar, 20_000);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(20_000, Demand(1, 1));
        Assert.Equal(0, Demand(1, 0));
        var after = FakeDriver.Current(Slot.ClockClkDomainsGetControl);
        int f = ClockDomainsLayout.OffsetAt(1, 0xf) + 8;
        Assert.Equal(f, Transaction.FirstDifference(before, after, []));
        Assert.Null(Transaction.FirstDifference(before.AsSpan(f + 4), after.AsSpan(f + 4), []));
        Assert.Equal("0", _journal.Load()!.Items.Single(i => i.Kind == ClockVdemand.Kind && i.Key == "xbar").Original);

        var co = new ClockDomainOffsets(ClockDomainsFixture.Client(), _journal, _sentinel, _host);
        Assert.Equal(TxOutcome.Applied, co.Apply(Gpu, ClockDomainId.Xbar, 400_000).Tx.Outcome);
        Assert.Equal(20_000, Demand(1, 1));
        Assert.Equal(400_000, ClockDomainsFixture.OffsetKhz(1));

        Assert.StartsWith("released", Apply(ClockDomainId.Xbar, 0).Ownership);
        Assert.Equal(400_000, ClockDomainsFixture.OffsetKhz(1));
        Assert.DoesNotContain(_journal.Load()!.Items, i => i.Kind == ClockVdemand.Kind);
    }

    [Fact]
    public void Outside_the_old_software_range_reaches_the_driver_and_reads_back()
    {
        ClockDomainsFixture.Seed();
        foreach (int uv in new[] { 50_001, -25_001, ClockVdemand.MinUv, ClockVdemand.MaxUv })
        {
            var r = Apply(ClockDomainId.Xbar, uv);
            Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
            Assert.Equal(uv, Demand(1, 1));
        }
        Assert.Equal(TxOutcome.NoChange, Apply(ClockDomainId.Sys, 0).Tx.Outcome);
        Assert.Equal(4, FakeDriver.Sets);
        Assert.StartsWith("released", Apply(ClockDomainId.Xbar, 0).Ownership);
        Assert.Empty(_journal.Load()?.Items ?? []);
    }

    /// <summary>A whole volt either way is a units slip (µV given as mV), refused before the driver; not a tuning range.</summary>
    [Fact]
    public void A_demand_past_a_volt_is_a_units_slip_and_aborts_before_the_driver()
    {
        ClockDomainsFixture.Seed();
        foreach (int uv in new[] { 1_000_001, -1_000_001, 20_000_000, int.MinValue, int.MaxValue })
        {
            var r = Apply(ClockDomainId.Xbar, uv);
            Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
            Assert.Contains("units or typing slip", r.Tx.Detail);
        }
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
    }

    /// <summary>The setter's rule holds both demand slots to ±1 V whoever builds the buffer: the same units check, no envelope.</summary>
    [Fact]
    public void The_hardware_layer_refuses_only_a_units_slip()
    {
        byte[] Buf(int slot, int uv)
        {
            var b = NvClient.NewBuffer(Slot.ClockClkDomainsSetControl);
            ClockDomainsParser.PrepareQuery(b, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ClockDomainsLayout.ControlAt(1)), 0xf);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ClockDomainsLayout.OffsetAt(1, 0xf) + 4 + 4 * slot), uv);
            return b;
        }
        string? Check(byte[] b) => WritePolicySet.Default.Check(Slot.ClockClkDomainsSetControl, b);
        Assert.Null(Check(Buf(1, 50_000)));
        Assert.Null(Check(Buf(0, -25_000)));
        Assert.Null(Check(Buf(1, 50_001)));
        Assert.Null(Check(Buf(0, -25_001)));
        Assert.Null(Check(Buf(1, 1_000_000)));
        Assert.Null(Check(Buf(0, -1_000_000)));
        Assert.Contains("voltage demand", Check(Buf(1, 1_000_001)));
        Assert.Contains("voltage demand", Check(Buf(0, int.MinValue)));
        Assert.Contains("voltage demand", Check(Buf(1, int.MaxValue)));
    }

    [Theory]
    [InlineData(400_000, 0)]
    [InlineData(500_000, 0)]
    [InlineData(500_000, -50_000)]
    [InlineData(500_000, 100_000)]
    public void High_xbar_offsets_and_voltage_demand_are_independent(int khz, int uv)
    {
        ClockDomainsFixture.Seed();
        var clocks = new ClockDomainOffsets(ClockDomainsFixture.Client(), _journal, _sentinel, _host);
        Assert.Equal(TxOutcome.Applied, clocks.Apply(Gpu, ClockDomainId.Xbar, khz).Tx.Outcome);
        Assert.True(Apply(ClockDomainId.Xbar, uv).Tx.Succeeded);
        Assert.Equal(khz, ClockDomainsFixture.OffsetKhz(1));
        Assert.Equal(uv, Demand(1, 1));
    }

    [Theory]
    [InlineData((int)FakeDriver.SetMode.RefuseUntouched)]
    [InlineData((int)FakeDriver.SetMode.ClampField)]
    public void Driver_rejection_or_silent_clamping_is_never_reported_as_success(int mode)
    {
        ClockDomainsFixture.Seed();
        FakeDriver.Mode = (FakeDriver.SetMode)mode;
        FakeDriver.Field = ClockDomainsLayout.OffsetAt(1, 0xf) + 8;
        var result = Apply(ClockDomainId.Xbar, 100_000);
        Assert.False(result.Tx.Succeeded);
        Assert.Equal(0, Demand(1, 1));
    }
}
