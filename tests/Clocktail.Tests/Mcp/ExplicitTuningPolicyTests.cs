using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Power;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using Clocktail.Mcp;
using Clocktail.Tests.Domain;

namespace Clocktail.Tests.Mcp;

/// <summary>Real MCP methods over fake NVAPI reads and an in-memory broker transport. No native GPU is opened.</summary>
[Collection("fake-driver")]
public class ExplicitTuningPolicyTests
{
    private sealed class Harness
    {
        public readonly FakeVf Vf = new();
        public readonly GpuSession Gpu = new(1, "fake");
        public readonly TuningPolicy Policy;
        public readonly Card Card;
        public readonly TuningTools Tools;
        public readonly List<BrokerRequest> Trials = [];
        public string Generation = "broker:driver:session:A";
        public Action? BeforeTrial;
        public bool SupportsPolicyScope = true;
        public bool OldBrokerAtTrial;
        public int UnscopedTrialAttempts;
        public Func<BrokerResponse>? ScopeReply;
        public Exception? ReadFailure;
        public uint? NvvddMaxUv = 1_100_000;
        public uint MinPower = 250_000, MaxPower = 450_000;

        public Harness(params (string Key, string Value)[] overrides)
        {
            var config = overrides.ToDictionary(p => "CLOCKTAIL_MCP_" + p.Key, p => p.Value);
            Policy = new(new TuningPolicySettings(k => config.GetValueOrDefault(k)));
            Slot[] extra = [Slot.ClientPwrPoliciesGetInfo, Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl,
                Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo];
            var hw = Vf.Install(extra);
            FakeDriver.Seed(Slot.ClientPwrPoliciesGetInfo);
            var originalQuery = FakeDriver.QueryResponder!;
            byte[] p0 = FakeDriver.State[FakeVf.PstatesWord];
            W(p0, 0xc, 2);
            int m = Pstates20Layout.Clock(0, 1);
            W(p0, m, 4); W(p0, m + 8, 1); I(p0, m + 0x10, -1_000_000); I(p0, m + 0x14, 3_000_000);
            byte[][] fixedReads = [ClockDomainsFixture.Topology(), ClockDomainsFixture.FullControl(),
                RailsFixture.Info(), RailsFixture.Control(), RailsFixture.Status(), RailsFixture.Devices()];
            foreach (var b in fixedReads) FakeDriver.State[BitConverter.ToUInt32(b)] = b;
            FakeDriver.QueryResponder = q =>
            {
                if (BitConverter.ToUInt32(q) == Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word)
                {
                    var b = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
                    W(b, 4, 1);
                    int at = PowerRequestsLayout.InfoEntry(0);
                    W(b, at, 1); W(b, at + 8, MinPower); W(b, at + 12, Math.Clamp(400_000u, MinPower, MaxPower)); W(b, at + 16, MaxPower);
                    return b;
                }
                if (BitConverter.ToUInt32(q) == Slots.Info(Slot.ClockClkDomainsGetControl).Shape!.Value.Word)
                {
                    var b = ClockDomainsFixture.FullControl();
                    W(b, 8, BitConverter.ToUInt32(q, 8));
                    return b;
                }
                return originalQuery(q);
            };
            Card = new Card(hw, Gpu, "fake-5080", () =>
            {
                if (ReadFailure != null) throw ReadFailure;
                return new(null, 950_000, 900_000, NvvddMaxUv, VfReader.Read(hw, Gpu));
            }, request =>
            {
                if (request.Kind == "policy-scope" && !SupportsPolicyScope) return new(false, "unknown operation");
                if (request.Kind == "policy-scope" && ScopeReply != null) return ScopeReply();
                if (request.Kind == "policy-scope") return Answer(new { gpu = "fake-5080", session = "A", game = "fake", generation = Generation });
                if (request.Op == BrokerRequest.OpProfile && request.Kind == "show") return Answer(new { profile = (object?)null });
                if (request.Kind is "try" or BrokerRequest.ScopedTrial)
                {
                    BeforeTrial?.Invoke();
                    if (request.Kind == "try") UnscopedTrialAttempts++;
                    // An old broker knows only "try" and silently ignores new JSON properties.
                    if (OldBrokerAtTrial && request.Kind != "try")
                        return new(false, "unknown experiment kind", Code: BrokerCode.BadRequest);
                    if (!OldBrokerAtTrial && request.ExpectedTuningScope != null && request.ExpectedTuningScope != Generation)
                        return new(false, "stale scope", Code: "consent_required");
                    Trials.Add(request);
                    return Answer(new { success = true });
                }
                return Answer(new { success = true });
            });
            Tools = new(Card, new Residency(Card), new LiveWatch(Card), Policy);
        }

        public Task Approve(string kind = "device") => Policy.Approve(kind, () => TuningTools.ReadPolicyScope(Card),
            () => "fake 5080 ranges", (_, _) => Task.FromResult<string?>(null), default);
        private static BrokerResponse Answer(object a) => new(true, Result: JsonSerializer.SerializeToElement(a));
        public static void W(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
        public static void I(byte[] b, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), value);
    }

    private static JsonElement Result(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static void Success(string json) { var r = Result(json); Assert.True(r.GetProperty("success").GetBoolean(), json); }
    private static void Code(string code, string json) => Assert.Equal(code, Result(json).GetProperty("code").GetString());

    [Fact]
    public async Task Real_explicit_paths_accept_fake_5080_profile_without_extra_Xbar_demand()
    {
        var h = new Harness();
        await h.Approve();
        Assert.Null(h.Card.Broker(BrokerRequest.OpExperiment, "policy-scope").Written);
        Assert.Empty(h.Trials); Assert.Equal(0, FakeDriver.Sets);
        Success(h.Tools.SetCoreOffset(240));
        Success(h.Tools.SetMemoryOffset(3000));
        var xbar = h.Tools.AdjustXbar(300, 0);
        Success(xbar);
        Assert.Single(Result(xbar).GetProperty("advisories").EnumerateArray());
        var args = JsonDocument.Parse(h.Trials.Last().Value!).RootElement;
        Assert.Equal(300_000, args.GetProperty("xbar_khz").GetInt32());
        Assert.False(args.TryGetProperty("xbar_vdemand_uv", out _));
        Success(h.Tools.SetPowerLimit(450));
        Success(h.Tools.SetPowerLimit(250));
        Assert.Equal(5, h.Trials.Count);
        Assert.All(h.Trials, t => Assert.Equal(h.Generation, t.ExpectedTuningScope));
        Assert.Equal(0, FakeDriver.Sets); // transport is in memory, not connected to the real broker
    }

    [Fact]
    public async Task Pending_requests_allow_conservative_steps_and_return_actionable_consent_errors()
    {
        var h = new Harness(("TUNING_POLICY", "device"));
        Success(h.Tools.SetCoreOffset(15));
        Success(h.Tools.SetMemoryOffset(250));
        Code("consent_required", h.Tools.SetCoreOffset(240));
        Code("consent_required", h.Tools.SetMemoryOffset(3000));
        Code("consent_required", h.Tools.SetPowerLimit(250));
        Assert.Equal(2, h.Trials.Count);
        await h.Approve();
        Success(h.Tools.SetCoreOffset(240));
    }

    [Fact]
    public async Task Overrides_do_not_activate_device_mode_or_widen_unrelated_controls()
    {
        var h = new Harness(("STEP_MHZ", "30"));
        Code("consent_required", h.Tools.SetCoreOffset(30));
        await h.Approve("overrides");
        Success(h.Tools.SetCoreOffset(30));
        Code("policy_limit", h.Tools.SetMemoryOffset(3000));
        Code("policy_limit", h.Tools.SetPowerLimit(250));
        Code("policy_limit", h.Tools.AdjustXbar(300));
        Assert.Single(h.Trials);
    }

    [Fact]
    public async Task Device_mode_keeps_other_domains_and_F8_and_daily_power_policies()
    {
        var h = new Harness();
        await h.Approve();
        Assert.False(Result(h.Tools.AdjustClockDomain("sys", 300)).GetProperty("success").GetBoolean());
        Assert.False(Result(await h.Tools.SetPowerCap(null!, 250)).GetProperty("success").GetBoolean());
        Assert.False(Result(await h.Tools.SetDailyPowerLimit(null!, 250)).GetProperty("success").GetBoolean());
        Assert.Empty(h.Trials);
        Success(await h.Tools.SetTuningPolicy(null!, "conservative"));
        Assert.Empty(h.Trials);
        Assert.False(h.Policy.Snapshot(TuningTools.ReadPolicyScope(h.Card)).Device);
    }

    [Fact]
    public async Task All_curve_paths_use_device_bounds_and_explicit_low_voltage_windows()
    {
        var h = new Harness();
        await h.Approve();
        Success(h.Tools.AdjustCurve(240, 800, 810));
        var adjusted = VfOffsets.Decode(JsonDocument.Parse(h.Trials.Last().Value!).RootElement.GetProperty("curve").GetString()!);
        Assert.Equal(0, adjusted[19]); // below the explicitly requested window
        Assert.Equal(240_000, adjusted[20]);
        Assert.True(adjusted[23] > 0); // upward propagation also passed the device budget check
        Success(h.Tools.SetCurve(VfOffsets.Encode(Enumerable.Repeat(240_000, 127).ToArray())));
        Success(h.Tools.ShiftCurve(50));
        Success(h.Tools.FlattenCurve(800, ceilingMhz: 2500));
        Code("device_limit", h.Tools.SetCurve(VfOffsets.Encode(Enumerable.Repeat(1_000_001, 127).ToArray())));
        Assert.Equal(4, h.Trials.Count);
        Assert.False(Result(h.Tools.AdjustCurve(15)).GetProperty("success").GetBoolean()); // no load: explicit voltages still needed
    }

    [Fact]
    public async Task Fresh_device_ranges_and_stale_scope_refuse_without_a_trial()
    {
        var h = new Harness();
        await h.Approve();
        h.MaxPower = 440_000;
        Code("device_limit", h.Tools.SetPowerLimit(450));
        h.BeforeTrial = () => h.Generation = "recovered";
        Code("consent_required", h.Tools.SetCoreOffset(240));
        Assert.Empty(h.Trials);
        Code("consent_required", h.Tools.SetCoreOffset(240)); // the next read notices revocation
    }

    [Fact]
    public async Task Unknown_or_inverted_bounds_noneditable_points_and_nonfinite_inputs_fail_closed()
    {
        var h = new Harness();
        await h.Approve();
        Code("device_limit", h.Tools.SetMemoryOffset(3000.001));
        Code("device_limit", h.Tools.SetCoreOffset(1000.001));
        Assert.False(Result(h.Tools.SetCoreOffset(double.NaN)).GetProperty("success").GetBoolean());
        Assert.False(Result(h.Tools.AdjustXbar(double.PositiveInfinity)).GetProperty("success").GetBoolean());
        Harness.W(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 8, 0);
        Code("device_limit", h.Tools.SetCoreOffset(240));
        h.MinPower = 500_000;
        Assert.False(Result(h.Tools.SetPowerLimit(450)).GetProperty("success").GetBoolean());
        Assert.Empty(h.Trials);
    }

    [Fact]
    public void Conservative_Xbar_reference_heuristic_still_rejects()
    {
        var h = new Harness();
        // Leave the step within 50 MHz but start with an offset already at 275 MHz.
        var original = FakeDriver.QueryResponder!;
        FakeDriver.QueryResponder = q =>
        {
            var a = original(q);
            if (a != null && BitConverter.ToUInt32(q) == Slots.Info(Slot.ClockClkDomainsGetControl).Shape!.Value.Word)
                Harness.I(a, ClockDomainsLayout.OffsetAt(1, 0xf), 275_000);
            return a;
        };
        Code("policy_limit", h.Tools.AdjustXbar(25));
        Assert.Empty(h.Trials);
    }

    [Fact]
    public async Task Effective_ranges_intersect_configured_bounds_and_do_not_expose_the_generation()
    {
        var h = new Harness(("MIN_POWER_W", "300"), ("MAX_XBAR_MHZ", "275"));
        await h.Approve();
        var report = JsonSerializer.SerializeToElement(TuningTools.PolicyReport(h.Card, h.Policy));
        var ranges = report.GetProperty("effective_ranges");
        Assert.Equal(300, ranges.GetProperty("ordinary_trial_power").GetProperty("min_w").GetDouble());
        Assert.Equal(450, ranges.GetProperty("ordinary_trial_power").GetProperty("max_w").GetDouble());
        Assert.Equal(275, ranges.GetProperty("xbar_offset").GetProperty("max_mhz").GetDouble());
        Assert.Equal("device", report.GetProperty("status").GetProperty("effective_policy").GetString());
        Assert.DoesNotContain(h.Generation, report.GetRawText());
        Assert.Empty(h.Trials);
    }

    [Fact]
    public async Task Policy_return_to_conservative_is_serialized_with_an_explicit_write()
    {
        var h = new Harness();
        await h.Approve();
        using var atWrite = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        h.BeforeTrial = () =>
        {
            atWrite.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
        };
        var writing = Task.Run(() => h.Tools.SetCoreOffset(240));
        Assert.True(atWrite.Wait(TimeSpan.FromSeconds(5)));
        var selecting = Task.Run(() => h.Policy.Approve("conservative", () => throw new Exception("must not read"),
            () => throw new Exception("must not read"), (_, _) => throw new Exception("must not prompt"), default));
        try
        {
            var interval = Task.Delay(50);
            Assert.Same(interval, await Task.WhenAny(selecting, interval));
        }
        finally { finish.Set(); }
        Success(await writing);
        await selecting;
        Assert.False(h.Policy.Snapshot(TuningTools.ReadPolicyScope(h.Card)).Device);
        Assert.Single(h.Trials);
    }

    [Fact]
    public async Task Pending_legacy_voltage_floor_is_not_silently_clipped()
    {
        var h = new Harness(("MIN_MV", "800"));
        Code("consent_required", h.Tools.AdjustCurve(15, 800, 860));
        Assert.Empty(h.Trials);
        await h.Approve("overrides");
        Success(h.Tools.AdjustCurve(15, 800, 810));
        Assert.Single(h.Trials);
    }

    [Theory]
    [InlineData(250_000u, 450_000u)]
    [InlineData(400_000u, 600_000u)]
    [InlineData(200_000u, 300_000u)]
    public async Task Different_board_ranges_are_queried_rather_than_inferred(uint min, uint max)
    {
        var h = new Harness { MinPower = min, MaxPower = max };
        await h.Approve();
        Success(h.Tools.SetPowerLimit(min / 1000.0));
        Success(h.Tools.SetPowerLimit(max / 1000.0));
        Code("device_limit", h.Tools.SetPowerLimit(min / 1000.0 - .001));
        Code("device_limit", h.Tools.SetPowerLimit(max / 1000.0 + .001));
        Assert.Equal(2, h.Trials.Count);
    }

    [Fact]
    public async Task An_older_broker_leaves_conservative_requests_usable_and_cannot_grant_approval()
    {
        var h = new Harness { SupportsPolicyScope = false };
        Success(h.Tools.SetCoreOffset(15));
        await Assert.ThrowsAsync<TuningPolicyException>(() => h.Approve());
        Assert.Single(h.Trials);
        Assert.Null(h.Policy.Snapshot(null).Scope);
    }

    [Fact]
    public async Task A_broker_downgrade_between_the_scope_read_and_write_rejects_the_scoped_verb()
    {
        var h = new Harness();
        await h.Approve();
        h.BeforeTrial = () => h.OldBrokerAtTrial = true;
        Code(BrokerCode.BadRequest, h.Tools.SetCoreOffset(240));
        Assert.Empty(h.Trials);
        Assert.Equal(0, h.UnscopedTrialAttempts); // never retry a relaxed target using old "try"
        Assert.Null(h.Policy.Snapshot(TuningTools.ReadPolicyScope(h.Card)).Scope);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"gpu\":\"other-gpu\",\"session\":\"A\",\"generation\":\"valid\"}")]
    [InlineData("{\"gpu\":\"fake-5080\",\"session\":\"\",\"generation\":\"valid\"}")]
    [InlineData("{\"gpu\":\"fake-5080\",\"session\":\"A\",\"generation\":\"\"}")]
    public async Task Missing_or_mismatched_scope_identity_cannot_grant(string json)
    {
        var h = new Harness();
        h.ScopeReply = () => new(true, Result: JsonDocument.Parse(json).RootElement.Clone());
        await Assert.ThrowsAsync<TuningPolicyException>(() => h.Approve());
        Assert.Null(h.Policy.Snapshot(null).Scope);
        Assert.Empty(h.Trials);
    }

    [Fact]
    public async Task Capability_read_failures_do_not_hide_policy_status_or_make_selection_look_failed()
    {
        var h = new Harness();
        await h.Approve();
        h.ReadFailure = new NvmlException("fake read", -1);
        var report = JsonSerializer.SerializeToElement(TuningTools.PolicyReport(h.Card, h.Policy));
        Assert.Equal("device", report.GetProperty("status").GetProperty("effective_policy").GetString());
        Assert.True(report.GetProperty("device_ranges").GetProperty("curve").TryGetProperty("error", out _));
        Success(await h.Tools.SetTuningPolicy(null!, "conservative"));
        Assert.Empty(h.Trials);
    }

    [Fact]
    public async Task Reported_core_range_includes_the_current_point_offset_budget()
    {
        var h = new Harness();
        await h.Approve();
        Harness.I(FakeDriver.State[FakeVf.ControlWord], VfLayout.OffsetField(80), 500_000);
        var report = JsonSerializer.SerializeToElement(TuningTools.PolicyReport(h.Card, h.Policy));
        var core = report.GetProperty("effective_ranges").GetProperty("core_offset");
        Assert.Equal(500, core.GetProperty("max_mhz").GetDouble());
        Assert.True(core.GetProperty("point_budget_readable").GetBoolean());
        Assert.Empty(h.Trials);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(500_000, true)]
    [InlineData(-1_300_000, true)]
    [InlineData(1_200_000, true)]
    [InlineData(0, false)]
    public void Reported_core_interval_agrees_with_the_existing_domain_budget(int offset, bool readable)
    {
        var core = new ClockOffset(0, 0, 0, 0, 1, true, 200_000, -1_000_000, 1_000_000, 2_000_000, 3_000_000);
        var limits = new VfLimits(core.MinKhz, core.MaxKhz, core.DeltaKhz);
        var budget = new VfBudget(readable ? new (int, int)[] { (0, offset) } : null, readable ? null : "unreadable");
        var range = TuningTools.CoreBudgetRange(core, budget);
        for (int candidate = core.MinKhz; candidate <= core.MaxKhz; candidate += 1000)
            Assert.Equal(budget.Check(limits, candidate) == null, candidate / 1000.0 >= range.Min && candidate / 1000.0 <= range.Max);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shift_refuses_a_missing_firmware_voltage_ceiling_even_with_approval(bool device)
    {
        var h = new Harness { NvvddMaxUv = null };
        if (device) await h.Approve();
        Code("device_limit", h.Tools.ShiftCurve(15));
        var report = JsonSerializer.SerializeToElement(TuningTools.PolicyReport(h.Card, h.Policy));
        Assert.False(report.GetProperty("device_ranges").GetProperty("curve").GetProperty("shift_available").GetBoolean());
        Assert.Empty(h.Trials);
        Assert.Equal(0, FakeDriver.Sets);
    }
}
