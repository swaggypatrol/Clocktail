using System.Reflection;

namespace Clocktail.Lab;

/// <summary>
/// The soloist (2026-10-27, from Etude / Blackwell Symphony): the lab's most failure-sensitive core load and the one that
/// reaches the top of the curve. On the reference card the SMs that fail first fail in the integer multiply-accumulate
/// paths, the INT8 tensor core first, then the 64-bit integer MAC and dp4a; the soloist plays exactly those over a quiet
/// fp32 line (Kernels/solo.cu), judged per pipe. It is played divisi: only a share of the SMs work at a time, a window that
/// moves on by its own width every chunk, the share fitted so the board stays under its power wall, so the clock stays on
/// the curve instead of being pulled under it. A pulsed load cannot do that: the limiter acts inside every burst (on the
/// reference card the SMs ran 2480-2580 MHz inside every burst at every duty and voltage, 900-1095 mV), while NVML,
/// averaging the gaps, read a clock near the point. So the soloist also says whether the point really ran: its blocks time
/// their own work, and a pass whose clock did not hold is no evidence (<see cref="SoloRules.Held"/>).
/// </summary>
public static class SoloKernel
{
    public const string Entry = "solo";
    public const int Lanes = 4;
    public static readonly string[] LaneNames = ["ctrl", "fp32", "int", "mma8"];
    /// <summary>The soloist's mix, repetitions per unit: fp32 1, int64 MAC 2, dp4a 2, INT8 tensor mma 4 (Etude's SoloWeights).</summary>
    public const uint WFp32 = 1, WInt = 2, WDp4a = 2, WS8 = 4;

    /// <summary>The PTX (built by Kernels/build_solo.py, embedded): compute_80, which the driver compiles for the card.</summary>
    public static string Ptx()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("solo.ptx")
            ?? throw new InvalidOperationException("solo.ptx is not embedded in the lab");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

/// <summary>What one launch's blocks say: the lanes that disagree with the majority (block, SM, lane), whether a majority
/// was there at all, each playing SM's clock while it worked, and how many of the SMs meant to play ran their full share.</summary>
public sealed record SoloJudgement(IReadOnlyList<(int Block, int Sm, int Lane)> Mismatches, bool MajorityLost, int ActiveBlocks,
    double? EffMhz, double Fill, double BusyMsMax);

/// <summary>One soloist chunk. Error != null: the context is dead or the launch failed, and the run stops.</summary>
public sealed record SoloOutcome(double ElapsedMs, SoloJudgement? Judgement, string? Error = null, int? ErrorCode = null)
{
    public static SoloOutcome Failed(string error, int? code = null) => new(0, null, error, code);
}

/// <summary>The soloist's rules, pure: the judge, the rotating window, the fit to the wall and the hold.</summary>
public static class SoloRules
{
    /// <summary>The board power the fitted share aims at: 85 % of the enforced limit (Etude's DivisiPowerFraction).</summary>
    public const double TargetFraction = 0.85;
    /// <summary>At or over this share of the limit the load is on the wall, and the clock is the limiter's, not the curve's.</summary>
    public const double WallFraction = 0.95;
    /// <summary>The part of the board power that does not scale with the SMs that play (memory, uncore, fans): a quarter of the limit.</summary>
    public const double BaseFraction = 0.25;
    /// <summary>At or over this share of the limit the board is pinned to it, and its reading says nothing of what the load wants.</summary>
    public const double PinnedFraction = 0.98;
    public const double MinBreadth = 0.2, MaxBreadth = 1.0, BreadthStep = 0.1, Grid = 0.05;
    /// <summary>The blocks' own clock against NVML's: under this the SMs worked under the clock NVML reports (Etude's EffClampRatio).</summary>
    public const double EffHeldRatio = 0.93;
    /// <summary>The share of chunks in which every SM meant to play ran its full occupancy.</summary>
    public const double FillHeld = 0.95;

    /// <summary>
    /// The blocks judge each other: every block that played computed the same work, so per lane the most frequent sum is
    /// the right one (on a tie the smallest) and a block that differs computed something wrong, on the SM it names. Fewer
    /// than half agreeing on a lane: no block can be trusted (MajorityLost). <paramref name="playing"/>: the SMs meant to
    /// play; Fill counts those that ran at least <paramref name="blocksPerSm"/> blocks.
    /// </summary>
    public static SoloJudgement Judge(ReadOnlySpan<ulong> sums, ReadOnlySpan<uint> sm, ReadOnlySpan<ulong> cycles, ReadOnlySpan<ulong> ns,
        int blocksPerSm, IReadOnlyCollection<int> playing)
    {
        int blocks = sm.Length;
        var active = new List<int>(blocks);
        for (int b = 0; b < blocks; b++) if (ns[b] != 0) active.Add(b);
        var bad = new List<(int, int, int)>();
        bool lost = false;
        for (int lane = 0; lane < SoloKernel.Lanes; lane++)
        {
            var counts = new Dictionary<ulong, int>();
            foreach (int b in active) { ulong v = sums[b * SoloKernel.Lanes + lane]; counts[v] = counts.GetValueOrDefault(v) + 1; }
            if (counts.Count == 0) continue;
            var major = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
            if (major.Value * 2 < active.Count) lost = true;
            foreach (int b in active) if (sums[b * SoloKernel.Lanes + lane] != major.Key) bad.Add((b, (int)sm[b], lane));
        }
        var perSm = new Dictionary<int, (double Mhz, int Blocks)>();
        double busyMs = 0;
        foreach (int b in active)
        {
            double mhz = ns[b] > 1 ? cycles[b] * 1000.0 / ns[b] : 0;
            var e = perSm.GetValueOrDefault((int)sm[b]);
            perSm[(int)sm[b]] = (e.Mhz + mhz, e.Blocks + 1);
            busyMs = Math.Max(busyMs, ns[b] / 1e6);
        }
        var smMhz = perSm.Values.Where(v => v.Mhz > 0).Select(v => v.Mhz / v.Blocks).Order().ToList();
        double? eff = smMhz.Count == 0 ? null : Math.Round(smMhz[smMhz.Count / 2], 1);
        int full = playing.Count(s => perSm.TryGetValue(s, out var e) && e.Blocks >= blocksPerSm);
        double fill = playing.Count == 0 ? 0 : (double)full / playing.Count;
        return new SoloJudgement(bad, lost, active.Count, eff, fill, busyMs);
    }

    /// <summary>How many of <paramref name="sms"/> SMs play at <paramref name="breadth"/>: at least one.</summary>
    public static int Players(int sms, double breadth) => Math.Clamp((int)Math.Round(sms * breadth, MidpointRounding.AwayFromZero), 1, sms);

    /// <summary>The SMs that play in <paramref name="chunk"/>: a window of <paramref name="players"/> SM ids that moves on by its
    /// own width every chunk and wraps, so every SM plays in turn.</summary>
    public static int[] Window(int sms, int players, long chunk)
    {
        var w = new int[players];
        long start = chunk * players % sms;
        for (int i = 0; i < players; i++) w[i] = (int)((start + i) % sms);
        return w;
    }

    /// <summary>The kernel's mask: one bit per %smid, eight words.</summary>
    public static uint[] Mask(IEnumerable<int> sms)
    {
        var m = new uint[8];
        foreach (int s in sms) if (s is >= 0 and < 256) m[s >> 5] |= 1u << (s & 31);
        return m;
    }

    /// <summary>
    /// The next share after a window at <paramref name="breadth"/> drew <paramref name="watts"/> against <paramref name="limitW"/>:
    /// unchanged off the wall; on it, the share that would draw <see cref="TargetFraction"/> of the limit if the power above the
    /// base scales with the SMs that play, at least one <see cref="BreadthStep"/> lighter, on the <see cref="Grid"/>, never under
    /// <see cref="MinBreadth"/>. Pinned to the limit (<see cref="PinnedFraction"/>), the reading is the wall's, not the load's
    /// (the whole reference card would draw ~680 W and read 600): one step more.
    /// Null: on the wall at the lightest share already.
    /// </summary>
    public static double? Next(double breadth, double watts, double limitW)
    {
        if (!(limitW > 0) || watts < limitW * WallFraction) return breadth;
        if (breadth <= MinBreadth + 1e-9) return null;
        double baseW = limitW * BaseFraction, target = limitW * TargetFraction;
        double scaled = watts > baseW ? breadth * (target - baseW) / (watts - baseW) : breadth - BreadthStep;
        if (watts >= limitW * PinnedFraction) scaled -= BreadthStep;
        double next = Math.Floor(Math.Min(scaled, breadth - BreadthStep) / Grid + 1e-9) * Grid;
        return Math.Round(Math.Max(MinBreadth, next), 2);
    }

    /// <summary>
    /// Whether the soloist ran the point the curve gives (null: it cannot be told): the SMs' own clock within
    /// <see cref="EffHeldRatio"/> of NVML's (not stretched), the SMs meant to play running their full share in
    /// <see cref="FillHeld"/> of the chunks, and the board under its wall (else the limiter set the clock). The why when not.
    /// </summary>
    public static (bool? Held, string? Why) Held(double? effMhz, double? smMhzP50, double fill, double? wattsP50, double? limitW)
    {
        if (effMhz is not > 0 || smMhzP50 is not > 0) return (null, "no clock reading");
        if (limitW is > 0 && wattsP50 >= limitW * WallFraction)
            return (false, Inv($"on the power wall ({wattsP50:0} W of {limitW:0}): the limiter set the clock, not the curve"));
        if (fill < FillHeld) return (false, Inv($"only {fill:P0} of the chunks ran every SM meant to play (another program on the GPU?)"));
        if (effMhz < EffHeldRatio * smMhzP50)
            return (false, Inv($"the SMs ran {effMhz:0} MHz while NVML read {smMhzP50:0}: the clock was stretched or shared"));
        return (true, null);
    }

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The leap (2026-10-27): the soloist on the whole chip, then on a quarter of it, over and over. The whole chip holds the board
/// on its wall, the clock in the middle of the curve; the quarter lets go of it, and the driver lifts the clock to the curve's
/// top within a quarter of a second, the voltage following. That jump is what hung the reference card in two games six times
/// that evening (The Last of Us Part II, Gears of War) on a curve every steady test had passed: each time a heavy scene turned
/// light, the clock went from ~2850 MHz at ~925 mV to the top in one 250 ms sample and the GPU froze within a second, whether
/// the top was 3127, 3082, 3022 or 2947 MHz, and with XBAR at +560, +500 or +400. A steady soloist parks the clock on its point
/// and never makes the move; the leap does nothing else. Judged per pipe like the soloist, and a hang is the leap's failure.
/// What it does not do: on that same curve it made the games' move 80 times (2835 MHz at 910 mV to 3120 at 1005, the clock ahead
/// of the voltage, MSVDD and XBAR at the games' values) and neither hung nor computed a lane wrong. The games failed in work the
/// soloist does not play (graphics, most likely), so a pass here is the soloist's pipes through the jump, not a game's.
/// </summary>
public static class LeapRules
{
    /// <summary>The whole chip, long enough for the board to settle on its wall and the clock to come down the curve.</summary>
    public const double HeavySeconds = 1.5;
    /// <summary>The quarter, long enough for the clock to reach the top and stay there: the reference card froze within a second of it.</summary>
    public const double LightSeconds = 1.5;
    /// <summary>The share of the SMs that play when the load lets go: ~300 W at the top on the reference card, as the games drew in the jump.</summary>
    public const double LightBreadth = 0.25;
    /// <summary>A leap the clock made: the light half's highest reading at least this far over the heavy half's settled clock.</summary>
    public const double MinJumpMhz = 90;
    /// <summary>The share of the cycles whose clock must have leapt, and the fewest cycles, for a pass to be evidence.</summary>
    public const double MinJumpedShare = 0.5;
    public const int MinCycles = 3;

    /// <summary>Whether the light half's peak leapt over the heavy half's settled clock.</summary>
    public static bool Leapt(double? heavyMhz, double? peakMhz) => heavyMhz > 0 && peakMhz - heavyMhz >= MinJumpMhz;

    /// <summary>
    /// Whether the leap made its move often enough to count (null: it cannot be told). It does not when the heavy half did not
    /// hold the clock under the top (the board under its wall at a low curve, a flat curve) or the light half never reached it.
    /// </summary>
    public static (bool? Held, string? Why) Held(int cycles, int leapt, double? heavyMhz, double? peakMhz)
    {
        if (cycles < MinCycles) return (null, Inv($"{cycles} cycle{(cycles == 1 ? "" : "s")}, fewer than {MinCycles}: give it 30 s or more"));
        if (leapt < cycles * MinJumpedShare)
            return (false, Inv($"the clock leapt in {leapt} of {cycles} cycles (the heavy half held {heavyMhz:0} MHz, the light half reached {peakMhz:0}): the load never made the move games make"));
        return (true, null);
    }

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
