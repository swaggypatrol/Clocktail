using System.Globalization;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;

namespace Clocktail.Broker;

/// <summary>How the always-on V/F history is doing, for ping, experiment "show" and the log.</summary>
public sealed record VfHistoryStatus(
    bool Degraded,
    string? Error,
    DateTimeOffset? DegradedSince,
    long Written,
    long Dropped,
    long Withheld,
    int Queued,
    int Held,
    DateTimeOffset? LastWrite,
    uint MinUvExclusive,
    IReadOnlyDictionary<string, string> Dirs)
{
    /// <summary>The broker's answers name their fields in snake_case by hand (BrokerResponse.Json has no naming policy).</summary>
    public object Wire() => new
    {
        degraded = Degraded, error = Error, degraded_since = DegradedSince, written = Written, dropped = Dropped,
        withheld = Withheld, queued = Queued, held = Held, last_write = LastWrite, min_uv_exclusive = MinUvExclusive, dirs = Dirs,
    };
}

/// <summary>
/// The always-on V/F history (the user, 2026-11-02): for as long as the broker runs, every flight-recorder look whose
/// measured NVVDD is above 825 mV goes to vf-history\ beside the GPU's journal as its time, its measured voltage (µV) and
/// its measured core clock: no session, client or load needed. The look is the <see cref="FlightSampler"/>'s own: nothing
/// more is read from the GPU.
/// <para>
/// A look is held in memory for <see cref="Quarantine"/> (the flight recorder's own window) before it may be written:
/// a hung GPU keeps answering with frozen readings until the driver resets it, and those looks must not become
/// history of healthy operation. While the window says the GPU looks hung (<see cref="FlightRecorder.HungNow"/>), and when
/// a driver reset is seen (<see cref="Reset"/>, the same hang boundary the crash trace uses), the held looks from the
/// hang on are withheld. A hang once seen stays latched for its GPU until the readings move again or a reset ends it:
/// a frozen stretch longer than the window fills it and would otherwise read as a steady load. A reset's span is kept
/// too, so a look already being read when the reset was seen, and handed over after it, is withheld as well. A look the
/// broker never got to write (the process killed, the driver lost) is simply not there.
/// </para>
/// <para>
/// The sampler only hands a look over (<see cref="Look"/>: a lock and a queue, no I/O, never throws to it). Writing is
/// <see cref="RunAsync"/>'s, in batches every <see cref="FlushEvery"/>; a failure (disk full, access denied, a lock held)
/// keeps the batch, at most <see cref="MaxQueuedPerGpu"/> looks per GPU (the oldest go first, counted in
/// <see cref="VfHistoryStatus.Dropped"/>), retries with a growing pause, and logs once when it starts failing and once
/// when it writes again. Dropped looks are a gap: nothing is credited for them.
/// </para>
/// </summary>
public sealed class VfHistoryRecorder
{
    public static readonly TimeSpan Quarantine = FlightRecorder.DefaultKeep;
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    /// <summary>Two hours of looks at four a second.</summary>
    public const int MaxQueuedPerGpu = 4 * 3600 * 2;

    private sealed class Lane(string dir)
    {
        public readonly string Dir = dir;
        public readonly Queue<VfObservation> Held = new();
        public readonly LinkedList<VfObservation> Ready = new();
        /// <summary>Where the hang the window last showed began, kept until the readings move again (or a reset ends it):
        /// a frozen stretch longer than the window fills it and no longer reads as a hang there.</summary>
        public DateTimeOffset? HungFrom;
        /// <summary>The spans resets withheld, for a look already being read when the reset was seen that arrives after it.</summary>
        public readonly List<(DateTimeOffset From, DateTimeOffset To)> Excluded = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Lane> _lanes;
    private readonly Action<string> _log;
    private readonly Action<string, IReadOnlyList<VfObservation>> _append;
    private readonly SemaphoreSlim _io = new(1, 1);
    private long _written, _dropped, _withheld;
    private bool _degraded, _droppingLogged, _shutDown;
    private string? _error;
    private DateTimeOffset? _degradedSince, _lastWrite;
    private int _failures;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    /// <param name="dirs">Each GPU's vf-history directory, by stable id.</param>
    /// <param name="append">The writer (tests give their own); <see cref="VfHistoryStore.Append"/> by default.</param>
    public VfHistoryRecorder(IReadOnlyDictionary<string, string> dirs, Action<string> log,
        Action<string, IReadOnlyList<VfObservation>>? append = null)
    {
        _lanes = dirs.ToDictionary(d => d.Key, d => new Lane(d.Value), StringComparer.OrdinalIgnoreCase);
        _log = log;
        _append = append ?? VfHistoryStore.Append;
    }

    /// <summary>
    /// One flight-recorder look: its time, NVVDD as the rail reader returned it (µV), the core clock NVML read, how long
    /// the look took, and the flight window with it already added. Kept (held) only when <see cref="VfHistoryFile.Admits"/>
    /// it and the read did not block. Never throws.
    /// </summary>
    public void Look(string id, DateTimeOffset at, uint? uv, uint? mhz, int readMs, IReadOnlyList<FlightSample>? window)
    {
        try
        {
            DateTimeOffset? suspect = window == null ? null : SuspectFrom(window, at);
            bool? stuck = window == null ? null : StillStuck(window, at);
            lock (_gate)
            {
                if (_shutDown || !_lanes.TryGetValue(id, out var lane)) return;
                lane.Excluded.RemoveAll(x => x.To < at - 2 * Quarantine);
                if (suspect is { } from)
                {
                    if (lane.HungFrom is not { } h || from < h) lane.HungFrom = from;
                    WithholdLocked(lane, from, at);
                }
                else if (lane.HungFrom != null && stuck == false) lane.HungFrom = null;   // the readings move again
                if (readMs < FlightRecorder.BlockedMs && VfHistoryFile.Admits(uv, mhz))
                {
                    if (lane.HungFrom != null || lane.Excluded.Any(x => at >= x.From && at <= x.To)) _withheld++;
                    else lane.Held.Enqueue(new VfObservation(at, uv!.Value, mhz!.Value, VfSource.Lightweight));
                }
                ReleaseLocked(lane, at - Quarantine);
            }
        }
        catch (Exception) { }   // the flight recorder is what matters here: a look not kept is a gap
    }

    /// <summary>A driver reset was seen: the looks of the hang it ends are withheld, from where the GPU stopped answering
    /// (<see cref="FlightRecorder.Hang"/>), or <see cref="FlightRecorder.HangDetectAtLeast"/> before the reset when the
    /// window shows no hang, as the crash trace takes it.</summary>
    public void Reset(string id, IReadOnlyList<FlightSample> window, DateTimeOffset seen)
    {
        try
        {
            var from = FlightRecorder.Hang(window, seen)?.Stopped ?? seen - FlightRecorder.HangDetectAtLeast;
            lock (_gate)
            {
                if (!_lanes.TryGetValue(id, out var lane)) return;
                if (lane.HungFrom is { } h && h < from) from = h;   // a hang longer than the window began before it
                lane.HungFrom = null;                                // the reset ends it; what follows is the recovered driver's
                lane.Excluded.Add((from, seen));
                WithholdLocked(lane, from, seen);
            }
        }
        catch (Exception) { }
    }

    /// <summary>When the window says the GPU looks hung now, where that began: the first look of the frozen tail, or the
    /// blocked read; null when it does not.</summary>
    public static DateTimeOffset? SuspectFrom(IReadOnlyList<FlightSample> window, DateTimeOffset now)
    {
        if (FlightRecorder.HungNow(window, now) == null) return null;
        var blocked = window.FirstOrDefault(s => s.ReadMs >= FlightRecorder.BlockedMs && now - s.At <= TimeSpan.FromSeconds(3));
        var frozen = FlightRecorder.FrozenFrom(window);
        DateTimeOffset? from = frozen != null && frozen.At != window[0].At ? frozen.At : null;
        if (blocked != null && (from == null || blocked.At < from)) from = blocked.At;
        return from ?? now;
    }

    /// <summary>Whether the window still ends stuck: a frozen tail (<see cref="FlightRecorder.FrozenFrom"/>, also one that
    /// fills the window), or a blocked read in the last 3 s. A latched hang (<c>HungFrom</c>) holds until this is false.</summary>
    public static bool StillStuck(IReadOnlyList<FlightSample> window, DateTimeOffset now) =>
        FlightRecorder.FrozenFrom(window) != null
        || window.Any(s => s.ReadMs >= FlightRecorder.BlockedMs && now - s.At <= TimeSpan.FromSeconds(3));

    private void WithholdLocked(Lane lane, DateTimeOffset from, DateTimeOffset to)
    {
        int before = lane.Held.Count;
        if (before > 0 && lane.Held.Any(o => o.At >= from && o.At <= to))
        {
            var keep = lane.Held.Where(o => o.At < from || o.At > to).ToList();
            lane.Held.Clear();
            foreach (var o in keep) lane.Held.Enqueue(o);
            _withheld += before - keep.Count;
        }
        // Looks already past the quarantine but not written yet: a hang longer than the window.
        for (var n = lane.Ready.Last; n != null && n.Value.At >= from;)
        {
            var prev = n.Previous;
            if (n.Value.At <= to) { lane.Ready.Remove(n); _withheld++; }
            n = prev;
        }
    }

    private void ReleaseLocked(Lane lane, DateTimeOffset upTo)
    {
        while (lane.Held.Count > 0 && lane.Held.Peek().At <= upTo) Enqueue(lane, lane.Held.Dequeue());
    }

    private void Enqueue(Lane lane, VfObservation o)
    {
        lane.Ready.AddLast(o);
        while (lane.Ready.Count > MaxQueuedPerGpu)
        {
            lane.Ready.RemoveFirst();
            _dropped++;
            if (!_droppingLogged)
            {
                _droppingLogged = true;
                _log($"vf history: {MaxQueuedPerGpu} observations are waiting to be written; the oldest are being dropped (a gap, not counted as observed time)");
            }
        }
    }

    /// <summary>Writes what is ready, one GPU's batch at a time; false when a write failed (the batch stays queued). Skips
    /// the attempt while backing off after a failure unless <paramref name="force"/>.</summary>
    public bool FlushOnce(DateTimeOffset now, bool force = false)
    {
        // A flush in progress: this round is its (the last one, at shutdown, waits for it a moment).
        if (!_io.Wait(force ? TimeSpan.FromSeconds(2) : TimeSpan.Zero)) return true;
        try
        {
            if (!force && now < _nextAttempt) return false;
            bool ok = true;
            foreach (var (id, lane) in _lanes)
            {
                List<VfObservation> batch;
                lock (_gate)
                {
                    if (lane.Ready.Count == 0) continue;
                    batch = [.. lane.Ready];
                    lane.Ready.Clear();
                }
                try
                {
                    _append(lane.Dir, batch);
                    lock (_gate) { _written += batch.Count; _lastWrite = now; }
                }
                catch (Exception e)
                {
                    ok = false;
                    lock (_gate)
                    {
                        for (int i = batch.Count - 1; i >= 0; i--) lane.Ready.AddFirst(batch[i]);
                        while (lane.Ready.Count > MaxQueuedPerGpu) { lane.Ready.RemoveFirst(); _dropped++; }
                    }
                    Failed(id, lane.Dir, e, now);
                }
            }
            if (ok) Recovered(now);
            return ok;
        }
        finally { _io.Release(); }
    }

    private void Failed(string id, string dir, Exception e, DateTimeOffset now)
    {
        bool first;
        lock (_gate)
        {
            first = !_degraded;
            _degraded = true;
            _degradedSince ??= now;
            _error = $"{e.GetType().Name}: {e.Message}";
            _failures++;
            var pause = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FlushEvery.Ticks << Math.Min(_failures, 10)));
            _nextAttempt = now + pause;
        }
        if (first) _log($"vf history: gpu {id}: writing to {dir} failed ({e.GetType().Name}: {e.Message}); keeping up to {MaxQueuedPerGpu} observations in memory and trying again");
    }

    private void Recovered(DateTimeOffset now)
    {
        string? line = null;
        lock (_gate)
        {
            if (_degraded)
                line = string.Create(CultureInfo.InvariantCulture,
                    $"vf history: writing again after {(now - _degradedSince!.Value).TotalSeconds:0} s; {_dropped} observations dropped in all so far");
            _degraded = false;
            _degradedSince = null;
            _error = null;
            _failures = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            _droppingLogged = false;
        }
        if (line != null) _log(line);
    }

    /// <summary>Writes every <see cref="FlushEvery"/> until cancelled. A failure is the recorder's state, not this loop's.</summary>
    public async Task RunAsync(Func<DateTimeOffset> now, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(FlushEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            FlushOnce(now());
        }
    }

    /// <summary>
    /// The broker is stopping: held looks go out up to where the window raises a doubt (a hang now or ending at a reset),
    /// and otherwise up to <see cref="FlightRecorder.HangDetectAtLeast"/> ago (a hang the driver has not declared yet looks
    /// like anything); later ones are withheld. One last write is tried; then nothing more is taken.
    /// </summary>
    public void Shutdown(DateTimeOffset now, Func<string, IReadOnlyList<FlightSample>?> windows)
    {
        lock (_gate)
        {
            if (_shutDown) return;
            _shutDown = true;
            foreach (var (id, lane) in _lanes)
            {
                DateTimeOffset cut = now - FlightRecorder.HangDetectAtLeast;
                try
                {
                    if (windows(id) is { Count: > 0 } w)
                    {
                        var doubt = SuspectFrom(w, now) ?? FlightRecorder.Hang(w, now)?.Stopped;
                        if (doubt is { } d && d < cut) cut = d;
                    }
                }
                catch (Exception) { }
                if (lane.HungFrom is { } hung && hung < cut) cut = hung;
                while (lane.Held.Count > 0)
                {
                    var o = lane.Held.Dequeue();
                    if (o.At < cut) Enqueue(lane, o); else _withheld++;
                }
            }
        }
        FlushOnce(now, force: true);
    }

    public VfHistoryStatus Status
    {
        get
        {
            lock (_gate)
                return new VfHistoryStatus(_degraded, _error, _degradedSince, _written, _dropped, _withheld,
                    _lanes.Values.Sum(l => l.Ready.Count), _lanes.Values.Sum(l => l.Held.Count), _lastWrite,
                    VfHistoryFile.MinUvExclusive, _lanes.ToDictionary(l => l.Key, l => l.Value.Dir, StringComparer.OrdinalIgnoreCase));
        }
    }
}
