using System.Diagnostics;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Broker;

/// <summary>
/// Looks at each GPU four times a second (core clock, NVVDD voltage, watts, temperature, utilization, limit reasons)
/// into its <see cref="FlightRecorder"/>, so a driver reset finds the last 60 s in memory. Reads only: one NVML call
/// per field and one rail status read, nothing written, nothing on disk. Once the driver is gone (<see cref="DriverLoss"/>)
/// the looks come back empty without calling it, until the broker's watch hands over to a fresh process.
/// </summary>
internal sealed class FlightSampler(GpuManager mgr, NvClient hw, IReadOnlyList<(string Id, GpuSession Session)> gpus)
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
                // A rail read that failed any other way skips NVML for this look too: after a device restart (ST08,
                // 2026-10-21) NVML read through the old handle crashed the worker with an access violation.
                DriverLoss.Why(mgr.Nvml, [session]);
                if (!railRead) nvml = null;
                var s = new FlightSample(at, 0,
                    Try(() => nvml!.ClockMhz(h, NvmlClock.Graphics), nvml),
                    uv / 1000,
                    Try(() => nvml!.PowerUsageMw(h), nvml) / 1000.0,
                    Try(() => nvml!.TemperatureC(h), nvml),
                    Try(() => nvml!.Utilization(h).Gpu, nvml),
                    Try(() => nvml!.ThrottleReasons(h), nvml));
                Recorders[id].Add(s with { ReadMs = (int)sw.ElapsedMilliseconds });
            }
            try { await Task.Delay(Every, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static T? Try<T>(Func<T> read, NvmlApi? nvml) where T : struct
    {
        if (nvml == null) return null;
        try { return read(); } catch (NvmlException) { return null; }
    }
}
