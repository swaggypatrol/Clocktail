using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Platform;

namespace Clocktail.Domain.Profiles;

/// <summary>
/// One look at the GPU: when it was taken, how long the reads took, and what they said. Any field the driver did not
/// answer is null. <see cref="ReadMs"/> matters: a hung GPU makes the reads block, so a slow look is the hang itself.
/// </summary>
public sealed record FlightSample(
    DateTimeOffset At,
    int ReadMs,
    uint? ClockMhz,
    uint? CoreMv,
    double? Watts,
    double? TempC,
    uint? UtilPercent,
    ulong? LimitReasons);

/// <summary>What the recorder says the GPU was doing when it hung, and why that sample.</summary>
public sealed record CrashPoint(FlightSample Sample, double SecondsBeforeReset, string Why)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Sample.ClockMhz?.ToString() ?? "?"} MHz at {Sample.CoreMv?.ToString() ?? "?"} mV, {Sample.Watts:0} W, {Sample.TempC:0} C, util {Sample.UtilPercent}% ({SecondsBeforeReset:0.0} s before the reset was seen)");
}

/// <summary>
/// The last <see cref="Keep"/> of GPU looks, in memory only (the user, 2026-10-18: keep a short window and drop the rest,
/// so the disk never fills; 60 s since a test hang at 17:59Z sat frozen ~30 s before Windows logged the reset). Nothing is written until a driver reset, when the window is frozen and saved as one trace, and
/// the sample just before the hang becomes the crash point: one sample of where the curve meets the edge.
/// </summary>
public sealed class FlightRecorder(TimeSpan keep)
{
    public static readonly TimeSpan DefaultKeep = TimeSpan.FromSeconds(60);
    /// <summary>Readings that stop moving for this long right up to the reset are the hung GPU repeating its last answer.</summary>
    public static readonly TimeSpan FrozenAtLeast = TimeSpan.FromSeconds(3);
    /// <summary>A frozen voltage read still flickers by one 5 mV step (995/1000 mV on 2026-10-18).</summary>
    public const int FrozenMvSlack = 5;
    /// <summary>The driver declares a hang after TdrDelay (2 s by default) and logs the reset only after recovering, so
    /// everything later than this before the event may already be the hang.</summary>
    public static readonly TimeSpan HangDetectAtLeast = TimeSpan.FromSeconds(2);
    /// <summary>A look this slow is the GPU no longer answering.</summary>
    public const int BlockedMs = 1000;
    /// <summary>Saved traces kept on disk; older ones are deleted (each is a few KB).</summary>
    public const int TracesKept = 30;

    public TimeSpan Keep { get; } = keep;
    private readonly Queue<FlightSample> _ring = new();
    private readonly object _gate = new();

    public void Add(FlightSample s)
    {
        lock (_gate)
        {
            _ring.Enqueue(s);
            while (_ring.Count > 0 && s.At - _ring.Peek().At > Keep) _ring.Dequeue();
        }
    }

    public IReadOnlyList<FlightSample> Freeze()
    {
        lock (_gate) return [.. _ring];
    }

    /// <summary>
    /// The last sample of the GPU working hard before the hang: under load (utilization ≥ 60 %, a core voltage read,
    /// power at least 60 % of the window's loaded median), at least <see cref="HangDetectAtLeast"/> before the reset was
    /// seen, and before the first blocked look. After a hang the readings lie (the wall lets go: 3150 MHz at 1035 mV
    /// with no load behind it on 2026-10-18), which is what the power and timing tests keep out. Null when the window
    /// holds no loaded sample (the reset came at idle, or the recorder had just started).
    /// </summary>
    public static CrashPoint? Pick(IReadOnlyList<FlightSample> window, DateTimeOffset resetSeenAt)
    {
        var frozen = FrozenFrom(window);
        var live = frozen == null ? window : window.Where(s => s.At < frozen.At).ToList();
        var loaded = live.Where(Loaded).ToList();
        if (loaded.Count == 0) return null;
        var watts = loaded.Select(s => s.Watts!.Value).Order().ToList();
        double floor = 0.6 * watts[watts.Count / 2];
        var blocked = window.FirstOrDefault(s => s.ReadMs >= BlockedMs);
        var before = resetSeenAt - HangDetectAtLeast;
        int at = loaded.FindLastIndex(s => s.Watts >= floor && s.At <= before && (blocked == null || s.At < blocked.At));
        if (at < 0) return null;
        // The sample of the wall letting go is not the failure point: on 2026-10-18 17:59Z the last loaded sample was
        // 2895 MHz at 910 mV, 598.6 W, then 2947 MHz at 970 mV, 514 W (power down 14 %, clock and voltage up as the
        // limit stopped holding), then the frozen tail. That middle sample passed the 60 % floor and was picked.
        while (at > 0 && LettingGo(loaded[at], loaded[at - 1])) at--;
        var pick = loaded[at];
        string why = frozen != null ? $"last loaded sample before the readings froze at {frozen.At:HH:mm:ss.f}"
            : blocked != null ? $"last loaded sample before the reads blocked at {blocked.At:HH:mm:ss.f}"
            : $"last loaded sample {HangDetectAtLeast.TotalSeconds:0} s or more before the reset";
        return new CrashPoint(pick, (resetSeenAt - pick.At).TotalSeconds, why);
    }

    /// <summary>
    /// Why the GPU looks hung right now, or null: the window ends in a frozen tail that power fell into (a steady pinned
    /// load that fills the window is not one), or a read in the last 3 s blocked for a second or more. The driver keeps
    /// answering for the whole TdrDelay of a hang (30 s on the reference machine), and a trial written then took the
    /// blame for the hang before it (2026-10-19, corrected by hand in the log).
    /// </summary>
    public static string? HungNow(IReadOnlyList<FlightSample> window, DateTimeOffset now)
    {
        if (window.Count == 0 || now - window[^1].At > TimeSpan.FromSeconds(3)) return null;
        if (window.LastOrDefault(s => s.ReadMs >= BlockedMs && now - s.At <= TimeSpan.FromSeconds(3)) is { } blocked)
            return string.Create(CultureInfo.InvariantCulture, $"a read of the GPU blocked for {blocked.ReadMs} ms at {blocked.At:HH:mm:ss}");
        return FrozenFrom(window) is { } frozen && frozen.At != window[0].At
            ? string.Create(CultureInfo.InvariantCulture, $"its readings have been frozen at {frozen.ClockMhz} MHz / {frozen.CoreMv} mV since {frozen.At:HH:mm:ss}")
            : null;
    }

    /// <summary>A sample of the GPU under load: 60 % utilization, or (a light-load stress run, a 250 Hz square wave NVML
    /// reads as 30-57 %) 25 % with at least 150 W behind it; an idle card draws well under that.</summary>
    public static bool Loaded(FlightSample s) =>
        s.CoreMv is > 0 && s.Watts is double w && w > 0 && (s.UtilPercent >= 60 || s.UtilPercent >= 25 && w >= 150);

    /// <summary>A sample of the limit letting go, against the one before it: power down at least 10 % while the clock
    /// rose 15 MHz or more, or NVVDD 10 mV or more (a GPU doing less work at a higher point: the hang beginning).</summary>
    public static bool LettingGo(FlightSample s, FlightSample before) =>
        s.Watts is double w && before.Watts is double b && w <= 0.9 * b
        && (s.ClockMhz >= before.ClockMhz + 15 || s.CoreMv >= before.CoreMv + 10);

    /// <summary>
    /// The first sample of a frozen tail, or null. A hung GPU can keep answering without blocking: on 2026-10-18 17:59Z it
    /// repeated 3180 MHz at 995–1000 mV, util 100 %, ~125 W for ~30 s until the reset (the wall had let go, so the clock
    /// sat at the curve's top). A tail counts as frozen when, up to the last sample, the clock is unchanged, the voltage
    /// stays within <see cref="FrozenMvSlack"/>, utilization stays ≥ 60 %, it lasts <see cref="FrozenAtLeast"/>, and either
    /// it fills the window or power fell into it (the sample before drew at least 1.5× the tail's highest power). A steady
    /// load pinned at the curve's cap does not drop power, so it is not mistaken for a hang.
    /// </summary>
    public static FlightSample? FrozenFrom(IReadOnlyList<FlightSample> window)
    {
        if (window.Count == 0) return null;
        var last = window[^1];
        if (last.ClockMhz is null || last.CoreMv is not uint lastMv || !(last.UtilPercent >= 60)) return null;
        int i = window.Count - 1;
        while (i > 0)
        {
            var p = window[i - 1];
            if (p.ClockMhz == last.ClockMhz && p.CoreMv is uint mv && Math.Abs((int)mv - (int)lastMv) <= FrozenMvSlack && p.UtilPercent >= 60) i--;
            else break;
        }
        var first = window[i];
        if (last.At - first.At < FrozenAtLeast) return null;
        if (i == 0) return first;
        double tailMax = window.Skip(i).Max(s => s.Watts ?? 0);
        return window[i - 1].Watts is double before && before >= 1.5 * tailMax ? first : null;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The frozen window and its crash point as crash-traces/trace-UTC.json beside the journal; keeps the newest <see cref="TracesKept"/>.</summary>
    public static string Save(string journalPath, DateTimeOffset at, string evidence, string driver, CrashPoint? point, IReadOnlyList<FlightSample> window)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "crash-traces");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"trace-{at.UtcDateTime:yyyyMMdd-HHmmss}.json"));
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new { at, evidence, driver, crash_point = point, samples = window }, Json));
        foreach (var old in Directory.GetFiles(dir, "trace-*.json").Order(StringComparer.Ordinal).SkipLast(TracesKept))
            try { File.Delete(old); } catch (IOException) { }
        return path;
    }
}
