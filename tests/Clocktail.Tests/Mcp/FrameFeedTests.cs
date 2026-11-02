using System.Globalization;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>A live watch's frame windows: a stretch of play starts a window of its own, so the menu before it (often
/// uncapped, hundreds of fps) is not averaged into its first line.</summary>
public sealed class FrameFeedTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n") + ".csv");

    private const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange";

    public FrameFeedTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Header + "\n");
    }

    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }

    private void Frames(string app, double ms, int count) => File.AppendAllLines(_path, Enumerable.Range(0, count).Select(_ =>
        string.Create(CultureInfo.InvariantCulture, $"{app},1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,NA,{ms},{ms}")));

    [Fact]
    public void A_window_started_at_load_start_holds_only_the_play_after_it()
    {
        var feed = FrameFeed.OnFile(_path, null);
        Frames("Game.exe", 2, 3000);     // 6 s of menu at 500 fps
        feed.StartWindow();              // load started
        Frames("Game.exe", 1000 / 90.0, 900);
        var w = feed.TakeWindow()!;
        Assert.Equal(900, w.Frames);
        Assert.Equal(90, w.AvgFps, 0);
        Assert.Equal(3900, feed.Total()!.Frames);   // the run's total still counts every frame
    }

    /// <summary>A watch during a live watch follows the live capture's file instead of starting a capture of its own,
    /// which would end the live one: it reads the header, then only the frames written after it began.</summary>
    [Fact]
    public void A_follower_reads_only_what_the_running_capture_writes_after_it_starts()
    {
        Frames("Game.exe", 2, 3000);                                     // the live watch's frames so far
        File.AppendAllText(_path, "Game.exe,1234,0x1,DXGI,0,0,1,Hard");    // and a frame half written
        var watch = FrameFeed.Following(_path, null);
        File.AppendAllText(_path, "ware: Independent Flip,0,NA,2,2\n");
        Frames("Game.exe", 1000 / 90.0, 900);
        var got = watch.Total()!;
        Assert.Equal(900, got.Frames);                                   // not the 3000 before it, nor the half frame
        Assert.Equal(90, got.AvgFps, 0);

        // A capture with only its header so far, or none yet, is read from the start.
        File.WriteAllText(_path, Header + "\n");
        var early = FrameFeed.Following(_path, null);
        Frames("Game.exe", 10, 100);
        Assert.Equal(100, early.Total()!.Frames);
        Assert.Null(FrameFeed.Following(_path + ".none", null).Total());
    }

    /// <summary>The sampler takes windows while live_read asks for the run's total: one lock around the read and the
    /// figures, so no frame is read twice or skipped, and no figure is taken while a frame is being added.</summary>
    [Fact]
    public async Task Live_read_and_the_sampler_reading_at_once_count_every_frame_once()
    {
        var feed = FrameFeed.OnFile(_path, null);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        bool done = false;
        int windows = 0;
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) Frames("Game.exe", 10, 1000);
            Volatile.Write(ref done, true);
        });
        var sampler = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
                try { windows += feed.TakeWindow()?.Frames ?? 0; } catch (Exception e) { errors.Enqueue(e); }
        });
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
                try { feed.Total(); } catch (Exception e) { errors.Enqueue(e); }
        });
        await Task.WhenAll(writer, sampler, reader);
        windows += feed.TakeWindow()?.Frames ?? 0;
        Assert.Empty(errors);
        Assert.Equal(200_000, feed.Total()!.Frames);
        Assert.Equal(200_000, windows);                    // the windows neither overlap nor drop a frame
        Assert.Equal(200_000, feed.Played()!.Frames);
    }

    /// <summary>A backlog bigger than one read (a long stretch nobody looked at) is read a chunk at a time, exactly: a
    /// line and a character cut by a chunk's edge come out whole.</summary>
    [Fact]
    public void A_backlog_larger_than_one_read_is_read_exactly()
    {
        int rows = 0;
        while (new FileInfo(_path).Length < FrameFeed.ChunkBytes - 20_000) { Frames("Game.exe", 10, 100); rows += 100; }
        while (new FileInfo(_path).Length < FrameFeed.ChunkBytes - 300) { Frames("Game.exe", 10, 1); rows++; }
        // An untimed row padded so that the 'ä' below starts on the chunk's last byte: its two bytes fall in two reads.
        long at = new FileInfo(_path).Length;
        const string Before = "Spiel-";
        string Untimed(int pad) => $"Game.exe,1234,0x1,DXGI,0,0,1,{new string('x', pad)},0,NA,0,0\n";
        int padding = (int)(FrameFeed.ChunkBytes - 1 - at - Before.Length - Untimed(0).Length);
        File.AppendAllText(_path, Untimed(padding));
        Assert.Equal((long)FrameFeed.ChunkBytes - 1 - Before.Length, new FileInfo(_path).Length);
        File.AppendAllText(_path, string.Create(CultureInfo.InvariantCulture, $"Spiel-ä.exe,1234,0x2,DXGI,0,0,1,Hardware: Independent Flip,0,NA,20,20\n"));
        for (int i = 0; i < 30; i++) { Frames("Game.exe", 10, 1000); rows += 1000; }
        Assert.True(new FileInfo(_path).Length > 2 * FrameFeed.ChunkBytes);

        Assert.Equal(rows, FrameFeed.OnFile(_path, null).Total()!.Frames);
        var spiel = FrameFeed.OnFile(_path, "Spiel-ä").Total()!;
        Assert.Equal("Spiel-ä.exe", spiel.Name);
        Assert.Equal(1, spiel.Frames);
    }

    [Fact]
    public void Reading_after_a_long_gap_gives_what_reading_along_the_way_gives()
    {
        var along = FrameFeed.OnFile(_path, null);
        for (int i = 0; i < 20; i++)
        {
            Frames("Game.exe", 7 + i % 5, 2000);
            Frames("dwm.exe", 16.67, 300);
            along.Poll();
        }
        Assert.Equal(along.Total(), FrameFeed.OnFile(_path, null).Total());
    }

    /// <summary>The run's fps under load: the frames of the windows, not the menu before or after them.</summary>
    [Fact]
    public void Played_holds_only_the_frames_of_the_windows()
    {
        var feed = FrameFeed.OnFile(_path, null);
        Frames("Game.exe", 2, 3000);     // the menu
        feed.StartWindow();
        Frames("Game.exe", 1000 / 90.0, 900);
        feed.TakeWindow();
        Frames("Game.exe", 2, 3000);     // the menu again
        feed.StartWindow();
        Frames("Game.exe", 1000 / 90.0, 450);
        feed.TakeWindow();
        var played = feed.Played()!;
        Assert.Equal(1350, played.Frames);
        Assert.Equal(90, played.AvgFps, 0);
        Assert.Equal(7350, feed.Total()!.Frames);
    }

    /// <summary>A capture runs for a set time (4 h at most, while a live watch runs 6): past its end a line says the
    /// capture ended, not that the game drew nothing.</summary>
    [Fact]
    public void No_frames_after_the_capture_ended_says_so()
    {
        Assert.Equal("no frames", FrameFeed.Words(null));
        Assert.Equal("no frames: the frame capture ended at 21:04", FrameFeed.Words(null, new DateTimeOffset(2026, 11, 2, 21, 4, 0, TimeSpan.Zero)));
        Assert.Null(FrameFeed.OnFile(_path, null).EndedAt);   // a file someone else runs: not known
    }

    [Fact]
    public void Without_it_the_pause_is_averaged_in()
    {
        var feed = FrameFeed.OnFile(_path, null);
        Frames("Game.exe", 2, 3000);
        Frames("Game.exe", 1000 / 90.0, 900);
        Assert.True(feed.TakeWindow()!.AvgFps > 200);   // the shape the fix removes
    }
}
