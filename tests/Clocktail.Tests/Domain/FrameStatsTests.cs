using System.Globalization;
using Clocktail.Domain.Fps;

namespace Clocktail.Tests.Domain;

/// <summary>PresentMon 2.6.0's CSV as it came on the reference machine (header as captured 2026-10-20), with a game added.</summary>
public class FrameStatsTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay";

    private static string Row(string app, double ms, double shown) => string.Create(CultureInfo.InvariantCulture,
        $"{app},1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,NA,{ms},{shown},0.1,1,1,0,{ms},1,1,1,1,1,1,0,0,0");

    private static List<string> Capture()
    {
        var lines = new List<string> { Header };
        // The game: 990 frames at 7 ms, 9 at 20 ms, 1 at 50 ms; the compositor at 60 Hz; one untimed first frame.
        lines.Add(Row("Game.exe", 0, 0));
        for (int i = 0; i < 990; i++) lines.Add(Row("Game.exe", 7, 7));
        for (int i = 0; i < 9; i++) lines.Add(Row("Game.exe", 20, 20));
        lines.Add(Row("Game.exe", 50, 50));
        for (int i = 0; i < 1200; i++) lines.Add(Row("dwm.exe", 16.67, 16.67));
        return lines;
    }

    [Fact]
    public void The_game_is_the_busiest_app_that_is_not_the_compositor()
    {
        var apps = FrameStats.Parse(Capture());
        Assert.Equal("dwm.exe", apps[0].Name);   // more frames, never the game
        var g = FrameStats.Game(apps, null)!;
        Assert.Equal("Game.exe", g.Name);
        Assert.Equal(1000, g.Frames);             // the untimed first frame is not one
        Assert.Equal(139.7, g.AvgFps);            // 1000 frames in 7.16 s
        Assert.Equal(43.5, g.Low1Fps);            // the slowest 10 frames: 9 x 20 ms and 50 ms, 23 ms on average
        Assert.Equal(20, g.Low01Fps);             // the slowest one: 50 ms
        Assert.Equal(7, g.P99Ms);                 // the 99th percentile sits at the stutter's edge: why the lows average the slowest frames
        Assert.Equal(7, g.P50Ms);
        Assert.Equal(139.7, g.DisplayedFps);
        Assert.Equal("Game.exe", FrameStats.Game(apps, "game")!.Name);   // by name, with or without .exe
        Assert.Null(FrameStats.Game(apps, "other"));
    }

    [Fact]
    public void Rows_read_in_windows_keep_the_header()
    {
        var all = Capture();
        var total = new Frames();
        foreach (var l in all.Take(500)) total.Add(l);
        var window = total.Next();
        foreach (var l in all.Skip(500).Take(500)) { total.Add(l); window.Add(l); }
        Assert.Equal(500, FrameStats.Game(window.Apps(), null)!.Frames);
        Assert.Equal(998, FrameStats.Game(total.Apps(), null)!.Frames);   // the first 500 lines held the header, an untimed frame and 498
    }

    [Fact]
    public void A_header_behind_a_byte_order_mark_is_still_the_header()
    {
        // PresentMon 2.6.0's file starts with one (seen on the reference machine).
        var apps = FrameStats.Parse([(char)0xFEFF + Header, Row("Game.exe", 10, 10)]);
        Assert.Equal("Game.exe", apps.Single().Name);
    }

    private static string RowOn(string app, int pid, string chain, double atMs, double ms) => string.Create(CultureInfo.InvariantCulture,
        $"{app},{pid},{chain},DXGI,0,0,1,Hardware: Independent Flip,{atMs},NA,{ms},{ms},0.1,1,1,0,{ms},1,1,1,1,1,1,0,0,0");

    /// <summary>MsBetweenPresents is per swap chain: a second chain of the game's exe (a launcher window, a second process)
    /// presenting alongside would double the time and turn its slow presents into a 1 % low that never happened.</summary>
    [Fact]
    public void A_second_swap_chain_presenting_alongside_is_left_out()
    {
        var lines = new List<string> { Header };
        for (int i = 0; i < 1000; i++) lines.Add(RowOn("Game.exe", 1234, "0x1", i * 10.0, 10));
        for (int i = 0; i < 10; i++) lines.Add(RowOn("Game.exe", 1234, "0x2", i * 1000.0, 1000));
        var g = FrameStats.Game(FrameStats.Parse(lines), null)!;
        Assert.Equal(1000, g.Frames);
        Assert.Equal(100, g.AvgFps);
        Assert.Equal(10, g.Seconds);
        Assert.Equal(100, g.Low1Fps);
        Assert.Equal(1, g.OtherChains);

        // Another process of the same name at the same time is another chain too.
        lines = [Header];
        for (int i = 0; i < 1000; i++) lines.Add(RowOn("Game.exe", 1234, "0x1", i * 10.0, 10));
        for (int i = 0; i < 10; i++) lines.Add(RowOn("Game.exe", 999, "0x1", i * 1000.0, 1000));
        Assert.Equal(100, FrameStats.Game(FrameStats.Parse(lines), null)!.AvgFps);
    }

    /// <summary>A chain made again (a mode change, the game restarted) presents after the first one ended: its frames are
    /// the same play and count.</summary>
    [Fact]
    public void A_swap_chain_made_again_later_counts_with_the_first()
    {
        var lines = new List<string> { Header };
        for (int i = 0; i < 500; i++) lines.Add(RowOn("Game.exe", 1234, "0x1", i * 10.0, 10));
        for (int i = 0; i < 300; i++) lines.Add(RowOn("Game.exe", 1234, "0x2", 6000 + i * 20.0, 20));
        var g = FrameStats.Game(FrameStats.Parse(lines), null)!;
        Assert.Equal(800, g.Frames);
        Assert.Equal(0, g.OtherChains);
        Assert.Equal(11, g.Seconds);
    }

    /// <summary>Displayed frames and the frame-time total are running sums now, not lists: the figures are the ones the
    /// lists gave.</summary>
    [Fact]
    public void Running_sums_give_what_the_lists_gave()
    {
        var rng = new Random(7);
        var lines = new List<string> { Header };
        var ms = new List<double>();
        var shown = new List<double>();
        for (int i = 0; i < 10_000; i++)
        {
            double m = Math.Round(4 + rng.NextDouble() * 30, 3), d = rng.Next(4) == 0 ? 0 : Math.Round(m * (0.5 + rng.NextDouble()), 3);
            lines.Add(Row("Game.exe", m, d));
            ms.Add(m);
            if (d > 0) shown.Add(d);
        }
        var g = FrameStats.Game(FrameStats.Parse(lines), null)!;
        var sorted = ms.Order().ToList();
        Assert.Equal(10_000, g.Frames);
        Assert.Equal(Math.Round(ms.Sum() / 1000, 1), g.Seconds);
        Assert.Equal(Math.Round(1000 * ms.Count / ms.Sum(), 1), g.AvgFps);
        Assert.Equal(Math.Round(1000 * 100 / sorted.TakeLast(100).Sum(), 1), g.Low1Fps);
        Assert.Equal(Math.Round(1000 * 10 / sorted.TakeLast(10).Sum(), 1), g.Low01Fps);
        Assert.Equal(Math.Round(sorted[4999], 2), g.P50Ms);
        Assert.Equal(Math.Round(sorted[9899], 2), g.P99Ms);
        Assert.Equal(Math.Round(1000 * shown.Count / shown.Sum(), 1), g.DisplayedFps);
    }

    [Fact]
    public void Apps_is_the_same_answer_until_a_frame_comes_in()
    {
        var f = new Frames();
        foreach (var l in Capture()) f.Add(l);
        var first = f.Apps();
        Assert.Same(first, f.Apps());
        f.Add(Row("Game.exe", 7, 7));
        var after = f.Apps();
        Assert.NotSame(first, after);
        Assert.Equal(1001, FrameStats.Game(after, null)!.Frames);
        Assert.Equal(1000, FrameStats.Game(first, null)!.Frames);
    }

    [Fact]
    public void Windows_added_together_are_the_rows_read_in_one()
    {
        var all = Capture();
        var one = new Frames();
        foreach (var l in all) one.Add(l);
        var a = new Frames();
        foreach (var l in all.Take(700)) a.Add(l);
        var b = a.Next();
        foreach (var l in all.Skip(700)) b.Add(l);
        a.Apps();   // a window's figures taken first, as a live watch does
        var played = new Frames();
        played.AddFrom(a);
        played.AddFrom(b);
        Assert.Equal(one.Count, played.Count);
        Assert.Equal(one.Apps(), played.Apps());
    }

    [Fact]
    public void Lines_before_the_header_and_broken_rows_are_skipped()
    {
        var apps = FrameStats.Parse(["warning: something", Header, "Game.exe,1,2", Row("Game.exe", 10, 0)]);
        Assert.Equal(1, apps.Single().Frames);
        Assert.Null(apps.Single().DisplayedFps);
    }
}
