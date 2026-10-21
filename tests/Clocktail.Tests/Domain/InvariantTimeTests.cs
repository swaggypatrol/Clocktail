using System.Globalization;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

/// <summary>Times written for programs (the System log query, file names, the tuning log's headings) do not depend on
/// the person's culture: Persian has another calendar, and some cultures write the time with dots.</summary>
public sealed class InvariantTimeTests : IDisposable
{
    private readonly CultureInfo _was = CultureInfo.CurrentCulture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));

    public InvariantTimeTests() => CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _was;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void The_System_log_query_is_in_the_event_logs_own_format()
    {
        if (!OperatingSystem.IsWindows()) return;
        var q = SystemLog.Query(SystemLog.DriverResetFilter, new DateTime(2026, 10, 19, 21, 48, 3, 250, DateTimeKind.Utc));
        Assert.Contains("@SystemTime>='2026-10-19T21:48:03.250Z'", q);
    }

    [Fact]
    public void A_residency_day_file_and_a_tuning_log_heading_are_Gregorian()
    {
        Assert.Equal("20261019.csv", Clocktail.Mcp.Residency.DayFile(new DateTime(2026, 10, 19)));
        string path = Path.Combine(_dir, "tuning-log.md");
        TuningLog.Append(path, new DateTimeOffset(2026, 10, 19, 21, 48, 3, TimeSpan.Zero), "Trial", ["x"]);
        Assert.Contains(new DateTimeOffset(2026, 10, 19, 21, 48, 3, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            File.ReadAllText(path));
    }
}
