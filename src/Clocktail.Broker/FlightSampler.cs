using System.Diagnostics;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Broker;

/// <summary>
/// Looks at each GPU four times a second (core clock, NVVDD voltage, watts, temperature, utilization, limit reasons)
/// into its <see cref="FlightRecorder"/>, so a driver reset finds the last 60 s in memory. Reads only: one NVML call
/// per field and one rail status read, nothing written to the GPU, the ring itself only in memory. Once the driver is gone (<see cref="DriverLoss"/>)
/// the looks come back empty without calling it, until the broker's watch hands over to a fresh process.
/// Each look is also handed to the always-on V/F history (<see cref="VfHistoryRecorder"/>), which keeps its time, measured
/// NVVDD and measured core clock when NVVDD is above 825 mV: no read of its own, and its disk work is on another task.
/// </summary>
internal sealed class FlightSampler(GpuManager mgr, NvClient hw, IReadOnlyList<(string Id, GpuSession Session)> gpus,
    VfHistoryRecorder? history = null)
{
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);

    public IReadOnlyDictionary<string, FlightRecorder> Recorders { get; } =
        gpus.ToDictionary(g => g.Id, _ => new FlightRecorder(FlightRecorder.DefaultKeep), StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken ct)
    {
        var masks = gpus.ToDictionary(g => g.Id, g => RailsReader.Mask(hw, g.Session));
        while (!ct.IsCancellationRequested)
        {
            foreach (var (id, session) in gpus)
            {
                var sw = Stopwatch.StartNew();
                var at = DateTimeOffset.Now;
                var dev = mgr.Gpus.FirstOrDefault(g => string.Equals(g.Identity.StableId, id, StringComparison.OrdinalIgnoreCase));
                var nvml = dev is { HasNvml: true } ? mgr.Nvml : null;
                nint h = dev?.NvmlHandle ?? 0;
                uint? uv = null;
                bool railRead = true;
                try { masks[id] ??= RailsReader.Mask(hw, session); if (masks[id] is { } m) uv = RailsReader.PresentUv(hw, session, m, Clocktail.Domain.Rails.Rail.Nvvdd); }
                catch (Exception e) when (e is NvApiException or InvalidOperationException) { railRead = false; }
                // The rail read comes first so that a handle NVAPI just found invalidated stops NVML before this look reads
                // it through the same dead driver (NVML then answers "lost" without being called; the broker hands over).
                // A rail read that failed any other way skips NVML for this look too, and one failed NVML read ends NVML
                // for the look; a failure that means the handle is stale latches NVML lost. Neither stops what pnputil
                // /restart-device does (ST08, 2026-10-21): the NVAPI read and the first NVML reads answered, and the
                // utilization read died of an access violation inside NVML. The supervisor starts a new worker 2 s later.
                DriverLoss.Why(mgr.Nvml, [session]);
                if (!railRead) nvml = null;
                var look = new NvmlLook(nvml);
                var clock = look.Read(() => nvml!.ClockMhz(h, NvmlClock.Graphics));
                var s = new FlightSample(at, 0,
                    clock,
                    uv / 1000,
                    look.Read(() => nvml!.PowerUsageMw(h)) / 1000.0,
                    look.Read(() => nvml!.TemperatureC(h)),
                    look.Read(() => nvml!.Utilization(h).Gpu),
                    look.Read(() => nvml!.ThrottleReasons(h)));
                int readMs = (int)sw.ElapsedMilliseconds;
                var recorder = Recorders[id];
                recorder.Add(s with { ReadMs = readMs });
                // The same look, its voltage as the rail reader returned it (µV, before the ring's mV): Look never throws,
                // and the guard is for anything it could not foresee; the ring above is unchanged either way.
                if (history != null)
                    try { history.Look(id, at, uv, clock, readMs, recorder.Freeze()); } catch (Exception) { }
            }
            try { await Task.Delay(Every, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>NVML statuses that mean the device handle went stale (a device restart under the process): invalid
    /// argument, not found, unknown. NVML's own "driver gone" statuses latch it already.</summary>
    public static bool StaleHandle(int code) => code is 2 or 6 or 999;

    /// <summary>The NVML reads of one look: after the first that fails, the rest of the look reads nothing.</summary>
    internal sealed class NvmlLook(NvmlApi? nvml)
    {
        private bool _failed;

        public T? Read<T>(Func<T> read) where T : struct
        {
            if (nvml == null || _failed || nvml.Lost) return null;
            try { return read(); }
            catch (NvmlException e)
            {
                _failed = true;
                if (StaleHandle(e.Code)) nvml.MarkLost(e.Code);
                return null;
            }
        }
    }
}
