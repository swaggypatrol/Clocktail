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
/// after the server restarts, a driver reset included.
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

    private readonly object _gate = new();
    private readonly Queue<Tick> _recent = new();
    private readonly List<Tick> _pending = [];
    private string? _dir;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int n = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                // A sampler must never take the server down: an unreadable second is just a missing sample.
                try { Take(); } catch (Exception) { }
                if (++n % 5 == 0) try { Flush(); } catch (Exception) { }
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
            _pending.Add(tick);
            if (_pending.Count > 7200) _pending.RemoveRange(0, _pending.Count - 7200);   // no log directory for two hours
        }
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

    /// <summary>Every loaded sample between two moments, from the day files (and what is not written yet).</summary>
    public IReadOnlyList<Tick> Between(DateTimeOffset from, DateTimeOffset to)
    {
        Flush();
        var ticks = new List<Tick>();
        if (Dir() is { } dir)
            for (var day = from.LocalDateTime.Date; day <= to.LocalDateTime.Date; day = day.AddDays(1))
            {
                string path = Path.Combine(dir, DayFile(day));
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(fs);
                while (r.ReadLine() is { } line)
                    if (Parse(line) is { } t && t.At >= from && t.At <= to) ticks.Add(t);
            }
        lock (_gate) ticks.AddRange(_pending.Where(t => t.At >= from && t.At <= to));
        return ticks;
    }

    /// <summary>The samples not yet on disk go there (also before a worker ends for a lost driver).</summary>
    public void Flush()
    {
        List<Tick> batch;
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
            File.AppendAllText(Path.Combine(dir, DayFile(day.Key)), sb.ToString());
        }
        lock (_gate) _pending.RemoveAll(batch.Contains);
    }

    private static Tick? Parse(string line)
    {
        var f = line.Split(',');
        if (f.Length < 4 || !DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
        if (!uint.TryParse(f[1], CultureInfo.InvariantCulture, out var uv) || !uint.TryParse(f[2], CultureInfo.InvariantCulture, out var mhz)
            || !uint.TryParse(f[3], CultureInfo.InvariantCulture, out var util)) return null;
        double? w = f.Length > 4 && double.TryParse(f[4], CultureInfo.InvariantCulture, out var x) ? x : null;
        return new Tick(at, uv, mhz, util, w);
    }

    /// <summary>residency\ beside the tuning log; null while the broker does not answer.</summary>
    private string? Dir()
    {
        if (_dir != null) return _dir;
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        if (r.Ok && r.Result is { } res && res.TryGetProperty("path", out var p) && p.GetString() is { } exp)
            _dir = Path.Combine(Path.GetDirectoryName(exp)!, "residency");
        return _dir;
    }
}
