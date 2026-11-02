using System.Globalization;

namespace Clocktail.Domain.Clocks;

/// <summary>One client clock limit set against the core clock a run actually sampled.</summary>
/// <param name="What">The limit in words (<see cref="ClientLimit.Describe"/>).</param>
/// <param name="Kind">"max", "min" or "pinned" (a V/F point lock or Boost Lock); "unknown" for an entry not decoded.</param>
/// <param name="TargetMhz">The clock the limit names: the cap, the floor, or the locked point's clock on the curve.</param>
/// <param name="OffPct">Share of the samples that ran on the wrong side of it (over a cap, under a floor or a pinned point),
/// to 0.1 %, for display: <paramref name="OffSamples"/> decides whether it held.</param>
/// <param name="OffSamples">How many samples ran on the wrong side of it; held only when none did.</param>
/// <param name="Words">What that means, for the agent and the log.</param>
public sealed record ClockLockOutcome(string What, string Kind, double? TargetMhz, double? P10Mhz, double? P50Mhz, double? P90Mhz,
    double? OffPct, int? OffSamples, string Words);

/// <summary>
/// Whether the client clock limits in force (<see cref="ClientLimitsLayout"/>) were what a run actually ran at. The record is
/// a request to the driver, not a hard P-state lock: the driver still lowers the clock under it for power, temperature and
/// voltage, and a floor or a point lock it could not hold shows as samples under the target. Setter success and readback say
/// only that the request is stored; the sampled clock says what ran. (MODS' hard lock, which stops that derating, is not
/// something Clocktail writes; see the manual's "why".)
/// </summary>
public static class ClockLockCheck
{
    /// <summary>Reading slack, not a tuning margin: two steps of the curve's 7.5 MHz grid, so a clock one grid step off a
    /// cap (nvidia-smi -lgc 1200,2400 ran at most 2392 MHz on the reference card) does not count as off it.</summary>
    public const double SlackMhz = 15;

    /// <summary>
    /// Each limit in <paramref name="held"/> against <paramref name="clocksMhz"/> (the samples under load).
    /// <paramref name="pinnedMhz"/> gives the clock a voltage lock pins (the nearest curve point's, or the curve's top for
    /// Boost Lock); null when it is unknown.
    /// </summary>
    public static IReadOnlyList<ClockLockOutcome> Check(IReadOnlyList<ClientLimit> held, IReadOnlyList<double> clocksMhz,
        Func<ClientLimit, double?> pinnedMhz)
    {
        var sorted = clocksMhz.Order().ToList();
        double? Q(double q) => sorted.Count == 0 ? null : Math.Round(sorted[(int)Math.Clamp(Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)], 1);
        var list = new List<ClockLockOutcome>();
        foreach (var l in held)
        {
            string what = l.Describe();
            var (kind, target) = (l.Domain, l.Mode) switch
            {
                (0, 2) => ("max", l.Value / 1000.0),
                (1, 2) => ("min", l.Value / 1000.0),
                (6, 3) => ("pinned", pinnedMhz(l)),
                _ => ("unknown", (double?)null),
            };
            if (target is not { } t)
            {
                list.Add(new ClockLockOutcome(what, kind, null, Q(0.1), Q(0.5), Q(0.9), null, null,
                    kind == "unknown" ? "not decoded: compare the clocks by hand" : "the locked point's clock is unknown (curve unreadable)"));
                continue;
            }
            int? off = sorted.Count == 0 ? null : kind == "max" ? sorted.Count(c => c > t + SlackMhz) : sorted.Count(c => c < t - SlackMhz);
            double? pct = off is { } n ? Math.Round(100.0 * n / sorted.Count, 1) : null;
            list.Add(new ClockLockOutcome(what, kind, Math.Round(t, 1), Q(0.1), Q(0.5), Q(0.9), pct, off, Words(kind, t, off, sorted.Count, pct, Q(0.1), Q(0.9))));
        }
        return list;
    }

    private static string Words(string kind, double target, int? off, int count, double? pct, double? p10, double? p90)
    {
        if (off is not { } n) return "no samples under load: nothing to compare the lock with";
        string I(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        string share = I($"{n} of {count} samples ({pct:0.#} %)");
        if (kind == "max")
            return n == 0 ? I($"held: no sample over {target:0} MHz (p90 {p90:0})")
                : I($"{share} ran over the {target:0} MHz cap (p90 {p90:0}): the cap was not what held the clock; read card_settings' clock_locks again");
        string side = kind == "min" ? "floor" : "locked point";
        return n == 0 ? I($"held: no sample under the {side}'s {target:0} MHz (p10 {p10:0})")
            : I($"{share} ran under the {side}'s {target:0} MHz (p10 {p10:0}): the driver derated under the lock (power, temperature or voltage; see limit reasons), so the run did not test {target:0} MHz throughout");
    }
}
