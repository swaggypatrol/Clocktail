using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>F03: the zero-RPM band (Control +0x64 stop, +0x68 start) as the 5090 FE holds it, 43 / 50 °C on policies 0
/// and 1: only an earlier start is written, a band at least 3 °C wide, and the backstops are not touched.</summary>
[Collection("fake-driver")]
public class FanStopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private static readonly GpuSession Gpu = new(1, "fake");

    public FanStopTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private OwnedWriteResult Write(NvClient hw, string key, string value)
    {
        var p = WriteRegistry.Find(FanStopWrites.Kind)!.Prepare(hw, Gpu, key, value, WriteOptions.None);
        Assert.Null(p.CannotWrite);
        return new OwnedWriter(hw, _journal, _sentinel, _host).Apply(Gpu, p.Cell, p.Plan!);
    }

    private static int Word(int policy, int at) =>
        BinaryPrimitives.ReadInt32LittleEndian(FanPolicyFixture.Stored.AsSpan(FanPolicyLayout.ControlEntry(policy) + at));

    [Fact]
    public void An_earlier_start_lands_on_its_policy_alone_and_stock_releases_it()
    {
        var hw = FanPolicyFixture.Install();
        Assert.Equal("43:50", FanStopWrites.Format(FanStopWrites.Read(FanPolicyFixture.Stored, 0)));
        var r = Write(hw, "0", "38:45");
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal((38 * 256, 45 * 256), (Word(0, FanStopWrites.StopAt), Word(0, FanStopWrites.StartAt)));
        Assert.Equal(43 * 256, Word(1, FanStopWrites.StopAt));                  // the other policy untouched
        Assert.Equal("50:20:1100,83:50:1775,88:100:2875", FanPolicyFixture.CurveOf(0));   // and the curve
        Assert.Equal("43:50", Assert.Single(_journal.Load()!.Items).Original);
        Assert.StartsWith("released", Write(hw, "0", "43:50").Ownership);
    }

    [Fact]
    public void A_later_start_a_narrow_band_and_a_backstop_are_refused()
    {
        var hw = FanPolicyFixture.Install();
        Assert.Contains("no later than the curve's first point", Write(hw, "0", "48:55").Tx.Detail);
        Assert.Contains("at least 3 °C wide", Write(hw, "0", "44:45").Tx.Detail);
        Assert.Contains("20 °C at the lowest", Write(hw, "0", "15:45").Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.NotNull(WriteRegistry.Find(FanStopWrites.Kind)!.Prepare(hw, Gpu, "4", "40:45", WriteOptions.None).CannotWrite);   // the firmware's backstop
    }

    [Fact]
    public void The_setter_rule_refuses_a_band_that_is_not_one()
    {
        FanPolicyFixture.Install();
        var b = (byte[])FanPolicyFixture.Stored.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(FanPolicyLayout.Mask), 1);
        Assert.Null(WritePolicySet.Default.Check(Slot.FanPolicySetControl, b));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(FanPolicyLayout.ControlEntry(0) + FanStopWrites.StopAt), 50 * 256);   // stop at start
        Assert.Contains("is not a band", WritePolicySet.Default.Check(Slot.FanPolicySetControl, b));
    }
}
