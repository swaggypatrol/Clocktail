using System.Text.Json;
using Clocktail.Domain.Platform;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>S-7: Windows' TDR settings decide what a hang looks like, and whether mapping an edge is safe at all.</summary>
public class TdrSettingsTests
{
    [Fact]
    public void Defaults_and_the_reference_machine()
    {
        var unset = new TdrSettings(null, null);
        Assert.Equal((2, 3, 30), (unset.Delay, unset.LevelOrDefault, unset.StressGraceS));
        var reference = new TdrSettings(30, 3);   // this machine, 2026-10-21
        Assert.Equal(45, reference.StressGraceS);
        Assert.Contains("about 30 s", reference.Describe());
        Assert.False(reference.Unsafe);
    }

    [Fact]
    public void Level_0_or_1_is_not_safe_to_map_an_edge()
    {
        Assert.True(new TdrSettings(2, 0).Unsafe);
        Assert.True(new TdrSettings(2, 1).Unsafe);
        Assert.Contains("never resets", new TdrSettings(2, 0).Describe());
        var s = JsonDocument.Parse("""{"tdr":{"delay_s":2,"level":0,"what":"TdrLevel 0: Windows never resets a hung GPU","safe_to_map":false}}""").RootElement;
        Assert.Equal(SettingsTools.Act, Assert.Single(SettingsTools.ReportRows(s, JsonDocument.Parse("{}").RootElement), r => r.Name == "Driver timeout (TDR)").Mark);
    }
}
