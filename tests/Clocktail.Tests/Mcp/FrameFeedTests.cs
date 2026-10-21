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

    [Fact]
    public void Without_it_the_pause_is_averaged_in()
    {
        var feed = FrameFeed.OnFile(_path, null);
        Frames("Game.exe", 2, 3000);
        Frames("Game.exe", 1000 / 90.0, 900);
        Assert.True(feed.TakeWindow()!.AvgFps > 200);   // the shape the fix removes
    }
}
