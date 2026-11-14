using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The residency day files: a flush at a time, so a sample goes to disk once; a day file another server holds
/// does not fail the tool reading it; and two servers' samplers read back as one sample a second.</summary>
public sealed class ResidencyLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-residency-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).ToLocalTime();

    private static IEnumerable<Residency.Tick> Seconds(int n, int ms = 0) =>
        Enumerable.Range(0, n).Select(i => new Residency.Tick(T0.AddSeconds(i).AddMilliseconds(ms), 900_000, 2500, 100, 400));

    private string DayPath => Path.Combine(_dir, Residency.DayFile(T0.LocalDateTime.Date));

    [Fact]
    public void Flushes_at_the_same_time_write_each_sample_once()
    {
        for (int round = 0; round < 20; round++)
        {
            var log = new ResidencyLog(() => _dir);
            foreach (var t in Seconds(200)) log.Add(t);
            Parallel.For(0, 8, _ => log.Flush());
            Assert.Equal(200, File.ReadAllLines(DayPath).Length);
            File.Delete(DayPath);
        }
    }

    [Fact]
    public void A_day_file_another_server_is_appending_to_does_not_fail_the_read()
    {
        var log = new ResidencyLog(() => _dir);
        foreach (var t in Seconds(30)) log.Add(t);
        log.Flush();
        foreach (var t in Seconds(30).Select(t => t with { At = t.At.AddSeconds(30) })) log.Add(t);
        using (new FileStream(DayPath, FileMode.Append, FileAccess.Write, FileShare.Read))   // as File.AppendAllText holds it
            Assert.Equal(60, log.Between(T0, T0.AddMinutes(5)).Count);                         // the batch, from memory
        Assert.Equal(60, log.Between(T0, T0.AddMinutes(5)).Count);                             // now on disk, read once
        Assert.Equal(60, File.ReadAllLines(DayPath).Length);
    }

    [Fact]
    public void Two_samplers_in_the_same_files_read_back_one_sample_a_second()
    {
        var a = new ResidencyLog(() => _dir);
        var b = new ResidencyLog(() => _dir);
        foreach (var t in Seconds(60, ms: 100)) a.Add(t);
        foreach (var t in Seconds(60, ms: 600)) b.Add(t);
        a.Flush();
        b.Flush();
        var read = a.Between(T0, T0.AddMinutes(5));
        Assert.Equal(60, read.Count);
        Assert.All(read, t => Assert.Equal(100, t.At.Millisecond));   // the earliest of each second, in order
        Assert.Equal(read.OrderBy(t => t.At), read);
    }

    [Fact]
    public void Between_filters_by_time_window_and_flush_handles_overflow_prefix_trim()
    {
        var log = new ResidencyLog(() => _dir);
        for (int i = -10; i < 15; i++)
            log.Add(new Residency.Tick(T0.AddSeconds(i), 900_000u + (uint)(i + 10), 2500, 100, 400.5));
        log.Flush();

        var bounded = log.Between(T0, T0.AddSeconds(9));
        Assert.Equal(10, bounded.Count);
        Assert.All(bounded, t => Assert.InRange(t.At, T0, T0.AddSeconds(9)));

        // Overflow MaxPending to trigger prefix eviction and ensure Flush removes remaining prefix cleanly.
        var overflow = new ResidencyLog(() => _dir);
        int total = ResidencyLog.MaxPending + 25;
        for (int i = 0; i < total; i++)
            overflow.Add(new Residency.Tick(T0.AddSeconds(100 + i), 910_000, 2600, 100, null));
        overflow.Flush();
        var kept = overflow.Between(T0.AddSeconds(99), T0.AddSeconds(100 + total));
        Assert.Equal(ResidencyLog.MaxPending, kept.Count);
        Assert.Equal(T0.AddSeconds(125), kept[0].At);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}