using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

public class FlightRecorderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    private static FlightSample S(double sec, uint mhz, uint mv, double w, uint util = 99, int readMs = 5) =>
        new(T0.AddSeconds(sec), readMs, mhz, mv, w, 70, util, 0x4);

    [Fact]
    public void Only_the_last_sixty_seconds_are_kept()
    {
        var r = new FlightRecorder(FlightRecorder.DefaultKeep);
        for (int i = 0; i <= 480; i++) r.Add(S(i * 0.25, 2800, 950, 590));
        var w = r.Freeze();
        Assert.Equal(241, w.Count);
        Assert.Equal(T0.AddSeconds(60), w[0].At);
    }

    /// <summary>The measured hang of 2026-10-18 17:59Z (re/tuning-results §2): 2895 MHz at 910 mV, 598.6 W; then the
    /// limit letting go, 2947 MHz at 970 mV, 514 W; then ~30 s frozen at 3180 MHz, 995-1000 mV, ~126 W.</summary>
    [Fact]
    public void The_sample_of_the_limit_letting_go_is_not_the_crash_point()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598.6));        // 0 .. 19.5 s
        w.Add(S(20.0, 2947, 970, 514));                                             // letting go
        for (int i = 0; i < 60; i++) w.Add(S(20.5 + i * 0.5, 3180, 995 + (uint)(i % 2) * 5, 126));   // frozen
        var p = FlightRecorder.Pick(w, T0.AddSeconds(51));
        Assert.NotNull(p);
        Assert.Equal((2895u, 910u), (p!.Sample.ClockMhz!.Value, p.Sample.CoreMv!.Value));
        Assert.Equal(T0.AddSeconds(19.5), p.Sample.At);

        // A drop in power at the same point (a lighter scene) is not letting go.
        Assert.False(FlightRecorder.LettingGo(S(1, 2895, 910, 450), S(0.5, 2895, 910, 598)));
        Assert.True(FlightRecorder.LettingGo(S(1, 2947, 970, 514), S(0.5, 2895, 910, 598.6)));
    }

    /// <summary>S-4: the GPU looks hung while its readings sit frozen after power fell, or a read blocks; a steady pinned
    /// load or an old window is not a hang.</summary>
    [Fact]
    public void A_hang_is_seen_while_it_lasts_and_a_steady_load_is_not_one()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 2895, 910, 598));
        for (int i = 0; i < 12; i++) w.Add(S(20 + i * 0.5, 3180, 1000, 126));
        Assert.Contains("frozen at 3180 MHz", FlightRecorder.HungNow(w, T0.AddSeconds(26)));
        Assert.Null(FlightRecorder.HungNow(w, T0.AddSeconds(40)));                               // the window is old: the recorder stopped

        var steady = Enumerable.Range(0, 80).Select(i => S(i * 0.5, 3045, 985, 380)).ToList();   // pinned at a flat top
        Assert.Null(FlightRecorder.HungNow(steady, T0.AddSeconds(40)));
        var blocked = new List<FlightSample>(steady) { S(40.2, 3045, 985, 380, readMs: 1800) };
        Assert.Contains("blocked for 1800 ms", FlightRecorder.HungNow(blocked, T0.AddSeconds(41)));
    }

    /// <summary>A hang at the top of the curve under nvgwls -l 40: NVML reads the square wave as ~40 % utilization at
    /// ~380 W. It used to leave no crash point (loaded meant 60 % or more).</summary>
    [Fact]
    public void A_light_load_hang_has_a_crash_point_and_idle_still_has_none()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.5, 3045, 985, 380, util: 40));
        for (int i = 0; i < 60; i++) w.Add(S(20 + i * 0.5, 3180, 1000, 120, util: 100));   // frozen, power fell into it
        var p = FlightRecorder.Pick(w, T0.AddSeconds(51));
        Assert.Equal(985u, p!.Sample.CoreMv);
        Assert.False(FlightRecorder.Loaded(S(0, 2100, 850, 90, util: 30)));   // the desktop: not load
    }

    [Fact]
    public void The_crash_point_is_the_last_loaded_sample_before_the_reads_block_not_the_wall_letting_go()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.25, 2820, 955, 597));          // 0 .. 9.75 s in game
        w.Add(S(10.0, 3150, 1035, 120, readMs: 2300));                             // hung: blocked read, wall gone
        w.Add(S(12.5, 3150, 1035, 110));
        var p = FlightRecorder.Pick(w, T0.AddSeconds(14));
        Assert.NotNull(p);
        Assert.Equal(955u, p!.Sample.CoreMv);
        Assert.Equal(T0.AddSeconds(9.75), p.Sample.At);
    }

    [Fact]
    public void Without_a_blocked_read_the_low_power_tail_and_the_last_two_seconds_are_left_out()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 20; i++) w.Add(S(i * 0.5, 2805, 940, 598));            // 0 .. 9.5 s
        w.Add(S(10, 3150, 1035, 140));                                             // the wall let go: low power
        w.Add(S(11.5, 2805, 940, 598));                                            // within 2 s of the reset: may be the hang
        var p = FlightRecorder.Pick(w, T0.AddSeconds(13));
        Assert.Equal(T0.AddSeconds(9.5), p!.Sample.At);
        Assert.Null(FlightRecorder.Pick([S(1, 210, 750, 30, util: 2)], T0.AddSeconds(5)));   // idle: no point
    }

    // 2026-10-18 17:59Z: the hung GPU kept answering without blocking, 3180 MHz at 995–1000 mV, util 100 %, ~125 W,
    // for ~30 s until Windows logged the reset.
    private static IEnumerable<FlightSample> Frozen(double from, double to)
    {
        for (double t = from; t < to; t += 0.25) yield return S(t, 3180, ((int)(t * 4) % 7 == 0) ? 995u : 1000u, 126 - (t - from) * 0.08, util: 100);
    }

    [Fact]
    public void A_hang_that_keeps_answering_is_cut_off_at_the_load_drop()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 120; i++) w.Add(S(i * 0.25, (uint)(2880 + i % 3 * 7), (uint)(915 - i % 2 * 5), 598));   // 0 .. 29.75 s at the wall
        w.AddRange(Frozen(30, 60));
        var p = FlightRecorder.Pick(w, T0.AddSeconds(60.5));
        Assert.NotNull(p);
        Assert.Equal(T0.AddSeconds(29.75), p!.Sample.At);
        Assert.InRange(p.Sample.CoreMv!.Value, 910u, 915u);
        Assert.Contains("froze", p.Why);
    }

    [Fact]
    public void A_window_that_is_frozen_throughout_gives_no_crash_point_rather_than_a_false_one()
    {
        Assert.Null(FlightRecorder.Pick([.. Frozen(0, 20)], T0.AddSeconds(20.5)));
    }

    [Fact]
    public void A_steady_load_at_the_curve_cap_is_not_taken_for_a_hang()
    {
        var w = new List<FlightSample>();
        for (int i = 0; i < 40; i++) w.Add(S(i * 0.25, (uint)(3120 + i % 2 * 15), 1040, 310));   // light load around the cap
        for (int i = 40; i < 80; i++) w.Add(S(i * 0.25, 3150, 1040, 305));                      // pinned at the cap for 10 s
        Assert.Null(FlightRecorder.FrozenFrom(w));
        var p = FlightRecorder.Pick(w, T0.AddSeconds(22));
        Assert.Equal(3150u, p!.Sample.ClockMhz);
    }
}
