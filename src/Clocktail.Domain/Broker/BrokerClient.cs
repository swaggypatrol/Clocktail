using System.IO.Pipes;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The unprivileged side: connect to the broker's pipe, send one request, read one response. A null return
/// means the broker is not reachable (not installed, not started), which is how the CLI learns to fall back to
/// running the op itself if it happens to be elevated, or to telling the user the broker is needed.
/// </summary>
public static class BrokerClient
{
    /// <summary>
    /// How long an answer may take once the request is sent: a write waits up to the window's 30 s for another one, and a
    /// call stuck in a hung driver waits out its TDR delay (30 s on the reference machine) before it fails. Past this the
    /// broker is taken for stuck and the caller is told so (<see cref="TimeoutException"/>), never left waiting forever.
    /// </summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(120);

    /// <exception cref="TimeoutException">The request went out and no answer came within <paramref name="answerWait"/>
    /// (default <see cref="AnswerWait"/>): whether it was carried out is unknown.</exception>
    /// <exception cref="IOException">The pipe broke after the request went out: whether it was carried out is unknown.</exception>
    public static BrokerResponse? TryCall(BrokerRequest req, string pipeName = BrokerTransport.PipeName, int connectMs = 2000, TimeSpan? answerWait = null)
    {
        var wait = answerWait ?? AnswerWait;
        // Validate before connecting: invalid arguments must never send a potentially mutating request.
        if (connectMs < 0) throw new ArgumentOutOfRangeException(nameof(connectMs));
        if (wait <= TimeSpan.Zero || wait.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(answerWait), "the answer wait must be a positive, finite supported timeout");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            try { pipe.Connect(connectMs); }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            // The write is under the same clock: a pipe's flush waits until the other end has read the request, so a
            // broker stuck before reading would hold the writer as long as one stuck before answering.
            var exchange = Task.Run(() =>
            {
                BrokerTransport.WriteRequest(pipe, req);
                return BrokerTransport.ReadResponse(pipe);
            });
            // Observe faults even if the timed wait finishes first and disposal aborts the I/O later.
            _ = exchange.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { return exchange.WaitAsync(wait).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"the Clocktail service took longer than {wait.TotalSeconds:0} s to answer");
            }
        }
        finally { pipe.Dispose(); }
    }

    /// <summary>Is a broker up and answering on this machine? A short-timeout ping.</summary>
    public static bool IsPresent(string pipeName = BrokerTransport.PipeName)
        => TryCall(new BrokerRequest(BrokerRequest.OpPing), pipeName, connectMs: 500) is { Ok: true };
}
