namespace Clocktail.Lab;

/// <summary>A memory candidate B measured against A. Raw runs are retained; scan readings are never overwritten by a recheck.</summary>
public sealed record MemoryComparison(string Stage, int A, int B, string Outcome, string Why,
    IReadOnlyList<SweepStep> Runs, double? BandwidthPct = null, double? LatencyPct = null, bool Disturbed = false)
{
    public const string Better = "better", Equivalent = "equivalent", Slower = "slower", NoGain = "no clear gain",
        Failed = "failed", Unjudged = "unjudged";
}

public static class MemoryComparisonRules
{
    public const double Spread = 0.02;

    public static MemoryComparison Judge(string stage, int a, int b, IReadOnlyList<SweepStep> runs)
    {
        MemoryComparison Result(string outcome, string why, double? gb = null, double? lat = null) =>
            new(stage, a, b, outcome, why, runs.ToArray(), gb, lat);
        if (runs.Any(r => r.OffsetMhz == b && r.Verdict == LabResult.Fail))
            return Result(MemoryComparison.Failed, "the candidate failed its correctness check");
        if (runs.Count != 4 || runs.Select(r => r.OffsetMhz).SequenceEqual(new[] { a, b, b, a }) == false)
            return Result(MemoryComparison.Unjudged, "the A B B A comparison did not finish");
        foreach (var r in runs)
            if (MemorySweep.Edge([r]) is { } edge)
                return Result(MemoryComparison.Unjudged, edge.Why);
        foreach (var (before, after) in new[] { (runs[0], runs[1]), (runs[3], runs[2]) })
            if (MemorySweep.InForce(before.OffsetMhz, before.MemMhz, after) is { } why)
                return Result(MemoryComparison.Unjudged, why);
        var gb = PairedMetric.Of(runs[0].Gbps, runs[1].Gbps, runs[2].Gbps, runs[3].Gbps)!;
        var lat = PairedMetric.Of(runs[0].LatencyNs, runs[1].LatencyNs, runs[2].LatencyNs, runs[3].LatencyNs)!;
        if (gb.Disturbed(Spread) || lat.Disturbed(Spread))
            return Result(MemoryComparison.Unjudged, "repeated runs of one setting differ by over 2%; the comparison was disturbed", gb.ChangePct, lat.ChangePct) with { Disturbed = true };
        if (gb.Decreased(MemorySweep.BandwidthFall) || lat.Increased(MemorySweep.LatencyRise))
            return Result(MemoryComparison.Slower, "the performance loss repeated in both pairs", gb.ChangePct, lat.ChangePct);
        if (gb.ChangePct >= -MemorySweep.BandwidthFall * 100 && lat.ChangePct <= MemorySweep.LatencyRise * 100
            && (gb.Increased(MemorySweep.BandwidthFall) || lat.Decreased(MemorySweep.LatencyRise)))
            return Result(MemoryComparison.Better, "a gain repeated in both pairs without a material loss in the other metric", gb.ChangePct, lat.ChangePct);
        if (Math.Abs(gb.ChangePct) <= MemorySweep.BandwidthFall * 100 && Math.Abs(lat.ChangePct) <= MemorySweep.LatencyRise * 100)
            return Result(MemoryComparison.Equivalent, "no material bandwidth or latency difference", gb.ChangePct, lat.ChangePct);
        return Result(MemoryComparison.NoGain, "no repeatable gain within the bandwidth and latency budgets", gb.ChangePct, lat.ChangePct);
    }

    /// <summary>At most three scan candidates: the least offset on the joint performance plateau, the bandwidth winner,
    /// and the latency winner. These are a shortlist, not recommendations, until compared and confirmed.</summary>
    public static IReadOnlyList<int> Shortlist(IReadOnlyList<SweepStep> steps)
    {
        var valid = steps.Where(r => MemorySweep.Edge([r]) == null).ToList();
        if (valid.Count == 0) return [];
        double gb = valid.Max(r => r.Gbps!.Value), lat = valid.Min(r => r.LatencyNs!.Value);
        var plateau = valid.Where(r => r.Gbps >= gb * (1 - MemorySweep.BandwidthFall) && r.LatencyNs <= lat * (1 + MemorySweep.LatencyRise))
            .OrderBy(r => r.OffsetMhz).FirstOrDefault();
        var bandwidth = valid.OrderByDescending(r => r.Gbps).ThenBy(r => r.OffsetMhz).First();
        var latency = valid.OrderBy(r => r.LatencyNs).ThenBy(r => r.OffsetMhz).First();
        return new[] { plateau ?? bandwidth, bandwidth, latency }.Select(r => r.OffsetMhz).Distinct().ToArray();
    }

    /// <summary>Normalize every comparison to the same baseline, using bandwidth first and latency to break a plateau;
    /// equivalent results prefer the lower offset. No unjudged comparison can produce a recommendation.</summary>
    public static int? Pick(int baseline, IReadOnlyList<MemoryComparison> comparisons)
    {
        int best = baseline;
        double gb = 0, lat = 0;
        bool judged = false;
        foreach (var c in comparisons.Where(c => c.A == baseline))
        {
            if (c.Outcome is MemoryComparison.Unjudged) return null;
            if (c.Outcome is not (MemoryComparison.Better or MemoryComparison.Equivalent or MemoryComparison.Slower or MemoryComparison.NoGain)) continue;
            judged = true;
            if (c.Outcome is not (MemoryComparison.Better or MemoryComparison.Equivalent) || c.BandwidthPct is not { } g || c.LatencyPct is not { } l) continue;
            if (g > gb + MemorySweep.BandwidthFall * 100
                || Math.Abs(g - gb) <= MemorySweep.BandwidthFall * 100 && l < lat - MemorySweep.LatencyRise * 100
                || Math.Abs(g - gb) <= MemorySweep.BandwidthFall * 100 && Math.Abs(l - lat) <= MemorySweep.LatencyRise * 100 && c.B < best)
                (best, gb, lat) = (c.B, g, l);
        }
        return judged ? best : null;
    }

    /// <summary>Effective scan readings after a paired recheck. The immutable raw scan and comparison remain in status/files.</summary>
    public static void Recheck(List<SweepStep> effective, MemoryComparison comparison)
    {
        if (comparison.Runs.Count != 4 || comparison.Outcome is MemoryComparison.Unjudged or MemoryComparison.Failed) return;
        foreach (int offset in new[] { comparison.A, comparison.B }.Distinct())
        {
            var runs = comparison.Runs.Where(r => r.OffsetMhz == offset).ToArray();
            int i = effective.FindIndex(r => r.OffsetMhz == offset);
            if (i >= 0) effective[i] = effective[i] with
            {
                Gbps = runs.Average(r => r.Gbps), LatencyNs = runs.Average(r => r.LatencyNs),
                MemMhz = runs.Average(r => r.MemMhz), TempP50 = runs.Average(r => r.TempP50),
            };
        }
    }
}
