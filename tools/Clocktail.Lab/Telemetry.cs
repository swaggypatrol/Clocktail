using System.Globalization;
using Clocktail.Hw;

namespace Clocktail.Lab;

/// <summary>One look at the card through NVML: what the load ran at. Null fields where NVML did not answer.</summary>
public sealed record LabSample(double T, int Phase, double? BoardW, uint? TempC, uint? SmMhz, uint? MemMhz, uint? UtilPct, int? PState = null);

public interface ISampler : IDisposable
{
    LabSample Read(double t, int phase);
}

/// <summary>
/// NVML on the GPU the CUDA engine runs on, matched by PCI address (the two number cards differently; NVML writes an
/// 8-digit domain, CUDA a 4-digit one). Reads only.
/// </summary>
public sealed class NvmlSampler : ISampler
{
    private readonly NvmlApi _nvml;
    private readonly nint _h;

    public NvmlSampler(string cudaPciBusId)
    {
        _nvml = NvmlApi.Load();
        var want = Pci(cudaPciBusId);
        for (uint i = 0; i < _nvml.DeviceCount(); i++)
        {
            var h = _nvml.HandleByIndex(i);
            if (Pci(_nvml.Pci(h).BusId) == want) { _h = h; return; }
        }
        _nvml.Dispose();
        throw new InvalidOperationException($"NVML has no GPU at PCI {cudaPciBusId}");
    }

    /// <summary>(domain, bus, device) from "dddd:bb:dd.f" or "dddddddd:bb:dd.f"; null when it does not parse.</summary>
    public static (uint, uint, uint)? Pci(string busId)
    {
        var p = busId.Trim().Split(':', '.');
        return p.Length >= 3 && uint.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var d)
            && uint.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
            && uint.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dev) ? (d, b, dev) : null;
    }

    public LabSample Read(double t, int phase)
    {
        T? Try<T>(Func<T> f) where T : struct { try { return f(); } catch (NvmlException) { return null; } }
        return new(t, phase, Try(() => _nvml.PowerUsageMw(_h) / 1000.0), Try(() => _nvml.TemperatureC(_h)),
            Try(() => _nvml.ClockMhz(_h, NvmlClock.Graphics)), Try(() => _nvml.ClockMhz(_h, NvmlClock.Memory)), Try(() => _nvml.Utilization(_h).Gpu),
            _nvml.PerformanceState(_h));
    }

    public void Dispose() => _nvml.Dispose();
}
