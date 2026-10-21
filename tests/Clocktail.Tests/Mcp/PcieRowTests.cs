using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>card_report's PCIe link row: a link below what the card and slot allow is judged only under load (it slows
/// down at idle by design), and any replay is worth a look.</summary>
public class PcieRowTests
{
    private static (string Name, string Mark, string Note)? Row(string pcie, double util)
    {
        var rows = SettingsTools.ReportRows(JsonDocument.Parse($"{{\"pcie\":{pcie}}}").RootElement,
            JsonDocument.Parse($"{{\"readings\":{{\"util_pct\":{util}}}}}").RootElement);
        return rows.Where(r => r.Name == "PCIe link").Select(r => ((string, string, string)?)r).SingleOrDefault();
    }

    private static string Link(int gen, int width, int replays = 0, int maxGen = 5, int gpuMax = 5) =>
        $"{{\"gen\":{gen},\"width\":{width},\"max_gen\":{maxGen},\"max_width\":16,\"gpu_max_gen\":{gpuMax},\"replays\":{replays}}}";

    [Fact]
    public void Full_speed_under_load_is_fine_and_says_so()
    {
        var r = Row(Link(5, 16), 98)!.Value;
        Assert.Equal(SettingsTools.Safe, r.Mark);
        Assert.Contains("as fast as the card and slot allow", r.Note);
    }

    [Fact]
    public void A_slow_link_is_a_look_under_load_and_only_information_at_idle()
    {
        var loaded = Row(Link(4, 8), 98)!.Value;
        Assert.Equal(SettingsTools.Look, loaded.Mark);
        Assert.Contains("a riser", loaded.Note);
        var idle = Row(Link(1, 16), 3)!.Value;
        Assert.Equal(SettingsTools.Info, idle.Mark);
        Assert.Contains("judge it under load", idle.Note);
    }

    [Fact]
    public void Replays_are_a_look_and_a_slower_slot_is_said_not_marked()
    {
        var replays = Row(Link(5, 16, replays: 12), 98)!.Value;
        Assert.Equal(SettingsTools.Look, replays.Mark);
        Assert.Contains("12 replays since boot", replays.Note);
        var gen4Board = Row(Link(4, 16, maxGen: 4), 98)!.Value;
        Assert.Equal(SettingsTools.Safe, gen4Board.Mark);
        Assert.Contains("the slot is Gen 4; the card can do Gen 5", gen4Board.Note);
    }

    [Fact]
    public void No_reading_no_row()
    {
        Assert.Null(Row("{\"error\":\"NVML does not answer\"}", 98));
    }
}
