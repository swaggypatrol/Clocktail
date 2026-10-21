using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>card_report and the give-back answers say why: fans another tool holds, the last crash, a cell that did
/// not go back.</summary>
public class ReasonRowsTests
{
    private static (string Name, string Mark, string Note) Row(string settings, string name) =>
        Assert.Single(SettingsTools.ReportRows(JsonDocument.Parse(settings).RootElement, JsonDocument.Parse("{}").RootElement), r => r.Name == name);

    private const string Manual = "\"fans\":{\"channels\":[{\"mode\":\"manual\",\"duty_pct\":40,\"rpm\":1300}]}";

    private static string With(string holds) => "{" + Manual + ",\"journal_holds\":" + holds + "}";

    [Fact]
    public void Fans_held_by_another_tool_are_not_shown_as_a_trials()
    {
        var foreign = Row(With("[\"vf/core\"]"), "Fans");
        Assert.Equal(SettingsTools.Look, foreign.Mark);
        Assert.Contains("other than Clocktail", foreign.Note);
        var ours = Row(With("[\"fan/0\",\"fan/1\"]"), "Fans");
        Assert.Equal(SettingsTools.Safe, ours.Mark);
        Assert.Contains("a trial holds them", ours.Note);
        Assert.Contains("unknown", Row(With("{\"error\":\"x\"}"), "Fans").Note);
    }

    [Fact]
    public void A_crash_in_the_last_day_is_a_row()
    {
        var r = Row("""{"last_crash":{"found_at":"2026-10-21T10:00:00+00:00","what":"driver reset (nvlddmkm 153)","held":2}}""", "Last crash");
        Assert.Equal(SettingsTools.Look, r.Mark);
        Assert.Contains("nvlddmkm 153", r.Note);
    }

    [Fact]
    public void A_refused_give_back_says_why()
    {
        var answer = new BrokerAnswer(false, JsonSerializer.SerializeToElement(new
        {
            success = false, items = new[] { new { Kind = "power", Key = "f8", action = "Refused", Detail = "an F8 request the CLI created cannot be removed" } },
        }), "failed", "failed");
        var j = JsonDocument.Parse(TuningTools.Applied(answer, null)).RootElement;
        Assert.Equal("not everything went back: power/f8: an F8 request the CLI created cannot be removed", j.GetProperty("error").GetString());
        Assert.Contains("(an F8 request", j.GetProperty("writes")[0].GetString());
    }
}
