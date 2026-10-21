using System.Diagnostics;
using System.Globalization;
using System.Text;
using Clocktail.Core.Gpu;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Rail = Clocktail.Domain.Rails.Rail;

namespace Clocktail.Mcp;

/// <summary>
/// The live watch, for a whole session of play: the card sampled several times a second in the background, one line
/// every few seconds under load, and a line the moment something happens (load starts or stops, the card stops
/// answering, it runs hot). This is how the reference card's games and benchmarks were watched: an nvidia-smi log at
/// 200 ms, a line every 10 s, followed live while the person played. The lines go to a log beside the tuning log as
/// they come, so an agent can follow it live (Claude Code's Monitor on tail -F) or read it in batches (live_read);
/// every sample goes to a CSV beside it, for the seconds before a crash. One watch at a time; it ends on live_stop,
/// after its time cap, or with the server.
/// </summary>
public sealed class LiveWatch(Card card) : IDisposable
{
    /// <summary>Loaded = this busy and drawing this much (watch's rule): menus and loading screens say nothing about a curve.</summary>
    public const int LoadedUtilPct = 50, LoadedW = 120;
    /// <summary>Not loaded for this long ends a stretch of load; a shorter dip (a loading screen, a menu) does not.</summary>
    public static readonly TimeSpan LoadGap = TimeSpan.FromSeconds(3);
    /// <summary>No answer from the card for this long is reported: a hang, or a driver reset under way.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(3);
    /// <summary>A V/F hang as the reference card showed it: the clock frozen high at full utilization while the power falls
    /// (3105-3180 MHz at 100 %, 125-200 W) for the ~30 s before the driver resets. Readings like that for this long are reported.</summary>
    public const int HangUtilPct = 95, HangMaxW = 250, HangMinMhz = 2000;
    /// <summary>Hot from here (NVIDIA's own slowdown starts near 90 °C); reported again only after it cools under the second.</summary>
    public const double HotC = 85, CoolAgainC = 82;
    /// <summary>The longest watch (CLOCKTAIL_MCP_MAX_LIVE_H): a forgotten one ends on its own.</summary>
    public static readonly int MaxHours = Env("CLOCKTAIL_MCP_MAX_LIVE_H", 6);
    public const int MinIntervalMs = 100, MaxIntervalMs = 2000, MinEverySeconds = 2, MaxEverySeconds = 120, MaxTailSeconds = 30;
    private static readonly TimeSpan KeepRecent = TimeSpan.FromSeconds(MaxTailSeconds);

    /// <summary>One sample: what the card ran, and the curve point its voltage sits on (the curve read every window).</summary>
    public sealed record Point(DateTimeOffset At, uint? ClockMhz, double? NvvddMv, double? MsvddMv, double? XbarMhz, double? BoardW,
        double? LimitW, double? TempC, uint? UtilPct, double? FanPct, string HeldBy, int? CurvePoint, bool Loaded)
    {
        public bool OnWall => BoardW is { } w && LimitW is { } l && w >= l - Limiter.WallBandW;
    }

    private sealed class Run
    {
        public required DateTimeOffset Started;
        public required int IntervalMs, EverySeconds;
        public required string? Note;
        public required string LogPath, CsvPath;
        public required StreamWriter Log, Csv;
        public readonly CancellationTokenSource Stop = new();
        public Task? Loop;
        public readonly List<string> Lines = [];
        public int ReadUpTo;
        public readonly List<Point> Loaded = [];
        public readonly Queue<Point> Recent = new();
        public int Samples, Stretches, Silences;
        /// <summary>When the card last answered, and since when it has not (set by the watchdog, cleared by the sampler).</summary>
        public DateTimeOffset LastAnswer;
        public DateTimeOffset? SilentSince;
        public System.Threading.Timer? Watchdog;
        public DateTimeOffset? Ended;
        public string? EndReason;
        public string? Summary;
        /// <summary>The game's frame rate (the broker's capture); null when it could not start.</summary>
        public FrameFeed? Frames;
    }

    private readonly object _gate = new();
    private Run? _run;

    public bool Running { get { lock (_gate) return _run is { Ended: null }; } }

    /// <summary>The longest frame-rate capture the service runs.</summary>
    public const int MaxFrameSeconds = 4 * 3600;

    public object Start(int intervalMs, int everySeconds, string? note, string? process = null)
    {
        if (intervalMs < MinIntervalMs || intervalMs > MaxIntervalMs) throw new ArgumentException($"intervalMs is {MinIntervalMs}..{MaxIntervalMs}");
        if (everySeconds < MinEverySeconds || everySeconds > MaxEverySeconds) throw new ArgumentException($"everySeconds is {MinEverySeconds}..{MaxEverySeconds}");
        lock (_gate)
        {
            if (_run is { Ended: null } r)
                throw new InvalidOperationException($"a live watch is already running since {r.Started:HH:mm:ss} ({r.LogPath}): live_read reads it, live_stop ends it");
            var dir = Dir();
            Directory.CreateDirectory(dir);
            var now = DateTimeOffset.Now;
            string stem = Path.Combine(dir, $"live-{now:yyyyMMdd-HHmmss}");
            var run = new Run
            {
                Started = now, IntervalMs = intervalMs, EverySeconds = everySeconds, Note = note,
                LogPath = stem + ".log", CsvPath = stem + ".csv", Log = Writer(stem + ".log"), Csv = Writer(stem + ".csv"),
            };
            run.Csv.WriteLine("time,clock_mhz,nvvdd_mv,msvdd_mv,xbar_mhz,board_w,limit_w,temp_c,util_pct,fan_pct,curve_point,held_by,limit_reasons,instant_w,vram_used_gib,temp_margin_c,gpc_mean_mv,gpc_min_mv");
            Emit(run, string.Create(CultureInfo.InvariantCulture, $"🟢 live watch started: a line every {everySeconds} s under load, sampled every {intervalMs} ms")
                + (note != null ? $". Playing: {note}" : ""));
            run.Frames = FrameFeed.Start(card, Math.Min(MaxHours * 3600, MaxFrameSeconds), process, out var noFrames);
            Emit(run, run.Frames != null ? "🎞️ frame rates on: each line under load ends with the game's fps" : $"ℹ️ no frame rates this time: {noFrames}");
            run.LastAnswer = now;
            run.Watchdog = new System.Threading.Timer(_ => Watch(run), null, 500, 500);
            run.Loop = Task.Factory.StartNew(() => Sample(run), TaskCreationOptions.LongRunning);
            _run = run;
            return new
            {
                success = true,
                log = run.LogPath,
                csv = run.CsvPath,
                interval_ms = intervalMs,
                every_s = everySeconds,
                ends_after_h = MaxHours,
                follow = $"Claude Code: run the Monitor tool on  tail -n +1 -F \"{run.LogPath.Replace('\\', '/')}\"  to see each line as it is written. " +
                         "Any agent: call live_read whenever the person talks to you; it returns the lines since the last read and the run so far.",
            };
        }
    }

    public object Read(int max, int tailSeconds)
    {
        if (max < 1) throw new ArgumentException("max is at least 1");
        if (tailSeconds < 0 || tailSeconds > MaxTailSeconds) throw new ArgumentException($"tailSeconds is 0..{MaxTailSeconds}");
        Run run;
        lock (_gate) run = _run ?? throw new InvalidOperationException("no live watch yet: live_start starts one");
        List<string> fresh;
        int skipped;
        List<string>? tail = null;
        lock (run)
        {
            fresh = run.Lines.Skip(run.ReadUpTo).ToList();
            run.ReadUpTo = run.Lines.Count;
            skipped = Math.Max(0, fresh.Count - max);
            if (skipped > 0) fresh = fresh.Skip(skipped).ToList();
            if (tailSeconds > 0)
            {
                var since = (run.Ended ?? DateTimeOffset.Now) - TimeSpan.FromSeconds(tailSeconds);
                tail = run.Recent.Where(p => p.At >= since).Select(Row).ToList();
            }
        }
        return new
        {
            running = run.Ended == null,
            ended = run.EndReason,
            lines = fresh,
            skipped = skipped > 0 ? $"{skipped} older line(s) left out; all are in {run.LogPath}" : null,
            last_seconds = tail,
            so_far = Totals(run),
            log = run.LogPath,
            csv = run.CsvPath,
        };
    }

    public object Stop()
    {
        Run run;
        lock (_gate) run = _run ?? throw new InvalidOperationException("no live watch yet: live_start starts one");
        if (run.Ended == null)
        {
            run.EndReason ??= "live_stop";
            run.Stop.Cancel();
            run.Loop?.Wait(TimeSpan.FromSeconds(10));
        }
        List<string> fresh;
        lock (run) { fresh = run.Lines.Skip(run.ReadUpTo).ToList(); run.ReadUpTo = run.Lines.Count; }
        return new { success = true, ended = run.EndReason, summary = run.Summary, lines = fresh.TakeLast(20), totals = Totals(run), log = run.LogPath, csv = run.CsvPath };
    }

    public void Dispose()
    {
        Run? run;
        lock (_gate) run = _run;
        if (run is not { Ended: null }) return;
        run.EndReason ??= "the server stopped";
        run.Stop.Cancel();
        run.Loop?.Wait(TimeSpan.FromSeconds(5));
    }

    private void Sample(Run run)
    {
        var token = run.Stop.Token;
        var clock = Stopwatch.StartNew();
        var every = TimeSpan.FromSeconds(run.EverySeconds);
        var cap = run.Started.AddHours(MaxHours);
        VfCurveSnapshot? curve = null;
        uint? maxUv = null;
        var shapeAt = DateTimeOffset.MinValue;
        bool inLoad = false, hot = false, hangReported = false;
        DateTimeOffset? notLoadedSince = null, hangSince = null;
        uint? hangMhz = null;
        DateTimeOffset loadSince = default, windowStart = default;
        var window = new List<Point>();
        long next = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now;
                if (now >= cap) { run.EndReason = $"{MaxHours} h, the cap (CLOCKTAIL_MCP_MAX_LIVE_H)"; break; }
                if (now - shapeAt >= every)
                {
                    // The curve's effective clocks move with temperature, and a trial may change it mid-watch: read it once a window.
                    try { curve = card.WithDriver(VfReader.Read); maxUv = card.WithDriver(RailsReader.Read).Get(Rail.Nvvdd)?.Limits.MaxUv; }
                    catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
                    shapeAt = now;
                }
                Card.Sample? s = null;
                try { s = card.ReadSample(); } catch (Exception) { }   // an unreadable moment is what the watchdog is for
                if (s?.Telemetry is { } t)
                {
                    var answered = DateTimeOffset.Now;
                    DateTimeOffset? silent;
                    lock (run) { silent = run.SilentSince; run.SilentSince = null; run.LastAnswer = answered; }
                    if (silent is { } since) Emit(run, string.Create(CultureInfo.InvariantCulture, $"🔁 the card answers again after {(answered - since).TotalSeconds:0} s"));
                    var p = ToPoint(s, t, curve, maxUv);
                    bool hangLike = p is { UtilPct: >= HangUtilPct, BoardW: < HangMaxW, ClockMhz: >= HangMinMhz } && (hangMhz == null || hangMhz == p.ClockMhz);
                    if (hangLike)
                    {
                        hangSince ??= now;
                        hangMhz = p.ClockMhz;
                        if (!hangReported && now - hangSince >= SilentAfter)
                        {
                            hangReported = true;
                            lock (run) run.Silences++;
                            Emit(run, string.Create(CultureInfo.InvariantCulture,
                                $"⛔ the clock is frozen at {p.ClockMhz} MHz / {p.NvvddMv:0} mV at full utilization with only {p.BoardW:0} W: the card has probably hung (a V/F hang looks like this for ~30 s before the driver resets). The last full-load sample before it, in the CSV, is the failure point"));
                        }
                    }
                    else
                    {
                        if (hangReported) Emit(run, "🔁 the readings look like work again");
                        hangSince = null;
                        hangMhz = null;
                        hangReported = false;
                    }
                    WriteCsv(run, p, t, s.Gpc);
                    lock (run)
                    {
                        run.Samples++;
                        run.Recent.Enqueue(p);
                        while (run.Recent.Count > 0 && p.At - run.Recent.Peek().At > KeepRecent) run.Recent.Dequeue();
                    }
                    if (p.TempC >= HotC && !hot)
                    {
                        hot = true;
                        Emit(run, string.Create(CultureInfo.InvariantCulture, $"🌡️ {p.TempC:0} °C: hot. NVIDIA's own slowdown starts near 90 °C; the fan curve or a lower wall brings it down"));
                    }
                    else if (p.TempC < CoolAgainC) hot = false;

                    if (p.Loaded)
                    {
                        notLoadedSince = null;
                        if (!inLoad)
                        {
                            inLoad = true;
                            loadSince = windowStart = now;
                            window.Clear();
                            // The frames start with the clocks: not the menu or loading screen before this stretch.
                            try { run.Frames?.StartWindow(); } catch (IOException) { }
                            lock (run) run.Stretches++;
                            Emit(run, "🎮 load started");
                        }
                        window.Add(p);
                        lock (run) run.Loaded.Add(p);
                    }
                    else if (inLoad)
                    {
                        notLoadedSince ??= now;
                        if (now - notLoadedSince >= LoadGap)
                        {
                            if (window.Count > 0) Emit(run, WindowLine(window, curve) + Fps(run));
                            window.Clear();
                            inLoad = false;
                            Emit(run, $"⏸️ load stopped after {Duration(notLoadedSince.Value - loadSince)}");
                        }
                    }
                    if (inLoad && now - windowStart >= every)
                    {
                        if (window.Count > 0) Emit(run, WindowLine(window, curve) + Fps(run));
                        window.Clear();
                        windowStart = now;
                    }
                }
                next += run.IntervalMs;
                long wait = next - clock.ElapsedMilliseconds;
                if (wait <= 0) next = clock.ElapsedMilliseconds;   // a slow read: carry on, no burst to catch up
                else token.WaitHandle.WaitOne((int)wait);
            }
        }
        catch (Exception e)
        {
            run.EndReason ??= $"the sampler failed: {e.Message}";
        }
        finally
        {
            if (inLoad && window.Count > 0) Emit(run, WindowLine(window, curve) + Fps(run));
            Finish(run);
        }
    }

    /// <summary>The watchdog, on its own timer: a read that blocks (the driver mid-hang) stops the sampler too, so a card
    /// that does not answer is noticed here, not in the sampler's loop.</summary>
    private static void Watch(Run run)
    {
        lock (run)
        {
            if (run.Ended != null || run.SilentSince != null || DateTimeOffset.Now - run.LastAnswer < SilentAfter) return;
            run.SilentSince = run.LastAnswer;
            run.Silences++;
        }
        Emit(run, "⛔ the card has not answered for 3 s: a hang, or a driver reset under way. A driver reset the service records as the trial's crash by itself, then puts the daily profile back; ask what the person saw and read data_points: mark_crash only if no crash shows there (the game fell to the desktop with no reset)");
    }

    /// <summary>The game's frame rate since the last line, for a window's line; nothing without a capture.</summary>
    private static string Fps(Run run)
    {
        if (run.Frames is not { } f) return "";
        try { return " · " + FrameFeed.Words(f.TakeWindow()); }
        catch (IOException) { return ""; }
    }

    private void Finish(Run run)
    {
        run.EndReason ??= "live_stop";
        run.Frames?.Stop();
        string summary = SummaryLine(run);
        Emit(run, $"⏹️ live watch ended ({run.EndReason}). {summary}");
        lock (run) { run.Summary = summary; run.Ended = DateTimeOffset.Now; }
        run.Watchdog?.Dispose();
        try { run.Log.Dispose(); run.Csv.Dispose(); } catch (IOException) { }
        if (run.EndReason != "the server stopped")
            try { card.Broker(BrokerRequest.OpExperiment, "note", $"Live watch: {summary}"); } catch (Exception) { }
    }

    private static Point ToPoint(Card.Sample s, TelemetrySnapshot t, VfCurveSnapshot? curve, uint? maxUv)
    {
        var reading = s.Reading()!;
        var top = curve == null ? null : Limiter.CurveTop(curve.Points, maxUv);
        int? point = s.NvvddUv is { } uv && curve is { Points.Count: > 0 } c ? c.Points.MinBy(x => Math.Abs((long)x.VoltageUv - uv))!.Index : null;
        return new Point(s.At, t.GraphicsClockMhz, s.NvvddUv / 1000.0, s.MsvddUv / 1000.0, s.Mhz(AllClocksLayout.Xbar), t.PowerWatts,
            t.PowerEnforcedWatts ?? t.PowerLimitWatts, t.TemperatureC, t.GpuUtilPercent, t.FanPercent.Count > 0 ? t.FanPercent.Average(x => (double)x) : null,
            Limiter.Key(Limiter.Holding(reading, maxUv, top?.EffectiveKhz)), point,
            t is { GpuUtilPercent: >= LoadedUtilPct, PowerWatts: >= LoadedW });
    }

    /// <summary>One window under load, the way the reference card's runs were read: clock range and median, the voltage and
    /// the curve point it sat on, MSVDD and XBAR, power against the wall, temperature, fans, and what held the clock.</summary>
    private static string WindowLine(List<Point> w, VfCurveSnapshot? curve)
    {
        var clocks = w.Where(p => p.ClockMhz != null).Select(p => (double)p.ClockMhz!.Value).ToList();
        int? point = Mode(w.Select(p => p.CurvePoint));
        var onCurve = point is { } i && curve?.Points.FirstOrDefault(x => x.Index == i) is { } vp ? $" ({vp.EffectiveKhz / 1000.0:0} MHz on the curve)" : "";
        var limit = w.Select(p => p.LimitW).LastOrDefault(x => x != null);
        var sb = new StringBuilder("📊 ");
        sb.Append(clocks.Count == 0 ? "clock unread" : string.Create(CultureInfo.InvariantCulture, $"{clocks.Min():0}-{clocks.Max():0} MHz (p50 {Pct(clocks, 0.5):0})"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $" @ {Pct(w.Select(p => p.NvvddMv), 0.5):0} mV, point {point?.ToString(CultureInfo.InvariantCulture) ?? "?"}{onCurve}"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $" · MSVDD {Pct(w.Select(p => p.MsvddMv), 0.5):0} mV · XBAR {Pct(w.Select(p => p.XbarMhz), 0.5):0} MHz"));
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $" · {Avg(w.Select(p => p.BoardW)):0} W avg, {w.Max(p => p.BoardW):0} max of {limit:0} ({100.0 * w.Count(p => p.OnWall) / w.Count:0} % on the wall)"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $" · {w.Max(p => p.TempC):0} °C · fans {Avg(w.Select(p => p.FanPct)):0} %"));
        sb.Append(" · ").Append(Words(Mode(w.Select(p => p.HeldBy)) ?? "idle"));
        return sb.ToString();
    }

    private object Totals(Run run)
    {
        List<Point> l;
        int samples, stretches, silences;
        lock (run) { l = [.. run.Loaded]; samples = run.Samples; stretches = run.Stretches; silences = run.Silences; }
        var clocks = l.Where(p => p.ClockMhz != null).Select(p => (double)p.ClockMhz!.Value).ToList();
        return new
        {
            started = run.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            minutes = Math.Round(((run.Ended ?? DateTimeOffset.Now) - run.Started).TotalMinutes, 1),
            under_load_min = Math.Round(l.Count * run.IntervalMs / 60000.0, 1),
            stretches_of_load = stretches,
            samples,
            card_stopped_answering = silences,
            p10_mhz = Pct(clocks, 0.1),
            p50_mhz = Pct(clocks, 0.5),
            p90_mhz = Pct(clocks, 0.9),
            p50_nvvdd_mv = Pct(l.Select(p => p.NvvddMv), 0.5),
            p50_msvdd_mv = Pct(l.Select(p => p.MsvddMv), 0.5),
            p50_xbar_mhz = Pct(l.Select(p => p.XbarMhz), 0.5),
            p50_board_w = Pct(l.Select(p => p.BoardW), 0.5),
            max_board_w = l.Count == 0 ? null : Round1(l.Max(p => p.BoardW)),
            max_temp_c = l.Count == 0 ? null : Round1(l.Max(p => p.TempC)),
            on_the_wall_pct = l.Count == 0 ? (double?)null : Math.Round(100.0 * l.Count(p => p.OnWall) / l.Count),
            curve_points = l.Where(p => p.CurvePoint != null).GroupBy(p => p.CurvePoint!.Value).OrderByDescending(g => g.Count()).Take(5)
                .Select(g => new { point = g.Key, mv = Pct(g.Select(p => p.NvvddMv), 0.5), p50_mhz = Pct(g.Where(p => p.ClockMhz != null).Select(p => (double)p.ClockMhz!.Value).ToList(), 0.5), pct = Math.Round(100.0 * g.Count() / l.Count) }),
            held_by = l.GroupBy(p => p.HeldBy).OrderByDescending(g => g.Count())
                .Select(g => new { what = Words(g.Key), pct = Math.Round(100.0 * g.Count() / l.Count) }),
            fps = FrameFeed.Show(SafeTotal(run)),
            fps_csv = run.Frames?.Path,
        };
    }

    private string SummaryLine(Run run)
    {
        List<Point> l;
        int stretches, silences;
        lock (run) { l = [.. run.Loaded]; stretches = run.Stretches; silences = run.Silences; }
        var minutes = ((run.Ended ?? DateTimeOffset.Now) - run.Started).TotalMinutes;
        if (l.Count == 0) return string.Create(CultureInfo.InvariantCulture, $"{minutes:0.#} min, never under load") + (run.Note != null ? $". Playing: {run.Note}" : "");
        var clocks = l.Where(p => p.ClockMhz != null).Select(p => (double)p.ClockMhz!.Value).ToList();
        int? point = Mode(l.Select(p => p.CurvePoint));
        var held = string.Join(", ", l.GroupBy(p => p.HeldBy).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{Words(g.Key)} {100.0 * g.Count() / l.Count:0} %"));
        return string.Create(CultureInfo.InvariantCulture,
            $"{minutes:0.#} min, {l.Count * run.IntervalMs / 60000.0:0.#} min of it under load in {stretches} stretch(es). P50 {Pct(clocks, 0.5):0} MHz @ {Pct(l.Select(p => p.NvvddMv), 0.5):0} mV (point {point}), " +
            $"P10 {Pct(clocks, 0.1):0}, P90 {Pct(clocks, 0.9):0}; {100.0 * l.Count(p => p.OnWall) / l.Count:0} % on the wall; max {l.Max(p => p.TempC):0} °C, {l.Max(p => p.BoardW):0} W; {held}")
            + (silences > 0 ? $"; the card stopped answering {silences} time(s)" : "")
            + (run.Frames != null ? $"; {FrameFeed.Words(SafeTotal(run))}" : "") + (run.Note != null ? $". Playing: {run.Note}" : "");
    }

    private static Clocktail.Domain.Fps.FrameStats.App? SafeTotal(Run run)
    {
        try { return run.Frames?.Total(); } catch (IOException) { return null; }
    }

    private static string Row(Point p) => string.Create(CultureInfo.InvariantCulture,
        $"{p.At:HH:mm:ss.ff} {p.ClockMhz} MHz {p.NvvddMv:0} mV {p.BoardW:0} W {p.TempC:0} °C {p.UtilPct} % {p.HeldBy}");

    private static void WriteCsv(Run run, Point p, TelemetrySnapshot t, Clocktail.Domain.Rails.GpcVoltages.Reading? gpc)
    {
        try
        {
            run.Csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{p.At:O},{p.ClockMhz},{p.NvvddMv:0.#},{p.MsvddMv:0.#},{p.XbarMhz:0},{p.BoardW:0.#},{p.LimitW:0},{p.TempC:0},{p.UtilPct},{p.FanPct:0},{p.CurvePoint},{p.HeldBy},{string.Join("|", t.ThrottleReasons)},{t.PowerInstantWatts:0.#},{TuningTools.Gib(t.VramUsedBytes)},{t.TempMarginC},{gpc?.MeanMv:0.#},{gpc?.MinMv}"));
        }
        catch (IOException) { }
    }

    private static void Emit(Run run, string text)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {text}";
        lock (run) run.Lines.Add(line);
        try { run.Log.WriteLine(line); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>The log and the CSV, shared for reading while they are written (tail -F, an editor), flushed every line.</summary>
    private static StreamWriter Writer(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>live\ beside the tuning log (the broker's state folder for this GPU), or the same place by convention when
    /// the broker does not answer.</summary>
    private string Dir()
    {
        var r = card.Broker(BrokerRequest.OpExperiment, "show");
        if (r.Ok && r.Result is { } res && res.TryGetProperty("path", out var p) && p.GetString() is { } exp)
            return Path.Combine(Path.GetDirectoryName(exp)!, "live");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", card.StableId, "live");
    }

    /// <summary>What held the clock, in the words the person reads (Limiter's ids joined by '+').</summary>
    public static string Words(string key) => string.Join(" + ", key.Split('+').Select(id => id switch
    {
        "power_wall" => "the power wall",
        "power_policy" => "a power sub-policy",
        "voltage_limit" => "the voltage limit",
        "curve_cap" => "the curve's top",
        "thermal" => "temperature",
        "hardware_slowdown" => "a hardware slowdown",
        "no_limit" => "nothing (the load asks for less)",
        "idle" => "idle",
        _ => id,
    }));

    private static string Duration(TimeSpan d) =>
        d.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalMinutes} min {d.Seconds} s") : string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:0} s");

    private static T? Mode<T>(IEnumerable<T?> xs) where T : struct =>
        xs.Where(x => x != null).GroupBy(x => x!.Value).OrderByDescending(g => g.Count()).Select(g => (T?)g.Key).FirstOrDefault();

    private static string? Mode(IEnumerable<string> xs) => xs.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    private static double? Round1(double? x) => x is { } v ? Math.Round(v, 1) : null;

    private static double? Avg(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x != null).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 1);
    }

    private static double? Pct(IEnumerable<double?> xs, double q) => Pct(xs.Where(x => x != null).Select(x => x!.Value).ToList(), q);

    private static double? Pct(List<double> xs, double q)
    {
        if (xs.Count == 0) return null;
        var v = xs.Order().ToList();
        return Math.Round(v[(int)Math.Clamp(Math.Round(q * (v.Count - 1)), 0, v.Count - 1)], 1);
    }

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}
