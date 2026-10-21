using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Fan;

/// <summary>
/// The firmware's own fan curves: FanPolicy, slots 58-61. Three
/// structures, each a header and 32 fixed-stride policy entries, sized by the live probe on 617.42 (2026-10-17):
/// Info 0x28 + 32 × 0x48 = 0x928 r1, Status 0x28 + 32 × 0x44 = 0x8a8 r1, Control 0x2c + 32 × 0xa4 = 0x14ac r2.
/// Status and Control GETs are queries: the mask at +4 names the policies the driver fills in (a zero mask reads
/// zeros), and a mask of one bit reads that policy alone with the mask echoed (live 2026-10-20), which is how a cell
/// reads and writes one policy.
/// <para>
/// On the RTX 5090 FE (VBIOS 98.02.2e.00.03): Info mask 0x33, policies 0, 1, 4, 5, all type 3; 0 and 1 editable
/// ([inferred] one per fan channel), 4 and 5 not. 0 and 1 hold 50 °C · 20 % · 1100, 83 °C · 50 % · 1775,
/// 88 °C · 100 % · 2875 rpm; 4 and 5 the same duties and rpm at 93/98/103 °C, the backstop that stays whatever
/// 0 and 1 say ([inferred] the firmware takes the highest demand). Identical on 2026-10-17 and 2026-10-20.
/// </para>
/// </summary>
public static class FanPolicyLayout
{
    public const int MaxPolicies = 32, Points = FanCurveRule.Points;
    public const int Mask = 4;
    public const int InfoHeader = 0x28, InfoStride = 0x48;
    public const int StatusHeader = 0x28, StatusStride = 0x44;
    public const int ControlFlags = 8, ControlHeader = 0x2c, ControlStride = 0xa4;

    /// <summary>Info entry: +0 type (−1 absent, else 2 or 3), +8 link (1, 1, 0, 2
    /// here), +0x2f editable (a type-3 policy is editable when it is 1; a type-2 one always is).</summary>
    public const int InfoType = 0, InfoLink = 8, InfoEditable = 0x2f;

    /// <summary>
    /// Control entry: +0 type (Info's), three points from +0x3c, 0xc apart: {temperature, signed 1/256 °C; duty, 16.16
    /// with 0x10000 = 100 %; rpm}. Q8.8 and 16.16 are settled by the live values (0x3200 = 50 °C, 0x8000 = 50 %).
    /// The words before the points (+0x24 0x42, +0x28 0xffbe, +0x2c 0x2b23, +0x30 0x10a, +0x34 0x400, +0x38 0x1000)
    /// and after them (+0x60 1; +0x64/+0x68 43/50 °C on 0 and 1, 63/70 on 4, 60/70 on 5; 50000, 100000, 61) are not
    /// decoded: a write keeps them as the driver holds them, and the readback compares them. [inferred] +0x64/+0x68 are
    /// the fans' stop/start band on the filtered temperature (Status +0x28): at idle on 2026-10-20 policies 0 and 1
    /// stopped their fans once it fell under 43 °C and kept them stopped as it rose back through 44 °C. So no curve
    /// point below the start temperature spins a stopped fan up.
    /// </summary>
    public const int ControlType = 0, ControlPoints = 0x3c;

    /// <summary>
    /// Status entry, [inferred] from live reads (2026-10-17 at 41 and 46 °C after load; 2026-10-20 idle, 36-46 °C):
    /// +0x24 a temperature in 2^-22 °C that follows NVML's GPU temperature; +0x28 the same temperature slowly filtered
    /// (2 °C under it while the card warms, 8 °C over it while it cools), which the target rpm follows (1212 rpm at
    /// 55.5 °C on the stock curve, to the rpm); +0x30 that target rpm, 0 while the fans are stopped; +0x34 +0x24 again
    /// in Q8.8, lightly filtered (not a duty); +0x3c 1 while the policy holds its fans stopped;
    /// +0x2c and +0x38 (one value in every policy, drifting by the second) unknown.
    /// </summary>
    public const int StatusType = 0, StatusTemperature = 0x24, StatusCurveTemperature = 0x28, StatusWord2c = 0x2c,
        StatusTargetRpm = 0x30, StatusFilteredTemperature = 0x34, StatusWord38 = 0x38, StatusWord3c = 0x3c;

    public static int InfoEntry(int i) => InfoHeader + i * InfoStride;
    public static int StatusEntry(int i) => StatusHeader + i * StatusStride;
    public static int ControlEntry(int i) => ControlHeader + i * ControlStride;
    public static int PointsAt(int i) => ControlEntry(i) + ControlPoints;
}

/// <summary>One curve point exactly as the Control entry holds it: temperature in 1/256 °C, duty in 1/65536, rpm.</summary>
public readonly record struct FanCurvePoint(int Temp, uint Duty, uint Rpm)
{
    public decimal TempC => Temp / 256m;
    public decimal DutyPct => Duty * 100m / FanCurveRule.DutyFull;

    public static int TempOf(decimal c) => (int)decimal.Round(c * 256m, MidpointRounding.AwayFromZero);
    public static uint DutyOf(decimal pct) => (uint)decimal.Round(pct * FanCurveRule.DutyFull / 100m, MidpointRounding.AwayFromZero);

    /// <summary>The three points as the firmware lays them out, for <see cref="FanCurveRule"/> and for the write.</summary>
    public static byte[] Encode(IReadOnlyList<FanCurvePoint> points)
    {
        var b = new byte[FanCurveRule.Size];
        for (int k = 0; k < points.Count; k++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(k * FanCurveRule.Stride), points[k].Temp);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(k * FanCurveRule.Stride + 4), points[k].Duty);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(k * FanCurveRule.Stride + 8), points[k].Rpm);
        }
        return b;
    }
}

/// <summary>A point as a request names it: temperature and duty, and rpm only when it is given.</summary>
public readonly record struct RequestedPoint(int Temp, uint Duty, uint? Rpm);

/// <summary>
/// The text of a curve. The canonical form, the journal's and the profile's, is every field of every point,
/// "50:20:1100,83:50:1775,88:100:2875" (°C : duty % : rpm), with at most three decimals: that is exact, every 1/256 °C
/// and every 1/65536 of duty comes back to the same raw value (an error under 0.0005 is under half a step of either),
/// so equal strings mean equal bytes. A request may leave rpm out ("50:20,79:60,86:100"); <see cref="Stock"/> is the
/// curve the policy had before this program first wrote it.
/// </summary>
public static class FanCurveText
{
    public const string Stock = "stock";

    public static string Format(IReadOnlyList<FanCurvePoint> points) =>
        string.Join(",", points.Select(p => $"{Temp(p.Temp)}:{Duty(p.Duty)}:{p.Rpm.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>A request in its short form (rpm only where it was named).</summary>
    public static string Format(IReadOnlyList<RequestedPoint> points) =>
        string.Join(",", points.Select(p => $"{Temp(p.Temp)}:{Duty(p.Duty)}" + (p.Rpm is { } r ? $":{r.ToString(CultureInfo.InvariantCulture)}" : "")));

    public static bool IsStock(string text) => string.Equals(text.Trim(), Stock, StringComparison.OrdinalIgnoreCase);

    /// <summary>Three points, "T:D" or "T:D:R" each, comma-separated. Ranges are the rule's to judge, not the parser's.</summary>
    public static IReadOnlyList<RequestedPoint> Parse(string text)
    {
        var tokens = text.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length != FanPolicyLayout.Points || tokens.Any(t => t.Length == 0))
            throw new FormatException($"'{text}' is not a fan curve: it takes {FanPolicyLayout.Points} points degC:duty% (or degC:duty%:rpm), comma-separated, or '{Stock}'");
        return tokens.Select(t =>
        {
            var f = t.Split(':', StringSplitOptions.TrimEntries);
            if (f.Length is not (2 or 3)) throw new FormatException($"'{t}' is not a point degC:duty% (or degC:duty%:rpm)");
            if (!decimal.TryParse(f[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var c) || Math.Abs(c) > 1_000_000)
                throw new FormatException($"'{f[0]}' is not a temperature in degC");
            if (!decimal.TryParse(f[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) || d > 1_000)
                throw new FormatException($"'{f[1]}' is not a duty in percent");
            uint? rpm = null;
            if (f.Length == 3)
                rpm = uint.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : throw new FormatException($"'{f[2]}' is not an rpm");
            return new RequestedPoint(FanCurvePoint.TempOf(c), FanCurvePoint.DutyOf(d), rpm);
        }).ToList();
    }

    /// <summary>A canonical text back to points; every field must be named.</summary>
    public static IReadOnlyList<FanCurvePoint> ParseExact(string text)
    {
        var req = Parse(text);
        if (req.Any(p => p.Rpm == null)) throw new FormatException($"'{text}' does not name every point's rpm");
        return req.Select(p => new FanCurvePoint(p.Temp, p.Duty, p.Rpm!.Value)).ToList();
    }

    /// <summary>The fewest decimals (three at most, always enough) that parse back to the same raw value: 60 % is
    /// 0x999a = 60.0006 %, written "60" because "60" reads back as 0x999a.</summary>
    private static string Temp(int raw) => Shortest(raw / 256m, c => FanCurvePoint.TempOf(c) == raw);
    private static string Duty(uint raw) => Shortest(raw * 100m / FanCurveRule.DutyFull, d => FanCurvePoint.DutyOf(d) == raw);

    private static string Shortest(decimal v, Func<decimal, bool> readsBack)
    {
        var r = v;
        for (int n = 0; n <= 3; n++)
            if (readsBack(r = decimal.Round(v, n, MidpointRounding.AwayFromZero))) break;
        return r.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

/// <summary>One policy as Info describes it. <see cref="Index"/> is the mask bit and the entry slot.</summary>
public sealed record FanPolicyInfoEntry(int Index, int Type, byte Link, bool Editable);

public sealed record FanPolicyInfo(uint Mask, IReadOnlyList<FanPolicyInfoEntry> Policies)
{
    public FanPolicyInfoEntry? Get(int index) => Policies.FirstOrDefault(p => p.Index == index);
    public IEnumerable<FanPolicyInfoEntry> Editable => Policies.Where(p => p.Editable);
}

/// <summary>One policy as Control holds it: its type and its three points.</summary>
public sealed record FanPolicyEntry(int Index, int Type, IReadOnlyList<FanCurvePoint> Points)
{
    public string Curve => FanCurveText.Format(Points);
}

/// <summary>One policy as Status reports it; see <see cref="FanPolicyLayout.StatusType"/> for what is inferred.</summary>
public sealed record FanPolicyStatusEntry(int Index, int Type, int Temperature, int CurveTemperature, uint Word2c, uint TargetRpm, int FilteredTemperature, uint Word38, uint Word3c)
{
    private const double Unit22 = 1 << 22;
    public double TemperatureC => Temperature / Unit22;
    public double CurveTemperatureC => CurveTemperature / Unit22;
    public double FilteredTemperatureC => FilteredTemperature / 256.0;
}

public static class FanPolicyParser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    private static void CheckShape(ReadOnlySpan<byte> raw, Slot slot, string what)
    {
        var shape = Slots.Info(slot).Shape ?? throw new InvalidDataException($"{slot}: structure size is unknown.");
        if (raw.Length != shape.Size || U(raw, 0) != shape.Word)
            throw new InvalidDataException($"The fan policy {what} read returned an unexpected version or size.");
    }

    private static IEnumerable<int> Bits(uint mask) => Enumerable.Range(0, FanPolicyLayout.MaxPolicies).Where(i => (mask >> i & 1) != 0);

    /// <summary>
    /// The policies Info lists: a mask bit whose type is −1 is
    /// not a policy; any other type must be 2 or 3; a type-3 policy's editable byte is 0 or 1.
    /// </summary>
    public static FanPolicyInfo ParseInfo(ReadOnlySpan<byte> raw)
    {
        CheckShape(raw, Slot.FanPolicyGetInfo, "info");
        uint mask = U(raw, FanPolicyLayout.Mask);
        var list = new List<FanPolicyInfoEntry>();
        foreach (int i in Bits(mask))
        {
            int e = FanPolicyLayout.InfoEntry(i);
            int type = I(raw, e + FanPolicyLayout.InfoType);
            if (type == -1) continue;
            if (type is not (2 or 3)) throw new InvalidDataException($"Fan policy {i} is of type {type}; only types 2 and 3 are fan curves.");
            byte editable = raw[e + FanPolicyLayout.InfoEditable];
            if (type == 3 && editable > 1) throw new InvalidDataException($"Fan policy {i}: edit capability {editable} is not 0 or 1.");
            list.Add(new FanPolicyInfoEntry(i, type, raw[e + FanPolicyLayout.InfoLink], type == 2 || editable == 1));
        }
        return new FanPolicyInfo(mask, list);
    }

    /// <summary>One policy's points from a Control read that asked for it; throws when the read does not hold it.</summary>
    public static IReadOnlyList<FanCurvePoint> PointsIn(ReadOnlySpan<byte> raw, int index)
    {
        CheckShape(raw, Slot.FanPolicyGetControl, "control");
        if ((U(raw, FanPolicyLayout.Mask) >> index & 1) == 0) throw new InvalidDataException($"The fan policy control read does not hold policy {index}.");
        int e = FanPolicyLayout.ControlEntry(index);
        int type = I(raw, e + FanPolicyLayout.ControlType);
        if (type is not (2 or 3)) throw new InvalidDataException($"Fan policy {index} reads as type {type}, not a fan curve.");
        var points = new List<FanCurvePoint>(FanPolicyLayout.Points);
        for (int k = 0, p = FanPolicyLayout.PointsAt(index); k < FanPolicyLayout.Points; k++, p += FanCurveRule.Stride)
            points.Add(new FanCurvePoint(I(raw, p), U(raw, p + 4), U(raw, p + 8)));
        return points;
    }

    /// <summary>
    /// Control asked for <paramref name="asked"/>, held to what a reader must refuse: the mask echoed, +8 bit 0
    /// clear, every asked policy Info lists of Info's type, and its points passing <see cref="FanCurveRule"/>.
    /// </summary>
    public static IReadOnlyList<FanPolicyEntry> ParseControl(ReadOnlySpan<byte> raw, FanPolicyInfo info, uint asked)
    {
        CheckShape(raw, Slot.FanPolicyGetControl, "control");
        if (U(raw, FanPolicyLayout.Mask) != asked) throw new InvalidDataException($"The fan policy control read answered for 0x{U(raw, FanPolicyLayout.Mask):x}, not the 0x{asked:x} it was asked.");
        if ((raw[FanPolicyLayout.ControlFlags] & 1) != 0) throw new InvalidDataException("The fan policy control read has +8 bit 0 set, an unsupported layout.");
        var list = new List<FanPolicyEntry>();
        foreach (int i in Bits(asked))
        {
            if (info.Get(i) is not { } p) continue;
            int type = I(raw, FanPolicyLayout.ControlEntry(i) + FanPolicyLayout.ControlType);
            if (type != p.Type) throw new InvalidDataException($"Fan policy {i} is type {p.Type} in Info but {type} in Control.");
            if (FanCurveRule.Check(raw.Slice(FanPolicyLayout.PointsAt(i), FanCurveRule.Size)) is { } why) throw new InvalidDataException($"Fan policy {i}: {why}.");
            list.Add(new FanPolicyEntry(i, type, PointsIn(raw, i)));
        }
        return list;
    }

    /// <summary>Status asked for <paramref name="asked"/>: one entry per asked policy Info lists. Read for display only.</summary>
    public static IReadOnlyList<FanPolicyStatusEntry> ParseStatus(ReadOnlySpan<byte> raw, FanPolicyInfo info, uint asked)
    {
        CheckShape(raw, Slot.FanPolicyGetStatus, "status");
        if (U(raw, FanPolicyLayout.Mask) != asked) throw new InvalidDataException($"The fan policy status read answered for 0x{U(raw, FanPolicyLayout.Mask):x}, not the 0x{asked:x} it was asked.");
        var list = new List<FanPolicyStatusEntry>();
        foreach (int i in Bits(asked))
        {
            if (info.Get(i) == null) continue;
            int e = FanPolicyLayout.StatusEntry(i);
            list.Add(new FanPolicyStatusEntry(i, I(raw, e + FanPolicyLayout.StatusType),
                I(raw, e + FanPolicyLayout.StatusTemperature), I(raw, e + FanPolicyLayout.StatusCurveTemperature), U(raw, e + FanPolicyLayout.StatusWord2c),
                U(raw, e + FanPolicyLayout.StatusTargetRpm), I(raw, e + FanPolicyLayout.StatusFilteredTemperature),
                U(raw, e + FanPolicyLayout.StatusWord38), U(raw, e + FanPolicyLayout.StatusWord3c)));
        }
        return list;
    }
}

public sealed record FanPolicySnapshot(
    FanPolicyInfo Info,
    IReadOnlyList<FanPolicyEntry> Policies,
    IReadOnlyList<FanPolicyStatusEntry> Status,
    byte[] InfoRaw,
    byte[] ControlRaw,
    byte[] StatusRaw);

public static class FanPolicyReader
{
    /// <summary>A read; for Status and Control, <paramref name="mask"/> names the policies asked for.</summary>
    public static byte[] Read(NvClient hw, GpuSession gpu, Slot slot, uint? mask = null)
    {
        var b = NvClient.NewBuffer(slot);
        if (mask is { } m) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(FanPolicyLayout.Mask), m);
        var r = hw.Call(gpu, slot, b);
        if (!r.IsOk) throw new InvalidOperationException($"{slot}: {r}");
        return b;
    }

    public static FanPolicyInfo ReadInfo(NvClient hw, GpuSession gpu) => FanPolicyParser.ParseInfo(Read(hw, gpu, Slot.FanPolicyGetInfo));

    /// <summary>Info, then Control and Status asked for every policy Info lists.</summary>
    public static FanPolicySnapshot Read(NvClient hw, GpuSession gpu)
    {
        var infoRaw = Read(hw, gpu, Slot.FanPolicyGetInfo);
        var info = FanPolicyParser.ParseInfo(infoRaw);
        var control = Read(hw, gpu, Slot.FanPolicyGetControl, info.Mask);
        var status = Read(hw, gpu, Slot.FanPolicyGetStatus, info.Mask);
        return new FanPolicySnapshot(info, FanPolicyParser.ParseControl(control, info, info.Mask), FanPolicyParser.ParseStatus(status, info, info.Mask), infoRaw, control, status);
    }
}

/// <summary>
/// What may be written to a firmware fan policy: the rules on a fresh
/// Control read of that one policy. The policy must be editable and still of Info's type; the three points pass
/// <see cref="FanCurveRule"/>; only the points change, every other word of the entry stays as the driver holds it;
/// and the mask names this policy alone, so a policy that does not change is never written.
/// <para>
/// rpm. Which column the firmware drives the fan by is not decoded: no mode word is known, and the
/// live Status cannot tell, because on the stock curve rpm is the same piecewise-linear function of temperature as
/// duty is (its target rpm, 1212 at 55.5 °C, is both). If it follows rpm, a duty-only edit would move only the
/// temperatures and leave each point's speed at stock, so 60 % would quietly run at 50 %'s 1775 rpm. So a request that
/// names no rpm gets, at each point, the rpm the policy's own reference points pair with that duty: linear between
/// them, the end's rpm beyond them (60 % → 1995 rpm on the 5090's 20/1100, 50/1775, 100/2875), never above 0x7fff and
/// never falling, because the reference itself does not. The two columns then describe one curve, whichever the
/// firmware reads, and no mode bit is touched. The reference is the journal's original when this program holds the
/// policy, else the curve on the card, so a second write maps duty to rpm the same way the first did.
/// </para>
/// </summary>
public static class FanPolicyRules
{
    /// <summary>The bound on a requested fan curve's temperatures (≤ 120 °C); a request outside
    /// [0, 120] °C is a typo, not a curve. A return to the origin and recovery are not held to it.</summary>
    public const int MaxRequestC = 120;

    /// <summary>The rpm the reference curve pairs with <paramref name="duty"/>; see <see cref="FanPolicyRules"/>.</summary>
    public static uint RpmFor(uint duty, IReadOnlyList<FanCurvePoint> reference)
    {
        if (duty <= reference[0].Duty) return reference[0].Rpm;
        for (int k = 1; k < reference.Count; k++)
        {
            var (a, b) = (reference[k - 1], reference[k]);
            if (duty > b.Duty || b.Duty == a.Duty) continue;
            long num = (long)(duty - a.Duty) * ((long)b.Rpm - a.Rpm), den = b.Duty - a.Duty;
            long rpm = a.Rpm + (num >= 0 ? (2 * num + den) / (2 * den) : -((-2 * num + den) / (2 * den)));
            return (uint)Math.Clamp(rpm, 0, FanCurveRule.RpmCeiling);
        }
        return reference[^1].Rpm;
    }

    /// <summary>The requested points with every rpm named: as given, or from the reference.</summary>
    public static IReadOnlyList<FanCurvePoint> Resolve(IReadOnlyList<RequestedPoint> request, IReadOnlyList<FanCurvePoint> reference) =>
        request.Select(p => new FanCurvePoint(p.Temp, p.Duty, p.Rpm ?? RpmFor(p.Duty, reference))).ToList();

    /// <summary>The points a write may put on the card; null when they hold.</summary>
    public static string? Validate(IReadOnlyList<FanCurvePoint> points) =>
        points.Count != FanPolicyLayout.Points ? $"a fan curve has {FanPolicyLayout.Points} points, not {points.Count}" : FanCurveRule.Check(FanCurvePoint.Encode(points));

    /// <summary>
    /// The plan on the fresh Control read of one policy. <paramref name="target"/> null means the policy as it stands
    /// (nothing to write: "stock" when nothing is held). <paramref name="origin"/> is the journal's original for the
    /// cell, or the value itself for recovery: the rpm reference, and the one target the request bound does not judge.
    /// </summary>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(FanPolicyInfoEntry policy, IReadOnlyList<RequestedPoint>? target, IReadOnlyList<FanCurvePoint>? origin, bool isRecovery) => raw =>
    {
        var s = raw.Span;
        int i = policy.Index;
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(s[FanPolicyLayout.Mask..]);
        if (mask != 1u << i) return new Mutation.Abort($"the driver answered for fan policies 0x{mask:x}, not policy {i} alone; nothing written");
        if ((s[FanPolicyLayout.ControlFlags] & 1) != 0) return new Mutation.Abort("the fan policy control block has +8 bit 0 set, an unsupported layout; nothing written");
        int type = BinaryPrimitives.ReadInt32LittleEndian(s[FanPolicyLayout.ControlEntry(i)..]);
        if (type != policy.Type) return new Mutation.Abort($"fan policy {i} reads as type {type} where Info says {policy.Type}: its identity changed; nothing written");
        if (!policy.Editable) return new Mutation.Abort($"fan policy {i} is not editable");

        var now = FanPolicyParser.PointsIn(s, i);
        var reference = origin ?? now;
        var want = target == null ? now : Resolve(target, reference);
        if (target != null && !isRecovery && !(origin != null && want.SequenceEqual(origin)))
            foreach (var p in want)
            {
                if (p.Temp < 0 || p.Temp > MaxRequestC * 256)
                    return new Mutation.Abort($"{p.TempC.ToString("0.###", CultureInfo.InvariantCulture)} degC is outside the 0..{MaxRequestC} degC a fan curve request may name; nothing written");
                // The firmware drives by rpm: a named rpm under the reference's pairing would let a duty that passes every
                // duty rule spin the fans slower than that duty does on the stock curve.
                if (RpmFor(p.Duty, reference) is var floor && p.Rpm < floor)
                    return new Mutation.Abort($"{p.Rpm} rpm at {p.DutyPct.ToString("0.###", CultureInfo.InvariantCulture)} % is under the {floor} rpm the policy's own curve pairs with that duty; an rpm may only be faster; nothing written");
            }
        if (Validate(want) is { } why) return new Mutation.Abort($"fan policy {i}: {why}");
        if (want.SequenceEqual(now)) return new Mutation.Apply([]);
        return Mutation.Of(new Edit(FanPolicyLayout.PointsAt(i), FanCurvePoint.Encode(want)));
    };
}

/// <summary>
/// A firmware fan policy is one owned cell: Control asked for that policy alone, read and written whole (gateway kind
/// 0x18), its three points as the cell, <see cref="FanPolicyRules"/> as the plan. Nothing stays running: the firmware
/// runs the curve itself. The journal's original is the baseline, so recovery, --recover and "stock" give the BIOS curve back.
/// FanCoolers in manual mode (<see cref="FanControl"/>) overrides every policy while it lasts: the curve governs the
/// fans only in auto.
/// </summary>
public static class FanPolicyWrites
{
    public const string Kind = "fancurve";

    public static string Key(int index) => index.ToString(CultureInfo.InvariantCulture);

    public static int ParseKey(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < FanPolicyLayout.MaxPolicies ? i
        : throw new ArgumentException($"'{key}' is not a fan policy index (0..{FanPolicyLayout.MaxPolicies - 1})");

    public static Action<byte[]> Query(int index) => b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(FanPolicyLayout.Mask), 1u << index);

    public static OwnedCell Cell(int index) => new(
        Kind, Key(index), "degC:%:rpm", $"firmware fan policy {index}: three points {{temperature, duty, rpm}}",
        new TransactionSpec(Slot.FanPolicyGetControl, Slot.FanPolicySetControl, PrepareRead: Query(index)),
        raw => FanCurveText.Format(FanPolicyParser.PointsIn(raw, index)));

    /// <summary>
    /// The registry's entry: key = policy index, value = a curve (<see cref="FanCurveText"/>) or "stock". Info is read
    /// fresh for every write: the policy must exist and be editable before any byte is planned.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite) Prepare(NvClient hw, GpuSession gpu, string key, string value, string? origin, bool isRecovery)
    {
        int index = ParseKey(key);
        var info = FanPolicyReader.ReadInfo(hw, gpu);
        var policy = info.Get(index)
            ?? throw new InvalidOperationException($"fan policy {index} does not exist (policies: {string.Join(", ", info.Policies.Select(p => p.Index))})");
        var cell = Cell(index);
        if (!policy.Editable)
            return (cell, null, $"fan policy {index} is not editable (the editable ones: {string.Join(", ", info.Editable.Select(p => p.Index))}); a non-editable policy is the firmware's own backstop");

        IReadOnlyList<FanCurvePoint>? from = null;
        try { if (origin != null) from = FanCurveText.ParseExact(origin); }
        catch (FormatException) { }   // a record this version cannot read: no reference, no shortcut to it
        IReadOnlyList<RequestedPoint>? target;
        if (FanCurveText.IsStock(value)) target = from?.Select(p => new RequestedPoint(p.Temp, p.Duty, p.Rpm)).ToList();
        else
        {
            try { target = FanCurveText.Parse(value); }
            catch (FormatException e) { return (cell, null, e.Message); }
        }
        return (cell, FanPolicyRules.Plan(policy, target, from, isRecovery), null);
    }
}
