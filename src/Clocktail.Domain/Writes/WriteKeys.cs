using System.Globalization;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;

namespace Clocktail.Domain.Writes;

/// <summary>
/// Canonicalizes write keys against the exact rules of each registered path (<see cref="WriteRegistry"/>) before
/// dispatcher rule checks, queue ordering, or journal lookups run. Invalid keys (including numeric enum loopholes such
/// as <c>"0"</c> for <c>pstates20</c>) are rejected with <see cref="ArgumentException"/>.
/// </summary>
public static class WriteKeys
{
    public static string Canonicalize(string kind, string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException($"'{kind}' write key cannot be empty");
        var path = WriteRegistry.Find(kind)
            ?? throw new ArgumentException($"'{kind}' is not a write path (one of: {string.Join(", ", WriteRegistry.Kinds)})");

        return path.Kind switch
        {
            ClockOffsets.Kind => key.ToLowerInvariant() switch
            {
                "core" or "memory" => key.ToLowerInvariant(),
                _ => throw new ArgumentException($"'{key}' is not a P0 clock domain (core, memory)"),
            },
            PowerCap.Kind => key.ToLowerInvariant() switch
            {
                "fe" or "f8" => key.ToLowerInvariant(),
                _ => throw new ArgumentException($"'{key}' is not a power request (fe, f8)"),
            },
            RailWrites.Kind => key.ToLowerInvariant() switch
            {
                "nvvdd" or "msvdd" => key.ToLowerInvariant(),
                _ => throw new ArgumentException($"'{key}' is not a voltage rail (nvvdd, msvdd)"),
            },
            ClockDomainOffsets.Kind => key switch
            {
                "xbar" or "sys" or "video" => key,
                _ => throw new ArgumentException($"'{key}' is not a clock domain written here (xbar, sys, video)"),
            },
            ClockVdemand.Kind => key switch
            {
                "graphics" or "xbar" or "sys" or "video" => key,
                _ => throw new ArgumentException($"'{key}' is not a clock domain whose voltage demand is written here (graphics, xbar, sys, video)"),
            },
            VoltBoost.Kind => key switch
            {
                "independent" or "legacy" => key,
                _ => throw new ArgumentException($"'{key}' is not a boost path (independent, legacy)"),
            },
            FanControl.Kind => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var fanId)
                ? FanControl.Key(fanId)
                : throw new ArgumentException($"'{key}' is not a fan channel id"),
            FanPolicyWrites.Kind => FanPolicyWrites.Key(FanPolicyWrites.ParseKey(key)),
            FanStopWrites.Kind => FanPolicyWrites.Key(FanPolicyWrites.ParseKey(key)),
            ClockLimitWrites.Kind => CanonicalClockLimitKey(key),
            ClockRatio.Kind => key.Equals(ClockRatio.Key, StringComparison.OrdinalIgnoreCase)
                ? ClockRatio.Key
                : throw new ArgumentException($"'{key}' is not a clock ratio (msvdd)"),
            CurrentLimitWrites.Kind => CurrentLimitWrites.Key(CurrentLimitWrites.ParseKey(key)),
            VfWrites.Kind => key == VfWrites.CoreKey
                ? VfWrites.CoreKey
                : throw new ArgumentException($"'{key}' is not a V/F curve (core)"),
            _ => throw new ArgumentException($"'{path.Kind}' has no key canonicalizer"),
        };
    }

    private static string CanonicalClockLimitKey(string key)
    {
        _ = ClockLimitWrites.Entry(key);
        return key.ToLowerInvariant();
    }
}
