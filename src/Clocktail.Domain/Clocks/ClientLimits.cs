using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

/// <summary>One entry of the driver's client clock-limit record: a clock domain, a mode and a value.</summary>
public sealed record ClientLimit(int Domain, uint Mode, uint Value)
{
    /// <summary>What it is, in words. Entry 6 mode 3 is a voltage lock (1 500 000 µV is Boost Lock, a sentinel above
    /// every point); entry 0 mode 2 the maximum-clock cap (kHz); entry 1 mode 2 [inferred] a floor. From the decompiled
    /// original [observed]; which of entries 0 and 1 is the floor is the open question (the T04 probe).</summary>
    public string Describe() => (Domain, Mode) switch
    {
        (6, 3) when Value == ClientLimitsLayout.BoostLockUv => "Boost Lock: the card held at its top boost",
        (6, 3) => string.Create(CultureInfo.InvariantCulture, $"voltage locked at {Value / 1000.0:0.#} mV (a V/F point lock)"),
        (0, 2) => string.Create(CultureInfo.InvariantCulture, $"maximum clock {Value / 1000.0:0.#} MHz"),
        (1, 2) => string.Create(CultureInfo.InvariantCulture, $"clock record 1: {Value / 1000.0:0.#} MHz (probably a floor)"),
        _ => string.Create(CultureInfo.InvariantCulture, $"entry {Domain}, mode {Mode}, value {Value}"),
    };
}

/// <summary>
/// PerfClientLimits (slot 26, 0x30c r2): the record NVML's locked clocks (nvidia-smi -lgc), Afterburner's Ctrl+L point
/// lock, Boost Lock and a V/F maximum-clock cap all live in. Such a lock outlives the program that set it and quietly
/// changes what a stress run or a curve test measures. Layout from the original's reader [observed]: +0 version word,
/// +4 0, +8 7 entries, each 0x18 bytes from +0xc: {+0 domain == its index, +4 0, +8 mode, +0xc 0, +0x10 value, +0x14 0}.
/// Read on the reference card (617.42): all seven entries 0.
/// </summary>
public static class ClientLimitsLayout
{
    public const int Size = 0x30c, Rev = 2, Count = 7, First = 0xc, Stride = 0x18;
    public const uint BoostLockUv = 1_500_000;

    /// <summary>The entries that hold something (mode not 0); throws on a layout other than the confirmed one.</summary>
    public static IReadOnlyList<ClientLimit> Parse(byte[] b)
    {
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        if (b.Length < Size || U(0) != new Versioned(Size, Rev).Word || U(4) != 0 || U(8) != Count)
            throw new InvalidDataException($"the clock-limit record is not the confirmed seven-domain layout (+4 = {U(4)}, +8 = {U(8)})");
        var held = new List<ClientLimit>();
        for (int k = 0; k < Count; k++)
        {
            int e = First + k * Stride;
            if (U(e) != k || U(e + 4) != 0 || U(e + 0xc) != 0 || U(e + 0x14) != 0)
                throw new InvalidDataException($"clock-limit entry {k} is not the confirmed layout");
            uint mode = U(e + 8), value = U(e + 0x10);
            if (mode != 0 || value != 0) held.Add(new ClientLimit(k, mode, value));
        }
        return held;
    }

    public static IReadOnlyList<ClientLimit> Read(NvClient hw, GpuSession gpu)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(Size, Rev).Word);
        var r = hw.CallRaw(gpu, Slot.PerfClientLimitsGetStatus, b);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return Parse(b);
    }
}
