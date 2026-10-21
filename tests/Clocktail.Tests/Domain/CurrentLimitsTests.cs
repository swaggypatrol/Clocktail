using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The raw policy table and the power monitor as the RTX 5090 reported them live (617.42, 2026-10-19): sixteen policy
/// records, of which 13 (NVVDD, 600 A) and 14 (MSVDD, 180 A) are current limits on the regulator outputs, and the
/// monitor's channels with their provider and type.
/// </summary>
internal static class CurrentLimitsFixture
{
    /// <summary>{class, monitor channel, unit, min, default, max} per record, as read.</summary>
    public static readonly (uint Class, uint Channel, uint Unit, uint Min, uint Def, uint Max)[] Records =
    [
        (0x15, 3, 0, 1, 5_001_000, 5_001_000), (0x03, 7, 0, 1, 1_001_000, 1_001_000), (0x00, 0, 0, 1, 600_000, 600_000),
        (0x12, 4, 0, 1, 5_001_000, 5_001_000), (0x12, 5, 0, 1, 5_001_000, 5_001_000),
        (0x13, 4, 0, 1, 5_001_000, 5_001_000), (0x13, 5, 0, 1, 5_001_000, 5_001_000),
        (0x17, 0, 0, 1, 600_000, 600_000), (0x18, 0, 0, 400_000, 575_000, 600_000), (0x19, 9, 1, 1, 60_000, 60_000),
        (0x15, 0xb, 1, 1, 5_001_000, 5_001_000), (0x12, 0xd, 1, 1, 5_001_000, 5_001_000), (0x12, 0xc, 1, 1, 5_001_000, 5_001_000),
        (0x13, 0xd, 1, 1, 600_000, 5_001_000), (0x13, 0xc, 1, 1, 180_000, 5_001_000), (0x18, 0, 0, 1, 50_010_000, 50_010_000),
        (0x00, 0, 0, 0, 0, 0),
    ];

    /// <summary>{channel, provider, type} for every channel in the monitor's mask (0x7abf).</summary>
    public static readonly (int Index, uint Provider, uint Type)[] Channels =
    [
        (0, 1, 0xf5), (1, 8, 0xff), (2, 8, 0xda), (3, 1, 0xd6), (4, 1, 0xf6), (5, 1, 0xd4), (7, 1, 0xe8), (9, 8, 0xa9),
        (11, 1, 0xd), (12, 6, 0x10), (13, 6, 0x1), (14, 6, 0x1),
    ];

    public static uint W(int record) => Records[record].Def;

    public static byte[] Info()
    {
        var b = NvClient.NewBuffer(Slot.PowerPolicyGetInfo);
        Put(b, 4, (1u << Records.Length) - 1);
        for (int i = 0; i < Records.Length; i++)
        {
            int o = PolicyTableLayout.InfoRecord(i);
            var r = Records[i];
            Put(b, o, r.Class); Put(b, o + 4, r.Channel); Put(b, o + 8, r.Unit); Put(b, o + 0xc, r.Min); Put(b, o + 0x10, r.Def); Put(b, o + 0x14, r.Max);
        }
        return b;
    }

    public static byte[] Control()
    {
        var b = NvClient.NewBuffer(Slot.PowerPolicyGetControl);
        Put(b, 4, (1u << Records.Length) - 1);
        for (int i = 0; i < Records.Length; i++)
        {
            Put(b, PolicyTableLayout.ControlRecord(i), Records[i].Class);
            Put(b, PolicyTableLayout.ControlLimit(i), Records[i].Def);
        }
        return b;
    }

    public static byte[] Monitor()
    {
        var b = NvClient.NewBuffer(Slot.PowerMonitorGetInfo);
        uint mask = 0;
        foreach (var c in Channels)
        {
            mask |= 1u << c.Index;
            Put(b, PowerMonitorLayout.Provider + c.Index * PowerMonitorLayout.ChannelStride, c.Provider);
            Put(b, PowerMonitorLayout.Type + c.Index * PowerMonitorLayout.ChannelStride, c.Type);
        }
        Put(b, PowerMonitorLayout.InfoMask, mask);
        return b;
    }

    /// <summary>
    /// Wires the three structures into the fake driver the way the driver behaves: CONTROL answers only the records
    /// its mask asks for (the header mask echoed, everything else zero), and a set changes only the records it selects.
    /// </summary>
    public static void Install(byte[]? info = null)
    {
        FakeDriver.State[Word(Slot.PowerPolicyGetInfo)] = info ?? Info();
        FakeDriver.State[Word(Slot.PowerMonitorGetInfo)] = Monitor();
        FakeDriver.State[Word(Slot.PowerPolicyGetControl)] = Control();
        uint control = Word(Slot.PowerPolicyGetControl);
        // Other structures keep the responders a test installed before (a power request table, say).
        var otherQuery = FakeDriver.QueryResponder;
        var otherApply = FakeDriver.RequestApplier;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != control) return otherQuery?.Invoke(q);
            var full = FakeDriver.State[control];
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(4));
            var a = new byte[full.Length];
            full.AsSpan(0, 4).CopyTo(a);
            Put(a, 4, mask);
            for (int i = 0; i < 32; i++)
                if ((mask >> i & 1) != 0 && PolicyTableLayout.ControlRecord(i) + PolicyTableLayout.ControlStride <= a.Length)
                    full.AsSpan(PolicyTableLayout.ControlRecord(i), PolicyTableLayout.ControlStride).CopyTo(a.AsSpan(PolicyTableLayout.ControlRecord(i)));
            return a;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) != control) return otherApply != null ? otherApply(state, req) : req;
            var next = (byte[])state.Clone();
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(4));
            for (int i = 0; i < 32; i++)
                if ((mask >> i & 1) != 0)
                    req.AsSpan(PolicyTableLayout.ControlRecord(i), PolicyTableLayout.ControlStride).CopyTo(next.AsSpan(PolicyTableLayout.ControlRecord(i)));
            return next;
        };
    }

    public static uint Limit(int record) => BinaryPrimitives.ReadUInt32LittleEndian(FakeDriver.State[Word(Slot.PowerPolicyGetControl)].AsSpan(PolicyTableLayout.ControlLimit(record)));

    public static uint Word(Slot s) => Slots.Info(s).Shape!.Value.Word;

    public static void Put(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);

    public static NvClient Client(bool enabled = true)
    {
        var policies = enabled ? WritePolicySet.Default.WithEnabled(Slot.PowerPolicySetControl) : WritePolicySet.Default;
        return new NvClient(FakeDriver.ApiFor([Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo, Slot.PowerMonitorGetStatus],
            [Slot.PowerPolicySetControl]), policies);
    }
}

[Collection("fake-driver")]
public class CurrentLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public CurrentLimitsTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
        FakeDriver.Reset();
    }

    public void Dispose() { FakeDriver.Reset(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void The_rails_limits_are_the_one_current_limit_record_on_each_regulator_output()
    {
        var records = CurrentLimitsParser.ParseInfo(CurrentLimitsFixture.Info());
        var channels = CurrentLimitsParser.ParseMonitorInfo(CurrentLimitsFixture.Monitor());
        Assert.Equal(13, CurrentLimitsParser.Identify(records, channels, Rail.Nvvdd, out _));
        Assert.Equal(14, CurrentLimitsParser.Identify(records, channels, Rail.Msvdd, out _));

        // Records 5 and 6 are current-limit records too, but in mW on 12 V inputs; records 11 and 12 sit on the same
        // regulator outputs with another class. A second mA current limit on an NVVDD output makes NVVDD ambiguous.
        var twice = records.Append(new PolicyRecord(20, 0x13, 14, 1, 1, 500_000, 5_001_000)).ToList();
        Assert.Null(CurrentLimitsParser.Identify(twice, channels, Rail.Nvvdd, out var why));
        Assert.Contains("records 13, 20", why);
        Assert.Equal(14, CurrentLimitsParser.Identify(twice, channels, Rail.Msvdd, out _));
    }

    [Fact]
    public void The_reader_gives_each_rails_bounds_and_the_limit_in_force()
    {
        CurrentLimitsFixture.Install();
        var limits = CurrentLimitsReader.Read(CurrentLimitsFixture.Client(enabled: false), _gpu);
        Assert.Equal(new CurrentLimit(Rail.Nvvdd, 13, 0xd, 1, 600_000, 5_001_000, 600_000), limits.Nvvdd);
        Assert.Equal(new CurrentLimit(Rail.Msvdd, 14, 0xc, 1, 180_000, 5_001_000, 180_000), limits.Msvdd);
    }

    private OwnedWriteResult Write(string key, string value, bool enabled = true)
    {
        var hw = CurrentLimitsFixture.Client(enabled);
        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        var path = WriteRegistry.Find(CurrentLimitWrites.Kind)!;
        var p = path.Prepare(hw, _gpu, key, value, new WriteOptions(Original: writer.AdoptedOriginal(path.Kind, key)));
        Assert.NotNull(p.Plan);
        return writer.Apply(_gpu, p.Cell, p.Plan!, target: value);
    }

    [Fact]
    public void A_lower_limit_lands_on_its_record_alone_and_the_original_goes_back()
    {
        CurrentLimitsFixture.Install();
        var r = Write("nvvdd", "350000");
        Assert.True(r.Tx.Succeeded, r.Tx.Detail);
        Assert.Equal(350_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(180_000u, CurrentLimitsFixture.Limit(14));             // the other rail is untouched
        Assert.Equal(575_000u, CurrentLimitsFixture.Limit(8));              // and so is the board power record
        Assert.Equal(("600000", "350000"), (r.Before, r.After));
        Assert.Equal(RecoveryReason.OwnedByThisProcess, _journal.Inspect().Reason);

        var back = Write("nvvdd", "600000");                                // the original: the hold is released
        Assert.True(back.Tx.Succeeded, back.Tx.Detail);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
        Assert.Equal(RecoveryReason.Nothing, _journal.Inspect().Reason);
    }

    [Fact]
    public void A_limit_may_go_above_the_default_but_never_leaves_the_drivers_range()
    {
        CurrentLimitsFixture.Install();
        var up = Write("msvdd", "250000");                                   // above the 180 A default (the user, 2026-10-21)
        Assert.True(up.Tx.Succeeded, up.Tx.Detail);
        Assert.Equal(250_000u, CurrentLimitsFixture.Limit(14));
        var over = Write("msvdd", "5001001");                                // past the driver's 5001 A
        Assert.False(over.Tx.Succeeded);
        Assert.Contains("outside", over.Tx.Detail);
        var zero = Write("nvvdd", "0");
        Assert.False(zero.Tx.Succeeded);
        Assert.Contains("outside", zero.Tx.Detail);
        Assert.Equal(1, FakeDriver.Sets);
    }

    [Fact]
    public void A_limit_another_tool_raised_can_still_be_put_back()
    {
        var info = CurrentLimitsFixture.Info();
        CurrentLimitsFixture.Install(info);
        CurrentLimitsFixture.Put(FakeDriver.State[CurrentLimitsFixture.Word(Slot.PowerPolicyGetControl)], PolicyTableLayout.ControlLimit(13), 700_000);
        Assert.True(Write("nvvdd", "500000").Tx.Succeeded);                  // the journal's original is 700 A
        var back = Write("nvvdd", "700000");
        Assert.True(back.Tx.Succeeded, back.Tx.Detail);
        Assert.Equal(700_000u, CurrentLimitsFixture.Limit(13));
    }

    [Fact]
    public void The_setter_is_off_unless_a_request_enables_it()
    {
        CurrentLimitsFixture.Install();
        var r = Write("nvvdd", "350000", enabled: false);
        Assert.False(r.Tx.Succeeded);
        Assert.Equal(600_000u, CurrentLimitsFixture.Limit(13));
    }

    [Fact]
    public void The_write_policy_lets_through_one_current_limit_record_and_nothing_else()
    {
        var policy = WritePolicySet.Default.WithEnabled(Slot.PowerPolicySetControl);
        byte[] One(int record, uint cls, uint limit)
        {
            var b = NvClient.NewBuffer(Slot.PowerPolicySetControl);
            CurrentLimitsFixture.Put(b, 4, 1u << record);
            CurrentLimitsFixture.Put(b, PolicyTableLayout.ControlRecord(record), cls);
            CurrentLimitsFixture.Put(b, PolicyTableLayout.ControlLimit(record), limit);
            return b;
        }
        Assert.Null(policy.Check(Slot.PowerPolicySetControl, One(13, 0x13, 350_000)));
        Assert.Contains("class 0x18", policy.Check(Slot.PowerPolicySetControl, One(8, 0x18, 380_000)));   // the 400 W floor's record
        Assert.Contains("limit", policy.Check(Slot.PowerPolicySetControl, One(13, 0x13, 0)));
        var two = One(13, 0x13, 350_000);
        CurrentLimitsFixture.Put(two, 4, 3u << 13);
        Assert.Contains("exactly one", policy.Check(Slot.PowerPolicySetControl, two));
        var stray = One(13, 0x13, 350_000);
        stray[PolicyTableLayout.ControlRecord(14) + 4] = 1;
        Assert.Contains("must be zero", policy.Check(Slot.PowerPolicySetControl, stray));
        Assert.Contains("disabled", WritePolicySet.Default.Check(Slot.PowerPolicySetControl, One(13, 0x13, 350_000)));
    }
}
