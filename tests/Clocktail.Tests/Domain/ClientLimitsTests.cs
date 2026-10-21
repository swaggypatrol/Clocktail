using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Clocks;
using Clocktail.Hw;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>The driver's client clock-limit record (slot 26) as the original reads it: seven entries, each its own domain.</summary>
public class ClientLimitsTests
{
    private static byte[] Record(params (int Entry, uint Mode, uint Value)[] set)
    {
        var b = new byte[ClientLimitsLayout.Size];
        void W(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        W(0, new Versioned(ClientLimitsLayout.Size, ClientLimitsLayout.Rev).Word); W(8, 7);
        for (int k = 0; k < 7; k++) W(ClientLimitsLayout.First + k * ClientLimitsLayout.Stride, (uint)k);
        foreach (var (k, mode, value) in set)
        {
            W(ClientLimitsLayout.First + k * ClientLimitsLayout.Stride + 8, mode);
            W(ClientLimitsLayout.First + k * ClientLimitsLayout.Stride + 0x10, value);
        }
        return b;
    }

    [Fact]
    public void An_empty_record_holds_nothing_as_on_the_reference_card() => Assert.Empty(ClientLimitsLayout.Parse(Record()));

    [Fact]
    public void Locks_are_read_and_named()
    {
        var held = ClientLimitsLayout.Parse(Record((0, 2, 2_400_000), (1, 2, 1_200_000), (6, 3, 950_000)));
        Assert.Equal(3, held.Count);
        Assert.Equal("maximum clock 2400 MHz", held[0].Describe());
        Assert.Contains("probably a floor", held[1].Describe());
        Assert.Equal("voltage locked at 950 mV (a V/F point lock)", held[2].Describe());
        Assert.Contains("Boost Lock", ClientLimitsLayout.Parse(Record((6, 3, ClientLimitsLayout.BoostLockUv)))[0].Describe());
    }

    [Fact]
    public void Another_layout_is_refused_not_guessed()
    {
        var b = Record();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 6);
        Assert.Throws<InvalidDataException>(() => ClientLimitsLayout.Parse(b));
        b = Record();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ClientLimitsLayout.First + 2 * ClientLimitsLayout.Stride), 5);   // entry 2 names domain 5
        Assert.Throws<InvalidDataException>(() => ClientLimitsLayout.Parse(b));
    }

    [Fact]
    public void Card_report_flags_a_lock_set_by_another_tool()
    {
        var s = JsonDocument.Parse("""{"clock_locks":[{"entry":0,"mode":2,"value":2400000,"what":"maximum clock 2400 MHz"}]}""").RootElement;
        var row = Assert.Single(SettingsTools.ReportRows(s, JsonDocument.Parse("{}").RootElement), r => r.Name == "Clock lock");
        Assert.Equal(SettingsTools.Look, row.Mark);
    }
}
