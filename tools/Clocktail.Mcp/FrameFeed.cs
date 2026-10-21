using System.Text;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Fps;

namespace Clocktail.Mcp;

/// <summary>
/// A game's frame rate while it plays: the broker runs the capture (PresentMon needs administrator rights), this reads
/// its CSV as it grows (the file is the user's, no rights needed), whole lines only, into the run's total and the
/// current window. Without the capture (an older service, PresentMon missing) the reason is kept and the watch goes on.
/// </summary>
public sealed class FrameFeed
{
    private readonly Card? _card;
    private readonly string? _process;
    private long _offset;
    private string _partial = "";
    private bool _dropPartial;
    private Frames _total = new(), _window = new();

    public string Path { get; }

    private FrameFeed(Card? card, string path, string? process) { _card = card; Path = path; _process = process; }

    /// <summary>A feed on a capture file someone else runs (or one that has ended): it reads, it never stops anything.</summary>
    public static FrameFeed OnFile(string path, string? process) => new(null, path, process);

    /// <summary>
    /// A feed on a capture that is running for someone else (the live watch's), from here on: its header, then only the
    /// frames written after now. The broker runs one capture at a time, and a second Start ends the first, so a watch
    /// during a live watch reads the live one's file instead. It never stops the capture.
    /// </summary>
    public static FrameFeed Following(string path, string? process)
    {
        var feed = new FrameFeed(null, path, process);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
            string text = Encoding.UTF8.GetString(head, 0, fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false));
            int nl = text.IndexOf('\n');
            if (nl < 0) return feed;   // no header yet: read from the start, as a fresh capture is read
            string header = text[..nl].TrimEnd('\r');
            feed._total.Add(header);
            feed._window.Add(header);
            long end = fs.Length;
            fs.Seek(end - 1, SeekOrigin.Begin);
            feed._dropPartial = fs.ReadByte() != '\n';   // mid-line: the rest of that frame is not this feed's
            feed._offset = end;
        }
        catch (IOException) { }   // not there yet: read from the start
        return feed;
    }

    /// <summary>Starts a capture for <paramref name="seconds"/>; null with the reason when it cannot.</summary>
    public static FrameFeed? Start(Card card, int seconds, string? process, out string? why)
    {
        var r = card.Broker(BrokerRequest.OpFps, "start", JsonSerializer.Serialize(new { seconds, process }));
        why = null;
        if (r.Ok && r.Result is { } res && res.TryGetProperty("path", out var p) && p.GetString() is { } path) return new FrameFeed(card, path, process);
        why = r.Code == BrokerCode.UnknownOp ? "the installed Clocktail service is older than this server: run the setup again for frame rates" : r.Error ?? "the capture did not start";
        return null;
    }

    /// <summary>Reads what PresentMon has written since the last look.</summary>
    public void Poll()
    {
        if (!File.Exists(Path)) return;   // PresentMon creates it with the first frame
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length <= _offset) return;
        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _offset];
        int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        _offset += n;
        string text = _partial + Encoding.UTF8.GetString(buf, 0, n);
        if (_dropPartial)
        {
            int nl = text.IndexOf('\n');
            if (nl < 0) { _partial = ""; return; }
            text = text[(nl + 1)..];
            _dropPartial = false;
        }
        int last = text.LastIndexOf('\n');
        _partial = last < 0 ? text : text[(last + 1)..];
        if (last < 0) return;
        foreach (var line in text[..last].Split('\n'))
        {
            string l = line.TrimEnd('\r');
            _total.Add(l);
            _window.Add(l);
        }
    }

    /// <summary>The game's frame rate over the whole capture so far.</summary>
    public FrameStats.App? Total() { Poll(); return FrameStats.Game(_total.Apps(), _process); }

    /// <summary>The game's frame rate since the last window, and a new window from here.</summary>
    public FrameStats.App? TakeWindow()
    {
        Poll();
        var game = FrameStats.Game(_window.Apps(), _process);
        _window = _total.Next();
        return game;
    }

    /// <summary>A new window from here, dropping what came since the last one: the frames of a pause in load (a menu, a
    /// loading screen, an alt-tab) belong to no window.</summary>
    public void StartWindow()
    {
        Poll();
        _window = _total.Next();
    }

    /// <summary>Waits up to <paramref name="wait"/> for a capture that ends by itself to finish writing.</summary>
    public void Settle(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        long before;
        do { before = _offset; Thread.Sleep(500); Poll(); } while (_offset != before && DateTime.UtcNow < until);
    }

    public void Stop()
    {
        try { _card?.Broker(BrokerRequest.OpFps, "stop"); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }

    /// <summary>One frame-rate result as the tools show it.</summary>
    public static object? Show(FrameStats.App? a) => a == null ? null : new
    {
        app = a.Name, frames = a.Frames, seconds = a.Seconds, avg_fps = a.AvgFps, low_1pct_fps = a.Low1Fps, low_01pct_fps = a.Low01Fps,
        frametime_p50_ms = a.P50Ms, frametime_p99_ms = a.P99Ms, displayed_fps = a.DisplayedFps,
    };

    /// <summary>One frame-rate result in a log line: "142 fps (1 % low 98, 0.1 % low 71) game.exe".</summary>
    public static string Words(FrameStats.App? a) => a == null ? "no frames" : string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{a.AvgFps:0} fps (1 % low {a.Low1Fps:0}, 0.1 % low {a.Low01Fps:0}) {a.Name}");
}
