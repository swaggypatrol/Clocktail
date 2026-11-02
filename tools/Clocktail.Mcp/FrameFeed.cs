using System.Globalization;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Fps;

namespace Clocktail.Mcp;

/// <summary>
/// A game's frame rate while it plays: the broker runs the capture (PresentMon needs administrator rights), this reads
/// its CSV as it grows (the file is the user's, no rights needed), whole lines only, into the run's total and the
/// current window. Without the capture (an older service, PresentMon missing) the reason is kept and the watch goes on.
/// Safe to use from the sampler and from live_read at once: one lock around the read and the figures. However long since
/// the last look, the file is read <see cref="ChunkBytes"/> at a time.
/// </summary>
public sealed class FrameFeed
{
    /// <summary>The most of the file read into memory at once.</summary>
    public const int ChunkBytes = 1 << 20;

    private readonly object _gate = new();
    private readonly Card? _card;
    private readonly string? _process;
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();   // keeps a character split between two reads
    private readonly StringBuilder _partial = new();
    private byte[]? _bytes;
    private char[]? _chars;
    private long _offset;
    private bool _dropPartial;
    private Frames _total = new(), _window = new();
    private readonly Frames _played = new();
    private DateTimeOffset? _stoppedAt;

    public string Path { get; }

    /// <summary>When the capture ends by itself (its seconds from the start); null for a file someone else runs.</summary>
    public DateTimeOffset? EndsAt { get; private init; }

    /// <summary>When the capture ended (its time ran out, or Stop); null while it runs or when that is not known.</summary>
    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (_gate) return _stoppedAt ?? (EndsAt is { } e && DateTimeOffset.Now >= e ? e : null);
        }
    }

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
        if (r.Ok && r.Result is { } res && res.TryGetProperty("path", out var p) && p.GetString() is { } path)
            return new FrameFeed(card, path, process) { EndsAt = DateTimeOffset.Now.AddSeconds(seconds) };
        why = r.Code == BrokerCode.UnknownOp ? "the installed Clocktail service is older than this server: run the setup again for frame rates" : r.Error ?? "the capture did not start";
        return null;
    }

    /// <summary>Reads what PresentMon has written since the last look.</summary>
    public void Poll()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return;   // PresentMon creates it with the first frame
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long end = fs.Length;
            if (end <= _offset) return;
            fs.Seek(_offset, SeekOrigin.Begin);
            _bytes ??= new byte[ChunkBytes];
            _chars ??= new char[Encoding.UTF8.GetMaxCharCount(ChunkBytes)];
            while (_offset < end)
            {
                int n = fs.Read(_bytes, 0, (int)Math.Min(_bytes.Length, end - _offset));
                if (n <= 0) break;
                _offset += n;
                Lines(_chars.AsSpan(0, _utf8.GetChars(_bytes, 0, n, _chars, 0, flush: false)));
            }
        }
    }

    /// <summary>Each whole line of <paramref name="text"/> into the total and the window; the unfinished last one waits for
    /// the rest.</summary>
    private void Lines(ReadOnlySpan<char> text)
    {
        while (text.Length > 0)
        {
            int nl = text.IndexOf('\n');
            if (nl < 0) { if (!_dropPartial) _partial.Append(text); return; }
            if (_dropPartial) _dropPartial = false;
            else
            {
                string line;
                if (_partial.Length > 0) { _partial.Append(text[..nl]); line = _partial.ToString(); _partial.Clear(); }
                else line = new string(text[..nl]);
                line = line.TrimEnd('\r');
                _total.Add(line);
                _window.Add(line);
            }
            text = text[(nl + 1)..];
        }
    }

    /// <summary>The game's frame rate over the whole capture so far, menus and loading screens included.</summary>
    public FrameStats.App? Total()
    {
        lock (_gate) { Poll(); return FrameStats.Game(_total.Apps(), _process); }
    }

    /// <summary>The game's frame rate over the windows taken so far: the frames under load, without the pauses that
    /// <see cref="StartWindow"/> dropped (a menu, a loading screen, an alt-tab).</summary>
    public FrameStats.App? Played()
    {
        lock (_gate) return FrameStats.Game(_played.Apps(), _process);
    }

    /// <summary>The game's frame rate since the last window, and a new window from here.</summary>
    public FrameStats.App? TakeWindow()
    {
        lock (_gate)
        {
            Poll();
            var game = FrameStats.Game(_window.Apps(), _process);
            _played.AddFrom(_window);
            _window = _total.Next();
            return game;
        }
    }

    /// <summary>A new window from here, dropping what came since the last one: the frames of a pause in load (a menu, a
    /// loading screen, an alt-tab) belong to no window.</summary>
    public void StartWindow()
    {
        lock (_gate)
        {
            Poll();
            _window = _total.Next();
        }
    }

    /// <summary>Waits up to <paramref name="wait"/> for a capture that ends by itself to finish writing.</summary>
    public void Settle(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        long before, after;
        do
        {
            lock (_gate) before = _offset;
            Thread.Sleep(500);
            Poll();
            lock (_gate) after = _offset;
        } while (after != before && DateTime.UtcNow < until);
    }

    /// <summary>Stops this feed's own capture (by its path: one the broker began since for someone else runs on).</summary>
    public void Stop()
    {
        if (_card == null) return;
        lock (_gate) _stoppedAt ??= DateTimeOffset.Now;
        try { _card.Broker(BrokerRequest.OpFps, "stop", JsonSerializer.Serialize(new { path = Path })); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }

    /// <summary>One frame-rate result as the tools show it.</summary>
    public static object? Show(FrameStats.App? a) => a == null ? null : new
    {
        app = a.Name, frames = a.Frames, seconds = a.Seconds, avg_fps = a.AvgFps, low_1pct_fps = a.Low1Fps, low_01pct_fps = a.Low01Fps,
        frametime_p50_ms = a.P50Ms, frametime_p99_ms = a.P99Ms, displayed_fps = a.DisplayedFps,
        other_swap_chains_left_out = a.OtherChains > 0 ? a.OtherChains : (int?)null,
    };

    /// <summary>One frame-rate result in a log line: "142 fps (1 % low 98, 0.1 % low 71) game.exe". With no frames
    /// after the capture ended (<paramref name="endedAt"/>), it says so: the game did not stop drawing.</summary>
    public static string Words(FrameStats.App? a, DateTimeOffset? endedAt = null) => a != null
        ? string.Create(CultureInfo.InvariantCulture, $"{a.AvgFps:0} fps (1 % low {a.Low1Fps:0}, 0.1 % low {a.Low01Fps:0}) {a.Name}")
        : endedAt is { } e ? string.Create(CultureInfo.InvariantCulture, $"no frames: the frame capture ended at {e:HH:mm}")
        : "no frames";
}
