using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Power;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>Two plausible driver models: additive P0 + point offsets, and a shared P0/VF control surface.
 /// These tests establish software behavior, not which native write on the user's card caused the loss.</summary>
[Collection("fake-driver")]
public sealed class EffectiveP0Tests
{
    internal sealed class Rig : IGpuTarget, IGpuTargets, IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("N"));
        private readonly FakeHost _host = new();
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session { get; } = new(1, "fake");
        public OwnershipJournal Journal { get; }
        public FakeHost Host => _host;
        public ApplySentinel Sentinel { get; }
        public IReadOnlyList<string> Ids => [StableId];
        public IGpuTarget? Resolve(string? id) => id == null || id == StableId ? this : null;
        public readonly FakeVf Vf = new();
        public readonly NvClient Hw;
        public readonly BrokerDispatcher Broker;
        public bool RefuseNonzeroCurve;
        public bool FailMemory;
        public bool FailCore;
        public bool CorruptNextStatus;
        public bool RefuseRollback;
        public bool WrongEffective;
        public Action? BeforeP0;
        public uint PowerMw = 400_000;
        public string ProfilePath => DailyProfile.PathBeside(Journal.Path);

        public Rig(bool shared, bool legacy = false, bool beginSession = true)
        {
            Directory.CreateDirectory(_dir);
            Journal = new(Path.Combine(_dir, "ownership.json"), _host, StableId);
            Sentinel = new(Path.Combine(_dir, "apply.lock"));
            if (legacy) { Vf.Mode = VfMode.Legacy; Vf.Editable = 80; Vf.MaskPoints = 84; }
            Vf.Install();
            var powerInfo = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
            W(powerInfo, 4, 1);
            int powerAt = PowerRequestsLayout.InfoEntry(0);
            W(powerInfo, powerAt, 1); W(powerInfo, powerAt + 8, 250_000); W(powerInfo, powerAt + 12, 400_000); W(powerInfo, powerAt + 16, 450_000);
            FakeDriver.State[BitConverter.ToUInt32(powerInfo)] = powerInfo;
            FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
            var domainInfo = ClockDomainsFixture.Topology();
            var domainControl = ClockDomainsFixture.FullControl();
            FakeDriver.State[BitConverter.ToUInt32(domainInfo)] = domainInfo;
            FakeDriver.State[BitConverter.ToUInt32(domainControl)] = domainControl;
            uint powerWord = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
            uint domainsWord = Slots.Info(Slot.ClockClkDomainsGetControl).Shape!.Value.Word;
            var vfQuery = FakeDriver.QueryResponder!;
            FakeDriver.QueryResponder = query =>
            {
                uint word = BitConverter.ToUInt32(query);
                if (word == powerWord)
                {
                    var answer = (byte[])query.Clone();
                    W(answer, PowerRequestsLayout.ControlEntry(0), PowerMw);
                    return answer;
                }
                if (word != domainsWord) return vfQuery(query);
                var control = FakeDriver.State[domainsWord];
                var result = new byte[control.Length];
                control.AsSpan(0, ClockDomainsLayout.ControlBase).CopyTo(result);
                uint mask = BitConverter.ToUInt32(query, 8);
                W(result, 8, mask);
                for (int i = 0; i < 32; i++)
                    if ((mask >> i & 1) != 0) control.AsSpan(ClockDomainsLayout.ControlAt(i), ClockDomainsLayout.ControlEntry).CopyTo(result.AsSpan(ClockDomainsLayout.ControlAt(i)));
                return result;
            };
            var p = FakeDriver.State[FakeVf.PstatesWord];
            W(p, 0xc, 2);
            int mem = Pstates20Layout.Clock(0, 1);
            W(p, mem, 4); W(p, mem + 8, 1); I(p, mem + 0x10, -1_000_000); I(p, mem + 0x14, 3_000_000);
            Vf.Effective = (i, o) => checked((uint)((long)Vf.Base(i) + o + (shared ? 0 : Core)));
            var vfSet = FakeDriver.RequestApplier!;
            FakeDriver.RequestApplier = (state, req) =>
            {
                uint word = BitConverter.ToUInt32(req);
                if (word == powerWord) { PowerMw = BitConverter.ToUInt32(req, PowerRequestsLayout.ControlEntry(0)); return state; }
                if (word == domainsWord)
                {
                    var result = (byte[])state.Clone();
                    uint mask = BitConverter.ToUInt32(req, 8);
                    for (int i = 0; i < 32; i++)
                        if ((mask >> i & 1) != 0) req.AsSpan(ClockDomainsLayout.ControlAt(i), ClockDomainsLayout.ControlEntry).CopyTo(result.AsSpan(ClockDomainsLayout.ControlAt(i)));
                    return result;
                }
                if (BitConverter.ToUInt32(req) == FakeVf.ControlWord)
                    return RefuseNonzeroCurve && Enumerable.Range(0, 127).Any(i => FakeVf.Off(req, i) != 0) ? state : vfSet(state, req);
                if (BitConverter.ToUInt32(req) != FakeVf.PstatesWord) return state;
                var domain = (ClockDomain)BitConverter.ToUInt32(req, 0x1c);
                int value = BitConverter.ToInt32(req, 0x28);
                BeforeP0?.Invoke();
                var next = (byte[])state.Clone();
                if (!(domain == ClockDomain.Memory && FailMemory || domain == ClockDomain.Core && FailCore || RefuseRollback && value == 0))
                    I(next, Pstates20Layout.Clock(0, domain == ClockDomain.Core ? 0 : 1) + 0xc, value);
                // Shared model: core writes mirror into point controls. Memory requests wipe them independently.
                for (int i = 0; i < 127; i++)
                    I(FakeDriver.State[FakeVf.ControlWord], VfLayout.OffsetField(i),
                        shared && domain == ClockDomain.Core ? value : 0);
                return next;
            };
            Vf.StatusHook = (status, _) =>
            {
                if (WrongEffective && Core != 0)
                    for (int i = 0; i < 127; i++) W(status, VfLayout.Status(i) + 4, Vf.Base(i));
                if (!CorruptNextStatus || Core == 0 && Memory == 0) return;
                CorruptNextStatus = false;
                W(status, VfLayout.Status(0) + 4, 0);
            };
            Hw = new(FakeDriver.ApiFor(
                [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus,
                 Slot.ClockClientClkVfPointsGetControl, Slot.GetPstates20, Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl,
                 Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl],
                [Slot.ClockClientClkVfPointsSetControl, Slot.SetPstates20, Slot.ClientPwrPoliciesSetControl, Slot.ClockClkDomainsSetControl]),
                WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl));
            Broker = new(Hw, this, _host, true);
            if (beginSession) Assert.True(Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"software regression"}""")).Ok);
        }

        private static void W(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        public static void I(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
        public int Core => BitConverter.ToInt32(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 0) + 0xc);
        public int Memory => BitConverter.ToInt32(FakeDriver.State[FakeVf.PstatesWord], Pstates20Layout.Clock(0, 1) + 0xc);
        public VfCurveSnapshot Curve => VfReader.Read(Hw, Session);
        public long Displacement(int i = 91) => (long)Curve.Points[i].EffectiveKhz - Vf.Base(i);
        public BrokerResponse CoreTo(int khz) => Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "try", Value: JsonSerializer.Serialize(new
        {
            curve = VfOffsets.Encode(Curve.Offsets()), keep_curve = true, change = "core",
            writes = new[] { new { kind = ClockOffsets.Kind, key = "core", value = khz.ToString(CultureInfo.InvariantCulture) } },
        })));
        public BrokerResponse MemoryTo(int khz) => Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "try", Value: JsonSerializer.Serialize(new
        { curve = VfOffsets.Encode(Curve.Offsets()), keep_curve = true, change = "memory", memory_khz = khz })));
        public void ExternalCurve(int khz)
        {
            for (int i = 0; i < 127; i++) I(FakeDriver.State[FakeVf.ControlWord], VfLayout.OffsetField(i), khz);
        }
        public void ExternalShape() { for (int i = 0; i < 127; i++) I(FakeDriver.State[FakeVf.ControlWord], VfLayout.OffsetField(i), i < 60 ? -30_000 : 90_000); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_P0_failure_restores_both_domains_and_curve_last(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalShape();
        var before = r.Curve;
        var result = r.Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "try", Value: JsonSerializer.Serialize(new
        {
            curve = VfOffsets.Encode(before.Offsets()), keep_curve = true, change = "two domains",
            writes = new[] {
                new { kind = ClockOffsets.Kind, key = "core", value = "240000" },
                new { kind = ClockOffsets.Kind, key = "memory", value = "4000000" }, // outside the firmware range
            },
        })));
        Assert.False(result.Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Memory);
        Assert.Equal(before.Offsets(), r.Curve.Offsets());
        Assert.Null(VfEffectiveCurve.Mismatch(before, r.Curve));
        Assert.False(r.Sentinel.IsBlocked(out _));
        Assert.Empty(r.Journal.Inspect().Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonuniform_shape_survives_raising_lowering_and_return_to_original(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalShape();
        var original = r.Curve;
        foreach (int target in new[] { 240_000, 120_000, -15_000, 0 })
        {
            var result = r.CoreTo(target);
            Assert.True(result.Ok, result.Error);
            Assert.Null(VfEffectiveCurve.Mismatch(original, r.Curve, target));
            for (int i = 0; i < 127; i++) Assert.Equal((i < 60 ? -30_000 : 90_000) + target, r.Displacement(i));
        }
        Assert.Empty(r.Journal.Inspect().Items);
    }

    [Fact]
    public void A_later_failure_with_unverified_coupled_rollback_reports_unknown()
    {
        using var r = new Rig(true);
        r.ExternalShape();
        r.RefuseRollback = true;
        var result = r.Broker.Dispatch(new(BrokerRequest.OpExperiment, Kind: "try", Value: JsonSerializer.Serialize(new
        {
            curve = VfOffsets.Encode(r.Curve.Offsets()), keep_curve = true, change = "two domains",
            writes = new[] {
                new { kind = ClockOffsets.Kind, key = "core", value = "240000" },
                new { kind = ClockOffsets.Kind, key = "memory", value = "4000000" },
            },
        })));
        Assert.False(result.Ok);
        Assert.Equal(Clocktail.Mcp.WriteOutcome.Unknown, Clocktail.Mcp.WriteOutcome.Of(result.Ok, result.Code, result.Result));
        Assert.True(r.Sentinel.IsBlocked(out _));
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == ClockOffsets.Kind && i.Original == "0");
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "-30000*60,90000*67");
    }

    [Fact]
    public void Regional_thermal_base_drift_is_not_added_to_saved_offsets()
    {
        using var r = new Rig(true);
        var stock = r.Vf.Base;
        r.BeforeP0 = () => r.Vf.Base = i => stock(i) - 15_000;
        Assert.True(r.CoreTo(240_000).Ok);
        Assert.Equal(Enumerable.Repeat(240_000, 127), r.Curve.Offsets());
        Assert.Equal(stock(91) - 15_000 + 240_000, r.Curve.Points[91].EffectiveKhz);
    }

    [Fact]
    public void Adaptive_repair_keeps_the_global_plus_point_firmware_bound()
    {
        using var r = new Rig(true);
        // Raw +600 is legal, but this hypothetical shared model would need +600 point controls too.
        Assert.False(r.CoreTo(600_000).Ok); // +1200 would exceed the unchanged +1000 combined validator
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_memory_uses_direct_effective_frequencies_and_preserves_controls(bool shared)
    {
        using var r = new Rig(shared, legacy: true);
        r.ExternalCurve(90_000);
        var before = r.Curve;
        Assert.True(r.MemoryTo(750_000).Ok);
        Assert.Equal(before.Offsets(), r.Curve.Offsets());
        Assert.Null(VfEffectiveCurve.Mismatch(before, r.Curve));
    }

    [Fact]
    public void Legacy_overlap_is_refused_without_treating_derived_base_as_independent()
    {
        using var r = new Rig(true, legacy: true);
        var result = r.CoreTo(240_000);
        Assert.False(result.Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement(60));
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Memory_alone_preserves_an_external_curve_and_its_original(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalCurve(90_000);
        var result = r.MemoryTo(750_000);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(90_000, r.Displacement());
        Assert.Equal("90000*127", Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refused_core_restores_native_curve_side_effects(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalCurve(90_000);
        r.FailCore = true;
        Assert.False(r.CoreTo(240_000).Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(90_000, r.Displacement());
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refused_memory_restores_previous_core_curve_and_trial(bool shared)
    {
        using var r = new Rig(shared);
        Assert.True(r.CoreTo(240_000).Ok);
        var prior = Experiment.Load(Experiment.PathBeside(r.Journal.Path))!.Trial;
        r.FailMemory = true;
        Assert.False(r.MemoryTo(750_000).Ok);
        Assert.Equal(0, r.Memory);
        Assert.Equal(240_000, r.Core);
        Assert.Equal(240_000, r.Displacement());
        Assert.Equal(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(Experiment.Load(Experiment.PathBeside(r.Journal.Path))!.Trial));
    }

    [Fact]
    public void Refused_curve_repair_rolls_P0_back_before_restoring_curve()
    {
        using var r = new Rig(true);
        r.RefuseNonzeroCurve = true;
        // Native core write alone supplies +240 here; memory then wipes it and its repair is refused.
        Assert.True(r.CoreTo(240_000).Ok);
        Assert.False(r.MemoryTo(750_000).Ok);
        // The original curve cannot be restored while the setter refuses it: retain debt and sentinel.
        Assert.True(r.Sentinel.IsBlocked(out _));
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "0*127");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_effective_readback_is_a_failure_and_rolls_back(bool shared)
    {
        using var r = new Rig(shared);
        r.CorruptNextStatus = true;
        var result = r.CoreTo(240_000);
        Assert.False(result.Ok);
        Assert.Equal(Clocktail.Mcp.WriteOutcome.None, Clocktail.Mcp.WriteOutcome.Of(result.Ok, result.Code, result.Result));
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Offset_readback_without_effective_change_cannot_report_success()
    {
        using var r = new Rig(true);
        r.WrongEffective = true;
        Assert.False(r.CoreTo(240_000).Ok);
        Assert.Equal(0, r.Core);
        Assert.Equal(0, r.Displacement());
    }

    [Fact]
    public void Failed_P0_rollback_retains_the_original_and_blocks_further_writes()
    {
        using var r = new Rig(true);
        r.WrongEffective = true;
        r.RefuseRollback = true;
        var result = r.CoreTo(240_000);
        Assert.False(result.Ok);
        Assert.Equal(Clocktail.Mcp.WriteOutcome.Unknown, Clocktail.Mcp.WriteOutcome.Of(result.Ok, result.Code, result.Result));
        Assert.Equal(240_000, r.Core);
        Assert.True(r.Sentinel.IsBlocked(out _));
        Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == ClockOffsets.Kind && i.Original == "0");
    }

    [Fact]
    public void Original_curve_is_on_disk_before_the_first_native_P0_write()
    {
        using var r = new Rig(true);
        r.BeforeP0 = () =>
        {
            Assert.True(r.Sentinel.IsBlocked(out _));
            Assert.Equal("0*127", Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
        };
        Assert.True(r.CoreTo(240_000).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_native_P0_readback_can_still_restore_the_whole_coupled_state(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalShape();
        var before = r.Curve;
        r.BeforeP0 = () => { r.BeforeP0 = null; FakeDriver.FailNextGets = 1; };
        var result = r.CoreTo(240_000);
        Assert.False(result.Ok);
        Assert.Equal(Clocktail.Mcp.WriteOutcome.None, Clocktail.Mcp.WriteOutcome.Of(result.Ok, result.Code, result.Result));
        Assert.Equal(0, r.Core);
        Assert.Null(VfEffectiveCurve.Mismatch(before, r.Curve));
        Assert.Equal(before.Offsets(), r.Curve.Offsets());
        Assert.Empty(r.Journal.Inspect().Items);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Live_ownership_refuses_before_any_curve_or_P0_write()
    {
        using var r = new Rig(true);
        var other = new FakeHost { Pid = 200 };
        new OwnershipJournal(r.Journal.Path, other, r.StableId).Claim(new(VfWrites.Kind, VfWrites.CoreKey, "15000*127", other.Now));
        r.Host.Alive.Add(200);
        Assert.False(r.CoreTo(240_000).Ok);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Equal(RecoveryReason.OwnedByLiveProcess, r.Journal.Inspect().Reason);
        Assert.False(r.Sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Adopted_original_wins_over_the_current_curve_and_native_side_effect()
    {
        using var r = new Rig(true);
        var other = new FakeHost { Pid = 200 };
        new OwnershipJournal(r.Journal.Path, other, r.StableId).Claim(new(VfWrites.Kind, VfWrites.CoreKey, "15000*127", other.Now));
        r.ExternalCurve(45_000);
        Assert.True(r.CoreTo(60_000).Ok);
        Assert.Equal(105_000, r.Displacement());
        Assert.Equal("15000*127", Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Equal(15_000, r.Displacement());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shaped_daily_save_and_reload_keep_the_explicit_curve_dependency(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalShape();
        var original = r.Curve;
        Assert.True(r.CoreTo(240_000).Ok);
        Assert.True(r.MemoryTo(750_000).Ok);
        var tuned = r.Curve;
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpRecover)).Ok);
        Assert.Null(VfEffectiveCurve.Mismatch(original, r.Curve));
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.Equal(tuned.Offsets(), r.Curve.Offsets());
        Assert.Null(VfEffectiveCurve.Mismatch(original, r.Curve, 240_000));
        Assert.True(r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.Contains(DailyProfile.Load(r.ProfilePath)!.Writes, w => w.Kind == VfWrites.Kind && w.Value == VfOffsets.Encode(tuned.Offsets()));
        Assert.Equal(VfOffsets.Encode(original.Offsets()), Assert.Single(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind).Original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Core_then_memory_are_verified_separately_by_effective_frequency(bool shared)
    {
        using var r = new Rig(shared);
        var core = r.CoreTo(240_000);
        Assert.True(core.Ok, core.Error);
        Assert.Equal(240_000, r.Core);
        Assert.Equal(240_000, r.Displacement()); // isolate the core write before any memory call
        var memory = r.MemoryTo(3_000_000);
        Assert.True(memory.Ok, memory.Error);
        Assert.Equal(3_000_000, r.Memory);
        Assert.Equal(240_000, r.Displacement()); // memory must preserve effective tuning
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_curve_shape_and_lowering_core_offsets_preserve_the_intended_delta(bool shared)
    {
        using var r = new Rig(shared);
        r.ExternalCurve(90_000);
        Assert.True(r.CoreTo(240_000).Ok);
        Assert.Equal(330_000, r.Displacement());
        Assert.True(r.CoreTo(120_000).Ok);
        Assert.Equal(210_000, r.Displacement());
        Assert.True(r.MemoryTo(750_000).Ok);
        Assert.Equal(210_000, r.Displacement());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keep_and_software_reload_preserve_the_verified_curve_and_ownership(bool shared)
    {
        using var r = new Rig(shared);
        Assert.True(r.CoreTo(240_000).Ok);
        Assert.True(r.MemoryTo(3_000_000).Ok);
        var keep = r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.True(keep.Ok, keep.Error);
        var profile = DailyProfile.Load(r.ProfilePath)!;
        Assert.Contains(profile.Writes, w => w.Kind == VfWrites.Kind); // dependency survives even if no explicit VF set was needed
        if (shared) Assert.Contains(r.Journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == "0*127");
        var recover = r.Broker.Dispatch(new(BrokerRequest.OpRecover));
        Assert.True(recover.Ok, recover.Error);
        Assert.Equal(0, r.Displacement());
        var load = r.Broker.Dispatch(new(BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(load.Ok, load.Error);
        Assert.Equal(240_000, r.Core);
        Assert.Equal(3_000_000, r.Memory);
        Assert.Equal(240_000, r.Displacement());
        Assert.Contains(DailyProfile.Load(r.ProfilePath)!.Writes, w => w.Kind == VfWrites.Kind);
    }
}
