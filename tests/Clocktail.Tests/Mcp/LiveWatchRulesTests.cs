using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>The live watch's rules: when a card is under load and when its readings look hung, scaled to this card's own
/// power limits (the reference card's 575 W figures are caps, not the rule); how long it was under load; and how each
/// period under a clock lock is checked on its own.</summary>
public class LiveWatchRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);

    private static TelemetrySnapshot T(uint util, double? w, double? enforced, double? @default = null) =>
        new(60, w, enforced, @default, enforced, null, null, 2700, null, null, null, util, null, null, null, [], []);

    private static LiveWatch.Point P(double sec, uint mhz, double w, uint util = 100, int? span = null) =>
        new(T0.AddSeconds(sec), mhz, 1000, 900, null, w, null, 60, util, 50, "no_limit", null, true, LockSpan: span);

    // ---- under load

    [Fact]
    public void A_small_card_at_full_use_is_under_load()
    {
        Assert.True(LiveWatch.IsLoaded(T(100, 100, 120, 130)));   // a 120 W limit can never reach the old fixed 120 W
        Assert.Equal(30, LiveWatch.LoadFloorW(T(100, 100, 120)));
    }

    [Fact]
    public void The_reference_card_keeps_its_120_watts()
    {
        Assert.Equal(120, LiveWatch.LoadFloorW(T(100, 130, 575)));
        Assert.True(LiveWatch.IsLoaded(T(100, 130, 575)));
        Assert.False(LiveWatch.IsLoaded(T(100, 110, 575)));
    }

    [Fact]
    public void Idle_or_no_limit_read_falls_back()
    {
        Assert.False(LiveWatch.IsLoaded(T(40, 100, 120)));        // not busy
        Assert.True(LiveWatch.IsLoaded(T(100, 130, null)));      // no limit read: 120 W
        Assert.False(LiveWatch.IsLoaded(T(100, 110, null)));
        Assert.False(LiveWatch.IsLoaded(T(100, null, 120)));     // no power read
    }

    // ---- hang-like readings

    [Fact]
    public void The_reference_hang_still_looks_like_one()
    {
        // 575 W card: frozen at 2800 MHz, 180 W, down from 450 W.
        double floor = LiveWatch.HangFloorW(T(100, 180, 575, 575));
        Assert.Equal(250, floor);
        Assert.True(LiveWatch.HangLike(P(0, 2800, 180), 2800, floor, 450));
        Assert.True(LiveWatch.HangLike(P(0, 2800, 180), null, floor, null));   // before any window: the floor alone
    }

    [Fact]
    public void A_steady_flat_curve_on_a_250_watt_card_is_not_a_hang()
    {
        double floor = LiveWatch.HangFloorW(T(99, 200, 250, 250));
        Assert.Equal(112.5, floor);
        Assert.False(LiveWatch.HangLike(P(0, 2700, 200, 99), 2700, floor, 205));
        // its real hang: the power falls well under its own load
        Assert.True(LiveWatch.HangLike(P(0, 2700, 90), 2700, floor, 210));
    }

    [Fact]
    public void A_light_load_at_a_fixed_clock_on_the_reference_card_is_not_a_hang()
    {
        // A deep undervolt in a light game: under 250 W, but steady, not fallen.
        Assert.False(LiveWatch.HangLike(P(0, 2400, 200), 2400, 250, 205));
    }

    [Fact]
    public void A_moving_clock_is_not_frozen() => Assert.False(LiveWatch.HangLike(P(0, 2800, 150), 2790, 250, 450));

    // ---- time under load

    [Fact]
    public void Time_under_load_is_from_the_samples_own_times()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), LiveWatch.LoadedStep(null, T0, 100));                       // the first
        Assert.Equal(TimeSpan.FromMilliseconds(150), LiveWatch.LoadedStep(T0, T0.AddMilliseconds(150), 100));    // slow reads
        Assert.Equal(TimeSpan.FromMilliseconds(200), LiveWatch.LoadedStep(T0, T0.AddSeconds(10), 100));          // a gap: two intervals
        var steps = Enumerable.Range(1, 600).Aggregate(TimeSpan.Zero, (sum, i) => sum + LiveWatch.LoadedStep(T0.AddMilliseconds(150 * (i - 1)), T0.AddMilliseconds(150 * i), 100));
        Assert.Equal(90, steps.TotalSeconds);   // 600 samples 150 ms apart: 90 s, not the 60 s that 600 x 100 ms says
    }

    // ---- clock locks per period

    private static readonly ClientLimit Cap = new(0, 2, 2_400_000), Floor = new(1, 2, 2_700_000), Pin = new(6, 3, 950_000);

    private static VfCurveSnapshot Curve(uint pointKhz, int offsetKhz) => new(VfMode.Regional, VfLayoutId.Regional132, [],
        [new(0, 900_000, 2_500_000, 2_500_000, 0, 900_000), new(1, 950_000, 2_800_000, pointKhz, offsetKhz, 950_000),
         new(2, 1_000_000, 2_950_000, 2_950_000, 0, 1_000_000)], 0, [], [], []);

    [Fact]
    public void A_released_lock_keeps_its_result()
    {
        var spans = LiveWatch.Track([], [Cap], null, null, T0);
        spans = LiveWatch.Track(spans, [], null, null, T0.AddMinutes(5));
        var released = Assert.Single(spans);
        Assert.Equal(T0.AddMinutes(5), released.To);

        var loaded = Enumerable.Range(0, 10).Select(i => P(i, i < 3 ? 2500u : 2390u, 400, span: 1))
            .Concat(Enumerable.Range(0, 10).Select(i => P(400 + i, 2900, 450))).ToList();
        var o = Assert.Single(LiveWatch.LocksBySpan(spans, loaded)!);
        Assert.Equal(10, o.Samples);
        var cap = Assert.Single(o.Outcomes);
        Assert.Equal(3, cap.OffSamples);
        Assert.Equal(30, cap.OffPct);
        Assert.Equal(T0.AddMinutes(5), o.To);
        Assert.Contains("ran over the 2400 MHz cap", LiveWatch.SpanWords([o]));
    }

    [Fact]
    public void A_changed_lock_keeps_both()
    {
        var spans = LiveWatch.Track([], [Cap], null, null, T0);
        spans = LiveWatch.Track(spans, [Floor], null, null, T0.AddMinutes(5));
        Assert.Equal(2, spans.Count);
        Assert.Equal(T0.AddMinutes(5), spans[0].To);
        Assert.Null(spans[1].To);

        var loaded = new[] { P(0, 2390, 400, span: 1), P(1, 2390, 400, span: 1), P(400, 2600, 400, span: 2), P(401, 2700, 400, span: 2) };
        var met = LiveWatch.LocksBySpan(spans, loaded)!;
        Assert.Equal(0, met[0].Outcomes[0].OffSamples);   // the cap held over its own samples
        Assert.Equal("min", met[1].Outcomes[0].Kind);
        Assert.Equal(1, met[1].Outcomes[0].OffSamples);   // only the floor's own samples count against it
        string words = LiveWatch.SpanWords(met);
        Assert.Contains("; clock lock 20:00-20:05: maximum clock 2400 MHz: held", words);
        Assert.Contains("; clock lock 20:05-now: ", words);
    }

    [Fact]
    public void An_unreadable_record_is_not_a_release()
    {
        var spans = LiveWatch.Track([], [Cap], null, null, T0);
        spans = LiveWatch.Track(spans, null, null, null, T0.AddMinutes(1));
        Assert.Null(Assert.Single(spans).To);
    }

    [Fact]
    public void A_point_lock_is_checked_against_the_curve_in_force_in_its_period()
    {
        var spans = LiveWatch.Track([], [Pin], Curve(2_800_000, 0), null, T0);
        spans = LiveWatch.Track(spans, [Pin], Curve(2_790_000, 0), null, T0.AddSeconds(10));   // warmer: same offsets
        Assert.Single(spans);
        spans = LiveWatch.Track(spans, [Pin], Curve(2_900_000, 100_000), null, T0.AddMinutes(5));   // a trial moved the point
        Assert.Equal(2, spans.Count);
        Assert.Equal(spans[0].Key, spans[1].Key);

        var met = LiveWatch.LocksBySpan(spans, [P(0, 2790, 400, span: 1), P(400, 2900, 400, span: 2)])!;
        Assert.Equal(2790, met[0].Outcomes[0].TargetMhz);   // the newest curve of its own period
        Assert.Equal(2900, met[1].Outcomes[0].TargetMhz);
        Assert.Equal(0, met[0].Outcomes[0].OffSamples);
    }

    [Fact]
    public void No_lock_at_any_read_is_nothing()
    {
        Assert.Empty(LiveWatch.Track([], [], null, null, T0));
        Assert.Null(LiveWatch.LocksBySpan([], [P(0, 2800, 400)]));
        Assert.Equal("", LiveWatch.SpanWords(null));
    }

    // ---- live_stop while the sampler is stuck in the driver

    [Fact]
    public void Live_stop_answers_with_the_run_so_far_while_the_sampler_waits_on_the_driver()
    {
        var stuck = new TaskCompletionSource().Task;
        var (summary, waiting) = LiveWatch.StopAnswer(stuck, TimeSpan.FromMilliseconds(10), () => null, () => "so far");
        Assert.Equal("so far", summary);
        Assert.Contains("still waiting on the driver", waiting);

        var (done, none) = LiveWatch.StopAnswer(Task.CompletedTask, TimeSpan.FromSeconds(1), () => "finished", () => "so far");
        Assert.Equal("finished", done);
        Assert.Null(none);
    }

    // ---- watch and stress_test

    private static Card.Sample S(double sec, uint util, double w) => new(T0.AddSeconds(sec), T(util, w, 575, 575), 950_000, null, null, null);

    [Fact]
    public void Watch_sets_the_whole_capture_fps_against_the_whole_watch_power()
    {
        var all = Enumerable.Range(0, 10).Select(i => i < 5 ? S(i, 10, 50) : S(i, 99, 450)).ToList();
        var loaded = all.Where(s => LiveWatch.IsLoaded(s.Telemetry!)).ToList();
        Assert.Equal(5, loaded.Count);

        var e = JsonSerializer.SerializeToElement(TuningTools.WatchEfficiency(all, loaded, 100));
        Assert.Equal(250, e.GetProperty("avg_board_w").GetDouble());
        Assert.Equal(40, e.GetProperty("fps_per_100w").GetDouble());   // not 100 / 450 W
        Assert.Contains("whole watch", e.GetProperty("over").GetString());

        var noFps = JsonSerializer.SerializeToElement(TuningTools.WatchEfficiency(all, loaded, null));
        Assert.Equal(450, noFps.GetProperty("avg_board_w").GetDouble());
    }

    [Fact]
    public void An_unreadable_lock_record_is_said_not_taken_for_no_lock()
    {
        Assert.Equal("; " + TuningTools.LocksUnread, TuningTools.LocksUnreadWords(null));
        Assert.Contains("a lock may be in force", TuningTools.LocksUnread);
        Assert.Equal("", TuningTools.LocksUnreadWords([]));
    }
}
