using Clocktail.Broker;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Broker;

/// <summary>The broker's always-on V/F history recorder, driven by fake flight-recorder looks and a fake writer: what
/// is kept, a hang's looks withheld, a bounded queue, failures retried and never passed to the sampler, a clean stop.</summary>
public class VfHistoryRecorderTests
{
    private const string Gpu = "GPU-0";
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public readonly List<string> Log = [];
        public readonly List<VfObservation> Written = [];
        public int Calls;
        public Func<int, Exception?> Fail = _ => null;
        public readonly FlightRecorder Flight = new(FlightRecorder.DefaultKeep);
        public readonly VfHistoryRecorder Recorder;
        public DateTimeOffset Now = T0;

        public Rig()
        {
            Recorder = new VfHistoryRecorder(new Dictionary<string, string> { [Gpu] = "unused" }, Log.Add, (_, batch) =>
            {
                if (Fail(++Calls) is { } e) throw e;
                Written.AddRange(batch);
            });
        }

        /// <summary>One look, as the sampler hands it over: into the ring, then to the recorder with the window.</summary>
        public void Look(uint? uv, uint? mhz, double watts = 400, uint util = 100, int readMs = 20)
        {
            Flight.Add(new FlightSample(Now, readMs, mhz, uv / 1000, watts, 60, util, 0));
            Recorder.Look(Gpu, Now, uv, mhz, readMs, Flight.Freeze());
            Now = Now.AddMilliseconds(250);
        }

        /// <summary>Healthy load: the clock moves every look, so nothing reads as frozen.</summary>
        public void Healthy(double seconds, uint uv = 900_000)
        {
            for (int i = 0; i < seconds * 4; i++) Look(uv + (uint)(i % 3) * 5_000, 2800 + (uint)(i % 5) * 15);
        }

        /// <summary>Looks with nothing admitted (idle under the gate) for the quarantine to pass.</summary>
        public void Idle(double seconds)
        {
            for (int i = 0; i < seconds * 4; i++) Look(700_000, 600 + (uint)(i % 2), watts: 30, util: 2);
        }
    }

    [Fact]
    public void Only_looks_strictly_above_825_mV_are_kept_with_their_measured_voltage_and_clock()
    {
        var rig = new Rig();
        rig.Look(825_000, 2500);   // equal: not kept
        rig.Look(825_001, 2501);
        rig.Look(912_345, 2617);   // µV as measured, not 912 mV
        rig.Look(null, 2600);      // the rail did not answer
        rig.Look(950_000, null);   // NVML did not answer
        rig.Look(700_000, 1800);   // light, under the gate
        rig.Idle(61);
        Assert.True(rig.Recorder.FlushOnce(rig.Now));
        Assert.Equal([(825_001u, 2501u), (912_345u, 2617u)], rig.Written.Select(o => (o.Uv, o.Mhz)));
        Assert.All(rig.Written, o => Assert.Equal(VfSource.Lightweight, o.Source));
        Assert.All(rig.Written, o => { Assert.Null(o.UtilPercent); Assert.Null(o.Watts); });
    }

    [Fact]
    public void A_blocked_read_is_not_kept()
    {
        var rig = new Rig();
        rig.Look(900_000, 2500, readMs: FlightRecorder.BlockedMs);
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.Empty(rig.Written);
    }

    [Fact]
    public void Nothing_is_written_until_it_has_left_the_flight_window()
    {
        var rig = new Rig();
        rig.Healthy(10);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.Empty(rig.Written);
        Assert.Equal(40, rig.Recorder.Status.Held);
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.Equal(40, rig.Written.Count);
        Assert.Equal(40, rig.Recorder.Status.Written);
    }

    [Fact]
    public void A_frozen_hang_tail_is_withheld_and_the_healthy_looks_before_it_kept()
    {
        var rig = new Rig();
        rig.Healthy(40);
        var frozenFrom = rig.Now;
        for (int i = 0; i < 20; i++) rig.Look(1_030_000, 3105, watts: 140);   // 5 s frozen at the top, power fallen into it
        Assert.NotNull(FlightRecorder.HungNow(rig.Flight.Freeze(), rig.Now.AddMilliseconds(-250)));
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.Equal(160, rig.Written.Count);
        Assert.All(rig.Written, o => Assert.True(o.At < frozenFrom));
        Assert.Equal(20, rig.Recorder.Status.Withheld);
    }

    [Fact]
    public void A_driver_reset_withholds_the_looks_of_the_hang_it_ends()
    {
        var rig = new Rig();
        rig.Healthy(30);                     // T0 … T0+29.75 s
        var seen = rig.Now;                  // no stuck look in the window: the crash trace's own 2 s before the reset
        rig.Recorder.Reset(Gpu, rig.Flight.Freeze(), seen);
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.Equal(112, rig.Written.Count);
        Assert.All(rig.Written, o => Assert.True(o.At < seen - FlightRecorder.HangDetectAtLeast));
    }

    [Fact]
    public void A_hang_frozen_for_longer_than_the_window_stays_withheld_until_the_readings_move_again()
    {
        // The 0.8.7 review: 10 s healthy, then 150 s frozen. Once the power fall left the 60 s window, the window was frozen
        // from its first look, read as a steady load, and 352 frozen looks (88 s) went into the history.
        var rig = new Rig();
        rig.Healthy(10);
        var frozenFrom = rig.Now;
        for (int i = 0; i < 150 * 4; i++) rig.Look(1_030_000, 3105, watts: 140);
        Assert.Null(FlightRecorder.HungNow(rig.Flight.Freeze(), rig.Now.AddMilliseconds(-250)));   // the window alone no longer says so
        var movedAt = rig.Now;
        rig.Healthy(5);                       // the readings move again: healthy looks count from here
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.DoesNotContain(rig.Written, o => o.At >= frozenFrom && o.At < movedAt);
        Assert.Equal(40, rig.Written.Count(o => o.At < frozenFrom));
        Assert.Equal(20, rig.Written.Count(o => o.At >= movedAt));
        Assert.Equal(600, rig.Recorder.Status.Withheld);
    }

    [Fact]
    public void A_reset_after_a_long_frozen_hang_withholds_from_where_the_hang_began()
    {
        var rig = new Rig();
        rig.Healthy(10);
        var frozenFrom = rig.Now;
        for (int i = 0; i < 90 * 4; i++) rig.Look(1_030_000, 3105, watts: 140);
        rig.Recorder.Reset(Gpu, rig.Flight.Freeze(), rig.Now.AddMilliseconds(-100));   // seen just after the last frozen look
        rig.Healthy(5);
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.DoesNotContain(rig.Written, o => o.At >= frozenFrom && o.At < frozenFrom.AddSeconds(90));
        Assert.Equal(60, rig.Written.Count);
    }

    [Fact]
    public void A_look_read_before_a_reset_and_handed_over_after_it_is_withheld()
    {
        // The sampler takes a look's time, reads it, the reset is seen meanwhile, and only then is the look handed over.
        var rig = new Rig();
        rig.Healthy(30);
        var lookAt = rig.Now;
        var seen = lookAt.AddMilliseconds(100);
        rig.Recorder.Reset(Gpu, rig.Flight.Freeze(), seen);
        rig.Flight.Add(new FlightSample(lookAt, 20, 2800, 900, 400, 60, 100, 0));
        rig.Recorder.Look(Gpu, lookAt, 900_000, 2800, 20, rig.Flight.Freeze());
        rig.Now = lookAt.AddMilliseconds(250);
        rig.Healthy(2);                       // after the reset: kept
        rig.Idle(61);
        rig.Recorder.FlushOnce(rig.Now);
        Assert.DoesNotContain(rig.Written, o => o.At >= seen - FlightRecorder.HangDetectAtLeast && o.At <= seen);
        Assert.Equal(8, rig.Written.Count(o => o.At > seen));
    }

    [Fact]
    public void A_failing_disk_keeps_the_batch_retries_later_and_logs_once_each_way()
    {
        var rig = new Rig { Fail = n => n <= 3 ? new IOException("disk full") : null };
        rig.Healthy(5);
        rig.Idle(61);
        Assert.False(rig.Recorder.FlushOnce(rig.Now));
        Assert.True(rig.Recorder.Status.Degraded);
        Assert.Contains("disk full", rig.Recorder.Status.Error);
        Assert.False(rig.Recorder.FlushOnce(rig.Now.AddMilliseconds(500)));   // backing off: not even tried
        Assert.Equal(1, rig.Calls);
        Assert.False(rig.Recorder.FlushOnce(rig.Now.AddSeconds(10)));
        Assert.False(rig.Recorder.FlushOnce(rig.Now.AddSeconds(60)));
        Assert.True(rig.Recorder.FlushOnce(rig.Now.AddSeconds(200)));
        Assert.Equal(20, rig.Written.Count);                                  // each look once, in order
        Assert.Equal(rig.Written.OrderBy(o => o.At), rig.Written);
        Assert.False(rig.Recorder.Status.Degraded);
        Assert.Single(rig.Log, l => l.Contains("failed"));
        Assert.Single(rig.Log, l => l.Contains("writing again"));
    }

    [Fact]
    public void An_unforeseen_writer_exception_is_the_recorders_state_not_the_samplers()
    {
        var rig = new Rig { Fail = _ => new InvalidOperationException("boom") };
        rig.Healthy(2);
        rig.Idle(61);
        var flush = Record.Exception(() => rig.Recorder.FlushOnce(rig.Now));
        Assert.Null(flush);
        Assert.True(rig.Recorder.Status.Degraded);
        Assert.Null(Record.Exception(() => rig.Recorder.Look("no-such-gpu", rig.Now, 900_000, 2500, 10, null)));
        Assert.Null(Record.Exception(() => rig.Recorder.Look(Gpu, rig.Now, 900_000, 2500, 10, [])));
    }

    [Fact]
    public void The_queue_is_bounded_and_what_it_drops_is_counted_once_in_the_log()
    {
        var log = new List<string>();
        var rec = new VfHistoryRecorder(new Dictionary<string, string> { [Gpu] = "unused" }, log.Add, (_, _) => throw new UnauthorizedAccessException("denied"));
        var at = T0;
        int n = VfHistoryRecorder.MaxQueuedPerGpu + 2000;
        for (int i = 0; i < n; i++, at = at.AddMilliseconds(250))
        {
            rec.Look(Gpu, at, 900_000, 2500, 10, null);
            if (i % 1000 == 0) rec.FlushOnce(at, force: true);
        }
        var s = rec.Status;
        Assert.True(s.Queued <= VfHistoryRecorder.MaxQueuedPerGpu);
        Assert.True(s.Held <= 4 * 60 + 1);
        Assert.True(s.Dropped > 0);
        Assert.Equal(n, s.Queued + s.Held + s.Dropped);
        Assert.Single(log, l => l.Contains("dropped"));
        Assert.Single(log, l => l.Contains("failed"));
    }

    [Fact]
    public void A_clean_stop_writes_the_held_looks_but_not_the_last_seconds_a_hang_could_still_be_in()
    {
        var rig = new Rig();
        rig.Healthy(20);
        var stop = rig.Now;
        rig.Recorder.Shutdown(stop, _ => rig.Flight.Freeze());
        Assert.Equal(72, rig.Written.Count);   // 20 s of looks less the last 2 s
        Assert.All(rig.Written, o => Assert.True(o.At < stop - FlightRecorder.HangDetectAtLeast));
        rig.Look(900_000, 2500);                // nothing more is taken
        rig.Recorder.Shutdown(rig.Now, _ => null);
        Assert.Equal(72, rig.Written.Count);
    }

    [Fact]
    public void The_status_goes_out_in_the_brokers_snake_case()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Rig().Recorder.Status.Wire(), Clocktail.Domain.Broker.BrokerResponse.Json);
        Assert.Contains("\"written\"", json);
        Assert.Contains("\"min_uv_exclusive\": 825000", json);
        Assert.DoesNotContain("\"Written\"", json);
    }

    [Fact]
    public async Task The_writer_loop_stops_when_asked()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var loop = rig.Recorder.RunAsync(() => rig.Now, cts.Token);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.IsCompletedSuccessfully);
    }

    [Fact]
    public void The_recorder_writes_real_day_files_the_reader_reads_back()
    {
        string dir = Path.Combine(Path.GetTempPath(), "clocktail-vfrec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rec = new VfHistoryRecorder(new Dictionary<string, string> { [Gpu] = dir }, _ => { });
            var at = T0;
            for (int i = 0; i < 4 * 70; i++, at = at.AddMilliseconds(250)) rec.Look(Gpu, at, i < 40 ? 901_234u : 800_000u, 2700, 10, null);
            Assert.True(rec.FlushOnce(at));
            var back = VfHistoryStore.ReadDay(dir, new DateOnly(2026, 11, 1));
            Assert.Equal(40, back.Count);
            Assert.All(back, o => Assert.Equal(901_234u, o.Uv));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
