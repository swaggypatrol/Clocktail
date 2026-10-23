using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

/// <summary>The lab's host-side logic without a GPU: the access orders, the chase's closed form, the run loop on a fake
/// engine, and ST02's stop rules.</summary>
public class LabTests
{
    /// <summary>Every access order is a bijection on the buffer's vectors, and the kernels' add-and-wrap stepping lands where
    /// the multiplication would: no vector skipped, none visited twice, the expected word only a function of the address.</summary>
    [Theory]
    [InlineData(1000UL, 96UL)]
    [InlineData(4096UL + 7, 256UL)]
    [InlineData(65536UL, 1360UL)]
    [InlineData(100003UL, 43520UL)]
    public void Every_order_visits_each_vector_once_and_the_stepping_matches(ulong n, ulong grid)
    {
        foreach (var pattern in Enum.GetValues<AccessPattern>())
        {
            var plan = PatternPlan.Of(pattern, n, grid);
            var seen = new bool[n];
            for (ulong v = 0; v < n; v++)
            {
                ulong m = plan.Map(v, n);
                Assert.True(m < n, $"{pattern}: {m} out of {n}");
                Assert.False(seen[m], $"{pattern}: {m} twice");
                seen[m] = true;
            }
            for (ulong gid = 0; gid < Math.Min(grid, 64); gid++)
            {
                ulong cur = (ulong)((UInt128)gid * plan.Multiplier % n);
                for (ulong v = gid; v < n; v += grid)
                {
                    ulong m = cur < plan.XorFull ? cur ^ 128 : cur;
                    Assert.Equal(plan.Map(v, n), m);
                    cur += plan.Delta;
                    if (cur >= n) cur -= n;
                }
            }
        }
    }

    /// <summary>The detached sweep's command line reads back as the arguments it was given, paths with spaces included.</summary>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\Users\MING\AppData\Local\Clocktail\x\lab\sweep-1", @"C:\Users\MING\AppData\Local\Clocktail\x\lab\sweep-1")]
    [InlineData(@"C:\Program Files\Clocktail\lab dir", "\"C:\\Program Files\\Clocktail\\lab dir\"")]
    [InlineData(@"ends with slash\", "\"ends with slash\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void The_sweep_command_line_quotes_as_windows_reads_it(string arg, string quoted) => Assert.Equal(quoted, Detach.Quote(arg));

    [Fact]
    public void The_strides_are_the_testers()
    {
        ulong n = 21632UL << 20 >> 4;   // a 21.6 GiB buffer in 16-byte vectors
        Assert.InRange(PatternPlan.Of(AccessPattern.Stride64K, n, 43520).Multiplier, 4090UL, 4102UL);
        Assert.InRange(PatternPlan.Of(AccessPattern.Stride128K, n, 43520).Multiplier, 8186UL, 8198UL);
        Assert.Equal(1UL, PatternPlan.Of(AccessPattern.Linear, n, 43520).Multiplier);
        Assert.Equal(n / 256 * 256, PatternPlan.Of(AccessPattern.BlockXor, n, 43520).XorFull);
    }

    [Fact]
    public void The_chase_end_is_the_walk()
    {
        uint mask = (1u << 12) - 1;
        for (uint t = 0; t < 8; t++)
        {
            uint s = Reference.ChaseStart(t, mask);
            for (uint h = 1; h <= 5000; h++)
            {
                s = unchecked(Reference.ChaseA * s + Reference.ChaseC) & mask;
                if (h % 997 == 0) Assert.Equal(s, Reference.ChaseEnd(Reference.ChaseStart(t, mask), h, mask));
            }
        }
    }

    [Fact]
    public void The_kernels_are_one_module_with_the_memory_pair_taking_the_order()
    {
        string ptx = Kernels.Module();
        foreach (var k in new[] { Kernels.Fma, Kernels.MemFill, Kernels.MemVerify, Kernels.ChaseFill, Kernels.ChaseRun })
            Assert.Contains($".visible .entry {k}(", ptx);
        Assert.Equal(ptx.Count(c => c == '{'), ptx.Count(c => c == '}'));
        Assert.Contains("ld.global.cg.v4.u32", ptx);
        Assert.Contains("@%p1 sub.s64 %rd23, %rd23, %rd2;", ptx);
    }

    private sealed class FakeEngine : ILoadEngine
    {
        public int ComputeThreads => 1 << 16;
        public int SmCount => 16;
        public ulong MemoryBufferBytes => 1UL << 30;
        public long BadAt = -1, Launches;
        public List<int> Threads = [];
        public Func<double> Ms = () => 10;
        public ChunkOutcome RunCompute(uint iterations, bool isReference, int threads = 0)
        {
            Threads.Add(threads);
            return new(Ms(), ++Launches == BadAt ? 1 : 0, (double)Math.Max(threads, 256) * iterations * 24);
        }
        public ChunkOutcome RunMemory(uint seed, AccessPattern pattern) =>
            new(pattern is AccessPattern.Linear or AccessPattern.BlockXor ? 1.4 : 25, ++Launches == BadAt ? 3 : 0, 2.0 * MemoryBufferBytes, null, Launches == BadAt ? 4096UL : null);
        public ChunkOutcome RunLatency(uint hops) { Launches++; return new(hops / 2000.0, 0, hops * 512.0); }
        public ChunkOutcome RunL2(uint seed) { Launches++; return new(4, 0, 2.0 * (32 << 20) * 256); }
        public void Dispose() { }
    }

    private sealed class FakeSampler(Func<double?> watts) : ISampler
    {
        public LabSample Read(double t, int phase) => new(t, phase, watts(), 60, 2800, 13801, 99, 0);
        public void Dispose() { }
    }

    /// <summary>A clock that moves only when the run sleeps or launches, so a phase of seconds runs in microseconds.</summary>
    private static (Func<double>, Action<int>) FakeTime(FakeEngine e)
    {
        double now = 0;
        long seen = 0;
        return (() => { if (e.Launches != seen) { now += (e.Launches - seen) * 0.01; seen = e.Launches; } return now; }, ms => now += ms / 1000.0);
    }

    [Fact]
    public void A_clean_run_passes_and_measures_each_order_after_the_first_round()
    {
        var e = new FakeEngine();
        var (clock, sleep) = FakeTime(e);
        var cfg = new LabConfig(LabConfig.Parse("burn,memory,latency", 30), SampleMs: 1000);
        var phases = new LabRunner(cfg, e, new FakeSampler(() => 400), clock, sleep, sampleWait: _ => Thread.Sleep(1)).Run(CancellationToken.None);
        var (verdict, reasons) = LabRunner.Judge(phases, 3);
        Assert.Equal(LabResult.Pass, verdict);
        Assert.Empty(reasons);
        var mem = phases[1];
        Assert.Equal(5, mem.Patterns!.Count);
        Assert.True(mem.Patterns.First(p => p.Pattern == "Linear").Chunks > mem.Patterns.First(p => p.Pattern == "Permute").Chunks);
        Assert.InRange(mem.Gbps!.Value, 1500, 1570);   // 2 GiB moved in 1.4 ms
        Assert.NotNull(phases[2].LatencyNs);
    }

    [Fact]
    public void A_wrong_word_fails_the_run_with_where()
    {
        var e = new FakeEngine { BadAt = 3 };
        var (clock, sleep) = FakeTime(e);
        var phases = new LabRunner(new LabConfig(LabConfig.Parse("memory", 10), SampleMs: 1000), e, new FakeSampler(() => 300), clock, sleep, sampleWait: _ => Thread.Sleep(1)).Run(CancellationToken.None);
        var (verdict, reasons) = LabRunner.Judge(phases, 1);
        Assert.Equal(LabResult.Fail, verdict);
        Assert.Contains("first at word 4096", Assert.Single(reasons));
    }

    [Fact]
    public void Burn_steers_its_threads_toward_the_board_power_asked_for()
    {
        var e = new FakeEngine();
        var (clock, sleep) = FakeTime(e);
        // The board draws in proportion to the threads launched: 600 W with all of them.
        var phases = new LabRunner(new LabConfig(LabConfig.Parse("burn", 20), TargetW: 300, SampleMs: 100), e,
            new FakeSampler(() => e.Threads.Count == 0 ? 600 : 600.0 * e.Threads[^1] / e.ComputeThreads), clock, sleep, sampleWait: _ => Thread.Sleep(1)).Run(CancellationToken.None);
        Assert.Equal(LabResult.Pass, LabRunner.Judge(phases, 1).Verdict);
        Assert.InRange(e.Threads[^1] / (double)e.ComputeThreads, 0.4, 0.6);
    }

    private static SweepStep S(int mhz, double gbps, double lat, double mem, string verdict = LabResult.Pass, double? othersMib = null) =>
        new(mhz, verdict, gbps, lat, mem, 60, 62, 1, null, 0, 0, null, verdict == LabResult.Pass ? null : "a bad word", "P0", 3, (ulong?)othersMib,
            new LabResult("", "", 0, 0, default, default, [], verdict, [], 3, (ulong?)othersMib, 2).Shared);

    [Fact]
    public void The_sweep_goes_on_while_each_step_is_faster()
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, 1510, 498, 13901) };
        Assert.Null(MemorySweep.Signal(steps));
        Assert.Equal(100, MemorySweep.Best(steps));
    }

    [Theory]
    [InlineData(1510, 498, 13851, LabResult.Pass, null, "did not follow the offset")]
    [InlineData(1510, 498, 13901, LabResult.Fail, null, "a bad word")]
    [InlineData(1510, 498, 13901, LabResult.Pass, 30000.0, "other work kept sharing the GPU")]
    public void A_failing_or_unjudgeable_step_stops_the_sweep_and_the_best_is_the_one_before(double gbps, double lat, double mem, string verdict, double? othersMib, string why)
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, gbps, lat, mem, verdict, othersMib) };
        Assert.Contains(why, MemorySweep.Signal(steps));
        Assert.Equal(50, MemorySweep.Best(steps));
    }

    /// <summary>A stable point is what a later agent reads as the offset proved, so only a step the sweep could judge is
    /// one: decided from the stop's values before anything is recorded. In 0.3.3 the trial was marked stable first, and only
    /// then did the sweep find, from the stop's words, that the offset had not been in force or the GPU had been shared.</summary>
    [Theory]
    [InlineData(1510, 498, 13851, LabResult.Pass, null, MemorySweep.StepEnd.Unjudged)]            // the clock did not follow: not in force
    [InlineData(1510, 498, 13901, LabResult.Pass, 30000.0, MemorySweep.StepEnd.Unjudged)]         // other work shared the GPU
    [InlineData(1510, 498, 13901, LabResult.Inconclusive, null, MemorySweep.StepEnd.Unjudged)]    // too hot, the runner: not measured
    [InlineData(1510, 498, 13901, LabResult.Fail, null, MemorySweep.StepEnd.Crash)]
    [InlineData(1510, 498, 13901, LabResult.Pass, null, MemorySweep.StepEnd.Stable)]
    public void Only_a_step_the_sweep_could_judge_is_marked_stable(double gbps, double lat, double mem, string verdict, double? othersMib, MemorySweep.StepEnd end)
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, gbps, lat, mem, verdict, othersMib) };
        Assert.Equal(end, MemorySweep.End(steps[^1], MemorySweep.Edge(steps), trend: false));
        Assert.Equal(end, MemorySweep.End(steps[^1], MemorySweep.Edge(steps), trend: true));   // the trend goes on past a slowdown, never past these
    }

    /// <summary>A fine sweep starts away from the daily offset: its first step's memory clock must follow the offset from the
    /// warm-up's, as each later step's follows the one before. Before 0.3.5 the first step had nothing to be checked against
    /// and was marked stable even with the offset not in force (CUDA work in P1).</summary>
    [Theory]
    [InlineData(13801, MemorySweep.StepEnd.Unjudged)]   // the clock stayed where the warm-up's was: not in force
    [InlineData(15469, MemorySweep.StepEnd.Stable)]     // +1668 MHz followed 1:1
    public void The_first_step_is_checked_against_the_warm_up(double mem, MemorySweep.StepEnd end)
    {
        var steps = new List<SweepStep> { S(1668, 1790, 470, mem) };
        Assert.Equal(end, MemorySweep.End(steps[^1], MemorySweep.Edge(steps, (0, 13801)), trend: false));
        Assert.Null(MemorySweep.Edge(steps));   // with no warm-up reading there is nothing to check it against
    }

    /// <summary>A slowdown that lasts is a judged step: stable (it passed), and the end of the sweep unless it runs for the trend.</summary>
    [Fact]
    public void A_lasting_slowdown_is_marked_stable_and_ends_the_sweep_unless_it_runs_for_the_trend()
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, 1490, 498, 13901), S(150, 1488, 497, 13951) };
        Assert.Equal(MemorySweep.StepEnd.StableThenStop, MemorySweep.End(steps[^1], MemorySweep.Edge(steps), trend: false));
        Assert.Equal(MemorySweep.StepEnd.Stable, MemorySweep.End(steps[^1], MemorySweep.Edge(steps), trend: true));
    }

    /// <summary>One slower step is watched, not trusted: bandwidth under the best once, or latency jumping once.</summary>
    [Theory]
    [InlineData(1490, 498, "bandwidth -1.0 % under the best")]
    [InlineData(1520, 510, "latency +2.4 % over the lowest")]
    public void One_slower_step_is_watched_and_the_sweep_goes_on(double gbps, double lat, string watch)
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 498, 13851), S(100, gbps, lat, 13901) };
        Assert.Null(MemorySweep.Signal(steps));
        Assert.Contains(watch, MemorySweep.Watch(steps));
    }

    [Fact]
    public void Bandwidth_under_the_best_twice_in_a_row_stops_the_sweep_and_the_best_is_before_the_first()
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, 1490, 498, 13901), S(150, 1488, 497, 13951) };
        Assert.Contains("bandwidth stayed under the best step's", MemorySweep.Signal(steps));
        Assert.Equal(50, MemorySweep.Best(steps));
    }

    /// <summary>A timing change and then retrying: latency jumps, and instead of falling with the clock it rises again.</summary>
    [Fact]
    public void Latency_that_jumps_and_rises_again_stops_the_sweep_and_the_best_is_before_the_jump()
    {
        var steps = new List<SweepStep> { S(758, 1734, 463.3, 14559), S(858, 1745, 460.6, 14659), S(958, 1749, 477.7, 14759), S(1058, 1750, 481.0, 14859) };
        Assert.Contains("latency rose again to 481 ns", MemorySweep.Signal(steps));
        Assert.Equal(858, MemorySweep.Best(steps));
    }

    /// <summary>The reference card on 2026-10-22 (XBAR +400 / +50 mV, ratio 0.95, MSVDD REL +50), +358 to +1958 in 100 MHz
    /// steps, every one free of errors: latency jumped 17 ns at +958 (a timing change) and fell with the clock after it to
    /// 451.8 ns, under +858's; bandwidth dipped once at +1558 and +1658 was the best yet. Nothing in it is retrying.</summary>
    [Fact]
    public void The_reference_cards_sweep_to_1958_runs_through_a_timing_change_and_a_dip()
    {
        double[][] data =
        [
            [358, 1692.8, 475.2], [458, 1703.8, 471.9], [558, 1713.3, 468.8], [658, 1727.0, 466.0], [758, 1734.4, 463.3],
            [858, 1744.8, 460.6], [958, 1749.1, 477.7], [1058, 1761.0, 475.3], [1158, 1766.1, 472.3], [1258, 1773.1, 469.7],
            [1358, 1778.9, 467.1], [1458, 1778.2, 464.4], [1558, 1757.1, 461.8], [1658, 1788.1, 459.1], [1758, 1800.5, 456.7],
            [1858, 1794.6, 454.1], [1958, 1798.0, 451.8],
        ];
        var steps = new List<SweepStep>();
        var watched = new List<int>();
        foreach (var d in data)
        {
            steps.Add(S((int)d[0], d[1], d[2], 13801 + d[0]));
            Assert.Null(MemorySweep.Signal(steps));
            if (MemorySweep.Watch(steps) != null) watched.Add((int)d[0]);
        }
        Assert.Equal([958, 1558], watched);
        Assert.Equal(1958, MemorySweep.Best(steps));
    }

    /// <summary>The reference card on 2026-10-21 (the daily before: XBAR +250, MSVDD REL 0), +358 to +858 in 50 MHz steps.</summary>
    [Fact]
    public void The_reference_cards_sweep_to_858_goes_on_all_the_way()
    {
        double[][] data =
        [
            [358, 1570.7, 498.7], [408, 1577.8, 497.8], [458, 1586.7, 496.2], [508, 1586.7, 494.9], [558, 1591.5, 493.5],
            [608, 1598.2, 491.9], [658, 1599.6, 489.8], [708, 1603.0, 488.7], [758, 1608.6, 487.6], [808, 1613.3, 486.4], [858, 1616.2, 484.5],
        ];
        var steps = data.Select(d => S((int)d[0], d[1], d[2], 13801 + d[0])).ToList();
        for (int i = 1; i <= steps.Count; i++) Assert.Null(MemorySweep.Signal(steps.Take(i).ToList()));
        Assert.Equal(858, MemorySweep.Best(steps));
    }

    /// <summary>The reference card's +0/+179/+358: bandwidth flat (the crossbar sets it), latency down. A healthy sweep goes on.</summary>
    [Fact]
    public void Flat_bandwidth_with_falling_latency_goes_on()
    {
        var steps = new List<SweepStep> { S(0, 1437, 531, 13801), S(179, 1440, 524, 13980), S(358, 1439, 518, 14159) };
        Assert.Null(MemorySweep.Signal(steps));
        Assert.Equal(358, MemorySweep.Best(steps));
    }

    /// <summary>A slow drift stops it too: latency rising step after step until it is 1 % over the lowest. The drift began at
    /// +100 (498 → 501), so the best is +50.</summary>
    [Fact]
    public void Latency_drifting_up_from_the_lowest_stops_the_sweep()
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1500, 498, 13851), S(100, 1500, 501, 13901), S(150, 1500, 503.5, 13951) };
        Assert.Contains("over the lowest step's 498", MemorySweep.Signal(steps));
        Assert.Equal(50, MemorySweep.Best(steps));
    }

    private sealed class TempSampler(Func<uint?> temp) : ISampler
    {
        public LabSample Read(double t, int phase) => new(t, phase, 400, temp(), 2800, 13801, 99, 0);
        public void Dispose() { }
    }

    /// <summary>The run's own guard, whoever started it (a job's measurement as much as lab_test): at the temperature limit
    /// it stops, and the verdict is inconclusive, neither a pass nor the setting's failure.</summary>
    [Fact]
    public void A_run_that_gets_too_hot_stops_itself_and_is_neither_a_pass_nor_a_failure()
    {
        var e = new FakeEngine();
        var (clock, sleep) = FakeTime(e);
        // The fake clock runs the load in microseconds; the sampler loops freely so its two readings come first.
        var runner = new LabRunner(new LabConfig(LabConfig.Parse("burn", 60_000), SampleMs: 100), e, new TempSampler(() => 95), clock, sleep, sampleWait: _ => Thread.Yield());
        var phases = runner.Run(CancellationToken.None);
        var (verdict, reasons) = LabRunner.Judge(phases, 1, runner.Interrupted);
        Assert.Equal(LabResult.Inconclusive, verdict);
        Assert.Contains("95 °C, the 85 °C limit", Assert.Single(reasons));
        Assert.Equal(LabResult.ByTemperature, runner.InterruptedBy);
        Assert.True(phases.Sum(p => p.Seconds) < 60_000);
    }

    [Fact]
    public void A_run_nobody_can_watch_stops_itself()
    {
        var e = new FakeEngine();
        var (clock, sleep) = FakeTime(e);
        var runner = new LabRunner(new LabConfig(LabConfig.Parse("burn", 60_000), SampleMs: 100), e, new TempSampler(() => null), clock, sleep, sampleWait: _ => Thread.Yield());
        var phases = runner.Run(CancellationToken.None);
        Assert.Equal(LabResult.ByTelemetry, runner.InterruptedBy);
        Assert.Equal(LabResult.Inconclusive, LabRunner.Judge(phases, 1, runner.Interrupted).Verdict);
        Assert.True(phases.Sum(p => p.Seconds) < 60_000);
    }
}
