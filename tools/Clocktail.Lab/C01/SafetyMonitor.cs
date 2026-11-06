using System.Diagnostics;

namespace Clocktail.Lab;

/// <summary>Monitors Aria throughout preparation and measurement on its own sensor thread.</summary>
public sealed class SafetyMonitor : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _cancel;
    private readonly Thread _thread;
    private string? _interrupted;
    private string? _reason;
    public string? Interrupted => Volatile.Read(ref _interrupted);
    public string? Reason => Volatile.Read(ref _reason);

    public SafetyMonitor(Func<ITemperatureSensor> open, double maxTemp, CancellationTokenSource cancel, TimeSpan? lostAfter = null)
    {
        _cancel = cancel;
        var limit = lostAfter ?? TimeSpan.FromSeconds(10);
        _thread = new Thread(() =>
        {
            try
            {
                using var sensor = open();
                var since = Stopwatch.StartNew();
                while (!_stop.IsCancellationRequested)
                {
                    uint? temp = sensor.ReadTemperature();
                    if (temp is { } t)
                    {
                        since.Restart();
                        if (t >= maxTemp) { Halt("temperature", $"GPU reached {t} °C, the {maxTemp:0} °C limit"); return; }
                        _ready.Set();
                    }
                    else if (since.Elapsed >= limit) { Halt("telemetry", "GPU temperature was unavailable for too long"); return; }
                    if (_stop.Token.WaitHandle.WaitOne(100)) return;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Halt("telemetry", $"temperature monitor failed: {e.Message}"); }
            finally { _ready.Set(); }
        }) { IsBackground = true, Name = "aria-temperature" };
        _thread.Start();
    }

    private void Halt(string by, string reason)
    {
        Volatile.Write(ref _reason, reason);
        Volatile.Write(ref _interrupted, by);
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    public bool WaitReady(TimeSpan timeout)
    {
        if (!_ready.Wait(timeout)) { Halt("telemetry", "temperature monitor did not start"); return false; }
        return Interrupted == null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(2))) Halt("telemetry", "temperature monitor did not stop");
        // The thread owns the sensor and may still be blocked in a driver call.
    }
}
