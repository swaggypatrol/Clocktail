using System.Globalization;

namespace Clocktail.Domain.Fps;

/// <summary>
/// A game's frame rate from a PresentMon capture, its CSV one row per frame: per application the frames, the average,
/// the 1 % and 0.1 % lows (the frame rate over the slowest 1 % and 0.1 % of frames: a stutter that a percentile at the
/// edge would hide still shows), the frame time's P50 and P99, and the rate frames reached the screen. Columns are found by name in the header, which changes between PresentMon versions: MsBetweenPresents is the
/// frame time (what PresentMon's own console counts as fps), MsBetweenDisplayChange the time between frames shown.
/// </summary>
public static class FrameStats
{
    /// <param name="OtherChains">Swap chains of the same application (another window, another process of that name)
    /// that presented at the same time as the one counted, and were left out: each chain's frame times run on its own
    /// clock, so mixing two would double the time and make up lows that never happened.</param>
    public sealed record App(string Name, int Frames, double Seconds, double AvgFps, double Low1Fps, double Low01Fps,
        double P50Ms, double P99Ms, double? DisplayedFps, int OtherChains = 0);

    /// <summary>The desktop compositor presents all the time; it is never the game.</summary>
    public const string Compositor = "dwm.exe";

    /// <summary>Every application in the capture that presented, most frames first.</summary>
    public static IReadOnlyList<App> Parse(IEnumerable<string> lines)
    {
        var f = new Frames();
        foreach (var l in lines) f.Add(l);
        return f.Apps();
    }

    /// <summary>The game: the named process (with or without .exe), else the application with the most frames other than
    /// the compositor. Null when nothing else presented.</summary>
    public static App? Game(IReadOnlyList<App> apps, string? process)
    {
        if (process is { Length: > 0 } p)
        {
            string want = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe";
            return apps.FirstOrDefault(a => string.Equals(a.Name, want, StringComparison.OrdinalIgnoreCase));
        }
        return apps.FirstOrDefault(a => !string.Equals(a.Name, Compositor, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One application's figures from the swap chains counted for it. A single chain's frame times are sorted
    /// where they are kept (their order is never used again); several are put together in a copy.</summary>
    internal static App Summarize(string name, IReadOnlyList<Frames.Series> parts, int otherChains)
    {
        List<double> sorted;
        if (parts.Count == 1) { sorted = parts[0].Ms; sorted.Sort(); }
        else
        {
            sorted = new List<double>(parts.Sum(p => p.Ms.Count));
            foreach (var p in parts) sorted.AddRange(p.Ms);
            sorted.Sort();
        }
        double total = 0, shownMs = 0;
        int shown = 0;
        foreach (var p in parts) { total += p.MsSum; shownMs += p.ShownSum; shown += p.Shown; }
        double P(double q) => sorted[(int)Math.Clamp(Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
        double Low(double share)
        {
            int n = Math.Max(1, (int)Math.Round(share * sorted.Count));
            double slow = 0;
            for (int i = sorted.Count - n; i < sorted.Count; i++) slow += sorted[i];
            return 1000 * n / slow;
        }
        static double R(double v) => Math.Round(v, 1);
        return new App(name, sorted.Count, Math.Round(total / 1000, 1), R(1000 * sorted.Count / total), R(Low(0.01)), R(Low(0.001)),
            Math.Round(P(0.5), 2), Math.Round(P(0.99), 2), shown == 0 ? null : R(1000 * shown / shownMs), otherChains);
    }
}

/// <summary>Frame rows as they come, line by line: the header first (it names the columns), then one row per frame. Rows
/// without a frame time (the first frame of a swap chain, one PresentMon could not time) are skipped. Each swap chain
/// (application, process, chain address, where the header has them) is kept apart, because MsBetweenPresents is the time
/// since that chain's previous present. Per chain the frame times are kept (the lows need the exact slowest frames), the
/// rest as running sums. Not for two threads at once: its reader holds one lock around it.</summary>
public sealed class Frames
{
    internal sealed class Series(string name)
    {
        public readonly string Name = name;
        public readonly List<double> Ms = [];
        public double MsSum, ShownSum;
        public int Shown;
        /// <summary>The span of the chain's presents on the capture's clock (TimeInMs); NaN without that column.</summary>
        public double First = double.NaN, Last = double.NaN;

        public void Time(double t)
        {
            if (double.IsNaN(First) || t < First) First = t;
            if (double.IsNaN(Last) || t > Last) Last = t;
        }

        /// <summary>Two chains that presented at the same time (an unknown span overlaps nothing: the rows are counted
        /// together, as before the chains were told apart).</summary>
        public bool Overlaps(Series o) => First < o.Last && o.First < Last;
    }

    private int _app = -1, _between = -1, _shown = -1, _pid = -1, _chain = -1, _time = -1;
    private readonly Dictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FrameStats.App>? _apps;
    private int _appsAt = -1;

    /// <summary>The header line, once seen: a window of rows read later needs it too.</summary>
    public string? Header { get; private set; }

    public int Count { get; private set; }

    public void Add(string line)
    {
        line = line.TrimStart((char)0xFEFF);   // PresentMon writes its CSV as UTF-8 with a byte-order mark
        if (line.Length == 0) return;
        var f = line.Split(',');
        if (_app < 0)
        {
            int app = Array.IndexOf(f, "Application"), between = Array.IndexOf(f, "MsBetweenPresents");
            if (app < 0 || between < 0) return;   // not the header
            (_app, _between, _shown, Header) = (app, between, Array.IndexOf(f, "MsBetweenDisplayChange"), line);
            (_pid, _chain, _time) = (Array.IndexOf(f, "ProcessID"), Array.IndexOf(f, "SwapChainAddress"), Array.IndexOf(f, "TimeInMs"));
            return;
        }
        if (f.Length <= Math.Max(_app, _between) || !double.TryParse(f[_between], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || ms <= 0) return;
        string key = f[_app] + "\0" + Field(f, _pid) + "\0" + Field(f, _chain);
        if (!_series.TryGetValue(key, out var s)) _series[key] = s = new Series(f[_app]);
        s.Ms.Add(ms);
        s.MsSum += ms;
        if (Number(f, _shown) is { } d && d > 0) { s.Shown++; s.ShownSum += d; }
        if (Number(f, _time) is { } t) s.Time(t);
        Count++;
    }

    /// <summary>Adds another accumulator's frames, chain by chain (the windows of play, put together for the run).</summary>
    public void AddFrom(Frames other)
    {
        foreach (var (key, o) in other._series)
        {
            if (!_series.TryGetValue(key, out var s)) _series[key] = s = new Series(o.Name);
            s.Ms.AddRange(o.Ms);
            s.MsSum += o.MsSum;
            s.Shown += o.Shown;
            s.ShownSum += o.ShownSum;
            if (!double.IsNaN(o.First)) { s.Time(o.First); s.Time(o.Last); }
        }
        Count += other.Count;
    }

    /// <summary>A fresh accumulator that knows this one's header (for the next window of rows).</summary>
    public Frames Next()
    {
        var n = new Frames();
        if (Header != null) n.Add(Header);
        return n;
    }

    /// <summary>Per application its busiest swap chain, with the chains that presented at other times (the game's chain
    /// made again after a mode change, the game started again) counted in and those that presented alongside it left
    /// out; most frames first. The same answer until a frame comes in.</summary>
    public IReadOnlyList<FrameStats.App> Apps()
    {
        if (_apps != null && _appsAt == Count) return _apps;
        var apps = new List<FrameStats.App>();
        foreach (var g in _series.Values.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var chains = g.OrderByDescending(s => s.Ms.Count).ToList();
            var counted = new List<Series> { chains[0] };
            foreach (var c in chains.Skip(1))
                if (!counted.Any(c.Overlaps)) counted.Add(c);
            apps.Add(FrameStats.Summarize(chains[0].Name, counted, chains.Count - counted.Count));
        }
        _apps = [.. apps.OrderByDescending(a => a.Frames)];
        _appsAt = Count;
        return _apps;
    }

    private static string Field(string[] f, int i) => i >= 0 && i < f.Length ? f[i] : "";

    private static double? Number(string[] f, int i) =>
        i >= 0 && i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}

/// <summary>A frame-rate capture the elevated broker runs (PresentMon needs administrator rights or the Performance Log
/// Users group). Start answers where the CSV goes; the capture ends by itself after its seconds, or at Stop.</summary>
public interface IFrameCapture
{
    /// <exception cref="InvalidOperationException">The capture could not start; the message says why.</exception>
    object Start(string dir, int seconds, string? process);

    /// <summary>Stops the capture writing <paramref name="path"/>; another one running is left alone. Null stops whatever
    /// runs (what a client older than the path asks).</summary>
    object Stop(string? path);

    /// <summary>A stop for <paramref name="path"/> is for the capture writing <paramref name="running"/>.</summary>
    static bool StopsIt(string? path, string? running) => path == null || string.Equals(path, running, StringComparison.OrdinalIgnoreCase);
}
