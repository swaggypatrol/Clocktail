using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The RTX 5090 FE's firmware fan policies as read live (2026-10-17 and 2026-10-20, driver 617.42) on the fake driver:
/// Info mask 0x33, policies 0 and 1 editable, 4 and 5 the backstop, every undecoded word as the card holds it. Status
/// and Control answer the mask they are asked (one bit, one policy), and a Set changes only the policies its mask names.
/// </summary>
internal static class FanPolicyFixture
{
    public static uint Word(Slot s) => Slots.Info(s).Shape!.Value.Word;
    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    public static readonly int[] Present = [0, 1, 4, 5];
    public const string Stock = "50:20:1100,83:50:1775,88:100:2875";
    public const string Backstop = "93:20:1100,98:50:1775,103:100:2875";

    public static byte[] Info()
    {
        var b = NvClient.NewBuffer(Slot.FanPolicyGetInfo);
        W(b, 4, 0x33); W(b, 8, 0xffff);
        byte[] link = [1, 1, 0, 0, 0, 2];
        foreach (int i in Present)
        {
            int e = FanPolicyLayout.InfoEntry(i);
            W(b, e, 3); b[e + 8] = link[i];
            b[e + 0x29] = 1; b[e + 0x2a] = 0x88; b[e + 0x2b] = 0x13; b[e + 0x2d] = 1;
            b[e + 0x2f] = (byte)(i < 2 ? 1 : 0);
        }
        return b;
    }

    public static byte[] Control()
    {
        var b = NvClient.NewBuffer(Slot.FanPolicyGetControl);
        W(b, 4, 0x33);
        foreach (int i in Present)
        {
            int e = FanPolicyLayout.ControlEntry(i);
            W(b, e, 3);
            uint[] before = [0x42, 0xffbe, 0x2b23, 0x10a, 0x400, 0x1000];
            for (int k = 0; k < before.Length; k++) W(b, e + 0x24 + 4 * k, before[k]);
            uint t0 = i < 2 ? 0x3200u : 0x5d00u;
            uint[] points = [t0, 0x3333, 1100, t0 + 0x2100 - (i < 2 ? 0u : 0x1c00u), 0x8000, 1775, t0 + 0x2600 - (i < 2 ? 0u : 0x1c00u), 0x10000, 2875];
            for (int k = 0; k < points.Length; k++) W(b, e + 0x3c + 4 * k, points[k]);
            uint[] after = [1, i < 2 ? 0x2b00u : i == 4 ? 0x3f00u : 0x3c00u, i < 2 ? 0x3200u : 0x4600u, 50000, 100000, 61];
            for (int k = 0; k < after.Length; k++) W(b, e + 0x60 + 4 * k, after[k]);
        }
        return b;
    }

    public static byte[] Status()
    {
        var b = NvClient.NewBuffer(Slot.FanPolicyGetStatus);
        W(b, 4, 0x33);
        foreach (int i in Present)
        {
            int e = FanPolicyLayout.StatusEntry(i);
            W(b, e, 3); W(b, e + 0x24, 0x9150000); W(b, e + 0x28, 0xb0b0000); W(b, e + 0x30, 1100); W(b, e + 0x34, 0x245d); W(b, e + 0x38, 0xc17b);
        }
        return b;
    }

    /// <summary>The masks the Set calls named, in order.</summary>
    public static readonly List<uint> SetMasks = [];

    /// <summary>Seeds the three structures and wires the four slots.</summary>
    public static NvClient Install()
    {
        FakeDriver.Reset();
        SetMasks.Clear();
        FakeDriver.State[Word(Slot.FanPolicyGetInfo)] = Info();
        FakeDriver.State[Word(Slot.FanPolicyGetControl)] = Control();
        FakeDriver.State[Word(Slot.FanPolicyGetStatus)] = Status();
        uint control = Word(Slot.FanPolicyGetControl), status = Word(Slot.FanPolicyGetStatus);
        FakeDriver.QueryResponder = q =>
        {
            uint word = U(q, 0);
            if (word != control && word != status) return null;
            var all = FakeDriver.State[word];
            var a = new byte[all.Length];
            uint asked = U(q, 4);
            W(a, 0, word); W(a, 4, asked);
            int stride = word == control ? FanPolicyLayout.ControlStride : FanPolicyLayout.StatusStride;
            for (int i = 0; i < 32; i++)
            {
                if ((asked >> i & 1) == 0) continue;
                int e = word == control ? FanPolicyLayout.ControlEntry(i) : FanPolicyLayout.StatusEntry(i);
                all.AsSpan(e, stride).CopyTo(a.AsSpan(e));
            }
            return a;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            uint mask = U(req, 4);
            SetMasks.Add(mask);
            for (int i = 0; i < 32; i++)
                if ((mask >> i & 1) != 0)
                    req.AsSpan(FanPolicyLayout.ControlEntry(i), FanPolicyLayout.ControlStride).CopyTo(state.AsSpan(FanPolicyLayout.ControlEntry(i)));
            return state;
        };
        return new NvClient(FakeDriver.ApiFor([Slot.FanPolicyGetInfo, Slot.FanPolicyGetStatus, Slot.FanPolicyGetControl], [Slot.FanPolicySetControl]));
    }

    public static byte[] Stored => FakeDriver.State[Word(Slot.FanPolicyGetControl)];
    public static string CurveOf(int policy) => FanCurveText.Format(FanPolicyParser.PointsIn(Stored, policy));
    public static byte[] Entry(byte[] control, int policy) => control.AsSpan(FanPolicyLayout.ControlEntry(policy), FanPolicyLayout.ControlStride).ToArray();
}

[Collection("fake-driver")]
public class FanPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public FanPolicyTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static WritePath Path_ => WriteRegistry.Find(FanPolicyWrites.Kind)!;
    private OwnedWriter Writer(NvClient hw) => new(hw, _journal, _sentinel, _host);

    private OwnedWriteResult Write(NvClient hw, string key, string value, string? origin = null)
    {
        var p = Path_.Prepare(hw, _gpu, key, value, new WriteOptions(Original: origin ?? _journal.Load()?.Items.FirstOrDefault(i => i.Key == key)?.Original));
        Assert.NotNull(p.Plan);
        return Writer(hw).Apply(_gpu, p.Cell, p.Plan!);
    }

    private static byte[] Points(params (int T, uint D, uint R)[] p) =>
        FanCurvePoint.Encode(p.Select(x => new FanCurvePoint(x.T, x.D, x.R)).ToList());

    [Fact]
    public void Layout_sizes_are_the_arithmetic_of_header_plus_32_entries()
    {
        Assert.Equal(new Versioned(FanPolicyLayout.InfoEntry(32), 1), Slots.Info(Slot.FanPolicyGetInfo).Shape);
        Assert.Equal(new Versioned(FanPolicyLayout.StatusEntry(32), 1), Slots.Info(Slot.FanPolicyGetStatus).Shape);
        Assert.Equal(new Versioned(FanPolicyLayout.ControlEntry(32), 2), Slots.Info(Slot.FanPolicyGetControl).Shape);
        Assert.Equal(0x214acu, Slots.Info(Slot.FanPolicySetControl).Shape!.Value.Word);
        Assert.Equal(Slots.Info(Slot.FanPolicyGetControl).Shape, Slots.Info(Slot.FanPolicySetControl).Shape);
    }

    [Fact]
    public void Info_lists_the_four_policies_and_only_0_and_1_are_editable()
    {
        var info = FanPolicyParser.ParseInfo(FanPolicyFixture.Info());
        Assert.Equal(0x33u, info.Mask);
        Assert.Equal([0, 1, 4, 5], info.Policies.Select(p => p.Index));
        Assert.Equal([0, 1], info.Editable.Select(p => p.Index));
        Assert.Equal([1, 1, 0, 2], info.Policies.Select(p => (int)p.Link));
        Assert.All(info.Policies, p => Assert.Equal(3, p.Type));
    }

    [Fact]
    public void Info_is_held_to_what_the_original_reader_refuses()
    {
        var absent = FanPolicyFixture.Info(); W(absent, FanPolicyLayout.InfoEntry(5), 0xffffffff);
        Assert.Equal([0, 1, 4], FanPolicyParser.ParseInfo(absent).Policies.Select(p => p.Index));   // type -1: not a policy
        var odd = FanPolicyFixture.Info(); W(odd, FanPolicyLayout.InfoEntry(4), 4);
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseInfo(odd));
        var flag = FanPolicyFixture.Info(); flag[FanPolicyLayout.InfoEntry(1) + FanPolicyLayout.InfoEditable] = 2;
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseInfo(flag));
        var two = FanPolicyFixture.Info(); W(two, FanPolicyLayout.InfoEntry(4), 2);   // a type-2 policy is always editable
        Assert.Contains(FanPolicyParser.ParseInfo(two).Editable, p => p.Index == 4);
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseInfo(new byte[0x928]));
    }

    [Fact]
    public void Control_reads_the_live_stock_curves()
    {
        var info = FanPolicyParser.ParseInfo(FanPolicyFixture.Info());
        var all = FanPolicyParser.ParseControl(FanPolicyFixture.Control(), info, 0x33);
        Assert.Equal([FanPolicyFixture.Stock, FanPolicyFixture.Stock, FanPolicyFixture.Backstop, FanPolicyFixture.Backstop], all.Select(p => p.Curve));
        var p0 = all[0].Points;
        Assert.Equal(new FanCurvePoint(0x3200, 0x3333, 1100), p0[0]);
        Assert.Equal(50m, p0[0].TempC);
        Assert.Equal(50m, p0[1].DutyPct);

        var echo = FanPolicyFixture.Control(); W(echo, 4, 0x3);
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseControl(echo, info, 0x33));
        var type = FanPolicyFixture.Control(); W(type, FanPolicyLayout.ControlEntry(1), 2);
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseControl(type, info, 0x33));
        var flags = FanPolicyFixture.Control(); flags[8] = 1;
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseControl(flags, info, 0x33));
        var falling = FanPolicyFixture.Control(); W(falling, FanPolicyLayout.PointsAt(4) + 0x10, 0x2000);
        Assert.Throws<InvalidDataException>(() => FanPolicyParser.ParseControl(falling, info, 0x33));
    }

    [Fact]
    public void Status_is_read_per_policy()
    {
        var info = FanPolicyParser.ParseInfo(FanPolicyFixture.Info());
        var s = FanPolicyParser.ParseStatus(FanPolicyFixture.Status(), info, 0x33);
        Assert.Equal([0, 1, 4, 5], s.Select(x => x.Index));
        Assert.Equal(36.328125, s[0].TemperatureC, 6);
        Assert.Equal(44.171875, s[0].CurveTemperatureC, 6);
        Assert.Equal(1100u, s[0].TargetRpm);
        Assert.Equal(36.363, s[0].FilteredTemperatureC, 3);
    }

    /// <summary>The canonical text is exact: every Q8.8 temperature and every 16.16 duty comes back to its raw value.</summary>
    [Fact]
    public void Curve_text_round_trips_every_raw_value()
    {
        for (uint d = 0; d <= FanCurveRule.DutyFull; d++)
        {
            int t = (int)(d * 7 % 0x8000) - 0x2000;
            var pts = new[] { new FanCurvePoint(t, d, 1100), new FanCurvePoint(t + 1, d, 1100), new FanCurvePoint(t + 2, d, 0x7fff) };
            Assert.Equal(pts, FanCurveText.ParseExact(FanCurveText.Format(pts)));
        }
        Assert.Equal(FanPolicyFixture.Stock, FanCurveText.Format(FanCurveText.ParseExact(FanPolicyFixture.Stock)));
        Assert.Equal("50:20,79:60,86:100", FanCurveText.Format(FanCurveText.Parse(" 50:20, 79:60 ,86:100")));
        Assert.Equal(new RequestedPoint(0x4f00, 0x999a, null), FanCurveText.Parse("50:20,79:60,86:100")[1]);
        Assert.Throws<FormatException>(() => FanCurveText.Parse("50:20,86:100"));
        Assert.Throws<FormatException>(() => FanCurveText.Parse("50:20,79,86:100"));
        Assert.Throws<FormatException>(() => FanCurveText.Parse("50:-5,79:60,86:100"));
        Assert.Throws<FormatException>(() => FanCurveText.Parse("50:20,79:60:fast,86:100"));
        Assert.Throws<FormatException>(() => FanCurveText.ParseExact("50:20,79:60,86:100"));
        Assert.True(FanCurveText.IsStock(" Stock "));
    }

    [Fact]
    public void A_point_without_rpm_takes_the_policys_own_duty_to_rpm_pairing()
    {
        var stock = FanCurveText.ParseExact(FanPolicyFixture.Stock);
        Assert.Equal(1995u, FanPolicyRules.RpmFor(FanCurvePoint.DutyOf(60), stock));
        Assert.Equal(1100u, FanPolicyRules.RpmFor(FanCurvePoint.DutyOf(20), stock));
        Assert.Equal(1100u, FanPolicyRules.RpmFor(FanCurvePoint.DutyOf(10), stock));    // below the first point: its rpm
        Assert.Equal(1775u, FanPolicyRules.RpmFor(0x8000, stock));
        Assert.Equal(2875u, FanPolicyRules.RpmFor(FanCurveRule.DutyFull, stock));
        Assert.Equal(1325u, FanPolicyRules.RpmFor(FanCurvePoint.DutyOf(30), stock));    // 1100 + 10/30 x 675
        var resolved = FanPolicyRules.Resolve(FanCurveText.Parse("50:20,79:60:2000,86:100"), stock);
        Assert.Equal("50:20:1100,79:60:2000,86:100:2875", FanCurveText.Format(resolved));
    }

    [Fact]
    public void The_point_check_is_the_originals()
    {
        Assert.Null(FanCurveRule.Check(Points((0x3200, 0x3333, 1100), (0x5300, 0x8000, 1775), (0x5800, 0x10000, 2875))));
        Assert.Null(FanCurveRule.Check(Points((0x3200, 0x8000, 1775), (0x5300, 0x8000, 1775), (0x5800, 0x8000, 1775))));   // level is allowed
        Assert.Contains("temperatures must increase", FanCurveRule.Check(Points((0x3200, 0x3333, 1100), (0x3200, 0x8000, 1775), (0x5800, 0x10000, 2875))));
        Assert.Contains("temperatures must increase", FanCurveRule.Check(Points((0x5300, 0x3333, 1100), (0x3200, 0x8000, 1775), (0x5800, 0x10000, 2875))));
        Assert.Contains("duty may not fall", FanCurveRule.Check(Points((0x3200, 0x8000, 1100), (0x5300, 0x3333, 1775), (0x5800, 0x10000, 2875))));
        Assert.Contains("exceeds 100 %", FanCurveRule.Check(Points((0x3200, 0x3333, 1100), (0x5300, 0x8000, 1775), (0x5800, 0x10001, 2875))));
        Assert.Contains("rpm may not fall", FanCurveRule.Check(Points((0x3200, 0x3333, 1775), (0x5300, 0x8000, 1100), (0x5800, 0x10000, 2875))));
        Assert.Contains("exceeds 32767", FanCurveRule.Check(Points((0x3200, 0x3333, 1100), (0x5300, 0x8000, 1775), (0x5800, 0x10000, 0x8000))));
        Assert.Null(FanCurveRule.Check(Points((-0x100, 0, 0), (0, 0, 0), (0x100, 0, 0))));   // signed temperatures, all-zero speeds
        // 0 -> 100 % within 1/256 °C: the duty slope does not fit the firmware's s32.
        Assert.Contains("too steep", FanCurveRule.Check(Points((0x3200, 0, 1100), (0x3201, 0x10000, 1100), (0x5800, 0x10000, 2875))));
        Assert.Null(FanCurveRule.Check(Points((0x3200, 0, 1100), (0x3300, 0x10000, 1100), (0x5800, 0x10000, 2875))));            // over 1 °C it does
    }

    [Fact]
    public void The_whitelist_takes_a_policy_write_and_refuses_broken_ones()
    {
        var on = WritePolicySet.Default;
        Assert.Equal(PolicyState.Enabled, on.Get(Slot.FanPolicySetControl)!.State);
        var one = FanPolicyFixture.Control(); W(one, 4, 1);
        Assert.Null(on.Check(Slot.FanPolicySetControl, one));
        Assert.Null(on.Check(Slot.FanPolicySetControl, FanPolicyFixture.Control()));
        var none = FanPolicyFixture.Control(); W(none, 4, 0);
        Assert.Contains("non-zero", on.Check(Slot.FanPolicySetControl, none));
        var flags = FanPolicyFixture.Control(); flags[8] = 1;
        Assert.Contains("bit 0", on.Check(Slot.FanPolicySetControl, flags));
        var empty = FanPolicyFixture.Control(); W(empty, 4, 1u << 2);                  // policy 2 is all zeros
        Assert.Contains("entry type 0", on.Check(Slot.FanPolicySetControl, empty));
        var level = FanPolicyFixture.Control(); W(level, 4, 1); W(level, FanPolicyLayout.PointsAt(0) + 0xc, 0x3200);
        Assert.Contains("policy 0", on.Check(Slot.FanPolicySetControl, level));
        Assert.Contains("must be 0x14ac", on.Check(Slot.FanPolicySetControl, new byte[0xeac]));
    }

    [Fact]
    public void A_curve_write_changes_only_the_points_of_the_one_policy_and_records_the_stock_curve_first()
    {
        var hw = FanPolicyFixture.Install();
        var before = (byte[])FanPolicyFixture.Stored.Clone();
        string? originalAtWrite = null;
        FakeDriver.OnSet = () => originalAtWrite ??= _journal.Load()?.Items.Single().Original;

        var r = Write(hw, "0", "50:20,79:60,86:100");
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.Equal(FanPolicyFixture.Stock, originalAtWrite);   // on disk before the first byte reached the driver
        Assert.Equal(FanPolicyFixture.Stock, r.Before);
        Assert.Equal("50:20:1100,79:60:1995,86:100:2875", r.After);
        Assert.Equal(Hold.Held, r.Hold);
        Assert.Equal([1u], FanPolicyFixture.SetMasks);            // the mask named policy 0 alone

        var after = FanPolicyFixture.Stored;
        foreach (int i in new[] { 1, 4, 5 }) Assert.Equal(FanPolicyFixture.Entry(before, i), FanPolicyFixture.Entry(after, i));
        // Inside policy 0 only the 36 bytes of points moved: type, the words before and after them are as they were.
        int e = FanPolicyLayout.ControlEntry(0), p = FanPolicyLayout.PointsAt(0);
        Assert.Equal(before.AsSpan(e, p - e).ToArray(), after.AsSpan(e, p - e).ToArray());
        Assert.Equal(before.AsSpan(p + FanCurveRule.Size, e + FanPolicyLayout.ControlStride - p - FanCurveRule.Size).ToArray(),
                     after.AsSpan(p + FanCurveRule.Size, e + FanPolicyLayout.ControlStride - p - FanCurveRule.Size).ToArray());
        Assert.Equal(FanPolicyFixture.Stock, FanPolicyFixture.CurveOf(1));
    }

    [Fact]
    public void An_equal_curve_writes_nothing_and_claims_nothing()
    {
        var hw = FanPolicyFixture.Install();
        var r = Write(hw, "1", "50:20,83:50,88:100");
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        var stock = Write(hw, "1", "stock");   // nothing held: the card's curve is the only stock there is
        Assert.Equal(TxOutcome.NoChange, stock.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void Stock_puts_the_journal_original_back_and_releases_it()
    {
        var hw = FanPolicyFixture.Install();
        var before = (byte[])FanPolicyFixture.Stored.Clone();
        Write(hw, "0", "45:30,70:55,80:100");
        var second = Write(hw, "0", "50:20,79:60,86:100");
        Assert.Equal(FanPolicyFixture.Stock, Assert.Single(_journal.Load()!.Items).Original);   // the first original wins
        Assert.Equal("50:20:1100,79:60:1995,86:100:2875", second.After);                         // rpm from the stock pairing, not the 45:30 one

        var back = Write(hw, "0", "stock");
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.Equal(Hold.Released, back.Hold);
        Assert.Null(_journal.Load()?.Items.FirstOrDefault());
        Assert.Equal(before, FanPolicyFixture.Stored);
    }

    [Fact]
    public void Requests_the_rules_refuse_never_reach_the_driver()
    {
        var hw = FanPolicyFixture.Install();
        foreach (var bad in new[] { "50:20,50:60,86:100", "50:60,79:20,86:100", "50:20,79:60,86:101", "50:20,79:60,130:100", "50:20,79:60:3000,86:100:2875" })
        {
            var r = Write(hw, "0", bad);
            Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        }
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.Contains("0..120 degC", Write(hw, "0", "50:20,79:60,130:100").Tx.Detail);
    }

    [Fact]
    public void A_named_rpm_under_the_duty_pairing_is_refused_whatever_the_duties_say()
    {
        var hw = FanPolicyFixture.Install();
        // Every duty passes the MCP's rules (100 % by 88 °C, none under 20 %); the firmware drives by the rpm column.
        foreach (var slow in new[] { "50:20:1100,79:60:1300,86:100:1500", "50:20:0,83:50:0,88:100:0", "50:20:1100,79:60:1995,86:100:2874" })
        {
            var r = Write(hw, "0", slow);
            Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
            Assert.Contains("may only be faster", r.Tx.Detail);
        }
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());

        var faster = Write(hw, "0", "50:20:1200,79:60:2100,86:100:2875");   // at or above the pairing: written as named
        Assert.Equal(TxOutcome.Applied, faster.Tx.Outcome);
        Assert.Equal("50:20:1200,79:60:2100,86:100:2875", faster.After);
        var founders = Write(hw, "1", "50:20:1100,79:60:1995,86:100:2875");  // the preset's own rpm are the pairing
        Assert.Equal(TxOutcome.Applied, founders.Tx.Outcome);
    }

    [Fact]
    public void A_policy_that_is_not_editable_or_not_there_is_refused_before_any_write()
    {
        var hw = FanPolicyFixture.Install();
        var p = Path_.Prepare(hw, _gpu, "4", "93:20,98:50,103:100", WriteOptions.None);
        Assert.Equal("fancurve/4", p.Cell.Id);
        Assert.Null(p.Plan);
        Assert.Contains("not editable", p.CannotWrite);
        Assert.Throws<InvalidOperationException>(() => Path_.Prepare(hw, _gpu, "2", FanPolicyFixture.Stock, WriteOptions.None));
        Assert.Throws<ArgumentException>(() => Path_.Prepare(hw, _gpu, "32", FanPolicyFixture.Stock, WriteOptions.None));
        Assert.Throws<ArgumentException>(() => Path_.Prepare(hw, _gpu, "fan0", FanPolicyFixture.Stock, WriteOptions.None));
        var junk = Path_.Prepare(hw, _gpu, "0", "fast", WriteOptions.None);
        Assert.Null(junk.Plan);
        Assert.Contains("not a fan curve", junk.CannotWrite);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_policy_whose_identity_changed_is_not_written()
    {
        var hw = FanPolicyFixture.Install();
        W(FanPolicyFixture.Stored, FanPolicyLayout.ControlEntry(0), 2);   // Control now says type 2 where Info says 3
        var r = Write(hw, "0", "50:20,79:60,86:100");
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("identity changed", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_curve_the_driver_does_not_retain_is_failed_and_restored()
    {
        var hw = FanPolicyFixture.Install();
        var before = (byte[])FanPolicyFixture.Stored.Clone();
        FakeDriver.Mode = FakeDriver.SetMode.ClampField;
        FakeDriver.Field = FanPolicyLayout.PointsAt(0) + 0xc + 4;   // point 1's duty
        FakeDriver.MisbehaveOnce = true;
        var r = Write(hw, "0", "50:20,79:60,86:100");
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Equal(Hold.Released, r.Hold);
        Assert.Equal(before, FanPolicyFixture.Stored);
    }

    [Fact]
    public void Recovery_and_the_registry_give_a_dead_owners_curve_back()
    {
        var hw = FanPolicyFixture.Install();
        var before = (byte[])FanPolicyFixture.Stored.Clone();
        _host.Pid = 32316;
        Write(hw, "0", "50:20,79:60,86:100");
        Write(hw, "1", "50:20,79:60,86:100");
        _sentinel.Begin("manual", false);
        _host.Pid = 100;

        var r = new Recovery(hw, _journal, _sentinel, _host).Run(_gpu, canWrite: true);
        Assert.Equal(2, r.Items.Count);
        Assert.All(r.Items, i => Assert.Equal(RecoveryAction.Written, i.Action));
        Assert.True(r.Clean, r.Detail);
        Assert.Equal(before, FanPolicyFixture.Stored);
    }

    [Fact]
    public void Registry_write_and_give_back_are_the_same_prepare()
    {
        var hw = FanPolicyFixture.Install();
        var w = Write(hw, "1", "40:25,75:60:2100,85:100");
        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        var item = Assert.Single(_journal.Load()!.Items);
        Assert.Equal(("fancurve", "1", FanPolicyFixture.Stock), (item.Kind, item.Key, item.Original));

        var back = Path_.Prepare(hw, _gpu, item.Key, item.Original, WriteOptions.Recovery);
        Assert.Equal("40:25:1213,75:60:2100,85:100:2875", Writer(hw).Look(_gpu, back.Cell));   // 25 % pairs with 1213 rpm on the stock curve
        var g = Writer(hw).Write(_gpu, back.Cell, back.Plan!);
        Assert.Equal(Hold.Released, g.Hold);
        Assert.Equal(FanPolicyFixture.Stock, FanPolicyFixture.CurveOf(1));
    }

    private sealed class Target(OwnershipJournal j, ApplySentinel s, GpuSession g) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => g;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    private static BrokerRequest Curve(int policy, string value) => new(BrokerRequest.OpWrite, Kind: FanPolicyWrites.Kind, Key: FanPolicyWrites.Key(policy), Value: value);

    /// <summary>
    /// The daily profile holds the fan curve like any other write: kept from the card (also once a profile exists, as
    /// the V/F curve is), given back by recovery and after a driver reset, and put back on unchanged; an equal curve is
    /// no write at all.
    /// </summary>
    [Fact]
    public void The_daily_profile_keeps_the_fan_curve_from_the_card_and_puts_it_back_after_a_reset()
    {
        var hw = FanPolicyFixture.Install();
        var d = new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        const string Mine = "50:20:1100,79:60:1995,86:100:2875";
        foreach (int i in new[] { 0, 1 }) Assert.True(d.Dispatch(Curve(i, "50:20,79:60,86:100")).Ok);
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.True(kept.Ok, kept.Error);
        var profile = DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;
        Assert.Equal([("fancurve", "0", Mine), ("fancurve", "1", Mine)], profile.Writes.Select(w => (w.Kind, w.Key, w.Value)));

        // A profile exists: the fan curve on the card still replaces the kept one.
        Assert.True(d.Dispatch(Curve(0, "45:30,75:60,85:100")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        profile = DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;
        Assert.Equal("45:30:1325,75:60:1995,85:100:2875", profile.Writes.Single(w => w.Key == "0").Value);

        // A driver reset: what is held goes back to stock, then the profile goes back on unchanged.
        var rec = d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "nvlddmkm 153"));
        Assert.True(rec.Ok, rec.Error);
        Assert.Equal(FanPolicyFixture.Stock, FanPolicyFixture.CurveOf(0));
        Assert.Equal(FanPolicyFixture.Stock, FanPolicyFixture.CurveOf(1));
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "reset", Value: "nvlddmkm 153")).Ok);
        Assert.Equal("45:30:1325,75:60:1995,85:100:2875", FanPolicyFixture.CurveOf(0));
        Assert.Equal(Mine, FanPolicyFixture.CurveOf(1));

        // Applied again with the curves already on: nothing is written.
        int sets = FakeDriver.Sets;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply")).Ok);
        Assert.Equal(sets, FakeDriver.Sets);
    }
    /// <summary>
    /// A trial may set the fan curve, on every policy the firmware lets be edited (the backstop stays). The data point
    /// carries it; with no profile kept, putting the daily profile back puts stock back; a keep takes it from the card.
    /// </summary>
    [Fact]
    public void A_trial_fan_curve_is_on_the_data_point_goes_back_with_the_profile_and_is_kept_from_the_card()
    {
        var hw = FanPolicyFixture.Install();
        var d = new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
        const string Mine = "50:20:1100,79:60:1995,86:100:2875";
        static BrokerRequest Exp(string kind, string? value = null) => new(BrokerRequest.OpExperiment, Kind: kind, Value: value);
        static string Try() => JsonSerializer.Serialize(new { curve = "0*127", change = "fan curve: 79 °C at 60 %", fan_curve = "50:20,79:60,86:100", keep_curve = true });

        Assert.True(d.Dispatch(Exp("begin", """{"game":"stress test"}""")).Ok);
        var t = d.Dispatch(Exp("try", Try()));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(Mine, FanPolicyFixture.CurveOf(0));
        Assert.Equal(Mine, FanPolicyFixture.CurveOf(1));
        Assert.Equal(FanPolicyFixture.Backstop, FanPolicyFixture.CurveOf(4));
        Assert.True(d.Dispatch(Exp("stable", "79 °C at 600 W")).Ok);
        Assert.Equal("50:20,79:60,86:100", Assert.Single(Experiment.ReadLast(Experiment.DataPathBeside(_journal.Path), 10)).FanCurve);

        var back = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "back_to_daily"));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(FanPolicyFixture.Stock, FanPolicyFixture.CurveOf(0));
        Assert.Equal(FanPolicyFixture.Stock, FanPolicyFixture.CurveOf(1));
        Assert.Empty(_journal.Inspect().Items);

        Assert.True(d.Dispatch(Exp("try", Try())).Ok);
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", Value: "holds 79 °C"));
        Assert.True(kept.Ok, kept.Error);
        var profile = DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;
        Assert.Equal([("fancurve", "0", Mine), ("fancurve", "1", Mine)], profile.Writes.Select(w => (w.Kind, w.Key, w.Value)));
    }
}
