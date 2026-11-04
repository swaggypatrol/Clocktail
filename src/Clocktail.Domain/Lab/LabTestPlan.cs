using System.Globalization;

namespace Clocktail.Domain.Lab;

public sealed record LabStage(string Phase, double Seconds);

/// <summary>Goal-based test budgets, shared by the MCP entry and the isolated load process. Custom bare phase lists
/// retain the old equal split; explicit phase:seconds schedules preserve each goal's own measurement time.</summary>
public sealed record LabTestPlan(string Profile, int Seconds, IReadOnlyList<LabStage> Stages)
{
    public static readonly IReadOnlyList<string> Phases = ["burn", "light", "pulse", "solo", "leap", "memory", "latency", "l2"];
    public string PhaseArgument => string.Join(",", Stages.Select(s => s.Phase + ":" + s.Seconds.ToString("R", CultureInfo.InvariantCulture)));

    public static LabTestPlan Create(string? profile = null, string? phases = null, int? seconds = null)
    {
        if (phases != null)
        {
            if (profile != null) throw new ArgumentException("choose a profile or custom phases, not both");
            var items = Items(phases);
            // A schedule without a total runs for the sum of its durations; a bare list keeps the legacy 60 s.
            int duration = seconds ?? (items[0].Seconds == null ? 60 : WholeSeconds(items.Sum(i => i.Seconds!.Value)));
            return new("custom", duration, Parse(phases, duration));
        }
        string goal = (profile ?? "core").Trim().ToLowerInvariant();
        LabStage[] stages = goal switch
        {
            "core" => [new("burn", 30), new("solo", 60), new("leap", 60)],
            "memory" => [new("memory", 60), new("latency", 30)],
            "uncore" => [new("l2", 30), new("memory", 30)],
            "all" => [new("burn", 30), new("solo", 60), new("leap", 60), new("memory", 60), new("latency", 30), new("l2", 30)],
            _ => throw new ArgumentException("profile is core, memory, uncore or all"),
        };
        int minimum = (int)stages.Sum(s => s.Seconds), total = seconds ?? minimum;
        if (total < minimum) throw new ArgumentException($"the {goal} profile needs at least {minimum} s; omit seconds for that budget, or give custom phases for a shorter screening run");
        return new(goal, total, stages.Select(s => s with { Seconds = s.Seconds * total / minimum }).ToArray());
    }

    /// <summary>The shortest phase a run may give: below it a phase's warm-up and sampling leave nothing to judge.</summary>
    public const double MinPhaseSeconds = 1;

    public static IReadOnlyList<LabStage> Parse(string phases, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("seconds must be positive and finite");
        var items = Items(phases);
        bool scheduled = items[0].Seconds != null;
        var stages = items.Select(i => new LabStage(i.Phase, i.Seconds ?? seconds / items.Length)).ToArray();
        if (scheduled && Math.Abs(stages.Sum(s => s.Seconds) - seconds) > 0.000001)
            throw new ArgumentException("phase durations must add up to seconds");
        if (stages.Any(s => s.Seconds < MinPhaseSeconds))
            throw new ArgumentException($"every phase needs at least {MinPhaseSeconds:0} s: give fewer phases or more seconds");
        return stages;
    }

    /// <summary>The phases, each with its duration when the list is a schedule (all have one, or none do).</summary>
    private static (string Phase, double? Seconds)[] Items(string phases)
    {
        var items = phases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0) throw new ArgumentException("no phase given");
        bool scheduled = items.Any(p => p.Contains(':'));
        return items.Select(p =>
        {
            var parts = p.Split(':', StringSplitOptions.TrimEntries);
            string phase = parts[0].ToLowerInvariant();
            if (!Phases.Contains(phase)) throw new ArgumentException($"'{phase}' is not a phase ({string.Join(", ", Phases)})");
            if (!scheduled) return (phase, (double?)null);
            if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
                || !double.IsFinite(duration) || duration <= 0)
                throw new ArgumentException("a schedule gives every phase a positive duration: burn:30,solo:60,leap:60");
            return (phase, (double?)duration);
        }).ToArray();
    }

    private static int WholeSeconds(double sum) => Math.Abs(sum - Math.Round(sum)) <= 0.000001 && sum <= int.MaxValue
        ? (int)Math.Round(sum)
        : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"the phase durations add up to {sum} s: give seconds, or whole-second totals"));
}
