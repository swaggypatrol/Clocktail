using System.Globalization;
using System.Text;
using Clocktail.Domain.Broker;
using Microsoft.Extensions.Hosting;

namespace Clocktail.Mcp;

/// <summary>
/// Where the card runs, once a second, for as long as the server is up: NVVDD, core clock, utilization and board power
/// under load (utilization at or above <see cref="MinUtilPct"/>; the desktop and menus say nothing about a curve).
/// The last minutes stay in memory for adjust_curve's operating point; every sample goes to a day file under
/// residency\ beside the tuning log, so a trial's outcome can be pinned to the curve points it actually ran on even
/// after the server restarts, a driver reset included. Every MCP client's worker runs its own sampler into the same day
/// files, so what is read back is one sample a second (<see cref="OnePerSecond"/>).
/// </summary>
public sealed class Residency(Card card) : BackgroundService
{
    public const int MinUtilPct = 50;
    /// <summary>During a stress run: nvgwls at load 30-50 is a 250 Hz square wave that NVML reads as 30-57 % utilization,
    /// under <see cref="MinUtilPct"/>, so the top of the curve (where the reference card's light-load hangs were) left no
    /// samples for its trial. While stress_test runs, a sample counts from this.</summary>
    public const int StressMinUtilPct = 20;
    private DateTimeOffset _stressUntil;

    /// <summary>stress_test is running until then (<see cref="DateTimeOffset.MinValue"/>: it has ended).</summary>
    public void StressUntil(DateTimeOffset until) { lock (_gate) _stressUntil = until; }

    /// <summary>Whether a sample at this utilization is load worth keeping.</summary>
    public static bool Counts(uint util, DateTimeOffset at, DateTimeOffset stressUntil) =>
        util >= (at < stressUntil ? StressMinUtilPct : MinUtilPct);
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    /// <summary>A day's file name, Gregorian whatever the person's calendar, so a day written is the day read back.</summary>
    public static string DayFile(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";

    public sealed record Tick(DateTimeOffset At, uint Uv, uint Mhz, uint Util, double? W);

    /// <summary>One sample a second (the earliest of each), in time order. With two servers open each second is on record
    /// twice, a few hundred milliseconds apart, and every duration counted from samples would double.</summary>
    public static List<Tick> OnePerSecond(IEnumerable<Tick> ticks) =>
        [.. ticks.OrderBy(t => t.At).DistinctBy(t => t.At.ToUnixTimeSeconds())];

    private readonly object _gate = new();
    private readonly Queue<Tick> _recent = new();
    private readonly ResidencyLog _log = new(() => BrokerDir(card));

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int n = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                // A sampler must never take the server down: an unreadable second is just a missing sample. Nor wait on a
                // tool's read of the day files: the batch goes out on a later round.
                try { Take(); } catch (Exception) { }
                if (++n % 5 == 0) try { _log.Flush(wait: false); } catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
        finally { try { Flush(); } catch (Exception) { } }
    }

    private void Take()
    {
        var (uv, mhz, util, w) = card.ReadPoint();
        var now = DateTimeOffset.Now;
        DateTimeOffset stress;
        lock (_gate) stress = _stressUntil;
        if (uv is not { } u || mhz is not { } m || util is not { } p || !Counts(p, now, stress)) return;
        var tick = new Tick(now, u, m, p, w);
        lock (_gate)
        {
            _recent.Enqueue(tick);
            while (_recent.Count > 0 && tick.At - _recent.Peek().At > Keep) _recent.Dequeue();
        }
        _log.Add(tick);
    }

    /// <summary>The median NVVDD under load over the last <paramref name="span"/>; null when the card was not loaded.</summary>
    public uint? MedianUv(TimeSpan span)
    {
        List<uint> v;
        lock (_gate)
        {
            var since = DateTimeOffset.Now - span;
            v = _recent.Where(t => t.At >= since).Select(t => t.Uv).Order().ToList();
        }
        return v.Count == 0 ? null : v[v.Count / 2];
    }

    /// <summary>The median NVVDD over the last <paramref name="span"/> of load, when the load ended within
    /// <paramref name="within"/> (a stress run that just finished); null when the card has not been loaded since.</summary>
    public uint? LastLoadMedianUv(TimeSpan within, TimeSpan span)
    {
        lock (_gate) return LastLoadMedian([.. _recent], DateTimeOffset.Now, within, span);
    }

    public static uint? LastLoadMedian(IReadOnlyList<Tick> ticks, DateTimeOffset now, TimeSpan within, TimeSpan span)
    {
        var recent = ticks.Where(t => t.At >= now - within).ToList();
        if (recent.Count == 0) return null;
        var end = recent.Max(t => t.At);
        var v = recent.Where(t => t.At >= end - span).Select(t => t.Uv).Order().ToList();
        return v[v.Count / 2];
    }

    /// <summary>Every loaded sample between two moments, one a second, from the day files (and what is not written yet).</summary>
    public IReadOnlyList<Tick> Between(DateTimeOffset from, DateTimeOffset to) => _log.Between(from, to);

    /// <summary>The samples not yet on disk go there (also before a worker ends for a lost driver).</summary>
    public void Flush() => _log.Flush();

    /// <summary>residency\ beside the tuning log; null while the broker does not answer.</summary>
    private static string? BrokerDir(Card card)
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        return r.Ok && r.Result is { } res && res.TryGetProperty("path", out var p) && p.GetString() is { } exp
            ? Path.Combine(Path.GetDirectoryName(exp)!, "residency") : null;
    }
}

/// <summary>
/// The residency day files and the samples not on disk yet. One flush at a time in this server, and a read waits for a
/// flush in progress, so a sample goes to disk once and is read once. Another server appending to the same day file holds
/// it for a moment: a flush then fails and is tried again later, and a read takes the batch from memory instead.
/// </summary>
public sealed class ResidencyLog(Func<string?> dir)
{
    /// <summary>Two hours at a sample a second: what is kept while there is no log directory.</summary>
    public const int MaxPending = 7200;
    private readonly object _gate = new();   // _pending
    private readonly object _io = new();     // the day files: one flush or read at a time
    private readonly List<Residency.Tick> _pending = [];
    private string? _dir;

    public void Add(Residency.Tick tick)
    {
        lock (_gate)
        {
            _pending.Add(tick);
            if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
        }
    }

    /// <summary>The samples not yet on disk go there. Without <paramref name="wait"/>, nothing happens while another flush
    /// or a read holds the files (the sampler's own rounds: the batch goes out on the next one).</summary>
    public void Flush(bool wait = true)
    {
        bool taken = false;
        try
        {
            if (wait) Monitor.Enter(_io, ref taken); else Monitor.TryEnter(_io, ref taken);
            if (taken) FlushHeld();
        }
        finally { if (taken) Monitor.Exit(_io); }
    }

    /// <summary>Every sample between two moments, one a second (<see cref="Residency.OnePerSecond"/>), from the day files
    /// and what is not written yet. A day file busy with another server's append does not fail the read.</summary>
    public List<Residency.Tick> Between(DateTimeOffset from, DateTimeOffset to)
    {
        var ticks = new List<Residency.Tick>();
        lock (_io)
        {
            try { FlushHeld(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (Dir() is { } dir)
                for (var day = from.LocalDateTime.Date; day <= to.LocalDateTime.Date; day = day.AddDays(1))
                {
                    string path = Path.Combine(dir, Residency.DayFile(day));
                    if (!File.Exists(path)) continue;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var r = new StreamReader(fs);
                    while (r.ReadLine() is { } line)
                        if (Parse(line) is { } t && t.At >= from && t.At <= to) ticks.Add(t);
                }
            lock (_gate) ticks.AddRange(_pending.Where(t => t.At >= from && t.At <= to));
        }
        return Residency.OnePerSecond(ticks);
    }

    /// <summary>The flush itself, with <see cref="_io"/> held: the batch leaves memory only once it is on disk.</summary>
    private void FlushHeld()
    {
        List<Residency.Tick> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
        }
        if (Dir() is not { } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var day in batch.GroupBy(t => t.At.LocalDateTime.Date))
        {
            var sb = new StringBuilder();
            foreach (var t in day)
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{t.At:O},{t.Uv},{t.Mhz},{t.Util},{t.W:0.#}\n"));
            File.AppendAllText(Path.Combine(dir, Residency.DayFile(day.Key)), sb.ToString());
        }
        var written = batch.ToHashSet();
        lock (_gate) _pending.RemoveAll(written.Contains);
    }

    private string? Dir() => _dir ??= dir();

    private static Residency.Tick? Parse(string line)
    {
        var f = line.Split(',');
        if (f.Length < 4 || !DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
        if (!uint.TryParse(f[1], CultureInfo.InvariantCulture, out var uv) || !uint.TryParse(f[2], CultureInfo.InvariantCulture, out var mhz)
            || !uint.TryParse(f[3], CultureInfo.InvariantCulture, out var util)) return null;
        double? w = f.Length > 4 && double.TryParse(f[4], CultureInfo.InvariantCulture, out var x) ? x : null;
        return new Residency.Tick(at, uv, mhz, util, w);
    }
}
