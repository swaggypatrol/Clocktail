using System.Buffers.Binary;
using Clocktail.Domain.Thermal;

namespace Clocktail.Tests.Domain;

/// <summary>Slot 38 as probed on the reference card (2026-10-21): +0xd4 the core, +0x160 the memory chip, 1/256 °C.</summary>
public class ThermChannelsTests
{
    private static byte[] Block(int core, int memory)
    {
        var b = new byte[ThermChannels.Size];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ThermChannels.CoreAt), core);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ThermChannels.MemoryAt), memory);
        return b;
    }

    [Fact]
    public void The_probes_readings_decode_as_measured()
    {
        var idle = Block(10265, 13312);     // 40.1 °C core (NVML 40), 52 °C memory
        Assert.Equal(40.1, ThermChannels.Celsius(idle, ThermChannels.CoreAt));
        Assert.Equal(52.0, ThermChannels.Celsius(idle, ThermChannels.MemoryAt));
        Assert.Equal(80.0, ThermChannels.Celsius(Block(19893, 80 * 256), ThermChannels.MemoryAt));   // full load, 90 s
    }

    [Fact]
    public void No_reading_or_an_impossible_value_is_null()
    {
        Assert.Null(ThermChannels.Celsius(Block(0, ThermChannels.NoReading), ThermChannels.MemoryAt));
        Assert.Null(ThermChannels.Celsius(Block(0, 0), ThermChannels.MemoryAt));
        Assert.Null(ThermChannels.Celsius(Block(0, 200 * 256), ThermChannels.MemoryAt));
        Assert.Null(ThermChannels.Celsius(new byte[0x100], ThermChannels.MemoryAt));   // too short
    }
}
