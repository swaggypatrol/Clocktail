namespace Clocktail.Domain.Vf;

/// <summary>Where an observation of the card's operating point came from. Neither source is more reliable than the
/// other: each is a measured NVVDD voltage and a measured core clock. They differ only in how often they look and in
/// what else they carry, which is what <see cref="VfDwell"/> accounts for.</summary>
public enum VfSource : byte
{
    /// <summary>The broker's always-on V/F history (vf-history\*.vfh): every flight-recorder look (250 ms) whose NVVDD was
    /// above <see cref="VfHistoryFile.MinUvExclusive"/>, with nothing but its time, voltage and clock.</summary>
    Lightweight = 1,
    /// <summary>The MCP server's residency day files (residency\*.csv): one look a second while utilization was at least
    /// 50 % (20 % during stress_test), with utilization and board power beside the voltage and clock.</summary>
    Residency = 2,
}

/// <summary>
/// One measured operating point: when it was looked at, the NVVDD it read (µV, as measured) and the core clock it read
/// (MHz). <see cref="UtilPercent"/> and <see cref="Watts"/> are what the residency sampler also read; the lightweight
/// history does not record them, and they are null for it rather than made up.
/// </summary>
public readonly record struct VfObservation(DateTimeOffset At, uint Uv, uint Mhz, VfSource Source, uint? UtilPercent = null, double? Watts = null);

/// <summary>An observation with the wall time it stands for, in seconds (<see cref="VfDwell"/>): the span
/// [<see cref="SpanStart"/>, <see cref="SpanEnd"/>), which starts <paramref name="StartOffset"/> seconds after the look
/// (0 unless a window clipped its start, <see cref="VfTimeline.Within"/>).</summary>
public readonly record struct WeightedObservation(VfObservation Observation, double Seconds, double StartOffset = 0)
{
    public DateTimeOffset At => Observation.At;
    public uint Uv => Observation.Uv;
    public uint Mhz => Observation.Mhz;
    public double SpanStart => StartOffset;
    public double SpanEnd => StartOffset + Seconds;
}

/// <summary>
/// How much observed time an observation stands for. Every source counts by elapsed time, not by sample count, so a
/// 250 ms stream does not weigh four times a 1 s one. Each observation stands for the time until the next observation
/// of any source (so two sources over the same seconds share that time instead of adding up), as long as that next look
/// came within <see cref="CapFactor"/> of its own source's interval. When it did not (a gap: the voltage fell to or
/// under the threshold, utilization fell under the residency gate, the card slept, the broker or a server restarted,
/// the driver reset, a write failed and was dropped), it stands for one interval of its own source and the rest of the
/// gap counts as nothing: unobserved time is never credited.
/// </summary>
public static class VfDwell
{
    /// <summary>A look this much later than its source's interval still follows on from it (sampler jitter, a slow read);
    /// later than that is a gap. Below 2, so a single missed look is not filled in.</summary>
    public const double CapFactor = 1.5;

    /// <summary>The longest span any observation can stand for (the residency interval times <see cref="CapFactor"/>):
    /// how far before a window's start a look can still reach into it.</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(1 * CapFactor);

    public static TimeSpan Interval(VfSource source) => source switch
    {
        VfSource.Lightweight => TimeSpan.FromMilliseconds(250),
        _ => TimeSpan.FromSeconds(1),
    };

    /// <summary>The seconds an observation followed by a look at <paramref name="nextAt"/> (null: none) stands for.</summary>
    public static double Seconds(VfObservation o, DateTimeOffset? nextAt)
    {
        double interval = Interval(o.Source).TotalSeconds;
        if (nextAt is not { } next) return interval;
        double gap = (next - o.At).TotalSeconds;
        if (gap < 0) gap = 0;
        return gap <= interval * CapFactor ? gap : interval;
    }

    /// <summary>The observations, in time order already (<see cref="VfTimeline.Normalize"/>), with their weights; streams,
    /// holding one observation back.</summary>
    public static IEnumerable<WeightedObservation> Weigh(IEnumerable<VfObservation> ordered)
    {
        VfObservation? held = null;
        foreach (var o in ordered)
        {
            if (held is { } h) yield return new WeightedObservation(h, Seconds(h, o.At));
            held = o;
        }
        if (held is { } last) yield return new WeightedObservation(last, Seconds(last, null));
    }
}

/// <summary>
/// Observations of every source in one time order, each with the time it stands for (<see cref="VfDwell"/>). The one
/// place that merges the sources for the curve history, data_points' by_point and the editor's running haze.
/// </summary>
public sealed class VfTimeline
{
    private readonly WeightedObservation[] _items;
    private readonly long[] _ticks;

    private VfTimeline(WeightedObservation[] items)
    {
        _items = items;
        _ticks = [.. items.Select(i => i.At.UtcTicks)];
    }

    public static VfTimeline Empty { get; } = new([]);

    public IReadOnlyList<WeightedObservation> Items => _items;

    public double TotalSeconds => _items.Sum(i => i.Seconds);

    public static VfTimeline Build(IEnumerable<VfObservation> observations) =>
        new([.. VfDwell.Weigh(Normalize(observations))]);

    /// <summary>
    /// The observations in time order, each look once. Residency is kept to one sample a second (the earliest): every MCP
    /// client's server writes its own sampler's samples into the same day files, so a second can be on record twice, a few
    /// hundred milliseconds apart. The lightweight history has one writer (the broker) and keeps every look, several a
    /// second, so a transition within a second is not lost; a record a retried write put down twice is the same
    /// millisecond, and counts once.
    /// </summary>
    public static IEnumerable<VfObservation> Normalize(IEnumerable<VfObservation> observations)
    {
        var residency = new Dictionary<long, VfObservation>();
        var light = new Dictionary<long, VfObservation>();
        foreach (var o in observations)
        {
            if (o.Source == VfSource.Residency)
            {
                long s = o.At.ToUnixTimeSeconds();
                if (!residency.TryGetValue(s, out var have) || o.At < have.At) residency[s] = o;
            }
            else
            {
                long ms = o.At.ToUnixTimeMilliseconds();
                light.TryAdd(ms, o);
            }
        }
        return residency.Values.Concat(light.Values).OrderBy(o => o.At.UtcTicks).ThenBy(o => o.Source);
    }

    /// <summary>Two sources, each in time order, merged in time order as they stream (for spans too long to hold in memory);
    /// the residency side must already be one a second.</summary>
    public static IEnumerable<VfObservation> MergeOrdered(IEnumerable<VfObservation> a, IEnumerable<VfObservation> b)
    {
        using var x = a.GetEnumerator();
        using var y = b.GetEnumerator();
        bool hx = x.MoveNext(), hy = y.MoveNext();
        long lastLight = long.MinValue;
        while (hx || hy)
        {
            VfObservation next;
            if (hx && (!hy || x.Current.At.UtcTicks <= y.Current.At.UtcTicks)) { next = x.Current; hx = x.MoveNext(); }
            else { next = y.Current; hy = y.MoveNext(); }
            if (next.Source == VfSource.Lightweight)
            {
                long ms = next.At.ToUnixTimeMilliseconds();
                if (ms == lastLight) continue;
                lastLight = ms;
            }
            yield return next;
        }
    }

    /// <summary>
    /// The observed time inside the window [<paramref name="from"/>, <paramref name="to"/>): every observation whose span
    /// [At, At + Seconds) overlaps it, with its span cut to the window. A look at the window's end lends it nothing; a
    /// look just before its start counts for the part of its span inside it. The sum of the result is exactly the
    /// observed time in the window, so trials, shares, percentiles and the minimum-time gate never count time outside
    /// their own window. Observations whose cut span is empty are left out.
    /// </summary>
    public IReadOnlyList<WeightedObservation> Within(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return Array.Empty<WeightedObservation>();
        long f = from.UtcTicks, t = to.UtcTicks;
        var result = new List<WeightedObservation>();
        foreach (var w in Between(from - VfDwell.MaxSpan, to))
        {
            long at = w.At.UtcTicks;
            long spanStart = at + (long)Math.Round(w.StartOffset * TimeSpan.TicksPerSecond);
            long spanEnd = at + (long)Math.Round(w.SpanEnd * TimeSpan.TicksPerSecond);
            long s = Math.Max(spanStart, f), e = Math.Min(spanEnd, t);
            if (e <= s) continue;
            result.Add(new WeightedObservation(w.Observation, (e - s) / (double)TimeSpan.TicksPerSecond, (s - at) / (double)TimeSpan.TicksPerSecond));
        }
        return result;
    }

    /// <summary>The observations looked at from <paramref name="from"/> to <paramref name="to"/>, both included, with
    /// their whole spans (which can reach past <paramref name="to"/>): for time accounting use <see cref="Within"/>.</summary>
    public IReadOnlyList<WeightedObservation> Between(DateTimeOffset from, DateTimeOffset to)
    {
        int lo = Array.BinarySearch(_ticks, from.UtcTicks);
        if (lo < 0) lo = ~lo;
        else while (lo > 0 && _ticks[lo - 1] == from.UtcTicks) lo--;
        int hi = Array.BinarySearch(_ticks, to.UtcTicks);
        if (hi < 0) hi = ~hi;
        else { while (hi + 1 < _ticks.Length && _ticks[hi + 1] == to.UtcTicks) hi++; hi++; }
        if (hi <= lo) return Array.Empty<WeightedObservation>();
        return new ArraySegment<WeightedObservation>(_items, lo, hi - lo);
    }

    /// <summary>The time-weighted 90th percentile of the clocks: the lowest clock at or under which 90 % of the observed
    /// time ran. With equal weights (one source at its own pace), exactly the sample-count P90 the history used before.</summary>
    public static uint P90Mhz(IReadOnlyList<WeightedObservation> obs)
    {
        if (obs.Count == 0) throw new ArgumentException("no observations", nameof(obs));
        var sorted = obs.OrderBy(o => o.Mhz).ToList();
        int n = sorted.Count;
        if (n == 1) return sorted[0].Mhz;
        double w0 = sorted[0].Seconds;
        if (sorted.All(o => Math.Abs(o.Seconds - w0) < 1e-9))
            return sorted[(int)Math.Round(0.9 * (n - 1))].Mhz;
        double total = sorted.Sum(o => o.Seconds), cum = 0;
        foreach (var o in sorted)
        {
            cum += o.Seconds;
            if (cum >= 0.9 * total - 1e-9) return o.Mhz;
        }
        return sorted[^1].Mhz;
    }
}
