using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using Clocktail.Broker;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using Clocktail.Tests.Domain;

namespace Clocktail.Tests.Broker;

public sealed class BrokerLifetimeTests
{
    [Fact]
    public async Task All_completed_tasks_and_callbacks_permit_disposal_in_order()
    {
        var logs = new ConcurrentQueue<string>();
        using var lifetime = new BrokerLifetime(logs.Enqueue);

        var events = new ConcurrentQueue<string>();
        var loopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var onceFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(lifetime.TryKeepRunning("loop", async ct =>
        {
            loopStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { events.Enqueue("loop-exited"); }
        }));

        Assert.True(lifetime.TryStartAsync("watch", async ct =>
        {
            watchStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { events.Enqueue("watch-exited"); }
        }));

        Assert.True(lifetime.TryStartOnce("once", () =>
        {
            events.Enqueue("once-done");
            onceFinished.TrySetResult();
        }));

        Assert.True(lifetime.TryEnterCallback());
        lifetime.ExitCallback();

        await Task.WhenAll(loopStarted.Task, watchStarted.Task, onceFinished.Task);

        bool disposed = lifetime.ShutdownAndRelease(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            stopWatchers: () => events.Enqueue("watchers-stopped"),
            closeDispatcher: _ => { events.Enqueue("dispatcher-closed"); return true; },
            flushVfHistory: () => events.Enqueue("vf-flushed"),
            disposeResources: () => events.Enqueue("resources-disposed"));

        Assert.True(disposed);
        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.Equal(0, lifetime.ActiveTaskCount);
        Assert.Equal(0, lifetime.ActiveCallbackCount);

        var list = events.ToArray();
        int disposeIndex = Array.IndexOf(list, "resources-disposed");
        Assert.True(disposeIndex >= 0);
        Assert.True(Array.IndexOf(list, "watchers-stopped") < disposeIndex);
        Assert.True(Array.IndexOf(list, "dispatcher-closed") < disposeIndex);
        Assert.True(Array.IndexOf(list, "loop-exited") < disposeIndex);
        Assert.True(Array.IndexOf(list, "watch-exited") < disposeIndex);
        Assert.True(Array.IndexOf(list, "vf-flushed") < disposeIndex);
    }

    [Fact]
    public async Task Blocked_task_timeout_retains_resource_and_completes_boundedly()
    {
        var logs = new ConcurrentQueue<string>();
        var lifetime = new BrokerLifetime(logs.Enqueue);

        using var taskEntered = new ManualResetEventSlim(false);
        using var unblockTask = new ManualResetEventSlim(false);
        bool disposed = false;
        bool watchersStopped = false;
        bool dispatcherClosed = false;
        bool vfFlushed = false;

        try
        {
            Assert.True(lifetime.TryKeepRunning("blocked-native-caller", _ =>
            {
                taskEntered.Set();
                unblockTask.Wait();
                return Task.CompletedTask;
            }));

            Assert.True(taskEntered.Wait(TimeSpan.FromSeconds(5)));

            var sw = Stopwatch.StartNew();
            bool released = lifetime.ShutdownAndRelease(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(80),
                stopWatchers: () => watchersStopped = true,
                closeDispatcher: _ => { dispatcherClosed = true; return true; },
                flushVfHistory: () => vfFlushed = true,
                disposeResources: () => disposed = true);
            sw.Stop();

            Assert.False(released);
            Assert.False(disposed);
            Assert.True(watchersStopped);
            Assert.True(dispatcherClosed);
            Assert.True(vfFlushed);
            Assert.True(lifetime.Token.IsCancellationRequested);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Shutdown took too long: {sw.Elapsed}");
            Assert.False(lifetime.TryStartOnce("late-once", () => { }));
            Assert.Contains(logs, m => m.Contains("retaining loaded GPU libraries", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            unblockTask.Set();
            Assert.True(await lifetime.ShutdownAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, lifetime.ActiveTaskCount);
            lifetime.Dispose();
        }
    }

    [Fact]
    public async Task Blocked_callback_timeout_retains_resource_and_rejects_once()
    {
        var logs = new ConcurrentQueue<string>();
        var lifetime = new BrokerLifetime(logs.Enqueue);

        Assert.True(lifetime.TryEnterCallback());
        bool disposed = false;

        try
        {
            var sw = Stopwatch.StartNew();
            bool released = lifetime.ShutdownAndRelease(
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(80),
                stopWatchers: () => { },
                closeDispatcher: _ => true,
                flushVfHistory: () => { },
                disposeResources: () => disposed = true);
            sw.Stop();

            Assert.False(released);
            Assert.False(disposed);
            Assert.Equal(1, lifetime.ActiveCallbackCount);
            Assert.False(lifetime.TryStartOnce("inside-callback-after-shutdown", () => { }));
            Assert.False(lifetime.TryEnterCallback());
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Shutdown took too long: {sw.Elapsed}");
        }
        finally
        {
            lifetime.ExitCallback();
            Assert.True(await lifetime.ShutdownAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, lifetime.ActiveCallbackCount);
            lifetime.Dispose();
        }
    }

    [Fact]
    public async Task Ongoing_callback_that_completes_within_timeout_permits_disposal_and_blocks_new_once()
    {
        var logs = new ConcurrentQueue<string>();
        using var lifetime = new BrokerLifetime(logs.Enqueue);

        Assert.True(lifetime.TryEnterCallback());
        bool disposed = false;

        var shutdownTask = Task.Run(() => lifetime.ShutdownAndRelease(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            stopWatchers: () => { },
            closeDispatcher: _ => true,
            flushVfHistory: () => { },
            disposeResources: () => disposed = true));

        Assert.True(SpinWait.SpinUntil(() => lifetime.IsShuttingDown, TimeSpan.FromSeconds(2)));
        Assert.False(disposed);
        Assert.False(lifetime.TryStartOnce("during-shutdown", () => { }));

        lifetime.ExitCallback();

        Assert.True(await shutdownTask);
        Assert.True(disposed);
        Assert.Equal(0, lifetime.ActiveCallbackCount);
    }

    [Fact]
    public void Dispatcher_close_timeout_retains_resource_even_when_tracked_tasks_are_quiescent()
    {
        var logs = new ConcurrentQueue<string>();
        using var lifetime = new BrokerLifetime(logs.Enqueue);

        bool disposed = false;
        bool released = lifetime.ShutdownAndRelease(
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(50),
            stopWatchers: () => { },
            closeDispatcher: _ => false,
            flushVfHistory: () => { },
            disposeResources: () => disposed = true);

        Assert.False(released);
        Assert.False(disposed);
        Assert.Contains(logs, m => m.Contains("retaining loaded GPU libraries", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Concurrent_shutdown_and_add_attempts_never_dispose_under_accepted_work()
    {
        for (int round = 0; round < 40; round++)
        {
            var logs = new ConcurrentQueue<string>();
            using var lifetime = new BrokerLifetime(logs.Enqueue);
            int runningWork = 0;
            int executed = 0;
            int disposedWhileActive = 0;
            bool disposed = false;
            using var startGate = new ManualResetEventSlim(false);

            var adders = Enumerable.Range(0, 4).Select(workerId => Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < 40; i++)
                {
                    if (lifetime.TryStartOnce($"once-{workerId}-{i}", () =>
                    {
                        Interlocked.Increment(ref runningWork);
                        Interlocked.Increment(ref executed);
                        Thread.SpinWait(50);
                        Interlocked.Decrement(ref runningWork);
                    }))
                    {
                        // Accepted before shutdown; ShutdownAndRelease must await it before disposing.
                    }

                    if (lifetime.TryEnterCallback())
                    {
                        try
                        {
                            Interlocked.Increment(ref runningWork);
                            Thread.SpinWait(20);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref runningWork);
                            lifetime.ExitCallback();
                        }
                    }
                }
            })).ToArray();

            var shutdownTask = Task.Run(() =>
            {
                startGate.Wait();
                Thread.SpinWait(100);
                return lifetime.ShutdownAndRelease(
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    stopWatchers: () => { },
                    closeDispatcher: _ => true,
                    flushVfHistory: () => { },
                    disposeResources: () =>
                    {
                        if (Volatile.Read(ref runningWork) != 0)
                            Interlocked.Increment(ref disposedWhileActive);
                        disposed = true;
                    });
            });

            startGate.Set();
            await Task.WhenAll([.. adders, shutdownTask]);

            Assert.True(await shutdownTask);
            Assert.True(disposed);
            Assert.Equal(0, disposedWhileActive);
            Assert.Equal(0, Volatile.Read(ref runningWork));
            Assert.Equal(0, lifetime.ActiveTaskCount);
            Assert.Equal(0, lifetime.ActiveCallbackCount);
            Assert.True(executed >= 0);
        }
    }

    [Fact]
    public void Worker_exceptions_are_observed_and_logged_without_unobserved_task_exception()
    {
        var logs = new ConcurrentQueue<string>();
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobserved.Enqueue(e.Exception);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;

        try
        {
            using var lifetime = new BrokerLifetime(logs.Enqueue);

            Assert.True(lifetime.TryStartOnce("faulting-once", () => throw new InvalidOperationException("once-boom")));
            Assert.True(lifetime.TryStartAsync("faulting-watch", _ => Task.FromException(new InvalidOperationException("watch-boom"))));

            int attempts = 0;
            using var retried = new ManualResetEventSlim(false);
            Assert.True(lifetime.TryKeepRunning("retrying-loop", ct =>
            {
                if (Interlocked.Increment(ref attempts) <= 2)
                    throw new InvalidOperationException($"loop-boom-{attempts}");
                retried.Set();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, retryDelay: TimeSpan.FromMilliseconds(10)));

            Assert.True(retried.Wait(TimeSpan.FromSeconds(5)));

            bool released = lifetime.ShutdownAndRelease(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                stopWatchers: () => { },
                closeDispatcher: _ => true,
                flushVfHistory: () => { },
                disposeResources: () => { });

            Assert.True(released);
            Assert.Equal(0, lifetime.ActiveTaskCount);
            Assert.Contains(logs, m => m.Contains("faulting-once failed", StringComparison.Ordinal) && m.Contains("once-boom", StringComparison.Ordinal));
            Assert.Contains(logs, m => m.Contains("faulting-watch failed", StringComparison.Ordinal) && m.Contains("watch-boom", StringComparison.Ordinal));
            Assert.Contains(logs, m => m.Contains("retrying-loop failed, starting it again", StringComparison.Ordinal) && m.Contains("loop-boom-1", StringComparison.Ordinal));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public void Long_running_process_does_not_accumulate_completed_one_shots()
    {
        var logs = new ConcurrentQueue<string>();
        using var lifetime = new BrokerLifetime(logs.Enqueue);
        int completed = 0;

        for (int i = 0; i < 1000; i++)
        {
            int n = i;
            Assert.True(lifetime.TryStartOnce($"shot-{n}", () =>
            {
                Interlocked.Increment(ref completed);
                if (n % 25 == 0) throw new InvalidOperationException("expected-shot-failure");
            }));
        }

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completed) == 1000 && lifetime.ActiveTaskCount == 0, TimeSpan.FromSeconds(10)),
            $"Expected 1000 completed and 0 active tasks, got completed={completed}, active={lifetime.ActiveTaskCount}");
        Assert.Equal(0, lifetime.ActiveTaskCount);
    }

    [Fact]
    public async Task Normal_exit_and_initialization_exception_cleanup_cancel_and_protect_resources()
    {
        // Case A: normal exit without prior CancelKeyPress still cancels token and cleans up.
        {
            var logs = new ConcurrentQueue<string>();
            using var lifetime = new BrokerLifetime(logs.Enqueue);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool loopCanceled = false;
            bool disposed = false;

            Assert.True(lifetime.TryKeepRunning("worker", async ct =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { loopCanceled = true; }
            }));

            await started.Task;

            bool released = lifetime.ShutdownAndRelease(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                stopWatchers: () => { },
                closeDispatcher: _ => true,
                flushVfHistory: () => { },
                disposeResources: () => disposed = true);

            Assert.True(released);
            Assert.True(loopCanceled);
            Assert.True(disposed);
        }

        // Case B: initialization exception after tasks and a callback started, before vfHistory existed, with a blocked worker retains resource.
        {
            var logs = new ConcurrentQueue<string>();
            var lifetime = new BrokerLifetime(logs.Enqueue);
            using var workerStarted = new ManualResetEventSlim(false);
            using var unblockWorker = new ManualResetEventSlim(false);
            bool watchersStopped = false;
            bool disposed = false;
            bool? released = null;

            try
            {
                try
                {
                    Assert.True(lifetime.TryKeepRunning("early-worker", _ =>
                    {
                        workerStarted.Set();
                        unblockWorker.Wait();
                        return Task.CompletedTask;
                    }));
                    Assert.True(workerStarted.Wait(TimeSpan.FromSeconds(5)));
                    Assert.True(lifetime.TryEnterCallback());

                    throw new InvalidOperationException("init-failure");
                }
                finally
                {
                    released = lifetime.ShutdownAndRelease(
                        TimeSpan.FromMilliseconds(30),
                        TimeSpan.FromMilliseconds(60),
                        stopWatchers: () => watchersStopped = true,
                        closeDispatcher: null,
                        flushVfHistory: null,
                        disposeResources: () => disposed = true);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message == "init-failure")
            {
                // Expected propagation after finally cleanup.
            }
            finally
            {
                unblockWorker.Set();
                lifetime.ExitCallback();
                Assert.True(await lifetime.ShutdownAsync(TimeSpan.FromSeconds(5)));
                lifetime.Dispose();
            }

            Assert.False(released);
            Assert.True(watchersStopped);
            Assert.False(disposed);
            Assert.True(lifetime.Token.IsCancellationRequested);
            Assert.False(lifetime.TryStartOnce("post-init-fail-once", () => { }));
        }
    }
}

[Collection("fake-driver")]
public sealed class BrokerShutdownIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-shutdown-int-" + Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public BrokerShutdownIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class Target(OwnershipJournal j, ApplySentinel s, GpuSession g) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => g;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    [Fact]
    public void Fake_dispatcher_close_and_vf_history_flush_integrate_with_lifetime_shutdown()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 1u);
        int e = PowerRequestsLayout.InfoEntry(0);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e + 8), 400_000);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e + 0xc), 575_000);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e + 0x10), 600_000);
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = info;
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);

        var api = FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl],
            [Slot.ClientPwrPoliciesSetControl]);
        var target = new Target(_journal, _sentinel, _gpu);
        var dispatcher = new BrokerDispatcher(new NvClient(api), target, _host, canWrite: true);

        var logs = new ConcurrentQueue<string>();
        var written = new List<VfObservation>();
        var vfHistory = new VfHistoryRecorder(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gpu-A"] = Path.Combine(_dir, "vf") },
            logs.Enqueue,
            (_, batch) => written.AddRange(batch));

        var t0 = DateTimeOffset.UtcNow;
        var flight = new FlightRecorder(FlightRecorder.DefaultKeep);
        var sample = new FlightSample(t0 - TimeSpan.FromSeconds(10), 10, 2700, 900, 300, 60, 95, 0);
        flight.Add(sample);
        vfHistory.Look("gpu-A", sample.At, 900_000, 2700, 10, flight.Freeze());

        using var lifetime = new BrokerLifetime(logs.Enqueue);
        var responder = new DriverResetResponder(dispatcher.Dispatch, target.Ids, logs.Enqueue, () => DateTimeOffset.UtcNow, _ => { });
        Assert.True(lifetime.TryKeepRunning("responder", responder.RunAsync));
        Assert.True(lifetime.TryKeepRunning("vf-writer", ct => vfHistory.RunAsync(() => DateTimeOffset.UtcNow, ct)));

        bool disposed = false;
        bool released = lifetime.ShutdownAndRelease(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            stopWatchers: () => { },
            closeDispatcher: wait => dispatcher.Close(wait),
            flushVfHistory: () => vfHistory.Shutdown(t0, _ => flight.Freeze()),
            disposeResources: () => disposed = true);

        Assert.True(released);
        Assert.True(disposed);
        Assert.Single(written);

        // Dispatcher was closed: subsequent write attempts immediately return WindowBusy without touching driver.
        var afterClose = dispatcher.Dispatch(new BrokerRequest(BrokerRequest.OpPower, "gpu-A", Value: "450000"));
        Assert.False(afterClose.Ok);
        Assert.Equal(BrokerCode.WindowBusy, afterClose.Code);
    }

    [Fact]
    public async Task Blocked_write_in_real_dispatcher_close_retains_resource_boundedly()
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 1u);
        int e = PowerRequestsLayout.InfoEntry(0);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e + 8), 400_000);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e + 0xc), 575_000);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(e + 0x10), 600_000);
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = info;
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);

        var api = FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl],
            [Slot.ClientPwrPoliciesSetControl]);
        var target = new Target(_journal, _sentinel, _gpu);

        using var writeEntered = new ManualResetEventSlim(false);
        using var releaseWrite = new ManualResetEventSlim(false);
        var dispatcher = new BrokerDispatcher(new NvClient(api), target, _host, canWrite: true, judgeBusyReason: () =>
        {
            writeEntered.Set();
            releaseWrite.Wait();
            return "blocked-by-test";
        });

        var logs = new ConcurrentQueue<string>();
        var lifetime = new BrokerLifetime(logs.Enqueue);
        bool disposed = false;

        try
        {
            Assert.True(lifetime.TryStartOnce("blocked-write", () =>
            {
                dispatcher.Dispatch(new BrokerRequest(BrokerRequest.OpPower, "gpu-A", Value: "450000"));
            }));

            Assert.True(writeEntered.Wait(TimeSpan.FromSeconds(5)));

            var sw = Stopwatch.StartNew();
            bool released = lifetime.ShutdownAndRelease(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(50),
                stopWatchers: () => { },
                closeDispatcher: wait => dispatcher.Close(wait),
                flushVfHistory: () => { },
                disposeResources: () => disposed = true);
            sw.Stop();

            Assert.False(released);
            Assert.False(disposed);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Bounded shutdown took {sw.Elapsed}");
        }
        finally
        {
            releaseWrite.Set();
            Assert.True(await lifetime.ShutdownAsync(TimeSpan.FromSeconds(5)));
            lifetime.Dispose();
        }
    }
}
