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
        new(mhz, verdict, gbps, lat, mem, 60, 62, 1, null, 0, 0, null, verdict == LabResult.Pass ? null : "a bad word", "P0", 22, (ulong?)othersMib,
            new LabResult("", "", 0, 0, default, default, [], verdict, [], 22, (ulong?)othersMib).Shared);

    [Fact]
    public void The_sweep_goes_on_while_each_step_is_faster()
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, 1510, 498, 13901) };
        Assert.Null(MemorySweep.Signal(steps));
        Assert.Equal(100, MemorySweep.Best(steps));
    }

    [Theory]
    [InlineData(1490, 498, 13901, LabResult.Pass, null, "bandwidth fell")]
    [InlineData(1520, 510, 13901, LabResult.Pass, null, "latency rose")]
    [InlineData(1510, 498, 13851, LabResult.Pass, null, "did not follow the offset")]
    [InlineData(1510, 498, 13901, LabResult.Fail, null, "a bad word")]
    [InlineData(1510, 498, 13901, LabResult.Pass, 30000.0, "other work shared the GPU")]
    public void A_slower_failing_or_unjudgeable_step_stops_the_sweep_and_the_best_is_the_one_before(double gbps, double lat, double mem, string verdict, double? othersMib, string why)
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1505, 499, 13851), S(100, gbps, lat, mem, verdict, othersMib) };
        Assert.Contains(why, MemorySweep.Signal(steps));
        Assert.Equal(50, MemorySweep.Best(steps));
    }

    /// <summary>The reference card's +0/+179/+358: bandwidth flat (the crossbar sets it), latency down. A healthy sweep goes on.</summary>
    [Fact]
    public void Flat_bandwidth_with_falling_latency_goes_on()
    {
        var steps = new List<SweepStep> { S(0, 1437, 531, 13801), S(179, 1440, 524, 13980), S(358, 1439, 518, 14159) };
        Assert.Null(MemorySweep.Signal(steps));
        Assert.Equal(358, MemorySweep.Best(steps));
    }

    /// <summary>Slower than the best step, not just the one before: a slow drift over two steps stops it too.</summary>
    [Fact]
    public void Latency_drifting_up_from_the_lowest_stops_the_sweep()
    {
        var steps = new List<SweepStep> { S(0, 1500, 500, 13801), S(50, 1500, 498, 13851), S(100, 1500, 501, 13901), S(150, 1500, 503.5, 13951) };
        Assert.Contains("over the lowest step's 498", MemorySweep.Signal(steps));
        Assert.Equal(100, MemorySweep.Best(steps));
    }
}
