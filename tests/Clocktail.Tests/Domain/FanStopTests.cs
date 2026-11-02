using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>F03: the zero-RPM band (Control +0x64 stop, +0x68 start) as the 5090 FE holds it, 43 / 50 °C on policies 0
/// and 1: any band is written that is one (stop under start, within 0..120 °C), and the backstops are not touched.</summary>
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
    public void Later_starts_narrow_bands_and_low_stops_reach_the_driver_but_uneditable_policies_do_not()
    {
        var hw = FanPolicyFixture.Install();
        Assert.Equal(TxOutcome.Applied, Write(hw, "0", "48:55").Tx.Outcome);
        Assert.Equal(TxOutcome.Applied, Write(hw, "0", "44:45").Tx.Outcome);
        Assert.Equal(TxOutcome.Applied, Write(hw, "0", "15:45").Tx.Outcome);
        Assert.Equal(3, FakeDriver.Sets);
        Assert.NotNull(WriteRegistry.Find(FanStopWrites.Kind)!.Prepare(hw, Gpu, "4", "40:45", WriteOptions.None).CannotWrite);   // the firmware's backstop
    }

    /// <summary>Structure, not policy: stop under start, both within 0..120 °C, refused before the driver.</summary>
    [Theory]
    [InlineData("50:50", "stop must be under start")]
    [InlineData("60:45", "stop must be under start")]
    [InlineData("-5:45", "outside 0..120")]
    [InlineData("40:130", "outside 0..120")]
    [InlineData("500:900", "outside 0..120")]
    public void A_band_that_is_not_one_is_refused_before_the_driver(string band, string why)
    {
        var hw = FanPolicyFixture.Install();
        var r = Write(hw, "0", band);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains(why, r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(TxOutcome.Applied, Write(hw, "0", "0:120").Tx.Outcome);   // the bounds themselves are a band
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
