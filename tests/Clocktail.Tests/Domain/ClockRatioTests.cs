using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>The XBAR/core clock propagation ratio as read live on the 5090 (617.42, 2026-10-21): relation mask 0x3fff,
/// relation 0's ratio 0xe660 (0.8999) at +0x68, 14 relations 0x108 apart in a block past the 0x75c its word names.</summary>
[Collection("fake-driver")]
public class ClockRatioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private static readonly GpuSession Gpu = new(1, "fake");

    public ClockRatioTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static readonly uint ControlWord = 0x1075c;

    private static NvClient Install()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetInfo);
        W(info, 4, 0xf); W(info, 8, 0x3fff);
        var control = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetControl);
        W(control, 4, 0x3fff); W(control, 0x68, 0xe660);
        for (int i = 0; i < 9; i++) W(control, 0x16c + i * 0x108, 2);
        W(control, 0xab8, 0xccc0); W(control, 0xbc0, 0x20000); W(control, 0xcc8, 0x13330); W(control, 0xdd0, 0x18000);
        FakeDriver.State[U(info, 0)] = info;
        FakeDriver.State[ControlWord] = control;
        return new NvClient(FakeDriver.ApiFor([Slot.ClockClkPropTopRelsGetInfo, Slot.ClockClkPropTopRelsGetControl], [Slot.ClockClkPropTopRelsSetControl]),
            WritePolicySet.Default.WithEnabled(Slot.ClockClkPropTopRelsSetControl));
    }

    [Fact]
    public void The_ratio_is_read_in_the_originals_call_form()
    {
        var b = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetControl);
        Assert.Equal((0x2000, ControlWord), (b.Length, U(b, 0)));   // the word says 0x75c, the buffer is larger
        var r = ClockRatio.Read(Install(), Gpu);
        Assert.Equal((0x3fffu, 0xe660u), (r.Relations, r.Raw));
        Assert.Equal(0.8999, Math.Round(r.Ratio, 4));
    }

    [Fact]
    public void A_ratio_lands_at_plus_0x68_alone_and_the_stock_one_releases_it()
    {
        var hw = Install();
        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        var (cell, plan, cannot) = ClockRatio.Prepare(hw, Gpu, "msvdd", "0.92");
        Assert.Null(cannot);
        Assert.Equal(TxOutcome.Applied, writer.Apply(Gpu, cell, plan!).Tx.Outcome);
        Assert.Equal((uint)Math.Round(0.92 * 65536), U(FakeDriver.State[ControlWord], 0x68));
        Assert.Equal(0xccc0u, U(FakeDriver.State[ControlWord], 0xab8));   // the other relations untouched
        Assert.Equal("58976", Assert.Single(_journal.Load()!.Items).Original);

        var (cell2, back, _) = ClockRatio.Prepare(hw, Gpu, "msvdd", "58976");
        Assert.StartsWith("released", writer.Apply(Gpu, cell2, back!).Ownership);
        Assert.Equal(TxOutcome.Aborted, writer.Apply(Gpu, cell2, ClockRatio.Plan(0x11000)).Tx.Outcome);   // above 1.0
    }

    [Fact]
    public void The_rule_takes_the_block_as_read_and_refuses_the_structure_size_alone()
    {
        Install();
        var p = WritePolicySet.Default.WithEnabled(Slot.ClockClkPropTopRelsSetControl);
        var block = (byte[])FakeDriver.State[ControlWord].Clone();
        Assert.Null(p.Check(Slot.ClockClkPropTopRelsSetControl, block));
        Assert.NotNull(p.Check(Slot.ClockClkPropTopRelsSetControl, block.AsSpan(0, 0x75c).ToArray()));
        W(block, 0x68, 0);
        Assert.NotNull(p.Check(Slot.ClockClkPropTopRelsSetControl, block));
        Assert.Equal(PolicyState.Disabled, WritePolicySet.Default.Get(Slot.ClockClkPropTopRelsSetControl)!.State);
    }
}
