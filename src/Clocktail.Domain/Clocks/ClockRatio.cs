using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

/// <summary>The clock propagation ratio as read: the relation mask the control was asked with, the ratio (U16.16) and the
/// control block's non-zero dwords (offset → value), for reading what else it holds.</summary>
public sealed record ClockRatioReading(uint Relations, uint Raw, IReadOnlyDictionary<int, uint> NonZero)
{
    public double Ratio => Raw / 65536.0;
}

/// <summary>
/// The NVVDD/MSVDD clock propagation ratio (the original's --msvdd-clock-ratio, alias --xbar-ratio): the U16.16 factor
/// at control +0x68, relation 0, that ties XBAR's clock to the core's. Read live on the 5090 (617.42, 2026-10-21): 0xe660,
/// 58976/65536 = 0.8999, the GPC2CLK→XBARCLK factor an overclocker reported and the ~0.9 × core XBAR is held to under
/// load. The block holds 14 relations 0x108 apart (others 0.8, 2.0, 1.2, 1.5: not identified), past the 0x75c its version
/// word names, so it is read in the original's call form (<see cref="Slots"/>: 0x5798 r1 in a 0x8000 buffer, 0x75c r1 in a
/// 0x2000 buffer); control +4 is the info's +8, the relation mask (0x3fff). A write is the block as read with +0x68
/// changed, gateway kind 9 [observed]; the readback must be the block with that one change. Drilled 2026-10-21: 0.92
/// read back exactly and ran XBAR 2595 MHz against a 2850 MHz core (0.911) at the 600 W wall, 2535 / 2865 at stock.
/// </summary>
public static class ClockRatio
{
    public const string Kind = "clockratio";
    public const string Key = "msvdd";
    public const int RatioAt = 0x68, InfoRelations = 8, ControlRelations = 4;
    /// <summary>The range a write may set, U16.16: 0.5..1.0 (XBAR at half the core, or at the core's clock).</summary>
    public const uint MinRaw = 0x8000, MaxRaw = 0x10000;

    public static string Words(uint raw) => string.Create(CultureInfo.InvariantCulture, $"{raw / 65536.0:0.0000}");

    public static OwnedCell Cell(uint relations) => new(Kind, Key, "U16.16", "the XBAR/core clock propagation ratio (relation 0)",
        new TransactionSpec(Slot.ClockClkPropTopRelsGetControl, Slot.ClockClkPropTopRelsSetControl, PrepareRead: Query(relations)),
        raw => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(RatioAt)).ToString(CultureInfo.InvariantCulture));

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(uint raw) => snapshot =>
    {
        if (raw < MinRaw || raw > MaxRaw) return new Mutation.Abort($"a ratio of {Words(raw)} is outside {Words(MinRaw)}..{Words(MaxRaw)}");
        uint now = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.Span[RatioAt..]);
        if (now == 0) return new Mutation.Abort("the driver returned an empty clock propagation control block");
        return now == raw ? new Mutation.Apply([]) : Mutation.Of(Edit.UInt32(RatioAt, raw));
    };

    /// <summary>The registry's entry: the value is the raw U16.16 (58976 = 0.8999), or a ratio with a decimal point.</summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        if (!key.Equals(Key, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{key}' is not a clock ratio (msvdd)");
        var cell = Cell(Relations(hw, gpu));
        uint? raw = value.Contains('.') && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0
            ? (uint)Math.Round(r * 65536)
            : uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : null;
        return raw is { } v ? (cell, Plan(v), null) : (cell, null, $"'{value}' is not a ratio (0.5..1.0, or its U16.16 value)");
    }

    public static uint Relations(NvClient hw, GpuSession gpu)
    {
        var info = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetInfo);
        var r = hw.Call(gpu, Slot.ClockClkPropTopRelsGetInfo, info);
        if (!r.IsOk) throw new InvalidOperationException($"clock propagation info read failed: {r}");
        uint relations = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(InfoRelations));
        if (relations == 0) throw new InvalidDataException("the driver reported no clock propagation relationships");
        return relations;
    }

    public static Action<byte[]> Query(uint relations) => b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ControlRelations), relations);

    public static ClockRatioReading Read(NvClient hw, GpuSession gpu)
    {
        uint relations = Relations(hw, gpu);
        var b = NvClient.NewBuffer(Slot.ClockClkPropTopRelsGetControl);
        Query(relations)(b);
        var r = hw.Call(gpu, Slot.ClockClkPropTopRelsGetControl, b);
        if (!r.IsOk) throw new InvalidOperationException($"clock propagation control read failed: {r}");
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(RatioAt));
        if (raw == 0) throw new InvalidDataException("the driver returned an empty clock propagation control block");
        var nonZero = new Dictionary<int, uint>();
        for (int o = 4; o + 4 <= b.Length; o += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) is var v and not 0) nonZero[o] = v;
        return new ClockRatioReading(relations, raw, nonZero);
    }
}
