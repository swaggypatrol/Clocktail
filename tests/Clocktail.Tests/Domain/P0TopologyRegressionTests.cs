using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Rig = Clocktail.Tests.Domain.EffectiveP0Tests.Rig;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class P0TopologyRegressionTests
{
    private static void UnsupportedOwnershipTopology(string topology)
    {
        var info = FakeDriver.State[FakeVf.InfoWord];
        var control = FakeDriver.State[FakeVf.ControlWord];
        if (topology == "mask")
        {
            // All 127 Core entries are readable; the advertised non-Core tail has only 131 entries.
            VfLayout.WriteMask(info, VfLayout.FirstPoints(131));
            VfLayout.WriteMask(control, VfLayout.FirstPoints(131));
        }
        else
        {
            // Keep a known mask and 127 readable Core records, but with a gap in the Core indices.
            info[VfLayout.Info(50) + 4] = 0;
            Rig.I(info, VfLayout.Info(128), 0);
            info[VfLayout.Info(128) + 4] = 1;
            Rig.I(control, VfLayout.Control(128), 0);
        }
    }

    public static IEnumerable<object[]> Requests() =>
        from path in new[] { "Apply", "OpClock", "OpWrite" }
        from topology in new[] { "mask", "sequence" }
        from offset in new[] { 90_000, 0 }
        select new object[] { path, topology, offset };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Readable_budget_is_not_downgraded_when_ownership_topology_is_unsupported(string path, string topology, int offset)
    {
        using var r = new Rig(false);
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0xc, 240_000);
        r.ExternalCurve(offset);
        UnsupportedOwnershipTopology(topology);
        var originalControl = (byte[])FakeDriver.State[FakeVf.ControlWord].Clone();
        var known = ClockOffsets.Curve(r.Hw, r.Session)!;
        Assert.NotNull(known.Offsets);
        Assert.Equal(127, known.Offsets.Count);
        var prepared = ClockOffsets.CurveForWrite(r.Hw, r.Session);
        bool ok;
        string? reason;
        if (path == "Apply")
        {
            var result = new ClockOffsets(r.Hw, r.Journal, r.Sentinel, r.Host).Apply(r.Session, ClockDomain.Core, 0);
            ok = result.Succeeded;
            reason = result.Tx.Detail;
        }
        else
        {
            var result = r.Broker.Dispatch(path == "OpClock" ? new(BrokerRequest.OpClock, Domain: "core", Khz: 0)
                : new(BrokerRequest.OpWrite, Kind: ClockOffsets.Kind, Key: "core", Value: "0"));
            ok = result.Ok;
            reason = result.Error ?? result.Result?.GetProperty("applied")[0].GetProperty("detail").GetString();
        }
        Assert.False(ok);
        Assert.Contains(offset == 0 ? "original" : "resets every one", reason);
        Assert.Equal(240_000, r.Core);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(originalControl, FakeDriver.State[FakeVf.ControlWord]);
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
        Assert.Equal(known.Offsets, prepared.Budget!.Offsets);
        Assert.Null(prepared.Budget.Unreadable);
        Assert.Null(prepared.Controls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90_000)]
    public void Unsupported_ownership_topology_does_not_block_a_no_change_request(int offset)
    {
        using var r = new Rig(false);
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0xc, 240_000);
        r.ExternalCurve(offset);
        UnsupportedOwnershipTopology("mask");
        var before = (byte[])FakeDriver.State[FakeVf.ControlWord].Clone();
        var result = r.Broker.Dispatch(new(BrokerRequest.OpWrite, Kind: ClockOffsets.Kind, Key: "core", Value: "240000"));
        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(before, FakeDriver.State[FakeVf.ControlWord]);
        Assert.Empty(r.Journal.Inspect().Items);
    }
}
