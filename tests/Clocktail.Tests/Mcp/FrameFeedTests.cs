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

    [Fact]
    public void Without_it_the_pause_is_averaged_in()
    {
        var feed = FrameFeed.OnFile(_path, null);
        Frames("Game.exe", 2, 3000);
        Frames("Game.exe", 1000 / 90.0, 900);
        Assert.True(feed.TakeWindow()!.AvgFps > 200);   // the shape the fix removes
    }
}
