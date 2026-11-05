using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;
using Rig = Clocktail.Tests.Domain.EffectiveP0Tests.Rig;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class StartupP0Tests
{
    private static void SaveDaily(Rig r, string? explicitCurve = null)
    {
        var writes = new List<ProfileWrite>
        {
            new(PowerCap.Kind, "fe", "450000"), new(ClockOffsets.Kind, "core", "240000"),
            new(ClockOffsets.Kind, "memory", "3000000"), new(ClockDomainOffsets.Kind, "xbar", "300000"),
        };
        if (explicitCurve != null) writes.Add(new(VfWrites.Kind, VfWrites.CoreKey, explicitCurve));
        new DailyProfile(DailyProfile.InWriteOrder(writes), r.Host.Now).Save(r.ProfilePath);
    }

    private static BrokerResponse Startup(Rig r, string kind = "apply") =>
        r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: kind, Value: "the broker started"));

    private static void AssertCoreAndOtherGroups(Rig r)
    {
        Assert.Equal(240_000, r.Core);
        Assert.Equal(240_000, r.Displacement());
        Assert.All(r.Curve.Points, p => Assert.Equal(240_000, VfEffectiveCurve.Displacement(p)));
        Assert.Equal(450_000u, r.PowerMw);
        Assert.Equal(300_000, ClockDomainsFixture.OffsetKhz(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_restores_the_reported_daily_without_an_explicit_VF_entry(bool shared)
    {
        using var r = new Rig(shared, beginSession: false);
        SaveDaily(r);
        var fileBefore = File.ReadAllBytes(r.ProfilePath);
        var result = Startup(r);
        Assert.True(result.Ok, result.Error);
        AssertCoreAndOtherGroups(r);
        Assert.Equal(3_000_000, r.Memory);
        Assert.Equal(fileBefore, File.ReadAllBytes(r.ProfilePath)); // application does not rewrite the saved daily
        Assert.Null(Experiment.Load(Experiment.PathBeside(r.Journal.Path)));
        Assert.Equal("0*127", Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
        var vf = Assert.Single(FactoryStock.Survey(r.Hw, r.Session,
            (kind, key) => r.Journal.Inspect().Items.FirstOrDefault(i => i.Kind == kind && i.Key == key)?.Original,
            (kind, key) => DailyProfile.Load(r.ProfilePath)!.Writes.FirstOrDefault(w => w.Kind == kind && w.Key == key)?.Value), c => c.Kind == VfWrites.Kind);
        Assert.False(vf.OtherTool); // native-generated +240 is attributed to Clocktail's captured original
        Assert.NotNull(vf.Original);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("resume")]
    [InlineData("after-reset")]
    public void Reapplying_a_daily_without_VF_does_not_give_its_generated_curve_back(string kind)
    {
        using var r = new Rig(true, beginSession: false);
        SaveDaily(r);
        // A state already applied by this broker: the missing profile VF entry is still an implicit P0 dependency.
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0xc, 240_000);
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 1) + 0xc, 3_000_000);
        r.ExternalCurve(240_000);
        r.Journal.Claim(new(ClockOffsets.Kind, "core", "0", r.Host.Now));
        r.Journal.Claim(new(ClockOffsets.Kind, "memory", "0", r.Host.Now));
        r.Journal.Claim(new(VfWrites.Kind, VfWrites.CoreKey, "0*127", r.Host.Now));
        var result = Startup(r, kind);
        Assert.True(result.Ok, result.Error);
        AssertCoreAndOtherGroups(r);
        Assert.Equal(3_000_000, r.Memory);
        Assert.Equal(Enumerable.Repeat(240_000, 127), r.Curve.Offsets());
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "0*127");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refused_memory_reports_partial_daily_and_preserves_effective_core(bool shared)
    {
        using var r = new Rig(shared, beginSession: false);
        SaveDaily(r);
        var fileBefore = File.ReadAllBytes(r.ProfilePath);
        r.FailMemory = true;
        var result = Startup(r);
        Assert.False(result.Ok);
        AssertCoreAndOtherGroups(r);
        Assert.Equal(0, r.Memory);
        Assert.True(result.Result!.Value.GetProperty("partial").GetBoolean());
        var omitted = Assert.Single(result.Result.Value.GetProperty("not_applied").EnumerateArray());
        Assert.Equal("memory", omitted.GetProperty("group").GetString());
        var memory = Assert.Single(omitted.GetProperty("readback").EnumerateArray());
        Assert.Equal("3000000", memory.GetProperty("requested").GetString());
        Assert.Equal("0", memory.GetProperty("current").GetString());
        Assert.StartsWith("partly applied,", DriverResetResponder.ProfileSummary(result));
        Assert.Contains("Daily profile partly applied", File.ReadAllText(TuningLog.PathBeside(r.Journal.Path)));
        Assert.Equal(fileBefore, File.ReadAllBytes(r.ProfilePath)); // omission is a result, not a saved profile edit
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_explicit_daily_curve_remains_authoritative_without_double_counting_P0(bool shared)
    {
        using var r = new Rig(shared, beginSession: false);
        SaveDaily(r, shared ? "240000*127" : "0*127");
        var result = Startup(r);
        Assert.True(result.Ok, result.Error);
        AssertCoreAndOtherGroups(r);
        Assert.Equal(3_000_000, r.Memory);
    }

    [Fact]
    public void Unverified_startup_memory_rollback_stops_application_and_reports_unknown()
    {
        using var r = new Rig(true, beginSession: false);
        SaveDaily(r);
        r.RefuseNonzeroCurve = true;
        var result = Startup(r);
        Assert.False(result.Ok);
        Assert.Equal(WriteOutcome.Unknown, WriteOutcome.Of(result.Ok, result.Code, result.Result));
        Assert.False(result.Result!.Value.GetProperty("partial").GetBoolean());
        Assert.Equal("memory", Assert.Single(result.Result.Value.GetProperty("not_applied").EnumerateArray()).GetProperty("group").GetString());
        Assert.StartsWith("application incomplete:", DriverResetResponder.ProfileSummary(result));
        Assert.Contains("Daily profile application incomplete", File.ReadAllText(TuningLog.PathBeside(r.Journal.Path)));
        Assert.True(r.Sentinel.IsBlocked(out _));
        Assert.Equal(0, ClockDomainsFixture.OffsetKhz(1)); // no later group is written after an unknown coupled outcome
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "0*127");
    }

    [Fact]
    public void Partial_result_reads_back_an_external_original_instead_of_calling_it_stock()
    {
        using var r = new Rig(true, beginSession: false);
        SaveDaily(r);
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 1) + 0xc, 750_000);
        r.ExternalCurve(90_000);
        r.FailMemory = true;
        var result = Startup(r);
        Assert.False(result.Ok);
        Assert.True(result.Result!.Value.GetProperty("partial").GetBoolean());
        Assert.Equal(240_000, r.Displacement()); // a P0-only core daily restores its uniform target, not the external curve
        Assert.Equal(750_000, r.Memory);
        var omitted = Assert.Single(result.Result.Value.GetProperty("not_applied").EnumerateArray());
        Assert.Equal("750000", omitted.GetProperty("readback")[0].GetProperty("current").GetString());
        Assert.DoesNotContain("stays at stock", result.Error);
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "90000*127");
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(90_000, r.Displacement());
        Assert.Equal(750_000, r.Memory);
    }

    [Fact]
    public void A_refused_explicit_curve_drops_its_core_group_and_restores_its_original()
    {
        using var r = new Rig(true, beginSession: false);
        SaveDaily(r, "240000*127");
        r.RefuseNonzeroCurve = true;
        var result = Startup(r);
        Assert.False(result.Ok);
        Assert.True(result.Result!.Value.GetProperty("partial").GetBoolean());
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.Equal(3_000_000, r.Memory);
        Assert.Equal(300_000, ClockDomainsFixture.OffsetKhz(1));
        Assert.False(r.Sentinel.IsBlocked(out _));
        Assert.Equal("core", Assert.Single(result.Result.Value.GetProperty("not_applied").EnumerateArray()).GetProperty("group").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_daily_applied_without_VF_can_be_kept_recovered_and_reloaded_with_its_dependency(bool shared)
    {
        using var r = new Rig(shared, beginSession: false);
        SaveDaily(r);
        Assert.True(Startup(r).Ok);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.Contains(DailyProfile.Load(r.ProfilePath)!.Writes,
            w => w.Kind == VfWrites.Kind && w.Value == (shared ? "240000*127" : "0*127"));
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Memory);
        Assert.Equal(0, r.Displacement());
        Assert.True(Startup(r).Ok);
        AssertCoreAndOtherGroups(r);
        Assert.Equal(3_000_000, r.Memory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_memory_only_daily_preserves_an_external_curve_without_claiming_it_as_generated(bool shared)
    {
        using var r = new Rig(shared, beginSession: false);
        r.ExternalShape();
        var original = r.Curve;
        new DailyProfile([new(ClockOffsets.Kind, "memory", "3000000")], r.Host.Now).Save(r.ProfilePath);
        Assert.True(Startup(r).Ok);
        Assert.Equal(3_000_000, r.Memory);
        Assert.Null(VfEffectiveCurve.Mismatch(original, r.Curve));
        var vf = Assert.Single(FactoryStock.Survey(r.Hw, r.Session,
            (kind, key) => r.Journal.Inspect().Items.FirstOrDefault(i => i.Kind == kind && i.Key == key)?.Original,
            (_, _) => null), c => c.Kind == VfWrites.Kind);
        Assert.True(vf.OtherTool); // Clocktail preserves and owes this pre-existing curve; it did not generate it.
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(0, r.Memory);
        Assert.Equal(original.Offsets(), r.Curve.Offsets());
    }

    [Fact]
    public void An_already_matching_raw_core_target_does_not_hide_a_lost_effective_curve()
    {
        using var r = new Rig(true, beginSession: false);
        SaveDaily(r);
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0xc, 240_000);
        r.Journal.Claim(new(ClockOffsets.Kind, "core", "0", r.Host.Now));
        r.Journal.Claim(new(VfWrites.Kind, VfWrites.CoreKey, "0*127", r.Host.Now));
        Assert.True(Startup(r).Ok);
        AssertCoreAndOtherGroups(r);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Back_to_a_P0_only_daily_removes_a_trials_point_curve_even_when_raw_core_matches(bool shared)
    {
        using var r = new Rig(shared, beginSession: false);
        SaveDaily(r);
        Assert.True(Startup(r).Ok);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"software regression"}""")).Ok);
        var trial = r.Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "try", Value: JsonSerializer.Serialize(new
        { curve = shared ? "330000*127" : "90000*127", change = "point curve above daily" })));
        Assert.True(trial.Ok, trial.Error);
        Assert.Equal(330_000, r.Displacement());
        var back = Startup(r);
        Assert.True(back.Ok, back.Error);
        AssertCoreAndOtherGroups(r);
        Assert.Equal(3_000_000, r.Memory);
        Assert.Null(Experiment.Load(Experiment.PathBeside(r.Journal.Path))!.Trial);
    }

    [Fact]
    public void A_legacy_P0_only_daily_reports_that_its_derived_base_cannot_prove_a_uniform_target()
    {
        using var r = new Rig(false, legacy: true, beginSession: false);
        SaveDaily(r);
        var result = Startup(r);
        Assert.False(result.Ok);
        Assert.True(result.Result!.Value.GetProperty("partial").GetBoolean());
        Assert.Contains("legacy derived baseline cannot verify", result.Error);
        Assert.Equal(0, r.Core);
        Assert.Equal(3_000_000, r.Memory);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }
}
