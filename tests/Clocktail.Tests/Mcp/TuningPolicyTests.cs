using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class TuningPolicyTests
{
    private static readonly TuningScope Scope = new("fake-5080", "session-A", "worker:driver:recovery:A", "game");
    private static TuningPolicySettings Settings(params (string Key, string Value)[] values)
    {
        var env = values.ToDictionary(p => "CLOCKTAIL_MCP_" + p.Key, p => p.Value);
        return new(k => env.GetValueOrDefault(k));
    }
    private static Task Approve(TuningPolicy p, string kind = "device", Func<TuningScope?>? scope = null) =>
        p.Approve(kind, scope ?? (() => Scope), () => "fake ranges: core -1000..1000, memory -1000..3000, XBAR -1000..1000 MHz; power 250..450 W",
            (_, _) => Task.FromResult<string?>(null), default);

    [Fact]
    public void Environment_is_a_request_and_tightening_applies_without_approval()
    {
        var p = new TuningPolicy(Settings(("TUNING_POLICY", "device"), ("STEP_MHZ", "10")));
        var a = p.Snapshot(Scope);
        Assert.Equal("conservative", a.Approved);
        Assert.Null(a.Scope);
        Assert.Equal(10, a.Bound("STEP_MHZ"));
        Assert.Equal(400, a.Bound("MIN_POWER_W"));
        Assert.Equal("policy_limit", Assert.Throws<TuningPolicyException>(() => a.Ceiling("STEP_MHZ", 240)).Code);
        Assert.Equal("consent_required", Assert.Throws<TuningPolicyException>(() => a.Ceiling("MEMORY_STEP_MHZ", 3000)).Code);
        a.Ceiling("STEP_MHZ", 10); // ordinary requests remain usable
    }

    [Fact]
    public async Task Legacy_consent_covers_only_configured_relaxations_and_lists_each()
    {
        var p = new TuningPolicy(Settings(("STEP_MHZ", "30"), ("MIN_POWER_W", "250"), ("MEMORY_STEP_MHZ", "100")));
        var before = p.Snapshot(Scope);
        Assert.Equal(15, before.Bound("STEP_MHZ"));
        Assert.Equal("consent_required", Assert.Throws<TuningPolicyException>(() => before.Ceiling("STEP_MHZ", 30)).Code);
        before.Ceiling("STEP_MHZ", 15);
        string? prompt = null;
        await p.Approve("overrides", () => Scope, () => "250..450 W", (text, _) =>
        { prompt = text; return Task.FromResult<string?>(null); }, default);
        Assert.Contains("STEP_MHZ=30", prompt);
        Assert.Contains("MIN_POWER_W=250", prompt);
        Assert.Contains("250..450 W", prompt);
        var after = p.Snapshot(Scope);
        Assert.False(after.Device);
        Assert.Equal(30, after.Bound("STEP_MHZ"));
        Assert.Equal(250, after.Bound("MIN_POWER_W"));
        Assert.Equal(100, after.Bound("MEMORY_STEP_MHZ"));
        Assert.Equal(200, after.Bound("MAX_ABOVE_DAILY_MHZ"));
        Assert.Equal(50, after.Bound("XBAR_STEP_MHZ"));
        Assert.Equal("policy_limit", Assert.Throws<TuningPolicyException>(() => after.Ceiling("STEP_MHZ", 31)).Code);
        Assert.Contains("approved_overrides", JsonSerializer.Serialize(after.Describe()));
    }

    [Fact]
    public async Task Fake_5080_targets_pass_policy_after_device_approval()
    {
        var p = new TuningPolicy(Settings());
        await Approve(p);
        var a = p.Snapshot(Scope);
        a.Ceiling("STEP_MHZ", 240);
        a.Ceiling("MAX_ABOVE_DAILY_MHZ", 240);
        a.Ceiling("MEMORY_STEP_MHZ", 3000);
        a.Ceiling("XBAR_STEP_MHZ", 300);
        a.Floor("MIN_POWER_W", 250);
        a.Ceiling("MAX_POWER_W", 450);
        a.Floor("MIN_MV", 800);
        Assert.True(a.Device);
        Assert.Equal(20, TuningTools.XbarDemandNeededMv(300)); // remains an observation; mode decides veto/advisory
    }

    [Fact]
    public async Task Explicit_bounds_remain_restrictions_in_device_mode()
    {
        var p = new TuningPolicy(Settings(("STEP_MHZ", "30"), ("MAX_ABOVE_DAILY_MHZ", "400"), ("MAX_XBAR_MHZ", "300")));
        await Approve(p);
        var a = p.Snapshot(Scope);
        a.Ceiling("STEP_MHZ", 30);
        Assert.Throws<TuningPolicyException>(() => a.Ceiling("STEP_MHZ", 31));
        Assert.Equal(400, a.Bound("MAX_ABOVE_DAILY_MHZ"));
        Assert.Equal(300, a.Bound("MAX_XBAR_MHZ"));
        Assert.Null(a.Bound("MEMORY_STEP_MHZ"));
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("missing")]
    public async Task Scope_changes_revoke_without_restoration_or_reprompt(string change)
    {
        var p = new TuningPolicy(Settings());
        await Approve(p);
        TuningScope? next = change switch
        {
            "gpu" => Scope with { Gpu = "other" },
            "session" => Scope with { Session = "B" },
            "generation" => Scope with { Generation = "after crash" },
            _ => null,
        };
        Assert.Equal("conservative", p.Snapshot(next).Approved);
        Assert.Equal("conservative", p.Snapshot(Scope).Approved);
        Assert.Equal("conservative", new TuningPolicy(Settings()).Snapshot(Scope).Approved);
    }

    [Fact]
    public async Task Stale_or_cancelled_confirmation_never_grants()
    {
        var p = new TuningPolicy(Settings());
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asking = p.Approve("device", () => Scope, () => "fake", (_, _) => { shown.SetResult(); return answer.Task; }, default);
        await shown.Task;
        await Approve(p, "conservative");
        answer.SetResult(null);
        Assert.Equal("consent_required", (await Assert.ThrowsAsync<TuningPolicyException>(() => asking)).Code);
        Assert.False(p.Snapshot(Scope).Device);

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<TuningPolicyException>(() => p.Approve("device", () => Scope, () => "fake", (_, _) =>
        { cts.Cancel(); return Task.FromResult<string?>(null); }, cts.Token));
        Assert.Null(p.Snapshot(Scope).Scope);
    }

    [Fact]
    public async Task Session_change_while_form_is_open_and_overlapping_approvals_are_rejected()
    {
        var p = new TuningPolicy(Settings());
        var current = Scope;
        await Assert.ThrowsAsync<TuningPolicyException>(() => p.Approve("device", () => current, () => "fake", (_, _) =>
        { current = Scope with { Session = "B" }; return Task.FromResult<string?>(null); }, default));
        Assert.False(p.Snapshot(current).Device);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = p.Approve("device", () => Scope, () => "fake", (_, _) => answer.Task, default);
        await Approve(p);
        answer.SetResult(null);
        await Assert.ThrowsAsync<TuningPolicyException>(() => first);
        Assert.True(p.Snapshot(Scope).Device); // the later explicit confirmation owns the grant
    }

    [Fact]
    public async Task No_session_or_decline_grants_nothing()
    {
        var p = new TuningPolicy(Settings());
        int prompts = 0;
        await Assert.ThrowsAsync<TuningPolicyException>(() => p.Approve("device", () => null, () => "fake",
            (_, _) => { prompts++; return Task.FromResult<string?>(null); }, default));
        Assert.Equal(0, prompts);
        await Assert.ThrowsAsync<TuningPolicyException>(() => p.Approve("device", () => Scope, () => "fake",
            (_, _) => Task.FromResult<string?>("declined"), default));
        Assert.Null(p.Snapshot(Scope).Scope);
    }

    [Theory]
    [InlineData("off", true, false, true)]
    [InlineData("off", false, true, false)]
    [InlineData("auto", null, true, true)]
    [InlineData("auto", false, true, false)]
    [InlineData("auto", null, null, false)]
    [InlineData("dialog", true, false, false)]
    public async Task Mandatory_consent_never_treats_off_as_acceptance(string mode, bool? form, bool? dialog, bool accepted)
    {
        int dialogs = 0;
        var result = await Consent.AskMandatory(mode, _ => Task.FromResult(form), _ => { dialogs++; return dialog; }, "policy", default);
        Assert.Equal(accepted, result == null);
        Assert.Equal(mode == "dialog" || form == null ? 1 : 0, dialogs);
    }

    [Fact]
    public async Task Existing_off_callers_still_skip_but_mandatory_cancellation_does_not()
    {
        Assert.Null(await Consent.Ask("off", null, _ => throw new Exception("should not ask"), "existing", default));
        Assert.NotNull(await Consent.AskMandatory("off", _ => Task.FromResult<bool?>(true), _ => true, "policy", new(true)));
    }

    [Theory]
    [InlineData("TUNING_POLICY", "invalid")]
    [InlineData("STEP_MHZ", "0")]
    [InlineData("MIN_MV", "-1")]
    [InlineData("MAX_ABOVE_DAILY_MHZ", "2147483648")]
    [InlineData("MAX_POWER_W", "NaN")]
    public void Invalid_configuration_is_actionable(string key, string value) => Assert.Throws<ArgumentException>(() => Settings((key, value)));

    [Theory]
    [InlineData(-1000, 1000)]
    [InlineData(-500, 500)]
    [InlineData(400, 600)]
    [InlineData(250, 450)]
    public void Device_boundaries_are_inclusive_and_never_widened(double min, double max)
    {
        TuningValues.DeviceRange(min, min, max, "fake");
        TuningValues.DeviceRange(max, min, max, "fake");
        Assert.Throws<TuningPolicyException>(() => TuningValues.DeviceRange(min - .001, min, max, "fake"));
        Assert.Throws<TuningPolicyException>(() => TuningValues.DeviceRange(max + .001, min, max, "fake"));
        Assert.Throws<TuningPolicyException>(() => TuningValues.DeviceRange(0, null, max, "fake"));
        Assert.Throws<TuningPolicyException>(() => TuningValues.DeviceRange(0, max, min, "fake"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public void Nonfinite_and_overflow_inputs_are_refused(double value)
    {
        Assert.Throws<ArgumentException>(() => TuningValues.Khz(value));
        Assert.Throws<ArgumentException>(() => TuningValues.Uv(value));
    }

    [Fact]
    public void Daily_cap_arithmetic_does_not_wrap() =>
        Assert.Equal(0, TuningTools.OverDaily([int.MaxValue], [int.MinValue], 200));

    [Fact]
    public async Task Logging_failure_cannot_prevent_revocation()
    {
        var p = new TuningPolicy(Settings()) { Log = _ => throw new IOException("fake logging failure") };
        await Approve(p);
        p.Revoke("test");
        Assert.Null(p.Snapshot(Scope).Scope);
    }

    [Fact]
    public async Task A_new_wider_configuration_never_inherits_approval()
    {
        var first = new TuningPolicy(Settings(("STEP_MHZ", "30")));
        await Approve(first, "overrides");
        Assert.Equal(30, first.Snapshot(Scope).Bound("STEP_MHZ"));
        var next = new TuningPolicy(Settings(("STEP_MHZ", "60")));
        var snapshot = next.Snapshot(Scope);
        Assert.Equal(15, snapshot.Bound("STEP_MHZ"));
        Assert.Equal("consent_required", Assert.Throws<TuningPolicyException>(() => snapshot.Ceiling("STEP_MHZ", 60)).Code);
    }

    [Fact]
    public void Flatten_offsets_cannot_wrap_into_a_different_firmware_value()
    {
        Clocktail.Domain.Vf.VfPoint[] points = [new(0, 800_000, uint.MaxValue, uint.MaxValue, 0, 800_000)];
        Assert.Throws<OverflowException>(() => TuningTools.FlattenPlan(points, [0], 800_000, 1, 0));
    }

    [Theory]
    [InlineData("STEP_MHZ", "10", 20, false)]
    [InlineData("STEP_MHZ", "30", 45, false)]
    [InlineData("MAX_XBAR_MHZ", "100", 200, false)]
    [InlineData("MAX_ABOVE_DAILY_MHZ", "100", 150, false)]
    [InlineData("MIN_POWER_W", "425", 300, true)]
    public void Approval_is_not_suggested_when_configured_restrictions_would_still_forbid_the_value(
        string key, string configured, double value, bool floor)
    {
        var p = new TuningPolicy(Settings(("TUNING_POLICY", "device"), (key, configured)));
        var snapshot = p.Snapshot(Scope);
        Assert.Equal("policy_limit", Assert.Throws<TuningPolicyException>(() =>
        {
            if (floor) snapshot.Floor(key, value); else snapshot.Ceiling(key, value);
        }).Code);
    }

    [Fact]
    public async Task Revocation_clears_approval_before_calling_the_audit_sink()
    {
        var p = new TuningPolicy(Settings());
        await Approve(p);
        string? observed = null;
        p.Log = _ =>
        {
            observed = p.Snapshot(Scope).Approved;
            throw new IOException("audit unavailable");
        };
        p.Revoke("test");
        Assert.Equal("conservative", observed);
        Assert.Null(p.Snapshot(Scope).Scope);
    }
}
