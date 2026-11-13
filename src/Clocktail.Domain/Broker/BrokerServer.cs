using System.IO.Pipes;
using System.Text.Json;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The broker's accept loop. It does not create the pipe itself: the caller passes a factory, because the only
/// part that differs between the real broker and a test is the pipe's security. On Windows the service hands in
/// a stream whose ACL admits just the installing user and LocalSystem; a test hands in a plain stream.
///
/// Every connection is served on its own task, and the next pipe instance is listening before the current one is
/// answered. Serving one at a time looked simpler but made every client hostage to the slowest one: a client that
/// connected and never sent its request (or a write waiting on the driver) left a read-only `ownership` query from
/// the UI hanging with no answer. Concurrency is safe because the dispatcher already serializes what must be
/// serialized (one write window); reads touch only files written atomically. A client gets
/// <see cref="RequestTimeout"/> to send its request and <see cref="ResponseTimeout"/> to receive the answer,
/// after which the connection is dropped.
/// </summary>
public sealed class BrokerServer
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Func<NamedPipeServerStream> _listen;
    private readonly BrokerDispatcher _dispatcher;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _retryDelay;

    public BrokerServer(
        Func<NamedPipeServerStream> listen,
        BrokerDispatcher dispatcher,
        TimeSpan? requestTimeout = null,
        TimeSpan? responseTimeout = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _listen = listen;
        _dispatcher = dispatcher;
        _requestTimeout = requestTimeout ?? RequestTimeout;
        _responseTimeout = responseTimeout ?? ResponseTimeout;
        _retryDelay = retryDelay ?? RetryDelay;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_requestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_responseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(_retryDelay, TimeSpan.Zero);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var inFlight = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = _listen();
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe?.Dispose();
                    break;
                }
                catch (IOException)
                {
                    pipe?.Dispose();
                    if (!await DelayRetryAsync(ct).ConfigureAwait(false)) break;
                    continue;
                }
                catch
                {
                    pipe?.Dispose();
                    throw;
                }
                inFlight.RemoveAll(t => t.IsCompleted);
                var served = Task.Run(() => ServeAsync(pipe, ct));
                ObserveFault(served);
                inFlight.Add(served);
            }
        }
        finally
        {
            // The host tracks this task before unloading native libraries. Never abandon an active
            // dispatch (including read-only GPU queries) if the next listener fails unexpectedly.
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
    }

    /// <summary>Accept and answer exactly one connection (tests use it to drive the loop one step at a time).</summary>
    public async Task ServeOneAsync(CancellationToken ct)
    {
        var pipe = _listen();
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
        await Task.Run(() => ServeAsync(pipe, ct)).ConfigureAwait(false);
    }

    private async Task<bool> DelayRetryAsync(CancellationToken ct)
    {
        if (_retryDelay == TimeSpan.Zero) return !ct.IsCancellationRequested;
        try
        {
            await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                BrokerRequest req;
                try
                {
                    var read = Task.Run(() => BrokerTransport.ReadRequest(pipe));
                    ObserveFault(read);
                    req = await read.WaitAsync(_requestTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception bad) when (bad is InvalidDataException or JsonException)
                {
                    await WriteResponseAsync(pipe, new BrokerResponse(false, bad.Message, Code: BrokerCode.BadRequest), ct).ConfigureAwait(false);
                    return;
                }

                BrokerResponse resp;
                try { resp = _dispatcher.FromClient(req); }
                catch (Exception e) { resp = new BrokerResponse(false, e.Message); }   // a dispatch bug answers the client, never kills the broker
                await WriteResponseAsync(pipe, resp, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException) { }
            finally
            {
                if (pipe.IsConnected) try { pipe.Disconnect(); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task WriteResponseAsync(NamedPipeServerStream pipe, BrokerResponse resp, CancellationToken ct)
    {
        try
        {
            var write = Task.Run(() => BrokerTransport.WriteResponse(pipe, resp));
            ObserveFault(write);
            await write.WaitAsync(_responseTimeout, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException bad)
        {
            var fallback = Task.Run(() => BrokerTransport.WriteResponse(pipe, new BrokerResponse(false, bad.Message, Code: BrokerCode.Failed)));
            ObserveFault(fallback);
            await fallback.WaitAsync(_responseTimeout, ct).ConfigureAwait(false);
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
