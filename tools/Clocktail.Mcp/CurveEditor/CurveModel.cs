using Clocktail.Domain.Vf;

namespace Clocktail.Mcp.CurveEditor;

/// <summary>
/// The rules the curve editor window keeps while the person drags (the user, 2026-11-01), as pure functions on the
/// per-point offsets so they are tested without a window or a card.
/// <para>
/// The curve has two parts. From the top of its low-voltage cliff up (<see cref="CliffTop"/>) it is the person's: every
/// offset on the 7.5 MHz grid and inside the firmware's range, and a dragged point stays visible (the points under it,
/// down to the cliff top, land no higher than it). Under the cliff top, where the card seldom runs under load, the curve is
/// for the eye: the person asked that it look smooth and monotonic without lifting the low points far (2026-11-01, after
/// a first version tapered 15 MHz a point down from the cliff and lifted 800 mV by 500 MHz: under the knee the chip is
/// most sensitive to voltage and nothing there is tested). So the cliff is reshaped in place as a transition band, from
/// its foot B (where the offsets start rising, <see cref="Foot"/>) to its top T: the offset eases along a smoothstep in
/// voltage, which keeps the stock ramp's own slope at both ends and bulges in the middle. The band lifts nothing under its
/// foot and nothing above its top, so the lift is bounded by the cliff the person already had (on the reference 5090's
/// daily curve: at most +184 MHz over the opened curve, at 840 mV, the peak slope 58 → 45 MHz/mV). The person drags the
/// foot (wider: gentler, more lift) and the top; a curve with no cliff is left alone.
/// </para>
/// <para>
/// The clock never falls as the voltage rises, by the driver's own doing: it reports, and runs, each point at the highest
/// landed clock of any point at or below it (the reference card's flat top: 960 mV lands at 2557.5 + 292.5 = 2842.5 MHz
/// and reads 2850, its left neighbour's). So a dip is not "fixed" by raising offsets the driver already lifts; the window
/// draws <see cref="Effective"/>. A point lands where the driver puts its offset on its base (<see cref="VfShapes.Landing"/>:
/// from a base on a half step a raise lands in whole 15 MHz units), so what is drawn is what the card runs at the
/// temperature the bases were read at.
/// </para>
/// </summary>
public static class CurveModel
{
    public const int GridKhz = 7500;
    /// <summary>A rising offset step over this is a cliff, not tuning (the person's first number, 2026-11-01).</summary>
    public const int CliffStepKhz = 15000;
    /// <summary>Where the person's part starts when the curve has no cliff: the last point at or under 850 mV.</summary>
    public const double FallbackTopMv = 850;
    /// <summary>A band narrower than this many intervals cannot be smooth: the foot moves down to make it so.</summary>
    public const int MinBandIntervals = 3;

    public static int Floor(double khz) => (int)(Math.Floor(khz / GridKhz) * GridKhz);
    public static int Ceil(double khz) => (int)(Math.Ceiling(khz / GridKhz) * GridKhz);
    public static int Snap(double khz) => (int)(Math.Round(khz / GridKhz, MidpointRounding.AwayFromZero) * GridKhz);

    /// <summary>The clock point <paramref name="i"/> lands at with offset <paramref name="offsetKhz"/>, kHz.</summary>
    public static long Clock(IReadOnlyList<int> baseKhz, int i, int offsetKhz) => VfShapes.Landing((uint)Math.Max(1, baseKhz[i]), offsetKhz);

    /// <summary>What every point runs at, kHz: the highest landed clock at or below it (the driver's own reading).</summary>
    public static long[] Effective(IReadOnlyList<int> baseKhz, IReadOnlyList<int> offsets)
    {
        var e = new long[offsets.Count];
        for (int i = 0; i < e.Length; i++) e[i] = Math.Max(i > 0 ? e[i - 1] : 0, Clock(baseKhz, i, offsets[i]));
        return e;
    }

    /// <summary>The offset on the grid whose landed clock is nearest <paramref name="targetKhz"/> (the lower one on a tie).</summary>
    public static int OffsetFor(IReadOnlyList<int> baseKhz, int i, double targetKhz)
    {
        int c = Snap(targetKhz - baseKhz[i]);
        int best = c;
        foreach (int a in new[] { c - GridKhz, c, c + GridKhz })
            if (Math.Abs(Clock(baseKhz, i, a) - targetKhz) < Math.Abs(Clock(baseKhz, i, best) - targetKhz)
                || (Math.Abs(Clock(baseKhz, i, a) - targetKhz) == Math.Abs(Clock(baseKhz, i, best) - targetKhz) && a < best)) best = a;
        return best;
    }

    /// <summary>The cliff: the first run of rising offset steps over <see cref="CliffStepKhz"/>, as (first point of the
    /// run, last point of the run); null when the curve has none.</summary>
    public static (int Start, int End)? Cliff(IReadOnlyList<int> offsets, int writable)
    {
        int n = Math.Min(writable, offsets.Count);
        for (int i = 1; i < n; i++)
        {
            if (offsets[i] - offsets[i - 1] <= CliffStepKhz) continue;
            int j = i;
            while (j + 1 < n && offsets[j + 1] - offsets[j] > CliffStepKhz) j++;
            return (i, j);
        }
        return null;
    }

    /// <summary>The top of the cliff, where the person's part of the curve starts; without a cliff, the last point at or
    /// under 850 mV (never the first or the last point, so a band and a part above it can exist).</summary>
    public static int CliffTop(IReadOnlyList<double> mv, IReadOnlyList<int> offsets, int writable)
    {
        int n = Math.Min(writable, offsets.Count);
        if (n < 3) return Math.Max(0, n - 1);
        if (Cliff(offsets, writable) is { } c)
        {
            // Up through any rise that goes on above the run: a band saved once has small steps near its top (a smoothstep
            // flattens there), and finding the same top again is what keeps a reopened band as it was saved.
            int end = c.End;
            while (end + 1 < n && offsets[end + 1] > offsets[end]) end++;
            return Math.Clamp(end, 1, n - 2);
        }
        int top = 0;
        for (int i = 0; i < n; i++) if (mv[i] <= FallbackTopMv) top = i;
        return Math.Clamp(top, 1, n - 2);
    }

    /// <summary>The foot of the cliff under <paramref name="top"/>: the last point under which the offsets do not rise
    /// (walking down from the cliff while each step rises), at least <see cref="MinBandIntervals"/> under the top; the top
    /// itself (no band) when there is no cliff under it.</summary>
    public static int Foot(IReadOnlyList<int> offsets, int top, int writable)
    {
        if (Cliff(offsets, writable) is not { } c || c.End > top) return top;
        int foot = c.Start - 1;
        while (foot > 0 && offsets[foot] - offsets[foot - 1] > 0) foot--;
        return Math.Max(0, Math.Min(foot, top - MinBandIntervals));
    }

    /// <summary>The band: points strictly between <paramref name="bottom"/> and <paramref name="top"/> take the offset a
    /// smoothstep in voltage gives between the two ends' offsets, on the grid. Nothing else changes.</summary>
    public static int[] Ease(IReadOnlyList<double> mv, IReadOnlyList<int> offsets, int bottom, int top)
    {
        var o = offsets.ToArray();
        if (bottom < 0 || top >= o.Length || top - bottom < 2) return o;
        double v0 = mv[bottom], v1 = mv[top];
        int from = o[bottom], to = o[top];
        for (int i = bottom + 1; i < top; i++)
        {
            double t = v1 > v0 ? Math.Clamp((mv[i] - v0) / (v1 - v0), 0, 1) : (double)(i - bottom) / (top - bottom);
            double s = t * t * (3 - 2 * t);
            o[i] = Snap(from + (to - from) * s);
        }
        // Each interior step at least one grid step where the span allows it: the band is then one unbroken rise, which
        // CliffTop and Foot find again as it was saved (near its ends a wide smoothstep rounds to flat steps otherwise).
        if (to - from >= (top - bottom) * GridKhz)
        {
            for (int i = bottom + 1; i < top; i++) o[i] = Math.Max(o[i], o[i - 1] + GridKhz);
            for (int i = top - 1; i > bottom; i--) o[i] = Math.Min(o[i], o[i + 1] - GridKhz);
        }
        return o;
    }

    /// <summary>
    /// The curve as the window shows it: the person's part (from <paramref name="top"/> up) from <paramref name="offsets"/>,
    /// with the point <paramref name="anchor"/> as given and the points under it, down to the top, landing no higher; under
    /// the top the band eased from the opened curve's offset at <paramref name="bottom"/> to the top's, and under the band
    /// the opened curve. Everything on the grid and inside the firmware's range; points at or past <paramref name="writable"/>
    /// as they are (the driver's setter cannot address them).
    /// </summary>
    public static int[] Apply(IReadOnlyList<double> mv, IReadOnlyList<int> opened, IReadOnlyList<int> offsets, IReadOnlyList<int> baseKhz,
        int top, int bottom, int anchor, int writable, int lowKhz, int highKhz)
    {
        var o = offsets.ToArray();
        int n = Math.Min(writable, o.Length);
        if (n == 0) return o;
        int lo = Ceil(lowKhz), hi = Floor(highKhz);
        top = Math.Clamp(top, 0, n - 1);
        bottom = Math.Clamp(bottom, 0, top);
        anchor = Math.Clamp(anchor, top, n - 1);
        // Under the band the opened curve exactly, the foot included: an off-grid offset another tool wrote stays as it is
        // and is not written (a write touches only what changes). With no band (bottom == top) the top is the person's.
        int keep = bottom < top ? bottom + 1 : bottom;
        for (int i = 0; i < keep; i++) o[i] = opened[i];
        for (int i = keep; i < n; i++) o[i] = Math.Clamp(Snap(o[i]), lo, hi);
        long cap = Clock(baseKhz, anchor, o[anchor]);
        for (int i = anchor - 1; i >= top; i--)
            while (o[i] > lo && Clock(baseKhz, i, o[i]) > cap) o[i] -= GridKhz;
        return Ease(mv, o, bottom, top);
    }

    /// <summary>The first broken rule as words, or null: what the window checks once more before it writes. Only points that
    /// differ from the opened curve are judged, as VfRules.Check judges: the write touches no other.</summary>
    public static string? Violation(IReadOnlyList<double> mv, IReadOnlyList<int> opened, IReadOnlyList<int> offsets, int writable, int lowKhz, int highKhz)
    {
        int n = Math.Min(writable, offsets.Count);
        for (int i = 0; i < n; i++)
        {
            if (offsets[i] == opened[i]) continue;
            if (offsets[i] % GridKhz != 0) return $"{mv[i]} mV 的 offset {offsets[i] / 1000.0} MHz 不在 7.5 MHz 步进上";
            if (offsets[i] < lowKhz || offsets[i] > highKhz)
                return $"{mv[i]} mV 的 offset {offsets[i] / 1000.0} MHz 超出固件范围 [{lowKhz / 1000.0}, {highKhz / 1000.0}] MHz";
        }
        return null;
    }

    /// <summary>The steepest rise of the running clock between neighbours in <paramref name="from"/>..<paramref name="to"/>,
    /// MHz per mV (0 for a flat or one-point span).</summary>
    public static double PeakSlope(IReadOnlyList<double> mv, IReadOnlyList<int> baseKhz, IReadOnlyList<int> offsets, int from, int to)
    {
        var e = Effective(baseKhz, offsets);
        double peak = 0;
        for (int i = Math.Max(1, from + 1); i <= to && i < e.Length; i++)
            if (mv[i] > mv[i - 1]) peak = Math.Max(peak, (e[i] - e[i - 1]) / 1000.0 / (mv[i] - mv[i - 1]));
        return peak;
    }

    /// <summary>
    /// One click of "平滑" on the person's part: the running clocks of points <paramref name="from"/>..<paramref name="to"/>
    /// through a Gaussian over voltage (<paramref name="sigmaMv"/>; the kernel stays inside the span, and the first point,
    /// the band's top, keeps its clock), then made monotonic by pooling adjacent violators (the least-squares monotonic
    /// fit), back onto the grid as offsets. Clicking again smooths more. The caller puts the result through
    /// <see cref="Apply"/>.
    /// </summary>
    public static int[] Smooth(IReadOnlyList<double> mv, IReadOnlyList<int> offsets, IReadOnlyList<int> baseKhz,
        int from, int to, int writable, double sigmaMv = 8)
    {
        var o = offsets.ToArray();
        to = Math.Min(to, Math.Min(writable, o.Length) - 1);
        from = Math.Max(0, from);
        if (to - from < 2) return o;
        int m = to - from + 1;
        var eff = Effective(baseKhz, o);
        var f = new double[m];
        for (int k = 0; k < m; k++) f[k] = eff[from + k];
        var s = new double[m];
        s[0] = f[0];
        for (int k = 1; k < m; k++)
        {
            double sum = 0, w = 0;
            for (int j = 0; j < m; j++)
            {
                double d = (mv[from + j] - mv[from + k]) / sigmaMv;
                if (Math.Abs(d) > 3) continue;
                double g = Math.Exp(-0.5 * d * d);
                sum += g * f[j];
                w += g;
            }
            s[k] = sum / w;
        }
        var iso = Isotonic(s);
        for (int k = 1; k < m; k++) o[from + k] = OffsetFor(baseKhz, from + k, iso[k]);
        return o;
    }

    /// <summary>Pool adjacent violators: the non-decreasing sequence nearest <paramref name="y"/> in least squares.</summary>
    public static double[] Isotonic(IReadOnlyList<double> y)
    {
        var mean = new List<double>();
        var count = new List<int>();
        foreach (var v in y)
        {
            mean.Add(v); count.Add(1);
            while (mean.Count > 1 && mean[^2] > mean[^1])
            {
                int c = count[^2] + count[^1];
                double merged = (mean[^2] * count[^2] + mean[^1] * count[^1]) / c;
                mean.RemoveAt(mean.Count - 1); count.RemoveAt(count.Count - 1);
                mean[^1] = merged; count[^1] = c;
            }
        }
        var r = new double[y.Count];
        int at = 0;
        for (int b = 0; b < mean.Count; b++) for (int k = 0; k < count[b]; k++) r[at++] = mean[b];
        return r;
    }
}
