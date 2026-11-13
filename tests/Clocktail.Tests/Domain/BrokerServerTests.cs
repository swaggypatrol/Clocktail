using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// Regression tests for <see cref="BrokerServer"/> pipe robustness (audit item 5):
/// malformed JSON responses, disconnect handling, creation retry throttling and cancellation,
/// bounded response-write timeouts, and observation of stalled background task exceptions.
/// </summary>
[Collection("fake-driver")]
public sealed class BrokerServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();

    public BrokerServerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Target(OwnershipJournal j, ApplySentinel s) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session { get; } = new(1, "fake");
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    private BrokerDispatcher Dispatcher()
    {
        FakeDriver.Reset();
        var hw = new NvClient(FakeDriver.ApiFor([]));
        var t = new Target(
            new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A"),
            new ApplySentinel(Path.Combine(_dir, "apply.lock")));
        return new BrokerDispatcher(hw, t, _host, canWrite: true);
    }

    private static NamedPipeServerStream NewPipe(string name, int maxInstances = 4, int outBufferSize = 0) =>
        new(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, outBufferSize);

    private static void WriteFrame(Stream s, byte[] payload, int? declaredLength = null)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, declaredLength ?? payload.Length);
        s.Write(len);
        if (payload.Length > 0) s.Write(payload);
        s.Flush();
    }

    private static async Task<List<Exception>> CaptureUnobservedWhileAsync(Func<Task> action)
    {
        var unobserved = new List<Exception>();
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved) unobserved.Add(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await action();
            await Task.Delay(50);
            // Encourage finalization of any completed/abandoned internal Task instances.
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
        return unobserved;
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"Op\":\"ping\",\"Mw\":\"not_a_number\"}")]
    public async Task Malformed_or_invalid_json_request_gets_bad_request_without_unobserved_exceptions(string json)
    {
        var unobserved = await CaptureUnobservedWhileAsync(async () =>
        {
            string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
            var server = new BrokerServer(() => NewPipe(name), Dispatcher());
            using var cts = new CancellationTokenSource();
            var loop = server.RunAsync(cts.Token);

            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(2000);
                WriteFrame(pipe, Encoding.UTF8.GetBytes(json));

                var resp = BrokerTransport.ReadResponse(pipe);
                Assert.False(resp.Ok);
                Assert.Equal(BrokerCode.BadRequest, resp.Code);
                Assert.False(string.IsNullOrWhiteSpace(resp.Error));
            }

            // Server continues serving subsequent valid requests cleanly.
            var ping = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), name));
            Assert.NotNull(ping);
            Assert.True(ping!.Ok);

            await cts.CancelAsync();
            await loop;
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task Oversized_frame_length_gets_bad_request_without_faulting_serve_task()
    {
        string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
        var server = new BrokerServer(() => NewPipe(name), Dispatcher());
        var serving = server.ServeOneAsync(CancellationToken.None);

        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        WriteFrame(pipe, [], declaredLength: BrokerTransport.MaxMessageBytes + 1);

        var resp = BrokerTransport.ReadResponse(pipe);
        Assert.False(resp.Ok);
        Assert.Equal(BrokerCode.BadRequest, resp.Code);
        Assert.Contains("out of range", resp.Error);

        await serving;
    }

    [Fact]
    public async Task Client_disconnect_mid_frame_and_after_invalid_json_does_not_fault_server()
    {
        var unobserved = await CaptureUnobservedWhileAsync(async () =>
        {
            string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
            var server = new BrokerServer(() => NewPipe(name), Dispatcher());
            using var cts = new CancellationTokenSource();
            var loop = server.RunAsync(cts.Token);

            // 1. Connect and immediately close without sending anything.
            using (var empty = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                await empty.ConnectAsync(2000);

            // 2. Send partial frame body and close mid-message.
            using (var truncated = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await truncated.ConnectAsync(2000);
                WriteFrame(truncated, Encoding.UTF8.GetBytes("{\"Op\":"), declaredLength: 64);
            }

            // 3. Send malformed JSON and immediately close before reading the bad_request response.
            using (var hitAndRun = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await hitAndRun.ConnectAsync(2000);
                WriteFrame(hitAndRun, Encoding.UTF8.GetBytes("{bad"));
            }

            // Server remains healthy.
            var ping = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), name));
            Assert.NotNull(ping);
            Assert.True(ping!.Ok);

            await cts.CancelAsync();
            await loop;
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task Creation_io_exception_retries_are_throttled_and_recover()
    {
        string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
        int attempts = 0;
        var server = new BrokerServer(
            () =>
            {
                int n = Interlocked.Increment(ref attempts);
                if (n <= 3) throw new IOException("simulated pipe creation failure");
                return NewPipe(name);
            },
            Dispatcher(),
            retryDelay: TimeSpan.FromMilliseconds(60));

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var loop = server.RunAsync(cts.Token);

        var ping = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), name, connectMs: 3000));
        sw.Stop();

        Assert.NotNull(ping);
        Assert.True(ping!.Ok);
        Assert.True(attempts >= 4);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(120), $"expected throttled retries, took only {sw.Elapsed}");

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task Creation_io_exception_retry_delay_is_throttled_and_cancellable()
    {
        int attempts = 0;
        var server = new BrokerServer(
            () =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("always failing");
            },
            Dispatcher(),
            retryDelay: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var sw = Stopwatch.StartNew();
        await server.RunAsync(cts.Token);
        sw.Stop();

        // Should cancel cleanly out of the 5 s retry delay without busy spinning.
        Assert.InRange(attempts, 1, 3);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cancellation took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task Slow_non_reading_client_times_out_response_write_without_blocking_other_clients()
    {
        var unobserved = await CaptureUnobservedWhileAsync(async () =>
        {
            string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
            var server = new BrokerServer(
                () => NewPipe(name, NamedPipeServerStream.MaxAllowedServerInstances, outBufferSize: 16),
                Dispatcher(),
                requestTimeout: TimeSpan.FromMilliseconds(500),
                responseTimeout: TimeSpan.FromMilliseconds(200));

            using var cts = new CancellationTokenSource();
            var loop = server.RunAsync(cts.Token);

            // Client sends a large unknown-op request (> pipe buffer) and never reads the response.
            using var nonReader = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await nonReader.ConnectAsync(2000);
            BrokerTransport.WriteRequest(nonReader, new BrokerRequest(new string('x', 256 * 1024)));

            // Concurrent normal client is not held up by the non-reading client.
            var sw = Stopwatch.StartNew();
            var ping = await Task.Run(() => BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), name));
            Assert.NotNull(ping);
            Assert.True(ping!.Ok);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"ping waited {sw.Elapsed}");

            // Wait past responseTimeout so the stalled write times out and tears down while test runs.
            await Task.Delay(350);

            await cts.CancelAsync();
            await loop;
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task Cancelling_server_disposes_stalled_connections_promptly_without_unobserved_exceptions()
    {
        var unobserved = await CaptureUnobservedWhileAsync(async () =>
        {
            string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
            var server = new BrokerServer(
                () => NewPipe(name),
                Dispatcher(),
                requestTimeout: TimeSpan.FromSeconds(30),
                responseTimeout: TimeSpan.FromSeconds(30));

            using var cts = new CancellationTokenSource();
            var loop = server.RunAsync(cts.Token);

            using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await silent.ConnectAsync(2000);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            await loop;
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"shutdown waited for stalled client: {sw.Elapsed}");
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task ServeOneAsync_times_out_stalled_response_write_and_observes_write_task_fault()
    {
        var unobserved = await CaptureUnobservedWhileAsync(async () =>
        {
            string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
            var server = new BrokerServer(
                () => NewPipe(name, outBufferSize: 16),
                Dispatcher(),
                requestTimeout: TimeSpan.FromSeconds(2),
                responseTimeout: TimeSpan.FromMilliseconds(200));

            var sw = Stopwatch.StartNew();
            var serving = server.ServeOneAsync(CancellationToken.None);

            using var nonReader = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await nonReader.ConnectAsync(2000);
            BrokerTransport.WriteRequest(nonReader, new BrokerRequest(new string('x', 256 * 1024)));

            await serving;
            sw.Stop();

            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task ServeOneAsync_cancellation_after_connection_stops_stalled_read_and_disposes_pipe()
    {
        var unobserved = await CaptureUnobservedWhileAsync(async () =>
        {
            string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
            NamedPipeServerStream? created = null;
            var server = new BrokerServer(
                () => created = NewPipe(name),
                Dispatcher(),
                requestTimeout: TimeSpan.FromSeconds(30));

            using var cts = new CancellationTokenSource();
            var serving = server.ServeOneAsync(cts.Token);

            using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await silent.ConnectAsync(2000);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            await serving;
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"ServeOneAsync cancellation waited: {sw.Elapsed}");
            Assert.NotNull(created);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => created!.WaitForConnectionAsync());
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task Unexpected_listener_failure_still_drains_accepted_connections()
    {
        string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
        int attempts = 0;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new BrokerServer(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return NewPipe(name);
            failed.TrySetResult();
            throw new UnauthorizedAccessException("simulated listener failure");
        }, Dispatcher(), requestTimeout: TimeSpan.FromMilliseconds(500));
        var loop = server.RunAsync(CancellationToken.None);
        using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await silent.ConnectAsync(2000);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(loop.IsCompleted); // accepted request remains tracked despite the listener failure
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => loop.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ServeOneAsync_cancellation_before_connection_disposes_listening_pipe()
    {
        string name = "clocktail-pipe-" + Guid.NewGuid().ToString("n");
        NamedPipeServerStream? created = null;
        var server = new BrokerServer(
            () => created = NewPipe(name),
            Dispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ServeOneAsync(cts.Token));

        Assert.NotNull(created);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => created!.WaitForConnectionAsync());
    }
}
