using Clocktail.Domain.Vf;

namespace Clocktail.Domain.Profiles;

/// <summary>
/// This card's own edge, point by point: for each curve point, the lowest offset (kHz) a crash was recorded at there, from
/// the crash data points that carry the flight recorder's voltage (the crash goes on the point nearest it, as by_point
/// puts it). A live line then says how far the curve now sits under what crashed at the point it runs on.
/// </summary>
public static class CrashEdge
{
    public static IReadOnlyDictionary<int, int> From(IReadOnlyList<DataPoint> points, IReadOnlyList<VfPoint> curve)
    {
        var edge = new Dictionary<int, int>();
        if (curve.Count == 0) return edge;
        foreach (var p in points)
        {
            if (p.Outcome != Experiment.Crash || p.CrashUv is not { } uv) continue;
            int[] offsets;
            try { offsets = VfOffsets.Decode(p.Curve); } catch (FormatException) { continue; }
            var at = curve.MinBy(c => Math.Abs((long)c.VoltageUv - uv))!;
            if (at.Index >= offsets.Length) continue;
            int off = offsets[at.Index];
            edge[at.Index] = edge.TryGetValue(at.Index, out var lowest) ? Math.Min(lowest, off) : off;
        }
        return edge;
    }

    /// <summary>The words for a live line: how far the offset now sits under the one that crashed at this point; null
    /// when no crash is on record there.</summary>
    public static string? Describe(IReadOnlyDictionary<int, int> edge, int point, int offsetNowKhz)
    {
        if (!edge.TryGetValue(point, out var crashed)) return null;
        double under = (crashed - offsetNowKhz) / 1000.0;
        return under > 0
            ? $"edge: {under:0.#} MHz under the offset that crashed at this point ({crashed / 1000.0:+0.#;-0.#;0})"
            : $"⚠️ at or over the offset that crashed at this point ({crashed / 1000.0:+0.#;-0.#;0}, now {offsetNowKhz / 1000.0:+0.#;-0.#;0})";
    }
}
