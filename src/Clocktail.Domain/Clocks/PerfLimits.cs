using System.Buffers.Binary;
using System.Text;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

/// <summary>A bound the driver's perf-limit arbitration sets on a clock domain, and the limit (by name) that set it.</summary>
public sealed record PerfLimitBound(uint Khz, string By)
{
    public double Mhz => Khz / 1000.0;
}

/// <summary>One clock domain's arbitration: the highest clock the driver allows (ceiling) and the lowest it holds (floor).</summary>
public sealed record DomainLimits(string Domain, PerfLimitBound? Ceiling, PerfLimitBound? Floor);

/// <summary>
/// The driver's perf-limit arbitration (PerfPerfLimits, slots 42/43), read live on the 5090 (617.42, 2026-10-21). The info
/// (0x12308 r2) names its limits: a 256-bit mask at +8 and one 0x90-byte record per limit from +0x290, the name first
/// (242 of them: RELIABILITY_ALT_NVVDD_0, CLIENT_LOW_STRICT_GPC_MAX, VMIN_NVVDD_0…). The status (0x207D90 r2) holds the
/// result as {kHz, limit index} pairs per clock domain, 0x10 apart: the ceilings from +0x2010 and the floors from +0x1ae8,
/// GPC, XBAR and memory first (the domains after them are not identified). Measured: at idle the GPC ceiling was 3082 MHz
/// set by RELIABILITY_ALT_NVVDD_0 (the curve's top under the voltage reliability limit) and XBAR's 2797 by
/// RELIABILITY_MSVDD_0; under nvidia-smi -lgc 210,2000 the GPC ceiling was 1995 by CLIENT_LOW_STRICT_GPC_MAX and its
/// floor 210 by CLIENT_LOW_STRICT_GPC_MIN, XBAR's ceiling 1792 with it; under full load the floor rose to the ceiling,
/// 3037 MHz, set by the driver's performance controller. The power wall is not one of these limits: it acts below them
/// (NVML's sw_power_cap), so a card at its wall runs under its ceiling.
/// </summary>
public static class PerfLimits
{
    public const int InfoSize = 0x12308, InfoRev = 2, Mask = 8, NamesAt = 0x290, NameStride = 0x90, NameLength = 0x40, Count = 256;
    public const int StatusSize = 0x207D90, StatusRev = 2, CeilingsAt = 0x2010, FloorsAt = 0x1ae8, DomainStride = 0x10;
    public static readonly string[] Domains = ["gpc", "xbar", "memory"];

    /// <summary>Each limit's name by index; null where the mask has no limit or the record has no name.</summary>
    public static string?[] Names(byte[] info)
    {
        var names = new string?[Count];
        for (int i = 0; i < Count; i++)
        {
            int at = NamesAt + i * NameStride;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(Mask + i / 32 * 4)) >> (i % 32) & 1) == 0 || at + NameLength > info.Length) continue;
            var raw = info.AsSpan(at, NameLength);
            int end = raw.IndexOf((byte)0);
            var text = Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]);
            names[i] = text.Length > 0 && text.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or '_') ? text : null;
        }
        return names;
    }

    /// <summary>The ceiling and floor of each identified domain; a pair whose limit index names no limit is left out.</summary>
    public static IReadOnlyList<DomainLimits> Parse(byte[] status, IReadOnlyList<string?> names)
    {
        PerfLimitBound? At(int o)
        {
            if (o + 8 > status.Length) return null;
            uint khz = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(o)), by = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(o + 4));
            return khz != 0 && by < names.Count && names[(int)by] is { } name ? new PerfLimitBound(khz, name) : null;
        }
        return [.. Domains.Select((d, i) => new DomainLimits(d, At(CeilingsAt + i * DomainStride), At(FloorsAt + i * DomainStride)))];
    }

    private static string?[]? _names;
    private static readonly object Gate = new();

    /// <summary>The arbitration now. The names are read once per process (they belong to the driver).</summary>
    public static IReadOnlyList<DomainLimits> Read(NvClient hw, GpuSession gpu)
    {
        string?[] names;
        lock (Gate)
        {
            if (_names == null)
            {
                var info = NvClient.NewBuffer(Slot.PerfPerfLimitsGetInfo);
                var ri = hw.Call(gpu, Slot.PerfPerfLimitsGetInfo, info);
                if (!ri.IsOk) throw new InvalidOperationException($"perf-limit info read failed: {ri}");
                _names = Names(info);
            }
            names = _names;
        }
        var status = NvClient.NewBuffer(Slot.PerfPerfLimitsGetStatus);
        var rs = hw.Call(gpu, Slot.PerfPerfLimitsGetStatus, status);
        if (!rs.IsOk) throw new InvalidOperationException($"perf-limit status read failed: {rs}");
        return Parse(status, names);
    }

    /// <summary>What a limit is, in words, by its family.</summary>
    public static string Means(string by) => by switch
    {
        _ when by.StartsWith("CLIENT_", StringComparison.Ordinal) => "a clock lock (nvidia-smi -lgc, set_clock_lock or another tool)",
        _ when by.StartsWith("RELIABILITY_", StringComparison.Ordinal) => "the rail's reliability voltage limit: the curve's top",
        _ when by.StartsWith("THERM_", StringComparison.Ordinal) => "a thermal policy",
        _ when by.StartsWith("PERF_CF_CONTROLLER", StringComparison.Ordinal) => "the driver's performance controller (it asks for the most the curve gives)",
        _ when by.StartsWith("ISMODEPOSSIBLE", StringComparison.Ordinal) => "what the display needs",
        _ when by.StartsWith("VMIN_", StringComparison.Ordinal) => "the rail's minimum voltage",
        _ when by.StartsWith("CUDA_", StringComparison.Ordinal) => "the driver's cap while a CUDA program has the GPU (the lab, a local AI model)",
        _ => "a driver limit",
    };
}
