using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Domain.Thermal;

/// <summary>
/// The thermal channels' status (slot 38, ThermChannelGetStatus), as probed on the reference card (2026-10-21, 617.42):
/// 0x34c8 r3, asked for every channel with +8 = 0xffff. Two fields read as temperatures in 1/256 °C: +0xd4 the GPU core
/// (it follows NVML's GPU temperature) and +0x160 the memory, in whole degrees: 52 °C idle, 80 under full load, 82 at the
/// peak, matching HWiNFO's "memory chip temperature" (not its junction, which reads ~4 °C hotter at idle). No hotspot field:
/// nothing else in the block reads as a temperature. 0xff00 marks a channel with no reading.
/// </summary>
public static class ThermChannels
{
    public const int Size = 0x34c8, Rev = 3, Mask = 8, CoreAt = 0xd4, MemoryAt = 0x160;
    public const int NoReading = 0xff00;

    /// <summary>A field as °C, or null when it holds no reading or nothing a GPU could be at (0..150 °C).</summary>
    public static double? Celsius(byte[] b, int at)
    {
        if (b.Length < at + 4) return null;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
        return raw == NoReading || raw <= 0 || raw > 150 * 256 ? null : Math.Round(raw / 256.0, 1);
    }

    /// <summary>The memory temperature, °C; null when the driver does not answer or the field holds no reading.</summary>
    public static double? MemoryC(NvClient hw, GpuSession gpu)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(Size, Rev).Word);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Mask), 0xffff);
        return hw.CallRaw(gpu, Slot.ThermChannelGetStatus, b).IsOk ? Celsius(b, MemoryAt) : null;
    }
}
