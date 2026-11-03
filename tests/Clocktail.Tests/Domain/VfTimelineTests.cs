using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Domain;

/// <summary>Observed time: by elapsed time, not by sample count; overlapping sources once; transitions within a second
/// kept; gaps (the gate, a restart, sleep, a reset) never credited.</summary>
public class VfTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static IEnumerable<VfObservation> Light(DateTimeOffset from, double seconds, uint uv = 900_000, uint mhz = 2500) =>
        Enumerable.Range(0, (int)(seconds * 4)).Select(i => new VfObservation(from.AddMilliseconds(i * 250), uv, mhz, VfSource.Lightweight));

    private static IEnumerable<VfObservation> Residency(DateTimeOffset from, int seconds, int ms = 0, uint uv = 900_000, uint mhz = 2500) =>
        Enumerable.Range(0, seconds).Select(i => new VfObservation(from.AddSeconds(i).AddMilliseconds(ms), uv, mhz, VfSource.Residency, 100, 400));

    [Fact]
    public void A_250_ms_stream_and_a_1_s_stream_over_the_same_minute_weigh_the_same()
    {
        var light = VfTimeline.Build(Light(T0, 60));
        var res = VfTimeline.Build(Residency(T0, 60));
        Assert.Equal(240, light.Items.Count);
        Assert.Equal(60, res.Items.Count);
        Assert.Equal(60, light.TotalSeconds, 6);
        Assert.Equal(60, res.TotalSeconds, 6);
    }

    [Fact]
    public void Both_sources_over_the_same_seconds_count_that_time_once()
    {
        var both = VfTimeline.Build(Light(T0, 60).Concat(Residency(T0, 60, ms: 600)));
        Assert.Equal(300, both.Items.Count);           // every look kept
        Assert.Equal(60, both.TotalSeconds, 6);        // not 120
        // Both are measured: neither is discounted, each holds the time until the next look.
        Assert.True(both.Items.Where(i => i.Observation.Source == VfSource.Residency).All(i => Math.Abs(i.Seconds - 0.15) < 1e-6));
    }

    [Fact]
    public void Two_servers_residency_seconds_count_once_and_a_lightweight_record_written_twice_too()
    {
        var t = VfTimeline.Build(Residency(T0, 30, ms: 100).Concat(Residency(T0, 30, ms: 500)));
        Assert.Equal(30, t.Items.Count);
        Assert.Equal(30, t.TotalSeconds, 6);
        var l = VfTimeline.Build(Light(T0, 10).Concat(Light(T0, 10)));
        Assert.Equal(40, l.Items.Count);
        Assert.Equal(10, l.TotalSeconds, 6);
    }

    [Fact]
    public void Several_operating_points_within_one_second_are_all_kept_with_their_share()
    {
        VfObservation[] second =
        [
            new(T0, 870_000, 2400, VfSource.Lightweight),
            new(T0.AddMilliseconds(250), 905_000, 2700, VfSource.Lightweight),
            new(T0.AddMilliseconds(500), 950_000, 2900, VfSource.Lightweight),
            new(T0.AddMilliseconds(750), 880_000, 2450, VfSource.Lightweight),
        ];
        var t = VfTimeline.Build(second);
        Assert.Equal(4, t.Items.Count);
        Assert.Equal([870_000u, 905_000u, 950_000u, 880_000u], t.Items.Select(i => i.Uv));
        Assert.All(t.Items, i => Assert.Equal(0.25, i.Seconds, 6));
    }

    [Fact]
    public void A_gap_is_never_credited_whatever_caused_it()
    {
        // 10 s, then an hour of nothing (the voltage at or under 825 mV, sleep, a restart, a driver reset), then 10 s.
        var t = VfTimeline.Build(Light(T0, 10).Concat(Light(T0.AddHours(1), 10)));
        Assert.Equal(20, t.TotalSeconds, 6);
        var r = VfTimeline.Build(Residency(T0, 5).Concat(Residency(T0.AddMinutes(10), 5)));
        Assert.Equal(10, r.TotalSeconds, 6);
    }

    [Fact]
    public void A_single_missed_look_is_not_filled_in()
    {
        var looks = Light(T0, 2).Where((_, i) => i != 4).ToList();   // the look at 1.00 s fell under the gate
        var t = VfTimeline.Build(looks);
        Assert.Equal(1.75, t.TotalSeconds, 6);
        Assert.Equal(0.25, t.Items.Single(i => i.At == T0.AddMilliseconds(750)).Seconds, 6);
    }

    [Fact]
    public void Sampler_jitter_is_still_time_observed()
    {
        // A slow look: 330 ms to the next (under 1.5 intervals) stands for those 330 ms.
        VfObservation[] looks = [new(T0, 900_000, 2500, VfSource.Lightweight), new(T0.AddMilliseconds(330), 900_000, 2500, VfSource.Lightweight)];
        Assert.Equal(0.33, VfTimeline.Build(looks).Items[0].Seconds, 6);
    }

    [Fact]
    public void Time_order_holds_across_sources_and_offsets()
    {
        var mixed = new[]
        {
            new VfObservation(T0.AddSeconds(2).ToOffset(TimeSpan.FromHours(8)), 900_000, 2500, VfSource.Lightweight),
            new VfObservation(T0, 900_000, 2500, VfSource.Residency, 90, 300),
            new VfObservation(T0.AddSeconds(1), 900_000, 2500, VfSource.Lightweight),
        };
        var t = VfTimeline.Build(mixed);
        Assert.Equal([T0, T0.AddSeconds(1), T0.AddSeconds(2)], t.Items.Select(i => i.At));
        Assert.Equal(2, t.Between(T0.AddSeconds(1), T0.AddSeconds(2)).Count);   // both ends included
        Assert.Empty(t.Between(T0.AddSeconds(3), T0.AddSeconds(4)));
    }

    [Fact]
    public void A_window_counts_only_the_time_inside_it_at_both_ends()
    {
        var t = VfTimeline.Build(Light(T0, 11.25));                     // looks 0.00 … 11.00 s
        var w = t.Within(T0.AddMilliseconds(100), T0.AddSeconds(10));   // starts inside the first look's span
        Assert.Equal(9.9, w.Sum(o => o.Seconds), 9);
        Assert.Equal(T0, w[0].At);
        Assert.Equal(0.1, w[0].StartOffset, 9);
        Assert.Equal(0.15, w[0].Seconds, 9);
        Assert.DoesNotContain(w, o => o.At >= T0.AddSeconds(10));       // a look at the end lends it nothing
        Assert.Empty(t.Within(T0.AddSeconds(5), T0.AddSeconds(5)));
        Assert.Empty(t.Within(T0.AddSeconds(6), T0.AddSeconds(5)));
    }

    [Fact]
    public void A_look_before_the_window_reaches_into_it_for_the_rest_of_its_span()
    {
        var t = VfTimeline.Build(Residency(T0, 10, ms: 500));            // 0.5, 1.5, … 9.5 s, a second each
        var w = t.Within(T0.AddSeconds(1), T0.AddSeconds(3));
        Assert.Equal([T0.AddMilliseconds(500), T0.AddMilliseconds(1500), T0.AddMilliseconds(2500)], w.Select(o => o.At));
        Assert.Equal([0.5, 1.0, 0.5], w.Select(o => Math.Round(o.Seconds, 9)));
        Assert.Equal(2, w.Sum(o => o.Seconds), 9);
    }

    [Fact]
    public void Adjacent_windows_split_the_observed_time_without_loss_or_overlap()
    {
        var t = VfTimeline.Build(Light(T0, 30).Concat(Residency(T0, 30, ms: 600)));
        double Sum(double a, double b) => t.Within(T0.AddSeconds(a), T0.AddSeconds(b)).Sum(o => o.Seconds);
        Assert.Equal(Sum(0, 30), Sum(0, 7.33) + Sum(7.33, 18.1) + Sum(18.1, 30), 9);
        Assert.Equal(30, Sum(0, 30), 9);
        Assert.Equal(30, Sum(-5, 40), 9);                               // nothing is invented outside the looks
    }

    [Fact]
    public void Merging_two_ordered_streams_matches_building_them_together()
    {
        var light = Light(T0, 20).ToList();
        var res = VfTimeline.Normalize(Residency(T0, 20, ms: 100)).ToList();
        var merged = VfDwell.Weigh(VfTimeline.MergeOrdered(light, res)).ToList();
        var built = VfTimeline.Build(light.Concat(res)).Items;
        Assert.Equal(built.Select(b => (b.At, b.Seconds)), merged.Select(m => (m.At, m.Seconds)));
    }

    [Fact]
    public void P90_is_by_time_and_equals_the_old_sample_count_rule_for_equal_weights()
    {
        var even = VfTimeline.Build(Enumerable.Range(0, 30).Select(i => new VfObservation(T0.AddSeconds(i), 900_000, 2500 + (uint)i, VfSource.Residency))).Items;
        var clocks = even.Select(e => e.Mhz).Order().ToList();
        Assert.Equal(clocks[(int)Math.Round(0.9 * (clocks.Count - 1))], VfTimeline.P90Mhz(even));
        // One clock held for 9 s, the other seen for a quarter second: the held one is the P90.
        WeightedObservation[] uneven =
        [
            new(new VfObservation(T0, 900_000, 2600, VfSource.Lightweight), 0.25),
            new(new VfObservation(T0.AddSeconds(1), 900_000, 2500, VfSource.Residency), 9),
        ];
        Assert.Equal(2500u, VfTimeline.P90Mhz(uneven));
    }
}
