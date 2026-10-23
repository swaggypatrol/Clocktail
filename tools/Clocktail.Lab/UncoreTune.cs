using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Platform;

namespace Clocktail.Lab;

/// <summary>The settings the uncore tuner moves, all absolute: XBAR's clock offset and its voltage demand on MSVDD, the
/// XBAR/core clock ratio (raw U16.16; 0.8999 = 58976 stock), the voltage boost (percent: every rail's REL limit +5 mV per
/// 25 %, +20 at 100 % on a 5090, so MSVDD's ceiling and XBAR's with it).</summary>
public sealed record UncoreSetting(int XbarMhz, int DemandMv, uint RatioRaw, int BoostPct)
{
    public double Ratio => RatioRaw / 65536.0;
    public override string ToString() => FormattableString.Invariant(
        $"XBAR {XbarMhz:+0;-0;0} MHz / {DemandMv:+0;-0;0} mV, ratio {Ratio:0.0000}, boost {BoostPct} %");
}

/// <summary>
/// What tune_uncore asks for: the card, its curve, where the settings are now, and the limits. The demand rule is
/// adjust_xbar's (measured on the reference card): above CliffMhz of XBAR offset the demand must be CliffDemandMv or more,
/// above HighMhz HighDemandMv, or memory-bound work drops MSVDD to its floor and pins the core near 1610 MHz.
/// BoostKey: the boost path the card takes ("legacy" on a 5090, whose NVVDD is kind 3, or "independent"). Candidates: a
/// list to compare, each against Start, instead of the search.
/// </summary>
public sealed record UncoreSpec(string StableId, string Pci, string Curve, UncoreSetting Start,
    int XbarMaxMhz = 500, int XbarStepMhz = 50, int DemandMaxMv = 50, int DemandStepMv = 10,
    int CliffMhz = 250, int CliffDemandMv = 20, int HighMhz = 300, int HighDemandMv = 50,
    double RatioMax = 0.95, double RatioStep = 0.025, int BoostMaxPct = 100, int BoostStepPct = 25,
    int Seconds = 20, int WarmupSeconds = 60, int GraceSeconds = 45, string BoostKey = "legacy",
    IReadOnlyList<UncoreSetting>? Candidates = null)
{
    /// <summary>The least voltage demand an XBAR offset needs.</summary>
    public int Needed(int xbarMhz) => xbarMhz > HighMhz ? HighDemandMv : xbarMhz > CliffMhz ? CliffDemandMv : 0;

    /// <summary>Why a setting is outside the limits or the demand rule; null when it may be tried.</summary>
    public string? Outside(UncoreSetting s) =>
        s.XbarMhz > XbarMaxMhz ? $"XBAR past +{XbarMaxMhz} MHz"
        : s.DemandMv > DemandMaxMv ? $"demand past +{DemandMaxMv} mV"
        : s.DemandMv < Needed(s.XbarMhz) ? $"XBAR +{s.XbarMhz} MHz needs +{Needed(s.XbarMhz)} mV of demand (the cliff)"
        : s.RatioRaw > (uint)Math.Round(RatioMax * 65536) ? FormattableString.Invariant($"the ratio past {RatioMax:0.000}")
        : s.BoostPct < 0 || s.BoostPct > BoostMaxPct ? $"the boost outside 0..{BoostMaxPct} %"
        : null;
}

/// <summary>One run of the comparison, as the rules read it: the L2 (crossbar) bandwidth, DRAM bandwidth, the core clock in
/// the memory phase (the cliff pins it near 1610 MHz), whether the run can be judged at all, and the driver resets during it
/// (the service records those itself).</summary>
public sealed record UncoreRun(string Verdict, double? L2Gbps, double? DramGbps, double? MemSmMhz, double? TempMax, bool Shared, string? Detail,
    int? DriverResets = null)
{
    public static UncoreRun Of(LabResult r, int? resets)
    {
        var l2 = r.Phases.FirstOrDefault(p => p.Kind == LabPhase.L2);
        var mem = r.Phases.FirstOrDefault(p => p.Kind == LabPhase.Memory);
        bool failed = r.Verdict == LabResult.Fail || resets > 0;
        string? detail = resets > 0 ? $"a driver reset during the run ({resets})" : r.Reasons.Count > 0 ? string.Join("; ", r.Reasons) : null;
        return new(failed ? LabResult.Fail : r.Verdict, l2?.Gbps, mem?.Gbps, mem?.SmMhzP50, r.Phases.Count > 0 ? r.Phases.Max(p => p.TempMax) : null, r.Shared, detail, resets);
    }
}

/// <summary>A candidate B against the incumbent A, run A B B A: what came out and the changes the rules read (%). Gain: the
/// L2 gain held (both pairs, and enough), whatever the rules that weigh its cost said; Veto: the rule that turned it down,
/// as a value the search acts on (the cliff gets more demand) and the confirmation reads (a cost is told, not a reason to
/// name nothing).</summary>
public sealed record Comparison(string Stage, UncoreSetting A, UncoreSetting B, string Outcome, string Why,
    double? L2GainPct, double? DramChangePct, double? CoreChangePct, IReadOnlyList<UncoreRun> Runs, bool Gain = false, string? Veto = null)
{
    public const string Better = "better", NotBetter = "not better", Failed = "failed", Unjudged = "unjudged";
    /// <summary>The rules that turn a gain down: the cliff, the core's cost, DRAM's.</summary>
    public const string Cliff = "cliff", CoreCost = "core", DramCost = "dram";
}

/// <summary>What <see cref="UncoreSearch.Judge"/> makes of four runs: the outcome and why, the changes (%), and what the
/// search and the job act on as values, never as words: whether the gain held, the rule that turned it down, runs disturbed
/// during the comparison (measured again), and whether only the candidate's runs disagreed (Erratic: the incumbent's agreed).</summary>
public sealed record Judgement(string Outcome, string Why, double? L2 = null, double? Dram = null, double? Core = null,
    bool Gain = false, string? Veto = null, bool Disturbed = false, bool Erratic = false);

/// <summary>
/// The uncore search, without the card: safe-ratchet coordinate ascent from the settings on the card, one axis at a time,
/// one step at a time, a candidate kept only when the A B B A comparison says it is faster. XBAR first, each step with the
/// voltage demand the rule asks for it and more where it hits the cliff, then the clock ratio, then the voltage boost (MSVDD's
/// REL limit, +5 mV per 25 %: MSVDD's own offsets cannot be set since driver 621.01), then the ratio and the boost together, in
/// rounds: the axes hold each other (XBAR runs between the ratio's floor, 0.9 x the core, and the clock MSVDD's reliability
/// limit allows, ~30 MHz apart on the reference card at the daily +250 MHz, so neither alone moves it much), and an axis runs
/// again when another one has moved the best since it last ran. An axis stops at a failure or at two candidates in a row that are not faster; an
/// unjudged comparison (a shared GPU, the incumbent itself failing) or lab_stop ends the search.
/// </summary>
public sealed class UncoreSearch(UncoreSpec spec, Func<string, UncoreSetting, UncoreSetting, Comparison> compare, Func<bool> stopAsked)
{
    public UncoreSetting Best { get; private set; } = spec.Start;
    public List<Comparison> Log { get; } = [];
    public string? Ended { get; private set; }
    public string Stage { get; private set; } = "xbar";

    /// <summary>The most rounds over the axes; each after the first runs only the axes something has moved since.</summary>
    public const int Rounds = 4;

    public void Run()
    {
        var after = new Dictionary<string, UncoreSetting>();
        for (int round = 0; round < Rounds && Ended == null; round++)
        {
            bool ran = false;
            foreach (var (axis, climb) in new (string, Action)[] { ("xbar", Xbar), ("ratio", Ratio), ("boost", Boost), ("window", Window) })
            {
                if (Ended != null) break;
                if (after.TryGetValue(axis, out var last) && last == Best) continue;
                climb();
                after[axis] = Best;
                ran = true;
            }
            if (!ran) break;
        }
    }

    private string Try(UncoreSetting candidate)
    {
        var c = compare(Stage, Best, candidate);
        Log.Add(c);
        if (c.Outcome == Comparison.Better) Best = candidate;
        if (c.Outcome == Comparison.Unjudged) Ended = c.Why;
        if (Ended == null && stopAsked()) Ended = "stopped (lab_stop)";
        return c.Outcome;
    }

    /// <summary>
    /// The best, once more against the start in a comparison of its own (stage "confirm"): it was reached a step at a time,
    /// each step against the best before it, or picked as the fastest of a list, where one fast reading wins. It stays the
    /// best when its L2 gain over the start holds there (<see cref="Holds"/>); otherwise the best goes back to the start, and
    /// an unjudged comparison ends the search. Null when there is nothing to confirm (the best is the start, or the search
    /// ended).
    /// </summary>
    public Comparison? Confirm()
    {
        if (Ended != null || Best == spec.Start) return null;
        Stage = "confirm";
        var found = Best;
        var c = compare(Stage, spec.Start, found);
        Log.Add(c);
        if (!Holds(c)) Best = spec.Start;
        if (c.Outcome == Comparison.Unjudged) Ended = $"{found} could not be measured again against the start: {c.Why}";
        return c;
    }

    /// <summary>Whether a confirmation keeps the best: better, or a gain that held and only costs the core or DRAM more than
    /// one step may. Each step stayed within that budget; what they add up to is told and left to a game or benchmark
    /// A B B A (the reference card's 617.42 run kept steps at -0.9 and -0.5 % core, inside the core's spread there). A sum
    /// past <see cref="CoreFall"/> reads as the cliff and names nothing: five steps or more near the budget, the safe side. A
    /// DRAM fall past <see cref="DramCap"/> names nothing either.</summary>
    public static bool Holds(Comparison c) => c.Outcome == Comparison.Better
        || c.Outcome == Comparison.NotBetter && c.Gain && c.Veto is Comparison.CoreCost or Comparison.DramCost
            && c.DramChangePct is not < -DramCap * 100;

    /// <summary>Climbs one axis from the best so far: each next candidate from <paramref name="next"/>, until it gives none, a
    /// candidate fails, or two in a row are not faster. A miss is stepped past (the next candidate one step beyond it), still
    /// against the best. True when one was kept.</summary>
    private bool Climb(Func<UncoreSetting, UncoreSetting?> next)
    {
        bool kept = false;
        int misses = 0;
        var from = Best;
        while (Ended == null && next(from) is { } candidate)
        {
            string o = Try(candidate);
            if (o == Comparison.Better) { kept = true; misses = 0; }
            else if (o == Comparison.Failed || ++misses >= 2) break;
            from = candidate;
        }
        return kept;
    }

    /// <summary>XBAR's offset a step at a time, each with the least demand the rule asks for it (never less than the best's);
    /// a step that hits the cliff (the memory phase's core pinned) is tried again with more demand, a step at a time, before
    /// it counts as a miss.</summary>
    private void Xbar()
    {
        Stage = "xbar";
        int misses = 0;
        for (int x = Best.XbarMhz + spec.XbarStepMhz; x <= spec.XbarMaxMhz && Ended == null; x += spec.XbarStepMhz)
        {
            int demand = Math.Max(Best.DemandMv, spec.Needed(x));
            if (demand > spec.DemandMaxMv) break;
            string o = Try(Best with { XbarMhz = x, DemandMv = demand });
            while (Ended == null && o == Comparison.NotBetter && Log[^1].Veto == Comparison.Cliff && demand + spec.DemandStepMv <= spec.DemandMaxMv)
            {
                demand += spec.DemandStepMv;
                o = Try(Best with { XbarMhz = x, DemandMv = demand });
            }
            if (o == Comparison.Better) misses = 0;
            else if (o == Comparison.Failed || ++misses >= 2) break;
        }
    }

    private void Ratio()
    {
        Stage = "ratio";
        uint step = (uint)Math.Round(spec.RatioStep * 65536), top = (uint)Math.Round(spec.RatioMax * 65536);
        Climb(s => s.RatioRaw + step <= top ? s with { RatioRaw = s.RatioRaw + step } : null);
    }

    /// <summary>The list instead of the search: each candidate against the start, A B B A, in order; one outside the limits is
    /// skipped. The best is the fastest that was better (then confirmed on its own: <see cref="Confirm"/>); an unjudged
    /// comparison or lab_stop ends the list.</summary>
    public void RunList(IEnumerable<UncoreSetting> candidates)
    {
        Stage = "list";
        double bestGain = 0;
        foreach (var c in candidates)
        {
            if (Ended != null) break;
            if (spec.Outside(c) is { } why)
            {
                Log.Add(new Comparison(Stage, spec.Start, c, Comparison.Failed, $"not tried: {why}", null, null, null, []));
                continue;
            }
            var r = compare(Stage, spec.Start, c);
            Log.Add(r);
            if (r.Outcome == Comparison.Better && r.L2GainPct is { } g && g > bestGain) { bestGain = g; Best = c; }
            if (r.Outcome == Comparison.Unjudged) Ended = r.Why;
            if (Ended == null && stopAsked()) Ended = "stopped (lab_stop)";
        }
    }

    /// <summary>The voltage boost a step at a time: MSVDD's ceiling, and XBAR's with it (NVVDD's too, which a curve cut flat
    /// under it never reaches).</summary>
    private void Boost()
    {
        Stage = "boost";
        Climb(s => s.BoostPct + spec.BoostStepPct <= spec.BoostMaxPct ? s with { BoostPct = s.BoostPct + spec.BoostStepPct } : null);
    }

    /// <summary>XBAR's window up as a whole: the ratio (its floor) and the boost (MSVDD's ceiling) a step each, together.</summary>
    private void Window()
    {
        Stage = "window";
        uint step = (uint)Math.Round(spec.RatioStep * 65536), top = (uint)Math.Round(spec.RatioMax * 65536);
        Climb(s => s.RatioRaw + step <= top && s.BoostPct + spec.BoostStepPct <= spec.BoostMaxPct
            ? s with { RatioRaw = s.RatioRaw + step, BoostPct = s.BoostPct + spec.BoostStepPct } : null);
    }

    /// <summary>
    /// The A B B A verdict. A candidate run that failed fails the candidate; an incumbent run that failed, a shared GPU or a
    /// missing reading leaves it unjudged, and so do runs of one setting more than <see cref="Disturbed"/> apart (measured
    /// again by the job). Otherwise it is better when the L2 bandwidth rose in both pairs by more than half of
    /// <see cref="Eps"/> and by <see cref="Eps"/> or more on the geometric mean, with DRAM bandwidth not falling by more than
    /// <see cref="DramFall"/> and the memory phase's core clock not by more than <see cref="CoreLoss"/> (<see cref="CoreFall"/>
    /// is the cliff).
    /// <para>
    /// A B B A cancels a drift that runs steadily through the four runs, not one run that reads off on its own. Before 0.3.4
    /// nothing looked at the runs of one setting: A 8500, B 10030 and 10030, A 10000 was "better, +8.79 %" (pairs +18 and
    /// +0.3 %) when B was 0.3 % faster and A's first run had read 15 % low, as runs did in the first search on 621.01, with
    /// builds running. A smaller reading-off can still carry one comparison (a run 1.5 % low with no real gain does about one
    /// time in four, as in 0.3.3), so the best is measured once more against the start before it is named
    /// (<see cref="Confirm"/>). Taking off every gain the most one bad run could have added was tried and dropped: it counts
    /// the candidate's own spread three times, and at 0.25 % of noise a run it kept 59 real 1 % gains in 100, where these
    /// rules keep 95.
    /// </para>
    /// </summary>
    public static Judgement Judge(UncoreRun a1, UncoreRun b1, UncoreRun b2, UncoreRun a2)
    {
        // A bad bit or a driver reset fails the candidate; a run that could not be made (too hot, the runner) says nothing.
        foreach (var b in new[] { b1, b2 })
            if (b.Verdict == LabResult.Fail) return new(Comparison.Failed, b.Detail ?? "the candidate's run failed");
        foreach (var b in new[] { b1, b2 })
            if (b.Verdict != LabResult.Pass) return new(Comparison.Unjudged, "the candidate's run could not be judged: " + (b.Detail ?? b.Verdict));
        foreach (var a in new[] { a1, a2 })
            if (a.Verdict != LabResult.Pass) return new(Comparison.Unjudged, "the incumbent's own run did not pass: " + (a.Detail ?? a.Verdict));
        if (new[] { a1, b1, b2, a2 }.Any(r => r.Shared)) return new(Comparison.Unjudged, $"other work kept sharing the GPU for {LabJob.SharedWaitMinutes} minutes: speeds are not the card's alone; close it (a screen saver too) and run again");
        if (new[] { a1, b1, b2, a2 }.Any(r => r.L2Gbps is not > 0 || r.DramGbps is not > 0 || r.MemSmMhz is not > 0))
            return new(Comparison.Unjudged, "a reading is missing");
        double r1 = b1.L2Gbps!.Value / a1.L2Gbps!.Value, r2 = b2.L2Gbps!.Value / a2.L2Gbps!.Value;
        double ra = a2.L2Gbps!.Value / a1.L2Gbps!.Value, rb = b2.L2Gbps!.Value / b1.L2Gbps!.Value;
        double l2 = (Math.Sqrt(r1 * r2) - 1) * 100;
        double dram = (Math.Sqrt(b1.DramGbps!.Value / a1.DramGbps!.Value * (b2.DramGbps!.Value / a2.DramGbps!.Value)) - 1) * 100;
        double core = (Math.Sqrt(b1.MemSmMhz!.Value / a1.MemSmMhz!.Value * (b2.MemSmMhz!.Value / a2.MemSmMhz!.Value)) - 1) * 100;
        // The gain itself, before the rules that weigh what it costs: both pairs up, and enough on the geometric mean.
        bool gain = r1 > 1 + Eps / 2 && r2 > 1 + Eps / 2 && l2 >= Eps * 100;
        Judgement Is(string outcome, string why, string? veto = null, bool disturbed = false) =>
            new(outcome, why, Math.Round(l2, 2), Math.Round(dram, 2), Math.Round(core, 2), gain && !disturbed, veto, disturbed);
        // The cliff first: the core pinned near 1610 MHz is no reading off, and the XBAR climb answers it with more demand.
        if (core < -CoreFall * 100)
            return Is(Comparison.NotBetter, Inv($"the memory phase's core clock fell {core:0.0} %: the cliff (MSVDD at its floor pins the core near 1610 MHz)"), Comparison.Cliff);
        // Runs of one setting this far apart were disturbed (with nothing else on the GPU they agree within 0.5 %): nothing
        // in the four runs can be judged, a loss no more than a gain. A's two runs are three runs apart, so a card still
        // settling by 0.7 % a run reads as disturbed too, and is measured again as well.
        bool aOff = Math.Abs(ra - 1) > Disturbed, bOff = Math.Abs(rb - 1) > Disturbed;
        if (aOff || bOff)
            return Is(Comparison.Unjudged, Inv($"runs of one setting read {Math.Max(Math.Abs(ra - 1), Math.Abs(rb - 1)) * 100:0.0} % apart (A {a1.L2Gbps:0} then {a2.L2Gbps:0} GB/s, B {b1.L2Gbps:0} then {b2.L2Gbps:0}): over {Disturbed * 100:0} %, something moved during the comparison (other work, or the card still settling)"),
                disturbed: true) with { Erratic = bOff && !aOff };
        if (core < -CoreLoss * 100)
            return Is(Comparison.NotBetter, Inv($"the memory phase's core clock fell {core:0.00} %: a slower core costs compute-bound work more than the uncore gains"), Comparison.CoreCost);
        if (dram < -DramFall * 100)
            return Is(Comparison.NotBetter, Inv($"DRAM bandwidth fell {dram:0.00} %"), Comparison.DramCost);
        if (gain)
            return Is(Comparison.Better, Inv($"L2 bandwidth {l2:+0.00} % in both pairs"));
        return Is(Comparison.NotBetter, Inv($"no clear gain: L2 {(r1 - 1) * 100:+0.00;-0.00} / {(r2 - 1) * 100:+0.00;-0.00} % in the two pairs"));
    }

    /// <summary>The least gain kept (0.5 %): a pass of the L2 test spreads ~1-5 %, the A B B A median well under that.</summary>
    public const double Eps = 0.005;

    /// <summary>Runs of one setting further apart than this (2 %) were disturbed: with the GPU to itself a setting's runs
    /// agree within 0.5 % on the reference card, and a run disturbed in the middle read 15-18 % low there.</summary>
    public const double Disturbed = 0.02;

    /// <summary>The most DRAM bandwidth may fall (0.5 %); the most the core may (1 %: one V/F bin is ~0.5 %; on the reference
    /// card ratio 0.925 with XBAR held under MSVDD's limit cost it 1.9 %); a fall past <see cref="CoreFall"/> is the cliff.</summary>
    public const double DramFall = 0.005, CoreLoss = 0.01, CoreFall = 0.05;

    /// <summary>The most DRAM bandwidth the best may cost in its confirmation (2 %, four steps' worth), as the cliff caps the
    /// core's: past it the uncore takes from the memory what games use, and a memory phase disturbed during the confirmation
    /// reads as such a fall too. Either way nothing is named.</summary>
    public const double DramCap = 0.02;

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record UncoreStatus(string Job, string State, DateTime StartedUtc, DateTime UpdatedUtc, string Stage, UncoreSetting Start,
    UncoreSetting Best, ulong? BufferMib, IReadOnlyList<Comparison> Comparisons, string? Why, string? Next);

/// <summary>
/// The uncore tuner as a job: the search above with each comparison run on the card. Each setting goes on as a trial through
/// the service (XBAR offset and demand, MSVDD REL and the clock ratio together, absolute), the lab measures L2 and DRAM
/// bandwidth A B B A, a candidate that fails is recorded as its trial's crash. The daily profile goes back on at the end and
/// nothing is kept: the status names the best setting for the person to try, once it has beaten the daily settings again in
/// a comparison of its own.
/// </summary>
public sealed class UncoreTune(UncoreSpec spec, string jobDir, string labExe, Action<string>? log = null)
    : LabJob(spec.StableId, spec.Pci, spec.GraceSeconds, jobDir, labExe)
{
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly List<Comparison> _log = [];
    private bool _wrote;
    private UncoreSetting _best = spec.Start;
    private string _stage = "warm-up";
    private ulong? _bufferMib;

    public int Run()
    {
        using var heartbeat = Heartbeat();
        using var card = ClaimCard();
        if (card == null) return Finish(SweepStatus.Failed, "another lab job is running on this card: lab_status, or lab_stop");
        try
        {
            var show = Broker("show");
            if (show is not { Ok: true, Result: { } res } || !res.TryGetProperty("experiment", out var exp) || exp.ValueKind != JsonValueKind.Object)
                return Finish(SweepStatus.Failed, "no experiment session is open: begin_session first");
            if (exp.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object)
                return Finish(SweepStatus.Failed, "a trial is in force: back_to_daily (or keep_as_daily) first, so the tuner starts from the daily profile");

            Save(SweepStatus.Running, null, $"warming the card for {spec.WarmupSeconds} s at {spec.Start}");
            var warm = MeasureAlone(spec.WarmupSeconds, "l2,memory", null, log);
            if (warm.Verdict != LabResult.Pass) return Finish(SweepStatus.Failed, $"the warm-up at the daily settings did not pass: {string.Join("; ", warm.Reasons)}");
            if (warm.Shared) return Finish(SweepStatus.Failed, "other work shared the GPU during the warm-up: close it and run again");
            _bufferMib = warm.BufferMib;

            var search = new UncoreSearch(spec, Compare, () => StopAsked);
            if (spec.Candidates is { Count: > 0 } list) search.RunList(list); else search.Run();
            var found = search.Best;
            var confirm = search.Confirm();
            // A search that ended before its best was measured again names nothing: the status's best is what is named.
            _best = search.Ended == null ? search.Best : spec.Start;
            if (search.Ended != null)
                return Finish(StopAsked ? SweepStatus.Stopped : SweepStatus.Failed,
                    search.Ended + (confirm == null && found != spec.Start ? $"; the best so far, {found}, was not measured again against the daily settings: nothing to name" : ""));
            if (confirm == null) return Finish(SweepStatus.Done, "nothing beat the daily settings");
            if (!UncoreSearch.Holds(confirm))
                return Finish(SweepStatus.Done, $"the search's best, {found}, did not hold up when measured again against the daily settings ({confirm.Outcome}: {confirm.Why}): nothing to name");
            // Both costs, not only the rule that spoke first: a core cost would hide a DRAM fall in the same comparison.
            return Finish(SweepStatus.Done, Inv($"best {found}: L2 {confirm.L2GainPct:+0.00} % over the daily settings, measured again against them")
                + (confirm.Outcome == Comparison.Better ? "" : Inv($"; but against them the memory phase's core moved {confirm.CoreChangePct:+0.00;-0.00} % and DRAM bandwidth {confirm.DramChangePct:+0.00;-0.00} %, more than one step may cost: a game or benchmark A B B A decides")));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Finish(SweepStatus.Failed, $"the tuner stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>A B B A on the card, the whole comparison under this job's measurement lease, so no other write lands
    /// between a setting and its runs. A candidate whose run failed (a bad bit, a driver reset) is its trial's crash (after a
    /// reset, the service's); one whose run could not be made (too hot, the runner), or whose trial changed under it, is
    /// unjudged and blamed on nothing.</summary>
    private Comparison Compare(string stage, UncoreSetting a, UncoreSetting b)
    {
        _stage = stage;
        Save(SweepStatus.Running, null, $"{stage}: {b} against {a}");
        var runs = new List<UncoreRun>();
        string? stamp = null;
        UncoreRun Run(UncoreSetting s)
        {
            var started = DateTime.UtcNow;
            var r = MeasureAlone(spec.Seconds, "l2,memory", _bufferMib, log);
            int? resets = OperatingSystem.IsWindows() ? Resets(started) : null;
            var run = UncoreRun.Of(r, resets);
            runs.Add(run);
            return run;
        }
        Comparison Done(string outcome, string why, double? l2 = null, double? dram = null, double? core = null, bool gain = false, string? veto = null)
        {
            var c = new Comparison(stage, a, b, outcome, why, l2, dram, core, runs, gain, veto);
            _log.Add(c);
            string line = Inv($"{stage}: {b} vs {a}: {outcome} ({why})");
            log?.Invoke(line);
            Broker("note", $"tune_uncore: {line}");
            File.AppendAllText(Path.Combine(JobDir, "comparisons.jsonl"), JsonSerializer.Serialize(c, LabJson.Options).ReplaceLineEndings(" ") + "\n");
            return c;
        }

        using var lease = Hold(log);
        if (lease == null) return Done(Comparison.Unjudged, StopAsked ? "stopped (lab_stop)" : $"another run held the card for {LeaseWaitMinutes} minutes");

        // Runs of one setting that disagree were disturbed by other work during a run (the lab looks for it before and just
        // after a run, not during one): the comparison is measured once more, whole, and left unjudged if that is disturbed too,
        // which ends the search. Unless only the candidate's runs disagreed both times, the incumbent's agreeing: then the
        // setting itself is erratic (or heats the card as it runs), a miss for it and not the end of hours of search.
        string? disturbed = null;
        bool firstErratic = false;
        while (true)
        {
            runs.Clear();
            if (Write(a) is { } wa) return Done(Comparison.Unjudged, $"the service did not write the incumbent: {wa}");
            stamp = TrialAt();
            var a1 = Run(a);
            // lab_stop kills the measuring child: its run is inconclusive, and that is not the candidate's failure.
            if (StopAsked) return Done(Comparison.Unjudged, "stopped (lab_stop)");
            if (Moved() is { } m1) return m1;
            if (a1.Verdict != LabResult.Pass) return Done(Comparison.Unjudged, "the incumbent's own run did not pass: " + (a1.Detail ?? a1.Verdict));
            if (Write(b) is { } wb) return Done(Comparison.Failed, $"the service refused the candidate: {wb}");
            stamp = TrialAt();
            var b1 = Run(b);
            if (StopAsked) return Done(Comparison.Unjudged, "stopped (lab_stop)");
            if (b1.Verdict == LabResult.Fail) return Crash(b1);
            if (Moved() is { } m2) return m2;
            if (b1.Verdict != LabResult.Pass) return Done(Comparison.Unjudged, "the candidate's run could not be judged: " + (b1.Detail ?? b1.Verdict));
            var b2 = Run(b);
            if (StopAsked) return Done(Comparison.Unjudged, "stopped (lab_stop)");
            if (b2.Verdict == LabResult.Fail) return Crash(b2);
            if (Moved() is { } m3) return m3;
            if (b2.Verdict != LabResult.Pass) return Done(Comparison.Unjudged, "the candidate's run could not be judged: " + (b2.Detail ?? b2.Verdict));
            if (Write(a) is { } wa2) return Done(Comparison.Unjudged, $"the service did not write the incumbent back: {wa2}");
            stamp = TrialAt();
            var a2 = Run(a);
            if (StopAsked) return Done(Comparison.Unjudged, "stopped (lab_stop)");
            if (Moved() is { } m4) return m4;
            var j = UncoreSearch.Judge(a1, b1, b2, a2);
            if (j.Disturbed && disturbed == null)
            {
                disturbed = j.Why;
                firstErratic = j.Erratic;
                string again = Inv($"{stage}: {b} vs {a}: {j.Why}: measuring the comparison again");
                log?.Invoke(again);
                Broker("note", $"tune_uncore: {again}");
                continue;
            }
            if (j.Disturbed && j.Erratic && firstErratic)
                return Done(Comparison.NotBetter, $"the candidate's own runs read apart twice while the incumbent's agreed (the first time {disturbed}; then {j.Why}): an erratic setting is not kept");
            string why = disturbed == null ? j.Why
                : j.Disturbed ? $"{j.Why}, measured twice (the first time {disturbed}): leave the machine alone while the tuner runs"
                : $"{j.Why}, measured twice (the first time {disturbed})";
            return Done(j.Outcome, why, j.L2, j.Dram, j.Core, j.Gain, j.Veto);
        }

        // The trial on the card must still be the one written before the run: a write that got past the lease (or a reset
        // that put the daily profile back) means the run measured something else.
        Comparison? Moved() => TrialAt() == stamp ? null : Done(Comparison.Unjudged, "the trial on the card changed during the measurement: the run is not the setting's");

        Comparison Crash(UncoreRun r)
        {
            // A driver reset is the service's to record; a bad bit with the GPU alive is recorded here.
            if (r.DriverResets is not > 0) Broker("crash", $"tune_uncore {b}: {r.Detail}");
            Thread.Sleep(5000);
            return Done(Comparison.Failed, r.Detail ?? "the candidate's run failed");
        }
    }

    /// <summary>The setting on the card as a trial (all four cells, absolute); the service's refusal, or null when it landed.</summary>
    private string? Write(UncoreSetting s)
    {
        // The boost goes on with every setting (back to the start's under it), so a setting never inherits the last trial's.
        var writes = new List<object>
        {
            new { kind = "clockratio", key = "msvdd", value = s.RatioRaw.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            new { kind = "boost", key = spec.BoostKey, value = s.BoostPct.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        var args = new Dictionary<string, object?>
        {
            ["curve"] = spec.Curve, ["change"] = $"uncore: {s} (tune_uncore)", ["keep_curve"] = true,
            ["xbar_khz"] = s.XbarMhz * 1000, ["xbar_vdemand_uv"] = s.DemandMv * 1000,
            ["writes"] = writes,
            ["context"] = new[] { "tune_uncore: the lab compares settings A B B A" },
        };
        var r = Broker("try", JsonSerializer.Serialize(args));
        if (r is not { Ok: true }) return r?.Error ?? "no answer";
        _wrote = true;   // the end puts the daily profile back over it, however the job ends
        Thread.Sleep(3000);
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int? Resets(DateTime since)
    {
        try { return SystemLog.Count(SystemLog.DriverResetFilter, since.AddSeconds(-2)); } catch (InvalidOperationException) { return null; }
    }

    private int Finish(string state, string why)
    {
        // The daily profile goes back on whatever happened (after a driver reset the service has done it already), and the
        // status says so when it did not.
        if (_wrote && DailyBack("tune_uncore finished") is { } notBack) why += "; " + notBack;
        string? next = state == SweepStatus.Done && _best != spec.Start
            ? Inv($"best {_best}: tell the person; to try it, adjust_xbar to {_best.XbarMhz:+0;-0;0} MHz and {_best.DemandMv:+0;-0;0} mV, set_clock_ratio {_best.Ratio:0.000}, set_voltage_boost {_best.BoostPct} % (each in its own steps), then lab_test, stress_test and their games; keep_as_daily (ask)")
            : null;
        Save(state, why, null, next);
        log?.Invoke($"{state}: {why}");
        return state == SweepStatus.Failed ? 1 : 0;
    }

    private void Save(string state, string? why, string? doing, string? next = null) =>
        SaveStatus(new UncoreStatus(Path.GetFileName(JobDir), state, _started, DateTime.UtcNow, doing ?? _stage, spec.Start, _best, _bufferMib, _log, why, next));
}
