using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>S14: card_report says when the daily profile was kept on another VBIOS (off the card, at stock) or another
/// driver (on the card, worth a new stress_test).</summary>
public class ProfileBindingTests
{
    private static IReadOnlyList<(string Name, string Mark, string Note)> Rows(string daily) =>
        SettingsTools.ReportRows(JsonDocument.Parse($$"""{"daily_profile":{{daily}}}""").RootElement, JsonDocument.Parse("{}").RootElement);

    [Fact]
    public void A_profile_kept_on_another_vbios_is_off_the_card_and_says_what_to_do()
    {
        var rows = Rows("""{"kept_at":"2026-10-21T10:00:00+00:00","suspended":false,"kept_on_vbios":"98.02.2E.00.0A","vbios_now":"98.02.30.00.01"}""");
        var row = Assert.Single(rows, r => r.Name == "Daily profile");
        Assert.Equal(SettingsTools.Look, row.Mark);
        Assert.Contains("now runs VBIOS 98.02.30.00.01", row.Note);
        Assert.Contains("load_daily", row.Note);
        Assert.DoesNotContain(rows, r => r.Name == "Kept on driver");
    }

    [Fact]
    public void A_new_driver_asks_for_a_test_and_the_same_card_says_nothing()
    {
        var row = Assert.Single(Rows("""{"kept_at":"2026-10-21T10:00:00+00:00","suspended":false,"kept_on_driver":"617.42","driver_now":"620.01"}"""), r => r.Name == "Kept on driver");
        Assert.Equal(SettingsTools.Look, row.Mark);
        Assert.Contains("re-run stress_test", row.Note);
        var same = Rows("""{"kept_at":"2026-10-21T10:00:00+00:00","suspended":false,"kept_on_vbios":"98.02.2E.00.0A","kept_on_driver":"617.42"}""");
        Assert.DoesNotContain(same, r => r.Name == "Kept on driver");
        Assert.Equal(SettingsTools.Info, Assert.Single(same, r => r.Name == "Daily profile").Mark);
    }
}
