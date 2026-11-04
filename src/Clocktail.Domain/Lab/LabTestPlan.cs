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
            int duration = seconds ?? 60;
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

    public static IReadOnlyList<LabStage> Parse(string phases, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("seconds must be positive and finite");
        var items = phases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0) throw new ArgumentException("no phase given");
        bool scheduled = items.Any(p => p.Contains(':'));
        var stages = items.Select(p =>
        {
            var parts = p.Split(':');
            string phase = parts[0].ToLowerInvariant();
            if (!Phases.Contains(phase)) throw new ArgumentException($"'{phase}' is not a phase ({string.Join(", ", Phases)})");
            double duration = seconds / items.Length;
            if (scheduled && (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
                || !double.IsFinite(duration) || duration <= 0))
                throw new ArgumentException("a schedule gives every phase a positive duration: burn:30,solo:60,leap:60");
            return new LabStage(phase, duration);
        }).ToArray();
        if (scheduled && Math.Abs(stages.Sum(s => s.Seconds) - seconds) > 0.000001)
            throw new ArgumentException("phase durations must add up to seconds");
        return stages;
    }
}
