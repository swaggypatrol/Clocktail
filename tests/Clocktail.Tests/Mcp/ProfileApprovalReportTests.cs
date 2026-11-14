using System.Text.Json;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>card_report, card_settings and gpu_status when the broker reports approval state on profile show.</summary>
public class ProfileApprovalReportTests
{
    private static readonly JsonElement EmptyLimiter = JsonDocument.Parse("{}").RootElement;

    private static IReadOnlyList<(string Name, string Mark, string Note)> Rows(string settingsJson) =>
        SettingsTools.ReportRows(JsonDocument.Parse(settingsJson).RootElement, EmptyLimiter);

    private static IReadOnlyList<(string Name, string Now)> Now(string settingsJson) =>
        SettingsTools.ReportNow(JsonDocument.Parse(settingsJson).RootElement, EmptyLimiter);

    private static string SettingsWithDaily(string dailyExtra, bool offCard = false) => $$"""
        {
          "power": {"limit_w": {{(offCard ? 575 : 600)}}, "daily_w": 600},
          "daily_profile": {"kept_at": "2026-10-21T10:00:00+00:00", "keeps": ["power/fe"]{{dailyExtra}}},
          "trial_in_force": false
        }
        """;

    private static BrokerAnswer ShowAnswer(string extraTopLevel, bool suspended = false)
    {
        string encoded = VfOffsets.Encode([0, 15000]);
        string json = $$"""
            {
              "success": true,
              "profile": {
                "KeptAt": "2026-10-21T10:00:00+00:00",
                "Suspended": {{(suspended ? "true" : "false")}},
                "Writes": [{"Kind": "{{VfWrites.Kind}}", "Key": "{{VfWrites.CoreKey}}", "Value": "{{encoded}}"}]
              }{{extraTopLevel}}
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return new BrokerAnswer(true, doc.RootElement.Clone(), null);
    }

    [Fact]
    public void Unapproved_and_not_suspended_says_waiting_for_approval_and_skips_off_daily_comparison()
    {
        string json = SettingsWithDaily("""
            ,"suspended":false,"approved":false
            """, offCard: true);

        Assert.Null(SettingsTools.DailyOffCard(JsonDocument.Parse(json).RootElement));

        var row = Assert.Single(Rows(json), r => r.Name == "Daily profile");
        var now = Assert.Single(Now(json), r => r.Name == "Daily profile").Now;
        Assert.Equal(SettingsTools.Look, row.Mark);
        Assert.Contains("waiting for approval", now);
        Assert.Contains("waiting for approval", row.Note);
        Assert.Contains("does not put it on", row.Note);
        Assert.Contains("load_daily", row.Note);
        Assert.Contains("Windows dialog", row.Note);
        Assert.Contains("once after updating", row.Note);
        Assert.DoesNotContain("the service puts it back after every boot and driver reset", row.Note);
        Assert.DoesNotContain("the card differs from it", row.Note);

        var offsets = TuningTools.DailyOffsetsFromShow(ShowAnswer("""
            ,"approved":false
            """), out string note);
        Assert.Equal([0, 15000], offsets);
        Assert.Contains("waiting for approval", note);
        Assert.Contains("load_daily", note);
        Assert.DoesNotContain("daily profile on", note);

        SettingsTools.DailyFromShow(ShowAnswer("""
            ,"approved":false
            """), out object? section, out _, out _);
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(section, SettingsTools.Json));
        Assert.False(parsed.RootElement.GetProperty("approved").GetBoolean());
        Assert.False(parsed.RootElement.TryGetProperty("approvals_unavailable", out _));
    }

    [Fact]
    public void Approved_absent_keeps_existing_output_unchanged()
    {
        string json = SettingsWithDaily("""
            ,"suspended":false
            """);

        Assert.Empty(SettingsTools.DailyOffCard(JsonDocument.Parse(json).RootElement)!);
        var row = Assert.Single(Rows(json), r => r.Name == "Daily profile");
        var now = Assert.Single(Now(json), r => r.Name == "Daily profile").Now;
        Assert.Equal(SettingsTools.Info, row.Mark);
        Assert.Equal("kept 2026-10-21, on the card", now);
        Assert.Equal("the service puts it back after every boot and driver reset", row.Note);

        TuningTools.DailyOffsetsFromShow(ShowAnswer(""), out string note);
        Assert.Equal("daily profile on", note);

        SettingsTools.DailyFromShow(ShowAnswer(""), out object? section, out _, out _);
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(section, SettingsTools.Json));
        Assert.False(parsed.RootElement.TryGetProperty("approved", out _));
        Assert.False(parsed.RootElement.TryGetProperty("approvals_unavailable", out _));
    }

    [Fact]
    public void Approved_true_keeps_existing_output_unchanged()
    {
        string json = SettingsWithDaily("""
            ,"suspended":false,"approved":true
            """);

        Assert.Empty(SettingsTools.DailyOffCard(JsonDocument.Parse(json).RootElement)!);
        var row = Assert.Single(Rows(json), r => r.Name == "Daily profile");
        var now = Assert.Single(Now(json), r => r.Name == "Daily profile").Now;
        Assert.Equal(SettingsTools.Info, row.Mark);
        Assert.Equal("kept 2026-10-21, on the card", now);
        Assert.Equal("the service puts it back after every boot and driver reset", row.Note);

        TuningTools.DailyOffsetsFromShow(ShowAnswer("""
            ,"approved":true
            """), out string note);
        Assert.Equal("daily profile on", note);

        SettingsTools.DailyFromShow(ShowAnswer("""
            ,"approved":true
            """), out object? section, out _, out _);
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(section, SettingsTools.Json));
        Assert.True(parsed.RootElement.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public void Suspended_and_unapproved_keeps_existing_suspended_wording()
    {
        string json = SettingsWithDaily("""
            ,"suspended":true,"approved":false
            """, offCard: true);

        Assert.Null(SettingsTools.DailyOffCard(JsonDocument.Parse(json).RootElement));
        var row = Assert.Single(Rows(json), r => r.Name == "Daily profile");
        var now = Assert.Single(Now(json), r => r.Name == "Daily profile").Now;
        Assert.Equal(SettingsTools.Info, row.Mark);
        Assert.Equal("suspended: stock until load_daily", now);
        Assert.Equal("", row.Note);

        TuningTools.DailyOffsetsFromShow(ShowAnswer("""
            ,"approved":false
            """, suspended: true), out string note);
        Assert.Equal("the daily profile is suspended (stock on the card)", note);
    }

    [Fact]
    public void Approvals_unavailable_shows_the_reason_and_setup_repair_command()
    {
        const string reason = "untrusted owner on C:\\ProgramData\\Clocktail\\Approvals";
        string escaped = JsonEncodedText.Encode(reason).ToString();
        string json = SettingsWithDaily($$"""
            ,"suspended":false,"approved":false,"approvals_unavailable":"{{escaped}}"
            """);

        var row = Assert.Single(Rows(json), r => r.Name == "Daily profile");
        Assert.Equal(SettingsTools.Look, row.Mark);
        Assert.Contains(reason, row.Note);
        Assert.Contains("npx -y clocktail@latest setup", row.Note);

        SettingsTools.DailyFromShow(ShowAnswer($$"""
            ,"approved":false,"approvals_unavailable":"{{escaped}}"
            """), out object? section, out _, out _);
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(section, SettingsTools.Json));
        Assert.False(parsed.RootElement.GetProperty("approved").GetBoolean());
        Assert.Equal(reason, parsed.RootElement.GetProperty("approvals_unavailable").GetString());
    }
}
