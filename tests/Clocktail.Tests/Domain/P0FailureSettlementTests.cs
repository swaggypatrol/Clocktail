using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using Clocktail.Mcp;
using Rig = Clocktail.Tests.Domain.EffectiveP0Tests.Rig;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public sealed class P0FailureSettlementTests
{
    public static IEnumerable<object[]> PathsAndModels() =>
        from path in new[] { "Apply", "OpClock", "OpWrite" }
        from shared in new[] { false, true }
        select new object[] { path, shared };

    private sealed record Result(bool Success, RestoreState Restore, bool Cleared, string? Written, bool? StateKnown);

    private static Result Core(Rig r, string path, ClockDomain domain = ClockDomain.Core)
    {
        if (path == "Apply")
        {
            var direct = new ClockOffsets(r.Hw, r.Journal, r.Sentinel, r.Host).Apply(r.Session, domain, 240_000);
            return new(direct.Succeeded, direct.Tx.Restore, direct.SentinelCleared, null, null);
        }
        var answer = r.Broker.Dispatch(path == "OpClock"
            ? new(BrokerRequest.OpClock, Domain: ClockOffsets.Key(domain), Khz: 240_000)
            : new(BrokerRequest.OpWrite, Kind: ClockOffsets.Kind, Key: ClockOffsets.Key(domain), Value: "240000"));
        var applied = answer.Result!.Value.GetProperty("applied")[0];
        return new(answer.Ok, Enum.Parse<RestoreState>(applied.GetProperty("restore").GetString()!),
            applied.GetProperty("sentinel_cleared").GetBoolean(), WriteOutcome.Of(answer.Ok, answer.Code, answer.Result),
            applied.TryGetProperty("state_known", out var known) ? known.GetBoolean() : null);
    }

    [Theory]
    [MemberData(nameof(PathsAndModels))]
    public void Refused_P0_restores_implicit_control_side_effects_before_clearing_sentinel(string path, bool shared)
    {
        using var r = new Rig(shared);
        r.FailCore = true;
        int statusReads = 0;
        r.Vf.StatusHook = (_, _) => statusReads++;
        bool sawRollback = false;
        var set = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, request) =>
        {
            if (BitConverter.ToUInt32(request) == FakeVf.ControlWord)
            {
                sawRollback = true;
                Assert.False(Task.Run(() =>
                {
                    bool entered = r.Session.TryEnter(TimeSpan.FromMilliseconds(20), out var gate);
                    if (entered) gate.Dispose();
                    return entered;
                }).GetAwaiter().GetResult());
                Assert.True(r.Sentinel.IsBlocked(out _));
                Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == ClockOffsets.Kind && i.Original == "0");
                Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "0*127");
            }
            return set(state, request);
        };
        var result = Core(r, path);
        Assert.False(result.Success);
        Assert.True(result.Cleared);
        Assert.Equal(shared, sawRollback);
        Assert.Equal(shared ? RestoreState.Restored : RestoreState.NotNeeded, result.Restore);
        Assert.Equal(0, statusReads); // Ordinary settlement must not acquire a full effective-status prerequisite.
        if (path != "Apply") Assert.Equal(WriteOutcome.None, result.Written);
        Assert.False(result.StateKnown == false);
        Assert.Equal(0, r.Core);
        Assert.All(r.Curve.Points, p => Assert.Equal(0, VfEffectiveCurve.Displacement(p)));
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData("Apply")]
    [InlineData("OpClock")]
    [InlineData("OpWrite")]
    public void Refused_memory_write_also_restores_unexpected_core_control_side_effects(string path)
    {
        using var r = new Rig(false);
        r.FailMemory = true;
        var set = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, request) =>
        {
            var next = set(state, request);
            if (BitConverter.ToUInt32(request) == FakeVf.PstatesWord) r.ExternalCurve(90_000);
            return next;
        };
        var result = Core(r, path, ClockDomain.Memory);
        Assert.False(result.Success);
        Assert.Equal(RestoreState.Restored, result.Restore);
        Assert.True(result.Cleared);
        if (path != "Apply") Assert.Equal(WriteOutcome.None, result.Written);
        Assert.Equal(0, r.Memory);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData("Apply", "unreadable")]
    [InlineData("OpClock", "unreadable")]
    [InlineData("OpWrite", "unreadable")]
    [InlineData("Apply", "refused")]
    [InlineData("OpClock", "refused")]
    [InlineData("OpWrite", "refused")]
    public void Unverified_control_rollback_keeps_both_debts_and_blocks_further_writes(string path, string fault)
    {
        using var r = new Rig(true);
        r.FailCore = true;
        bool injected = false;
        r.BeforeP0 = () => injected = true;
        var query = FakeDriver.QueryResponder!;
        FakeDriver.QueryResponder = request =>
        {
            var answer = query(request);
            if (injected && fault == "unreadable" && BitConverter.ToUInt32(request) == FakeVf.ControlWord && answer != null)
                answer[VfLayout.MaskOffset] ^= 1;
            return answer;
        };
        var set = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, request) =>
            injected && fault == "refused" && BitConverter.ToUInt32(request) == FakeVf.ControlWord ? state : set(state, request);
        var result = Core(r, path);
        Assert.True(injected);
        Assert.False(result.Success);
        Assert.False(result.Cleared);
        Assert.Equal(RestoreState.RestoreFailed, result.Restore);
        if (path != "Apply") Assert.Equal(WriteOutcome.Unknown, result.Written);
        if (path == "OpWrite") Assert.False(result.StateKnown);
        Assert.True(r.Sentinel.IsBlocked(out _));
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == ClockOffsets.Kind && i.Original == "0");
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "0*127");
        int sets = FakeDriver.Sets;
        Assert.False(r.Broker.Dispatch(new(BrokerRequest.OpClock, Domain: "memory", Khz: 250_000)).Ok);
        Assert.Equal(sets, FakeDriver.Sets);
        injected = false;
        r.BeforeP0 = null;
        r.FailCore = false;
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData("Apply")]
    [InlineData("OpClock")]
    [InlineData("OpWrite")]
    public void Verified_failure_restores_preoperation_controls_without_overwriting_an_existing_original(string path)
    {
        using var r = new Rig(true);
        r.Journal.Claim(new(ClockOffsets.Kind, "core", "-15000", r.Host.Now));
        r.Journal.Claim(new(VfWrites.Kind, VfWrites.CoreKey, "-30000*127", r.Host.Now));
        r.FailCore = true;
        var result = Core(r, path);
        Assert.False(result.Success);
        Assert.Equal(RestoreState.Restored, result.Restore);
        Assert.True(result.Cleared);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == ClockOffsets.Kind && i.Original == "-15000");
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "-30000*127");
    }
}
