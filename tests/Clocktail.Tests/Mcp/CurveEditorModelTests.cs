using Clocktail.Domain.Vf;
using Clocktail.Mcp.CurveEditor;

namespace Clocktail.Tests.Mcp;

/// <summary>The curve editor's rules (the user, 2026-11-01): the low-voltage cliff reshaped in place as a smooth band that
/// lifts nothing under its foot, the person's part above it on the 7.5 MHz grid with a dragged point visible, and the
/// clock as the driver runs it.</summary>
public class CurveEditorModelTests
{
    /// <summary>The reference 5090's daily curve on 2026-11-01: a cliff 0 → +652.5 MHz over points 59..65 (820..860 mV).</summary>
    private const string Daily = "0*60,15000,60000,142500,285000,472500,652500,577500,517500,502500,487500,472500,457500,435000,420000,405000,390000,367500*2,345000,330000,315000,292500,262500,240000,217500,202500,180000,157500,120000,97500,82500,67500,52500,37500,22500,7500,-7500,-15000,-30000,-37500,-52500,-67500,-75000,-90000,-97500,-112500,-120000,-135000,-142500,-157500,-172500,-180000,-195000,-202500,-210000,-225000,-232500,-240000,-247500,-255000,-270000,-277500,-285000,-292500,-300000,-315000,-322500";

    private static readonly int[] Opened = VfOffsets.Decode(Daily);
    private static int N => Opened.Length;
    private static readonly double[] Mv = [.. Enumerable.Range(0, N).Select(i => 540 + 5.0 * i)];   // point 64 at 860 mV
    /// <summary>A stock-like curve on the 7.5 MHz grid: the floor, a ~105 MHz/point ramp, then 75 MHz a point (steep enough
    /// that the daily curve rises, as it does on the card).</summary>
    private static readonly int[] Base = [.. Enumerable.Range(0, N).Select(i => i < 46 ? 180_000 : i <= 66 ? 180_000 + (i - 45) * 105_000 : 180_000 + 21 * 105_000 + (i - 66) * 75_000)];
    private const int Lo = -1_000_000, Hi = 1_000_000;

    private static int[] Open(int[] opened, out int top, out int bottom)
    {
        top = CurveModel.CliffTop(Mv, opened, N);
        bottom = CurveModel.Foot(opened, top, N);
        return CurveModel.Apply(Mv, opened, opened, Base, top, bottom, top, N, Lo, Hi);
    }

    [Fact]
    public void The_cliff_is_found_from_its_foot_to_its_top()
    {
        Assert.Equal((61, 65), CurveModel.Cliff(Opened, N));   // 825→835 is the first step over 15 MHz; the run ends at 860 mV
        Assert.Equal(65, CurveModel.CliffTop(Mv, Opened, N));
        Assert.Equal(59, CurveModel.Foot(Opened, 65, N));   // the offsets start rising at 825 mV: the foot is 820
        // No cliff: the person's part starts at the last point under 850 mV and there is no band.
        var flat = new int[N];
        int top = CurveModel.CliffTop(Mv, flat, N);
        Assert.Equal(Array.FindLastIndex(Mv, v => v <= 850), top);
        Assert.Equal(top, CurveModel.Foot(flat, top, N));
    }

    [Fact]
    public void The_band_reshapes_the_cliff_in_place_and_lifts_nothing_under_its_foot()
    {
        var o = Open(Opened, out int top, out int bottom);
        Assert.Equal((65, 59), (top, bottom));
        for (int i = 0; i <= bottom; i++) Assert.Equal(Opened[i], o[i]);
        for (int i = top; i < N; i++) Assert.Equal(Opened[i], o[i]);
        for (int i = bottom; i < top; i++) Assert.True(o[i + 1] >= o[i], $"point {i}");   // a smoothstep rises
        for (int i = bottom + 1; i < top; i++) Assert.InRange(o[i], Opened[bottom], Opened[top]);
        Assert.All(o, x => Assert.Equal(0, x % CurveModel.GridKhz));
        Assert.Null(CurveModel.Violation(Mv, Opened,o, N, Lo, Hi));
        // Smoother than the cliff: the steepest rise of the running clock falls.
        Assert.True(CurveModel.PeakSlope(Mv, Base, o, bottom, top) < CurveModel.PeakSlope(Mv, Base, Opened, bottom, top));
        // Written and opened again, the same band is found and nothing moves.
        var again = Open(o, out int top2, out int bottom2);
        Assert.Equal((top, bottom), (top2, bottom2));
        Assert.Equal(o, again);
    }

    [Fact]
    public void Without_a_band_the_top_point_itself_can_be_dragged()
    {
        var flat = new int[N];
        var o = Open(flat, out int top, out int bottom);
        Assert.Equal(top, bottom);
        Assert.Equal(flat, o);
        var next = (int[])o.Clone();
        next[top] = 150_000;
        var d = CurveModel.Apply(Mv, flat, next, Base, top, bottom, top, N, Lo, Hi);
        Assert.Equal(150_000, d[top]);
        for (int i = 0; i < top; i++) Assert.Equal(0, d[i]);
    }

    [Fact]
    public void A_one_point_cliff_gets_a_band_of_three_intervals()
    {
        var jump = new int[N];
        for (int i = 70; i < N; i++) jump[i] = 600_000;
        Assert.Equal(70, CurveModel.CliffTop(Mv, jump, N));
        Assert.Equal(67, CurveModel.Foot(jump, 70, N));
        var o = Open(jump, out _, out _);
        Assert.Equal(0, o[67]);
        Assert.True(o[68] > 0 && o[69] > o[68] && o[69] < 600_000);
    }

    [Fact]
    public void A_dragged_point_stays_where_it_is_put_and_the_points_under_it_stay_visible()
    {
        var o = Open(Opened, out int top, out int bottom);
        var next = (int[])o.Clone();
        next[80] += 300_000;   // far above the flat part to its right
        var d = CurveModel.Apply(Mv, Opened, next, Base, top, bottom, 80, N, Lo, Hi);
        Assert.Equal(next[80], d[80]);
        for (int i = 81; i < N; i++) Assert.Equal(o[i], d[i]);   // the driver lifts them; their offsets stay
        Assert.True(CurveModel.Effective(Base, d)[85] >= CurveModel.Clock(Base, 80, d[80]));
        // Dragging it down pulls the points left of it (down to the band's top) under it, never the ones right of it.
        next = (int[])o.Clone();
        next[80] -= 400_000;
        d = CurveModel.Apply(Mv, Opened, next, Base, top, bottom, 80, N, Lo, Hi);
        for (int i = 81; i < N; i++) Assert.Equal(o[i], d[i]);
        for (int i = top; i < 80; i++) Assert.True(CurveModel.Clock(Base, i, d[i]) <= CurveModel.Clock(Base, 80, d[80]), $"point {i}");
        Assert.Equal(CurveModel.Clock(Base, 80, d[80]), CurveModel.Effective(Base, d)[80]);   // the dragged point shows
        Assert.Null(CurveModel.Violation(Mv, Opened,d, N, Lo, Hi));
    }

    [Fact]
    public void Offsets_stay_in_the_firmware_range()
    {
        var o = Open(Opened, out int top, out int bottom);
        var next = (int[])o.Clone();
        next[90] = 2_000_000;
        var d = CurveModel.Apply(Mv, Opened, next, Base, top, bottom, 90, N, Lo, Hi);
        Assert.All(d, x => Assert.InRange(x, Lo, Hi));
        Assert.NotNull(CurveModel.Violation(Mv, Opened,next, N, Lo, Hi));
    }

    [Fact]
    public void The_curve_runs_at_the_highest_landed_clock_at_or_below_each_point()
    {
        // The reference 5090's flat top: 950 mV lands at 2850, 960 mV at 2557.5 + 292.5 = 2842.5, and reads 2850.
        int[] b = [2_535_000, 2_557_000], o = [315_000, 292_500];
        Assert.Equal([2_850_000L, 2_850_000L], CurveModel.Effective(b, o));
    }

    [Fact]
    public void Smoothing_keeps_the_bands_top_the_flat_top_and_the_rules_and_smooths()
    {
        var o = Open(Opened, out int top, out int bottom);
        var jag = (int[])o.Clone();
        for (int i = top + 2; i < 90; i += 3) jag[i] -= 30_000;   // dents
        jag = CurveModel.Apply(Mv, Opened, jag, Base, top, bottom, top, N, Lo, Hi);
        var s = CurveModel.Apply(Mv, Opened, CurveModel.Smooth(Mv, jag, Base, top, N - 1, N), Base, top, bottom, top, N, Lo, Hi);
        Assert.Null(CurveModel.Violation(Mv, Opened,s, N, Lo, Hi));
        Assert.Equal(jag[top], s[top]);
        long cap = CurveModel.Effective(Base, jag).Max();
        Assert.True(CurveModel.Effective(Base, s).Max() <= cap + CurveModel.GridKhz);
        double Rough(int[] x)
        {
            var e = CurveModel.Effective(Base, x);
            return Enumerable.Range(top + 1, 30).Sum(i => Math.Pow(e[i + 1] - 2.0 * e[i] + e[i - 1], 2));
        }
        Assert.True(Rough(s) < Rough(jag));
    }

    /// <summary>A wide band's smoothstep rounds to small steps near its ends: the top is found again by following the rise,
    /// and every step of the band is a rise, so a saved band reopens exactly as it was (the first version reopened it
    /// narrower and eased it again, a different curve each time).</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(40)]
    public void A_band_widened_by_hand_is_found_again_as_it_was_saved(int foot)
    {
        var saved = CurveModel.Apply(Mv, Opened, Opened, Base, 65, foot, 65, N, Lo, Hi);
        for (int i = foot; i < 65; i++) Assert.True(saved[i + 1] > saved[i], $"point {i}");
        Assert.Equal(65, CurveModel.CliffTop(Mv, saved, N));
        Assert.Equal(foot, CurveModel.Foot(saved, 65, N));
        Assert.Equal(saved, CurveModel.Apply(Mv, saved, saved, Base, 65, foot, 65, N, Lo, Hi));
    }

    [Fact]
    public void Under_the_foot_nothing_moves_not_even_an_off_grid_offset_another_tool_wrote()
    {
        var opened = (int[])Opened.Clone();
        opened[30] = 4_000;   // Afterburner writes whole MHz
        var o = CurveModel.Apply(Mv, opened, opened, Base, 65, 59, 65, N, Lo, Hi);
        Assert.Equal(4_000, o[30]);
        Assert.Null(CurveModel.Violation(Mv, opened, o, N, Lo, Hi));   // unchanged, so not written and not judged
        var moved = (int[])o.Clone();
        moved[80] += 4_000;
        Assert.NotNull(CurveModel.Violation(Mv, opened, moved, N, Lo, Hi));
    }

    [Fact]
    public void The_cliff_top_follows_a_rise_that_goes_on_above_the_steep_part()
    {
        var o = (int[])Opened.Clone();
        o[66] = 660_000; o[67] = 667_500;   // small rises right above the cliff belong to it
        Assert.Equal(67, CurveModel.CliffTop(Mv, o, N));
        Assert.Equal(65, CurveModel.CliffTop(Mv, Opened, N));   // the daily curve falls right after its top
    }

    [Fact]
    public void Pooling_adjacent_violators_is_the_monotonic_fit()
    {
        Assert.Equal([1, 2, 2, 4], CurveModel.Isotonic([1, 3, 1, 4]));
        Assert.Equal([2, 2, 2], CurveModel.Isotonic([3, 2, 1]));
    }

    [Theory]
    [InlineData("+15 MHz on points 65-69 (860-885 mV), points 70-80 lifted to stay above", true)]
    [InlineData("flat top at 2955 MHz from 950 mV: points 81-126 lowered", true)]
    [InlineData("whole curve written: points 61-126", true)]
    [InlineData("curve shifted left 25 mV from 860 mV: points 67-126", true)]
    [InlineData("curve editor: points 60-64", true)]
    [InlineData("XBAR voltage demand -25 → -40 mV", false)]
    [InlineData("XBAR offset +560 → +400 MHz", false)]
    [InlineData("power limit 575 → 450 W", false)]
    public void A_crash_is_the_curves_evidence_only_under_a_curve_trial(string change, bool curve) =>
        Assert.Equal(curve, Clocktail.Mcp.TuningTools.CurveTrialChange(change));

    [Fact]
    public void The_offset_for_a_clock_lands_where_the_driver_lands_it()
    {
        // A base on a half step (2317.5 MHz): a raise lands in whole 15 MHz units, so +22.5 is no better than +15.
        int[] b = [2_317_500];
        Assert.Equal(2_332_000, CurveModel.Clock(b, 0, 22_500));
        Assert.Equal(15_000, CurveModel.OffsetFor(b, 0, 2_332_500));
    }
}
