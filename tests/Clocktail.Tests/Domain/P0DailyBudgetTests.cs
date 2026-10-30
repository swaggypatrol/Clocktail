using System.Globalization;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Rig = Clocktail.Tests.Domain.EffectiveP0Tests.Rig;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class P0DailyBudgetTests
{
    private static void LimitAndCurve(Rig r)
    {
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0x14, 300_000);
        r.ExternalCurve(240_000);
    }

    [Theory]
    [InlineData(false, 90_000)]
    [InlineData(true, 90_000)]
    [InlineData(false, -90_000)]
    [InlineData(true, -90_000)]
    public void Implicit_daily_budgets_its_replacement_not_the_discarded_curve(bool shared, int target)
    {
        using var r = new Rig(shared, beginSession: false);
        LimitAndCurve(r);
        new DailyProfile([new(ClockOffsets.Kind, "core", target.ToString(CultureInfo.InvariantCulture))], r.Host.Now).Save(r.ProfilePath);
        var saved = File.ReadAllBytes(r.ProfilePath);
        for (int i = 0; i < 2; i++)
        {
            var result = r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "apply"));
            Assert.True(result.Ok, result.Error);
            Assert.Equal(target, r.Core);
            Assert.All(r.Curve.Points, p => Assert.Equal(target, VfEffectiveCurve.Displacement(p)));
            Assert.Equal(saved, File.ReadAllBytes(r.ProfilePath));
            Assert.Equal("240000*127", Assert.Single(r.Journal.Inspect().Items, j => j.Kind == VfWrites.Kind).Original);
            Assert.False(r.Sentinel.IsBlocked(out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relative_shape_preservation_still_refuses_an_excessive_combined_budget(bool shared)
    {
        using var r = new Rig(shared);
        LimitAndCurve(r);
        var result = r.CoreTo(90_000);
        Assert.False(result.Ok);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(0, r.Core);
        Assert.Equal(240_000, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Implicit_daily_still_refuses_an_actual_replacement_over_the_combined_budget()
    {
        using var r = new Rig(true, beginSession: false);
        LimitAndCurve(r);
        new DailyProfile([new(ClockOffsets.Kind, "core", "180000")], r.Host.Now).Save(r.ProfilePath);
        var result = r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.False(result.Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(240_000, r.Displacement());
        Assert.False(r.Sentinel.IsBlocked(out _));
    }
}
