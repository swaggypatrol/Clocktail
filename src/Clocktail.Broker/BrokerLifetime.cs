namespace Clocktail.Broker;

/// <summary>
/// Tracks background worker tasks and event-log callbacks for a broker run so shutdown cancels all work,
/// prevents new one-shot scheduling, waits boundedly for active work, observes exceptions, and only permits
/// native GPU library disposal once every tracked task, callback, and write window is quiescent.
/// </summary>
public sealed class BrokerLifetime : IDisposable
{
    public static readonly TimeSpan DefaultCloseWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultDrainWait = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly HashSet<Task> _activeTasks = [];
    private readonly Action<string> _log;
    private int _activeCallbacks;
    private TaskCompletionSource? _callbacksDrainedTcs;
    private bool _shuttingDown;
    private bool _disposed;

    public BrokerLifetime(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _token = _cts.Token;
    }

    public CancellationToken Token => _token;

    public bool IsShuttingDown
    {
        get { lock (_gate) return _shuttingDown; }
    }

    public int ActiveTaskCount
    {
        get { lock (_gate) return _activeTasks.Count; }
    }

    public int ActiveCallbackCount
    {
        get { lock (_gate) return _activeCallbacks; }
    }

    public bool IsQuiescent
    {
        get { lock (_gate) return _activeTasks.Count == 0 && _activeCallbacks == 0; }
    }

    /// <summary>
    /// Transitions to shutting down (rejecting new work and callbacks) and cancels <see cref="Token"/>.
    /// Idempotent and thread-safe.
    /// </summary>
    public void BeginShutdown()
    {
        lock (_gate)
        {
            if (_disposed || _shuttingDown) return;
            _shuttingDown = true;
        }
        CancelTokenSafe();
    }

    /// <summary>
    /// Schedules and tracks a one-shot background action unless shutdown has already started.
    /// Completed tasks are pruned immediately so long-running processes do not retain them.
    /// </summary>
    public bool TryStartOnce(string name, Action work)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);

        Task? tracked = null;
        lock (_gate)
        {
            if (_shuttingDown || _disposed || _cts.IsCancellationRequested) return false;
            tracked = Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    LogSafe($"{name} failed: {e.GetBaseException()}");
                }
                finally
                {
                    // Read the captured task only after its assignment/add under this same gate.
                    lock (_gate) _activeTasks.Remove(tracked!);
                }
            });
            _activeTasks.Add(tracked);
        }
        return true;
    }

    /// <summary>
    /// Starts and tracks an async background task bound to <see cref="Token"/> unless shutdown has already started.
    /// Exceptions are logged and observed; completed tasks are removed from tracking immediately.
    /// </summary>
    public bool TryStartAsync(string name, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);

        Task? tracked = null;
        lock (_gate)
        {
            if (_shuttingDown || _disposed || _cts.IsCancellationRequested) return false;
            var ct = _cts.Token;
            tracked = Task.Run(async () =>
            {
                try
                {
                    await work(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Normal shutdown cancellation.
                }
                catch (Exception e)
                {
                    LogSafe($"{name} failed: {e.GetBaseException()}");
                }
                finally
                {
                    lock (_gate) _activeTasks.Remove(tracked!);
                }
            });
            _activeTasks.Add(tracked);
        }
        return true;
    }

    /// <summary>
    /// Starts and tracks a restart-on-failure background loop bound to <see cref="Token"/>.
    /// </summary>
    public bool TryKeepRunning(string name, Func<CancellationToken, Task> loop, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(loop);
        var delay = retryDelay ?? TimeSpan.FromSeconds(5);
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));

        return TryStartAsync(name, async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await loop(ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LogSafe($"{name} failed, starting it again in {delay.TotalSeconds:0.#} s: {e}");
                }
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>
    /// Enters an external callback scope (e.g. an OS event-log callback). Returns false if shutdown has begun.
    /// Callers receiving true must pair with <see cref="ExitCallback"/> in a finally block.
    /// </summary>
    public bool TryEnterCallback()
    {
        lock (_gate)
        {
            if (_shuttingDown || _disposed || _cts.IsCancellationRequested) return false;
            _activeCallbacks++;
            return true;
        }
    }

    /// <summary>Exits a previously entered callback scope and notifies shutdown waiters if all callbacks have drained.</summary>
    public void ExitCallback()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_activeCallbacks <= 0)
                throw new InvalidOperationException("ExitCallback called without a matching TryEnterCallback.");
            _activeCallbacks--;
            if (_activeCallbacks == 0 && _callbacksDrainedTcs != null)
            {
                drained = _callbacksDrainedTcs;
                _callbacksDrainedTcs = null;
            }
        }
        drained?.TrySetResult();
    }

    /// <summary>Captures the work admitted before BeginShutdown closed the gate, including callbacks already in flight.</summary>
    private Task PendingWork()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_activeCallbacks == 0) return Task.WhenAll(_activeTasks);
            _callbacksDrainedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.WhenAll(_activeTasks.Append(_callbacksDrainedTcs.Task));
        }
    }

    /// <summary>Cancels work and asynchronously waits up to the supplied timeout for workers and callbacks to drain.</summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        BeginShutdown();
        var all = PendingWork();
        try
        {
            await all.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }

        return IsQuiescent;
    }

    /// <summary>
    /// Full bounded broker shutdown sequence: signals shutdown and cancels tasks, stops event watchers, closes
    /// the write dispatcher boundedly, drains tracked tasks and callbacks boundedly, runs final V/F history flush,
    /// and disposes native GPU resources ONLY when every worker, callback, and write window is quiescent.
    /// Returns true if resources were disposed, false if retained due to non-quiescence.
    /// </summary>
    public bool ShutdownAndRelease(
        TimeSpan closeWait,
        TimeSpan drainWait,
        Action? stopWatchers,
        Func<TimeSpan, bool>? closeDispatcher,
        Action? flushVfHistory,
        Action disposeResources)
    {
        ArgumentNullException.ThrowIfNull(disposeResources);
        if (closeWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(closeWait));
        if (drainWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(drainWait));

        BeginShutdown();

        if (stopWatchers != null)
        {
            try { stopWatchers(); }
            catch (Exception e) { LogSafe($"stopping event watchers failed: {e.GetBaseException().Message}"); }
        }

        bool writeClosed = true;
        if (closeDispatcher != null)
        {
            try { writeClosed = closeDispatcher(closeWait); }
            catch (Exception e)
            {
                writeClosed = false;
                LogSafe($"closing dispatcher failed: {e.GetBaseException().Message}");
            }
        }

        // A blocked native worker can starve the pool that dispatches async timeouts. Bound this wait on the closing thread.
        bool quiescent = PendingWork().Wait(drainWait) && IsQuiescent;

        if (flushVfHistory != null)
        {
            try { flushVfHistory(); }
            catch (Exception e) { LogSafe($"vf history: the last write did not finish: {e.GetBaseException().Message}"); }
        }

        if (writeClosed && quiescent)
        {
            try { disposeResources(); }
            finally { Dispose(); }
            return true;
        }

        LogSafe("retaining loaded GPU libraries on exit because background work did not finish in time");
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shuttingDown = true;
            if (_activeTasks.Count == 0 && _activeCallbacks == 0)
                _cts.Dispose();
        }
    }

    private void CancelTokenSafe()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void LogSafe(string message)
    {
        try { _log(message); }
        catch (Exception) { }
    }
}
