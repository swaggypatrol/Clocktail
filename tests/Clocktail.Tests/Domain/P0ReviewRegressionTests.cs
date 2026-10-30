using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;
using Rig = Clocktail.Tests.Domain.EffectiveP0Tests.Rig;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class P0ReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_final_proof_after_reaching_original_retains_both_originals_for_recovery(bool shared)
    {
        using var r = new Rig(shared);
        Assert.True(r.CoreTo(240_000).Ok);
        var originalItems = r.Journal.Inspect().Items.OrderBy(i => i.Id).ToArray();
        int zeroStatusReads = 0;
        bool injected = false;
        r.Vf.StatusHook = (status, _) =>
        {
            // Native read, V/F preparation, then final proof: fail after both controls reached zero.
            if (r.Core == 0 && ++zeroStatusReads == 3)
            {
                injected = true;
                BitConverter.GetBytes(0u).CopyTo(status, VfLayout.Status(0) + 4);
            }
        };
        var result = r.CoreTo(0);
        Assert.True(injected);
        Assert.False(result.Ok);
        Assert.True(result.Result!.Value.GetProperty("restored").GetBoolean());
        Assert.Equal(240_000, r.Core);
        Assert.Equal(240_000, r.Displacement());
        Assert.Equal(originalItems, r.Journal.Inspect().Items.OrderBy(i => i.Id).ToArray());
        Assert.Equal(WriteOutcome.None, WriteOutcome.Of(result.Ok, result.Code, result.Result));
        Assert.False(r.Sentinel.IsBlocked(out _));
        var recovered = r.Broker.Dispatch(new(BrokerRequest.OpRecover));
        Assert.True(recovered.Ok, recovered.Error);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_return_releases_originals_only_after_the_final_coupled_proof(bool shared)
    {
        using var r = new Rig(shared);
        Assert.True(r.CoreTo(240_000).Ok);
        var originals = r.Journal.Inspect().Items.OrderBy(i => i.Id).ToArray();
        int zeroReads = 0;
        bool heldAtProof = false;
        r.Vf.StatusHook = (_, _) =>
        {
            if (r.Core == 0 && ++zeroReads == 3)
                heldAtProof = originals.SequenceEqual(r.Journal.Inspect().Items.OrderBy(i => i.Id));
        };
        var result = r.CoreTo(0);
        Assert.True(result.Ok, result.Error);
        Assert.True(heldAtProof);
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 45_000)]
    [InlineData(true, 45_000)]
    public void Memory_only_daily_does_not_keep_a_later_point_curve_trial(bool shared, int original)
    {
        using var r = new Rig(shared);
        r.ExternalCurve(original);
        new DailyProfile([new(ClockOffsets.Kind, "memory", "750000")], r.Host.Now).Save(r.ProfilePath);
        var saved = File.ReadAllBytes(r.ProfilePath);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "try", Value: JsonSerializer.Serialize(new
        { curve = VfOffsets.Encode(Enumerable.Repeat(original + 90_000, 127).ToArray()), change = "temporary point curve" }))).Ok);
        Assert.Equal(original + 90_000, r.Displacement());
        var back = r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(original, r.Displacement());
        Assert.Equal(Enumerable.Repeat(original, 127), r.Curve.Offsets());
        Assert.Equal(750_000, r.Memory);
        Assert.Null(Experiment.Load(Experiment.PathBeside(r.Journal.Path))!.Trial);
        Assert.Equal(saved, File.ReadAllBytes(r.ProfilePath));
        Assert.Equal(VfOffsets.Encode(Enumerable.Repeat(original, 127).ToArray()), Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(0, r.Memory);
        Assert.Equal(original, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
    }

    private static void LowPointsAtZero(Rig r)
    {
        Rig.I(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0xc, -240_000);
        r.Vf.Base = i => 180_000u + (uint)i * 20_000;
        r.Vf.Effective = (i, offset) => (uint)Math.Max(0L, (long)r.Vf.Base(i) + r.Core + offset);
        Assert.Throws<InvalidDataException>(() => r.Curve);
    }

    [Theory]
    [InlineData("Apply", false)]
    [InlineData("OpClock", false)]
    [InlineData("OpWrite", false)]
    [InlineData("Apply", true)]
    [InlineData("OpClock", true)]
    [InlineData("OpWrite", true)]
    public void Ordinary_P0_return_to_zero_captures_controls_without_requiring_valid_status(string path, bool held)
    {
        using var r = new Rig(false);
        LowPointsAtZero(r);
        int statusQueries = 0;
        r.Vf.StatusHook = (_, _) => statusQueries++;
        if (held) r.Journal.Claim(new(ClockOffsets.Kind, "core", "0", r.Host.Now));
        bool reachedDriver = false;
        r.BeforeP0 = () =>
        {
            reachedDriver = true;
            Assert.Equal("0*127", Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
            Assert.True(r.Sentinel.IsBlocked(out _));
        };
        if (path == "Apply")
        {
            var result = new ClockOffsets(r.Hw, r.Journal, r.Sentinel, r.Host).Apply(r.Session, ClockDomain.Core, 0);
            Assert.True(result.Succeeded, result.Tx.ToString());
        }
        else
        {
            var result = r.Broker.Dispatch(path == "OpClock" ? new(BrokerRequest.OpClock, Domain: "core", Khz: 0)
                : new(BrokerRequest.OpWrite, Kind: ClockOffsets.Kind, Key: "core", Value: "0"));
            Assert.True(result.Ok, result.Error);
        }
        Assert.True(reachedDriver);
        Assert.Equal(0, statusQueries);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.False(r.Sentinel.IsBlocked(out _));
        if (held) Assert.Empty(r.Journal.Inspect().Items);
        else Assert.Equal("0*127", Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
    }

    [Theory]
    [InlineData("Apply")]
    [InlineData("OpClock")]
    [InlineData("OpWrite")]
    public void Status_independent_return_keeps_an_existing_VF_original(string path)
    {
        using var r = new Rig(false);
        LowPointsAtZero(r);
        r.Journal.Claim(new(ClockOffsets.Kind, "core", "0", r.Host.Now));
        r.Journal.Claim(new(VfWrites.Kind, VfWrites.CoreKey, "15000*127", r.Host.Now));
        if (path == "Apply") Assert.True(new ClockOffsets(r.Hw, r.Journal, r.Sentinel, r.Host).Apply(r.Session, ClockDomain.Core, 0).Succeeded);
        else Assert.True(r.Broker.Dispatch(path == "OpClock" ? new(BrokerRequest.OpClock, Domain: "core", Khz: 0)
            : new(BrokerRequest.OpWrite, Kind: ClockOffsets.Kind, Key: "core", Value: "0")).Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal("15000*127", Assert.Single(r.Journal.Inspect().Items).Original);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(15_000, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
    }

    [Theory]
    [InlineData("Apply", 0, true)]
    [InlineData("OpWrite", 0, true)]
    [InlineData("Apply", -270_000, false)]
    [InlineData("OpWrite", -270_000, false)]
    public void Unreadable_controls_keep_the_existing_bounded_return_rule_and_recorded_debt(string path, int target, bool allowed)
    {
        using var r = new Rig(false);
        LowPointsAtZero(r);
        r.Journal.Claim(new(ClockOffsets.Kind, "core", "0", r.Host.Now));
        r.Journal.Claim(new(VfWrites.Kind, VfWrites.CoreKey, "15000*127", r.Host.Now));
        var query = FakeDriver.QueryResponder!;
        FakeDriver.QueryResponder = request =>
        {
            var answer = query(request);
            if (BitConverter.ToUInt32(request) == FakeVf.ControlWord && answer != null)
                answer[VfLayout.MaskOffset] ^= 1; // unreadable controls, rather than merely invalid effective Status
            return answer;
        };
        bool succeeded = path == "Apply"
            ? new ClockOffsets(r.Hw, r.Journal, r.Sentinel, r.Host).Apply(r.Session, ClockDomain.Core, target).Succeeded
            : r.Broker.Dispatch(new(BrokerRequest.OpWrite, Kind: ClockOffsets.Kind, Key: "core", Value: target.ToString(System.Globalization.CultureInfo.InvariantCulture))).Ok;
        Assert.Equal(allowed, succeeded);
        Assert.Equal(allowed ? 0 : -240_000, r.Core);
        Assert.Equal(allowed ? 1 : 0, FakeDriver.Sets);
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "15000*127");
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Fact]
    public void V_F_writes_still_require_full_curve_validation()
    {
        using var r = new Rig(false);
        LowPointsAtZero(r);
        var result = r.Broker.Dispatch(new(BrokerRequest.OpWrite, Kind: VfWrites.Kind, Key: VfWrites.CoreKey, Value: "15000*127"));
        Assert.False(result.Ok);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Empty(r.Journal.Inspect().Items);
    }
}
