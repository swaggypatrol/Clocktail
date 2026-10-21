using Clocktail.Core.Native;
using Clocktail.Hw;

namespace Clocktail.Core.Gpu;

public sealed record TelemetrySnapshot(
    double? TemperatureC,
    double? PowerWatts,
    double? PowerLimitWatts,
    double? PowerDefaultWatts,
    double? PowerEnforcedWatts,
    double? PowerMinWatts,
    double? PowerMaxWatts,
    uint? GraphicsClockMhz,
    uint? MemoryClockMhz,
    uint? GraphicsMaxMhz,
    uint? MemoryMaxMhz,
    uint? GpuUtilPercent,
    uint? MemUtilPercent,
    ulong? VramTotalBytes,
    ulong? VramUsedBytes,
    IReadOnlyList<uint> FanPercent,
    IReadOnlyList<string> ThrottleReasons,
    double? PowerInstantWatts = null,
    int? TempMarginC = null,
    /// <summary>The performance state (0 = P0); null when unread.</summary>
    int? PState = null);

public static class Telemetry
{
    // nvmlClocksThrottleReasons bit values (public NVML headers). 0x400 is not in them: it is named "Reliability Voltage"
    // here, and on the 5090 it is set exactly while the stock curve sits at the rail's MAX (StabilityLab runs).
    private static readonly (ulong Bit, string Name)[] Reasons =
    [
        (0x1, "gpu_idle"), (0x2, "applications_clocks_setting"), (0x4, "sw_power_cap"),
        (0x8, "hw_slowdown"), (0x10, "sync_boost"), (0x20, "sw_thermal_slowdown"),
        (0x40, "hw_thermal_slowdown"), (0x80, "hw_power_brake_slowdown"), (0x100, "display_clock_setting"),
        (0x400, "reliability_voltage"),
    ];

    private static T? Try<T>(Func<T> f) where T : struct
    {
        try { return f(); } catch (NvmlException) { return null; }
    }

    public static TelemetrySnapshot Read(GpuDevice gpu, NvmlApi nvml)
    {
        if (!gpu.HasNvml) throw new InvalidOperationException("This GPU has no matching NVML device.");
        var h = gpu.NvmlHandle;
        var cons = Try(() => nvml.PowerConstraintsMw(h));
        var util = Try(() => nvml.Utilization(h));
        var mem = Try(() => nvml.Memory(h));

        var fans = new List<uint>();
        uint nf = nvml.FanCount(h);
        for (uint i = 0; i < nf; i++) { var f = nvml.FanPercent(h, i); if (f != null) fans.Add(f.Value); }

        ulong tr = nvml.ThrottleReasons(h);
        var names = Reasons.Where(r => (tr & r.Bit) != 0).Select(r => r.Name).ToList();

        static double? W(uint? mw) => mw == null ? null : mw.Value / 1000.0;
        var instant = nvml.FieldValues(h, NvmlField.PowerInstantMw)?[0].Value;
        return new TelemetrySnapshot(
            Try(() => (double)nvml.TemperatureC(h)),
            W(Try(() => nvml.PowerUsageMw(h))),
            W(Try(() => nvml.PowerLimitMw(h))),
            W(Try(() => nvml.PowerDefaultMw(h))),
            W(Try(() => nvml.PowerEnforcedMw(h))),
            cons == null ? null : cons.Value.MinMw / 1000.0,
            cons == null ? null : cons.Value.MaxMw / 1000.0,
            Try(() => nvml.ClockMhz(h, NvmlClock.Graphics)),
            Try(() => nvml.ClockMhz(h, NvmlClock.Memory)),
            Try(() => nvml.MaxClockMhz(h, NvmlClock.Graphics)),
            Try(() => nvml.MaxClockMhz(h, NvmlClock.Memory)),
            util?.Gpu, util?.Memory, mem?.Total, mem?.Used, fans, names,
            instant / 1000.0, nvml.MarginTemperatureC(h), nvml.PerformanceState(h));
    }

}
