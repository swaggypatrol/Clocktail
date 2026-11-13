using Clocktail.Broker;

namespace Clocktail.Tests.Broker;

public sealed class BrokerLogRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-logrot-tests-" + Guid.NewGuid().ToString("n"));
    private string LogPath => Path.Combine(_dir, "broker.log");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Small_existing_log_is_preserved_and_appended_without_deletion()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "existing line 1\n");

        using (var w = new RotatingLogWriter(LogPath, maxBytes: 4096, maxBackups: 3))
        {
            w.WriteLine("new line 2");
        }

        string text = File.ReadAllText(LogPath);
        Assert.Contains("existing line 1", text);
        Assert.Contains("new line 2", text);
        Assert.False(File.Exists(LogPath + ".1"));
    }

    [Fact]
    public void Oversized_existing_log_on_startup_is_rotated_to_backup_not_deleted()
    {
        Directory.CreateDirectory(_dir);
        string prior = new string('A', 600) + "\n";
        File.WriteAllText(LogPath, prior);

        using (var w = new RotatingLogWriter(LogPath, maxBytes: 512, maxBackups: 3))
        {
            w.WriteLine("fresh startup line");
        }

        Assert.True(File.Exists(LogPath + ".1"));
        Assert.Equal(prior, File.ReadAllText(LogPath + ".1"));
        Assert.Equal("fresh startup line" + Environment.NewLine, File.ReadAllText(LogPath));
    }

    [Fact]
    public void Continuous_writes_rotate_and_bound_backup_count()
    {
        using (var w = new RotatingLogWriter(LogPath, maxBytes: 256, maxBackups: 2))
        {
            for (int i = 0; i < 50; i++)
                w.WriteLine($"entry-{i:000}-" + new string('z', 40));
        }

        Assert.True(File.Exists(LogPath));
        Assert.True(File.Exists(LogPath + ".1"));
        Assert.True(File.Exists(LogPath + ".2"));
        Assert.False(File.Exists(LogPath + ".3"));

        // Newest entry is in the active log, and no file exceeds maxBytes + 1 line (~320 bytes).
        Assert.Contains("entry-049-", File.ReadAllText(LogPath));
        Assert.True(new FileInfo(LogPath).Length < 400);
        Assert.True(new FileInfo(LogPath + ".1").Length < 400);
        Assert.True(new FileInfo(LogPath + ".2").Length < 400);
    }

    [Fact]
    public void Concurrent_writes_across_threads_do_not_drop_or_corrupt_lines()
    {
        using (var w = new RotatingLogWriter(LogPath, maxBytes: 1024, maxBackups: 3))
        {
            Parallel.For(0, 80, i => w.WriteLine($"thread-msg-{i:000}"));
        }

        var allLines = new List<string>();
        foreach (var f in new[] { LogPath, LogPath + ".1", LogPath + ".2", LogPath + ".3" })
            if (File.Exists(f)) allLines.AddRange(File.ReadAllLines(f));

        // Note: with maxBackups=3 and ~80 small lines (~1.4 KB total), at most 1 rotation happens so all lines survive.
        for (int i = 0; i < 80; i++)
            Assert.Contains($"thread-msg-{i:000}", allLines);
    }

    [Fact]
    public void Locked_backup_target_does_not_crash_or_stop_logging()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath + ".1", "locked backup\n");

        // Lock backup .1 without FileShare.Delete so File.Move(..., overwrite: true) to .1 fails with IOException.
        using (var locked = new FileStream(LogPath + ".1", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var w = new RotatingLogWriter(LogPath, maxBytes: 128, maxBackups: 1))
        {
            for (int i = 0; i < 10; i++)
                w.WriteLine($"still-logged-{i:00}-" + new string('x', 30));
        }

        string active = File.ReadAllText(LogPath);
        Assert.Contains("still-logged-00-", active);
        Assert.Contains("still-logged-09-", active);
    }
}
