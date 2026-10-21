using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace Clocktail.Hw;

public delegate string? SpanCheck(ReadOnlySpan<byte> buf);

/// <summary>One declarative constraint on a buffer about to be written to the driver. Returns null when satisfied.</summary>
public abstract record Rule
{
    public abstract string? Check(ReadOnlySpan<byte> b);

    protected static bool Has(ReadOnlySpan<byte> b, int off, int len) => off >= 0 && off + len <= b.Length;
    protected static uint U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b[off..]);
    protected static int I32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b[off..]);
    protected static string Short(int off) => $"buffer ends before +0x{off:x}";
}

public sealed record ExactSize(int Size) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b) => b.Length == Size ? null : $"size is 0x{b.Length:x}, must be 0x{Size:x}";
}

public sealed record Dword(int Off, uint Value) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b) =>
        !Has(b, Off, 4) ? Short(Off) : U32(b, Off) == Value ? null : $"+0x{Off:x} is 0x{U32(b, Off):x}, must be 0x{Value:x}";
}

public sealed record DwordIn(int Off, params uint[] Values) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b) =>
        !Has(b, Off, 4) ? Short(Off) : Array.IndexOf(Values, U32(b, Off)) >= 0 ? null
        : $"+0x{Off:x} is 0x{U32(b, Off):x}, must be one of {string.Join(", ", Values.Select(v => $"0x{v:x}"))}";
}

public sealed record DwordNonZero(int Off) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b) => !Has(b, Off, 4) ? Short(Off) : U32(b, Off) != 0 ? null : $"+0x{Off:x} must be non-zero";
}

public sealed record DwordMax(int Off, uint Max) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b) =>
        !Has(b, Off, 4) ? Short(Off) : U32(b, Off) <= Max ? null : $"+0x{Off:x} is {U32(b, Off)}, must be at most {Max}";
}

/// <summary>A non-zero bit set that stays inside <paramref name="Allowed"/>.</summary>
public sealed record SubsetMask(int Off, uint Allowed) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b)
    {
        if (!Has(b, Off, 4)) return Short(Off);
        uint v = U32(b, Off);
        if (v == 0) return $"+0x{Off:x} mask must be non-zero";
        return (v & ~Allowed) == 0 ? null : $"+0x{Off:x} mask 0x{v:x} has bits outside 0x{Allowed:x}";
    }
}

/// <summary>Exactly one bit, inside <paramref name="Allowed"/>.</summary>
public sealed record SingleBit(int Off, uint Allowed) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b)
    {
        if (!Has(b, Off, 4)) return Short(Off);
        uint v = U32(b, Off);
        if (BitOperations.PopCount(v) != 1) return $"+0x{Off:x} mask 0x{v:x} must select exactly one item";
        return (v & ~Allowed) == 0 ? null : $"+0x{Off:x} bit 0x{v:x} is not allowed (0x{Allowed:x})";
    }
}

public sealed record Int32Range(int Off, int Min, int Max) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b)
    {
        if (!Has(b, Off, 4)) return Short(Off);
        int v = I32(b, Off);
        return v >= Min && v <= Max ? null : $"+0x{Off:x} is {v}, must be within [{Min}, {Max}]";
    }
}

/// <summary>All bytes from <paramref name="From"/> for <paramref name="Length"/> bytes (-1 = to the end) are zero.</summary>
public sealed record ZeroBytes(int From, int Length = -1) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b)
    {
        int len = Length < 0 ? b.Length - From : Length;
        if (!Has(b, From, len)) return Short(From);
        int i = b.Slice(From, len).IndexOfAnyExcept((byte)0);
        return i < 0 ? null : $"+0x{From + i:x} must be zero";
    }
}

public sealed record Custom(string What, SpanCheck Fn) : Rule
{
    public override string? Check(ReadOnlySpan<byte> b) => Fn(b);
}

/// <summary>
/// The check on a firmware fan policy's three points, applied when a policy is read and again to every policy
/// written. A point is {temperature: signed, 1/256 °C; duty: 16.16, 0x10000 =
/// 100 %; rpm}. Temperatures strictly increase; duty is at most 100 % and never falls; rpm is at most 0x7fff (an
/// encoding limit, not the fan's measured range) and never falls; and each segment's slope, in the fixed point it is
/// computed in, fits s32: duty ((Δduty·256 + ΔT/2) / ΔT)·256, rpm (Δrpm·65536 + ΔT/2) / ΔT.
/// </summary>
public static class FanCurveRule
{
    public const int Points = 3, Stride = 0xc, Size = Points * Stride;
    public const uint DutyFull = 0x10000, RpmCeiling = 0x7fff;

    /// <summary>Null when the points (<see cref="Size"/> bytes, point k at k·0xc) hold.</summary>
    public static string? Check(ReadOnlySpan<byte> p)
    {
        if (p.Length < Size) return $"the points need 0x{Size:x} bytes, not 0x{p.Length:x}";
        var t = new long[Points];
        var duty = new long[Points];
        var rpm = new long[Points];
        for (int k = 0; k < Points; k++)
        {
            t[k] = BinaryPrimitives.ReadInt32LittleEndian(p[(k * Stride)..]);
            duty[k] = BinaryPrimitives.ReadUInt32LittleEndian(p[(k * Stride + 4)..]);
            rpm[k] = BinaryPrimitives.ReadUInt32LittleEndian(p[(k * Stride + 8)..]);
            if (duty[k] > DutyFull) return $"point {k} duty {Pct(duty[k])} % exceeds 100 %";
            if (rpm[k] > RpmCeiling) return $"point {k} rpm {rpm[k]} exceeds {RpmCeiling}";
        }
        for (int k = 1; k < Points; k++)
        {
            long dt = t[k] - t[k - 1];
            if (dt < 1) return $"point {k} at {C(t[k])} degC is not above point {k - 1} at {C(t[k - 1])} degC: temperatures must increase";
            if (dt > int.MaxValue) return $"points {k - 1} and {k} are too far apart in temperature";
            if (duty[k] < duty[k - 1]) return $"point {k} duty {Pct(duty[k])} % is below point {k - 1}'s {Pct(duty[k - 1])} %: duty may not fall";
            long s = (dt >> 1) + (duty[k] - duty[k - 1]) * 0x100;
            if (s > int.MaxValue || s / dt * 0x100 > int.MaxValue) return $"the duty slope between points {k - 1} and {k} is too steep for the firmware's fixed point";
            if (rpm[k] < rpm[k - 1]) return $"point {k} rpm {rpm[k]} is below point {k - 1}'s {rpm[k - 1]}: rpm may not fall";
            s = (dt >> 1) + (rpm[k] - rpm[k - 1]) * 0x10000;
            if (s > int.MaxValue || s / dt > int.MaxValue) return $"the rpm slope between points {k - 1} and {k} is too steep for the firmware's fixed point";
        }
        return null;
    }

    private static string C(long q8) => (q8 / 256.0).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Pct(long duty) => (duty * 100.0 / DutyFull).ToString("0.###", CultureInfo.InvariantCulture);
}

public enum PolicyState
{
    /// <summary>Writes pass when every rule holds.</summary>
    Enabled,
    /// <summary>Writes are refused. A complete rule set can be enabled explicitly; an incomplete one cannot.</summary>
    Disabled,
    /// <summary>Writes are refused and nothing can enable them.</summary>
    Forbidden,
}

/// <summary>
/// The write whitelist for one setter: what a buffer must look like before it may reach the driver.
/// </summary>
public sealed record WritePolicy(Slot Slot, PolicyState State, string Basis, IReadOnlyList<Rule> Rules, bool Complete, string? Reason = null)
{
    public string? Check(ReadOnlySpan<byte> b)
    {
        switch (State)
        {
            case PolicyState.Forbidden: return $"{Slot} is forbidden by design";
            case PolicyState.Disabled: return $"{Slot} is disabled: {Reason}";
        }
        foreach (var rule in Rules)
        {
            var why = rule.Check(b);
            if (why != null) return $"{Slot}: {why}";
        }
        return null;
    }
}

/// <summary>Immutable set of write policies. Setters without a policy are refused.</summary>
public sealed class WritePolicySet
{
    private readonly Dictionary<Slot, WritePolicy> _byslot;

    private WritePolicySet(Dictionary<Slot, WritePolicy> byslot) => _byslot = byslot;

    public static WritePolicySet Default { get; } = new(WritePolicies.Table.ToDictionary(p => p.Slot));

    public WritePolicy? Get(Slot slot) => _byslot.GetValueOrDefault(slot);
    public IEnumerable<WritePolicy> All => _byslot.Values;

    public string? Check(Slot slot, ReadOnlySpan<byte> buf)
    {
        var info = Slots.Info(slot);
        if (!info.IsWrite) return null;
        return _byslot.TryGetValue(slot, out var p) ? p.Check(buf) : $"{slot} has no write policy; writes are refused by default";
    }

    /// <summary>A copy in which a setter is disabled. Switching a setter off is always safe; only switching on needs a complete rule.</summary>
    public WritePolicySet WithDisabled(Slot slot, string reason)
    {
        if (!_byslot.TryGetValue(slot, out var p)) throw new InvalidOperationException($"{slot} has no write policy.");
        if (p.State == PolicyState.Forbidden) return this;
        var copy = new Dictionary<Slot, WritePolicy>(_byslot) { [slot] = p with { State = PolicyState.Disabled, Reason = reason } };
        return new WritePolicySet(copy);
    }

    /// <summary>A copy in which a disabled setter with a complete rule set is enabled. Deliberate opt-in only.</summary>
    public WritePolicySet WithEnabled(Slot slot)
    {
        if (!_byslot.TryGetValue(slot, out var p)) throw new InvalidOperationException($"{slot} has no write policy.");
        if (p.State == PolicyState.Forbidden) throw new InvalidOperationException($"{slot} is forbidden and cannot be enabled.");
        if (!p.Complete) throw new InvalidOperationException($"{slot} cannot be enabled: its rule set is incomplete ({p.Reason}).");
        var copy = new Dictionary<Slot, WritePolicy>(_byslot) { [slot] = p with { State = PolicyState.Enabled } };
        return new WritePolicySet(copy);
    }
}

/// <summary>
/// The rules themselves. Each entry names its evidence. A setter is enabled only when the whole rule is
/// known and the structure layout it depends on is verified; everything else starts disabled.
/// </summary>
public static class WritePolicies
{
    private static WritePolicy On(Slot s, string basis, params Rule[] rules) => new(s, PolicyState.Enabled, basis, rules, Complete: true);
    private static WritePolicy Off(Slot s, string basis, string reason, bool complete, params Rule[] rules) => new(s, PolicyState.Disabled, basis, rules, complete, reason);

    public static readonly IReadOnlyList<WritePolicy> Table =
    [
        On(Slot.VoltVoltRailsSetControl, "kind 1",
            new ExactSize(0xac8), new Dword(0, 0x20ac8),
            new DwordIn(4, 1, 3)),                       // rail mask: NVVDD alone or NVVDD+MSVDD (writable iff mask in {1,3})

        On(Slot.SetPstates20, "kinds 2 (core) / 3 (memory)",
            new ExactSize(0x1cf8), new Dword(0, 0x21cf8),
            new Dword(0x8, 1), new Dword(0xc, 1), new Dword(0x14, 0), new Dword(0x18, 1),   // one pstate (P0), one clock entry
            new DwordIn(0x1c, 0, 4),                     // domain: 0 = core, 4 = memory
            new Dword(0x24, 1),
            new Int32Range(0x28, -2147483000, 2147483000)), // kHz offset; only int32 extremes are guarded

        On(Slot.ClientPowerPoliciesSetStatus, "kind 4",
            new ExactSize(0x48), new Dword(0, 0x10048), new DwordNonZero(4)),

        // Kind 7: a non-empty point mask inside the first 128 points, and every selected point a
        // type-0 entry (a Core frequency point). It never looks at the offsets: their bounds are the domain plan's
        // (VfRules: the firmware's core offset range for the point and for global + point).
        Off(Slot.ClockClientClkVfPointsSetControl, "kind 7", "rule complete; enabled after its live drill", complete: true,
            new ExactSize(0x2420), new Dword(0, 0x12420),
            new ZeroBytes(0x14, 0x10),                   // mask bits 128..255: never writable
            new Custom("non-empty point mask; every selected point is a type-0 (Core frequency) entry", b =>
            {
                bool any = false;
                for (int i = 0; i < 128; i++)
                {
                    if ((b[4 + (i >> 3)] >> (i & 7) & 1) == 0) continue;
                    any = true;
                    uint type = BinaryPrimitives.ReadUInt32LittleEndian(b[(0x44 + i * 0x24)..]);
                    if (type != 0) return $"point {i} is entry type {type}; only type-0 (Core frequency) points may be written";
                }
                return any ? null : "the point mask selects no point";
            })),

        // Kind 0xf carries the XBAR/SYS/Video offsets and voltage demands. Kinds 6/12/14 (single-bit masks
        // 0x2/0x8/0x10) are not used. The mask bit is an entry index,
        // not a domain id: on the 5090 XBAR is entry 1, SYS 3, domain 0x15 entry 4 (topology, 2026-10-17).
        // On since its live drill (2026-10-20 08:03, 617.42): XBAR +75 read back exact and moved nothing else (V/F curve,
        // P0 offsets, both rails, the other domains, the fan curve), XBAR under nvgwls -l 50 2340 → 2422 MHz at the same
        // MSVDD; 0 put it back and released the record.
        On(Slot.ClockClkDomainsSetControl, "kind 0xf",
            new ExactSize(0x61a4), new Dword(0, 0x261a4), new SingleBit(8, 0xffffffff),
            new Custom("the selected entry has a known type, an offset within the gateway bound, voltage demands within the standard −25..+50 mV, zero slots 3/4 after it; every other entry is zero", b =>
            {
                int idx = BitOperations.TrailingZeroCount(BinaryPrimitives.ReadUInt32LittleEndian(b[8..]));
                int e = 0x124 + idx * 0x304;
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(b[e..]);
                int f = type == 0xf ? e + 0x114 : type is 1 or (>= 3 and <= 10) ? e + 0x10c : -1;
                if (f < 0) return $"entry {idx} type {type} has no known layout";
                int khz = BinaryPrimitives.ReadInt32LittleEndian(b[f..]);
                if (khz < -0x1f40000 || khz > 0x1f3fc18) return $"+0x{f:x} offset {khz} kHz is outside the gateway bound";
                // The voltage demands (NVVDD's, MSVDD's): the standard range; no XOC here.
                for (int s = 4; s <= 8; s += 4)
                {
                    int uv = BinaryPrimitives.ReadInt32LittleEndian(b[(f + s)..]);
                    if (uv < -25_000 || uv > 50_000) return $"+0x{f + s:x} voltage demand {uv} µV is outside −25000..50000";
                }
                if (BinaryPrimitives.ReadUInt32LittleEndian(b[(f + 0xc)..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(b[(f + 0x10)..]) != 0)
                    return $"+0x{f + 0xc:x}/+0x{f + 0x10:x} must be zero";
                int i = b[0x124..e].IndexOfAnyExcept((byte)0);
                if (i >= 0) return $"+0x{0x124 + i:x} must be zero (outside the selected entry)";
                i = b[(e + 0x304)..].IndexOfAnyExcept((byte)0);
                return i >= 0 ? $"+0x{e + 0x304 + i:x} must be zero (outside the selected entry)" : null;
            })),

        // The original's one-entry request (FUN_1401b6d50): the record cleared, +8 = 1, one entry at +0xc. Entry 0/1 are the
        // maximum/minimum clock (mode 2, kHz), entry 6 the voltage lock (mode 3, µV; 1.5 V is Boost Lock); mode 0 with
        // value 0 releases. Off by default: a clock-limit write enables it for itself.
        Off(Slot.PerfClientLimitsSetStatus, "kind 8", "enabled per write (clocklimit)", complete: true,
            new ExactSize(0x30c), new Dword(0, 0x2030c), new Dword(4, 0), new Dword(8, 1),
            new Custom("one entry: the max/min clock (0/1, mode 2, at most 5 GHz) or the voltage lock (6, mode 3, 450 mV..1.5 V), or mode 0 with value 0 to release; every other byte zero", b =>
            {
                static uint U(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
                int rest = b[0x24..].IndexOfAnyExcept((byte)0);
                if (rest >= 0) return $"+0x{0x24 + rest:x} must be zero (one entry only)";
                if (U(b, 0x10) != 0 || U(b, 0x18) != 0 || U(b, 0x20) != 0) return "the entry's reserved words must be zero";
                uint dom = U(b, 0xc), mode = U(b, 0x14), value = U(b, 0x1c);
                if (dom is not (0 or 1 or 6)) return $"entry {dom} is not a clock limit Clocktail writes";
                if (mode == 0) return value == 0 ? null : "mode 0 releases an entry, with value 0";
                if (mode != (dom == 6 ? 3u : 2u)) return $"entry {dom} takes mode {(dom == 6 ? 3 : 2)}, not {mode}";
                bool inRange = dom == 6 ? value is >= 450_000 and <= 1_500_000 : value is > 0 and <= 5_000_000;
                return inRange ? null : $"value {value} is outside entry {dom}'s range";
            })),

        Off(Slot.ClockClkPropTopRelsSetControl, "kind 9 (inferred)", "unverified; off by design", complete: false,
            new ExactSize(0x75c), new Dword(0, 0x1075c), new Dword(4, 0x3fff),
            new Dword(0x64, 0), new DwordNonZero(0x68),
            new Dword(0x170, 0), new Dword(0x278, 0), new Dword(0x380, 0), new Dword(0x488, 0), new Dword(0x590, 0)),

        // Fan control (kind 16): the whole 0x5ac block, read-modify-written. These are the structure's invariants. The
        // firmware range is not a
        // buffer property: FanRules checks every manual level against a fresh Status read before the write.
        On(Slot.ClientFanCoolersSetControl, "kind 16",
            new ExactSize(0x5ac), new Dword(0, 0x105ac),
            new Custom("1..32 channels at +8, ids unique, every level <= 0x7fffffff", b =>
            {
                int n = b[8];
                if (n is 0 or > 32) return $"+0x8 channel count {n} must be 1..32";
                var ids = new HashSet<uint>();
                for (int i = 0; i < n; i++)
                {
                    int e = 0x2c + i * 0x2c;
                    if (!ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(b[e..]))) return $"+0x{e:x} channel id repeats";
                    if (BinaryPrimitives.ReadUInt32LittleEndian(b[(e + 4)..]) > 0x7fffffff) return $"+0x{e + 4:x} level exceeds 0x7fffffff";
                }
                return null;
            })),

        // Kind 0x13: a rail's current limit (OCP), layout 1 read live on 617.42. The
        // buffer is a CONTROL query answer for one record with its limit changed: one mask bit, that record a
        // current-limit record (class 0x13) with a limit in (0, 0x7fffffff], every other byte zero. The board power
        // records (class 0x18, kind 0x1b) cannot pass. Bounds (the driver's range, never above the default) are the
        // plan's. Complete; off until enabled per request, as the V/F points are.
        Off(Slot.PowerPolicySetControl, "kind 0x13 (current limit, layout 1)", "layout read live; enabled per request", complete: true,
            new ExactSize(0xa4c), new Dword(0, 0x10a4c), new SingleBit(4, 0xffffffff),
            new Custom("the selected record is a current-limit record {class 0x13, limit 1..0x7fffffff}; every other byte zero", b =>
            {
                int rec = BitOperations.TrailingZeroCount(BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));
                int e = 0x1c + rec * 0x28;
                if (e + 0x28 > b.Length) return $"record {rec} is past the end of the buffer";
                uint cls = BinaryPrimitives.ReadUInt32LittleEndian(b[e..]), limit = BinaryPrimitives.ReadUInt32LittleEndian(b[(e + 4)..]);
                if (cls != 0x13) return $"record {rec} is class 0x{cls:x}; only current-limit records (0x13) may be written";
                if (limit is 0 or > 0x7fffffff) return $"record {rec} limit {limit} must be 1..0x7fffffff";
                for (int o = 8; o < b.Length; o++)
                    if (b[o] != 0 && (o < e || o >= e + 8)) return $"+0x{o:x} must be zero";
                return null;
            })),

        // F8 (kind 0x14) / FE (kind 0x15) watt requests, both through slot 48.
        // On since its live drill (2026-10-17, drills 12 and 13: equal value, 560 W, back to 575 W; NVML confirmed
        // the driver enforces the request). Values stay bounded by the board's own range (PowerRequestRules); creating a
        // request that does not exist yet is a separate, deliberate step because the driver cannot delete one.
        On(Slot.ClientPwrPoliciesSetControl, "kinds 0x14 (F8) / 0x15 (FE)",
            new ExactSize(0x528), new Dword(0, 0x10528), new SingleBit(4, 0xffffffff),
            new Custom("one board entry {value mW not 0/-1, command 0xf8|0xfe} at 0x28 + board*0x28, every other byte zero", b =>
            {
                int board = BitOperations.TrailingZeroCount(BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));
                int e = 0x28 + board * 0x28;
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(b[e..]), cmd = BinaryPrimitives.ReadUInt32LittleEndian(b[(e + 4)..]);
                if (cmd is not (0xf8 or 0xfe)) return $"+0x{e + 4:x} command is 0x{cmd:x}, must be 0xf8 or 0xfe";
                if (value is 0 or 0xffffffff) return $"+0x{e:x} request value must not be 0 or 0xffffffff";
                for (int o = 8; o < b.Length; o++)
                    if (b[o] != 0 && (o < e || o >= e + 8)) return $"+0x{o:x} must be zero";
                return null;
            })),

        Off(Slot.ThermChannelSetControl, "kind 0x16", "danger mode (freezes a temperature the firmware trusts); per-channel entry checks pending", complete: false,
            new ExactSize(0x28cc), new Dword(0, 0x328cc), new SubsetMask(8, 0x3e)),   // channels 1..5

        On(Slot.ClientVoltRailsSetControl, "kind 0x17",
            new ExactSize(0x28), new Dword(0, 0x10028), new DwordMax(4, 100), new ZeroBytes(8)),

        Off(Slot.ClockClkVfPointsSetControl, "kind 0", "rule unknown", complete: false,
            new ExactSize(0x424604), new Dword(0, 0x474604)),

        // Kind 0x18, the firmware fan curve: the Control r2 block with the mask at +4 naming the policies written. A block
        // with +8 bit 0 set or an entry type other than 2 or 3 is refused, and the points of every policy written are
        // checked (FanCurveRule). That a written policy is
        // editable and of Info's type, and that the mask names only policies that change, are not buffer properties:
        // FanPolicyRules judges them against a fresh Info read, and its plan writes one policy at a time. On from the
        // start: the layout read live (2026-10-17, 2026-10-20); the first write is its live drill.
        On(Slot.FanPolicySetControl, "kind 0x18",
            new ExactSize(0x14ac), new Dword(0, 0x214ac), new SubsetMask(4, 0xffffffff),
            new Custom("+8 bit 0 clear; every selected policy a type-2/3 entry whose three points pass the point check", b =>
            {
                if ((b[8] & 1) != 0) return "+0x8 bit 0 must be clear";
                uint mask = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
                for (int i = 0; i < 32; i++)
                {
                    if ((mask >> i & 1) == 0) continue;
                    int e = 0x2c + i * 0xa4;
                    int type = BinaryPrimitives.ReadInt32LittleEndian(b[e..]);
                    if (type is not (2 or 3)) return $"policy {i} is entry type {type}; only type-2/3 policies have points";
                    if (FanCurveRule.Check(b.Slice(e + 0x3c, FanCurveRule.Size)) is { } why) return $"policy {i}: {why}";
                }
                return null;
            })),

        Off(Slot.ThermalPolicySetControl, "kind 0x19", "only the version word is known", complete: false,
            new ExactSize(0x3fc), new Dword(0, 0x303fc)),

        On(Slot.SetCoolerLevels, "legacy cooler levels",
            new ExactSize(0x1c), new Dword(0, 0x1001c),
            new Custom("3 x {level, policy}: level <= 100, policy in {1 manual, 0x20 auto}, at most one entry non-zero", b =>
            {
                int nonZero = 0;
                for (int i = 0; i < 3; i++)
                {
                    uint level = BinaryPrimitives.ReadUInt32LittleEndian(b[(4 + i * 8)..]);
                    uint policy = BinaryPrimitives.ReadUInt32LittleEndian(b[(8 + i * 8)..]);
                    if (level == 0 && policy == 0) continue;
                    nonZero++;
                    if (level > 100) return $"cooler {i}: level {level} exceeds 100";
                    if (policy is not (1 or 0x20)) return $"cooler {i}: policy 0x{policy:x} must be 1 (manual) or 0x20 (auto)";
                }
                return nonZero <= 1 ? null : "only the target cooler entry may be non-zero";
            })),

        new WritePolicy(Slot.RegisterOp, PolicyState.Forbidden, "never called", [], Complete: false, "raw register access"),
    ];
}
