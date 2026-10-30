using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using Clocktail.Mcp;
using Clocktail.Tests.Domain;
namespace Clocktail.Tests.Mcp;
[Collection("fake-driver")]
public class P0EffectiveReadbackTests
{
    private sealed class Harness
    {
        public bool EchoOnly;
        public bool SharedCore;
        public readonly FakeVf Vf = new();
        public readonly GpuSession Gpu = new(1, "fake");
        public readonly TuningTools Tools;
        public Harness()
        {
            var hw = Vf.Install();
            byte[] p0 = FakeDriver.State[FakeVf.PstatesWord];
            BinaryPrimitives.WriteUInt32LittleEndian(p0.AsSpan(0xc), 2);
            int m = Pstates20Layout.Clock(0, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(p0.AsSpan(m), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(p0.AsSpan(m + 8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(m + 0x10), -1_000_000);
            BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(m + 0x14), 3_000_000);
            Vf.Effective = (i, offset) => checked((uint)((long)Vf.Base(i) + offset +
                (SharedCore ? 0 : BitConverter.ToInt32(p0, Pstates20Layout.Clock(0, 0) + 0xc))));
            var card = new Card(hw, Gpu, "fake", () => new(null, 950_000, 900_000, 1_100_000, VfReader.Read(hw, Gpu)), request =>
            {
                if (request.Op == BrokerRequest.OpProfile && request.Kind == "show")
                    return new(true, Result: JsonSerializer.SerializeToElement(new { profile = (object?)null }));
                if (request.Kind == "try" && !EchoOnly)
                {
                    var trial = JsonDocument.Parse(request.Value!).RootElement;
                    if (trial.TryGetProperty("writes", out var writes))
                        foreach (var write in writes.EnumerateArray())
                            if (write.GetProperty("kind").GetString() == ClockOffsets.Kind && write.GetProperty("key").GetString() == "core")
                            {
                                int value = int.Parse(write.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                                BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(Pstates20Layout.Clock(0, 0) + 0xc), value);
                                if (SharedCore) for (int i = 0; i < 127; i++) BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.ControlWord].AsSpan(VfLayout.OffsetField(i)), value);
                            }
                    if (trial.TryGetProperty("memory_khz", out var mem)) BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(m + 0xc), mem.GetInt32());
                }
                return new(true, Result: JsonSerializer.SerializeToElement(new { success = true }));
            });
            Tools = new(card, new Residency(card), new LiveWatch(card));
        }
    }
    [Fact]
    public void Broker_success_without_an_effective_core_change_is_not_reported_as_intact()
    {
        var h = new Harness { EchoOnly = true };
        var result = JsonDocument.Parse(h.Tools.SetCoreOffset(240)).RootElement;
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("curve_intact").GetBoolean());
    }
    [Fact]
    public void Changed_control_bytes_with_the_requested_effective_shift_are_intact()
    {
        var h = new Harness { SharedCore = true };
        var result = JsonDocument.Parse(h.Tools.SetCoreOffset(240)).RootElement;
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("curve_intact").GetBoolean());
        Assert.True(JsonDocument.Parse(h.Tools.SetMemoryOffset(3000)).RootElement.GetProperty("curve_intact").GetBoolean());
    }
}
