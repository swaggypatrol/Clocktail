using System.Globalization;
using System.Text.Json;

namespace Clocktail.Domain.Lab;

/// <summary>Defines and judges a score; the caller owns the worker processes and card lease.</summary>
public static class SymphonyScore
{
    public sealed record Movement(string Name, int Seconds, string? Phases = null);
    public sealed record MovementResult(int? ExitCode, JsonElement? Report, string? Error = null);
    public sealed record ScoreResult(string Verdict, IReadOnlyList<string> Reasons, IReadOnlyList<JsonElement> Reports);

    public static IReadOnlyList<Movement> Movements(LabTestPlan plan)
    {
        if (plan.Profile != "symphony" || plan.Stages.Count != 4 || plan.Stages[0].Phase != "c01"
            || string.Join(",", plan.Stages.Skip(1).Select(s => s.Phase)) != "burn,solo,leap")
            throw new ArgumentException("invalid symphony score");
        var core = plan.Stages.Skip(1).ToArray();
        return [new("aria", checked((int)plan.Stages[0].Seconds)),
            new("core", plan.Seconds - checked((int)plan.Stages[0].Seconds),
                string.Join(",", core.Select(s => s.Phase + ":" + s.Seconds.ToString("R", CultureInfo.InvariantCulture))))];
    }

    /// <summary>One movement must finish cleanly before the next starts. Observed failures survive bad exits.</summary>
    public static ScoreResult Perform(IEnumerable<Movement> movements, Func<Movement, MovementResult> run)
    {
        List<string> reasons = [];
        List<JsonElement> reports = [];
        bool ran = false;
        foreach (var movement in movements)
        {
            ran = true;
            var result = run(movement);
            if (result.Report is { ValueKind: JsonValueKind.Object } report) reports.Add(report);
            string? verdict = result.Report is { ValueKind: JsonValueKind.Object } r
                && r.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (verdict == "fail")
            {
                reasons.Add($"{movement.Name}: " + Details(result.Report, result.Error ?? "a checked workload failed"));
                return new("fail", reasons, reports);
            }
            if (result.ExitCode != 0 || verdict != "pass")
            {
                reasons.Add($"{movement.Name}: " + Details(result.Report, result.Error ?? "the movement did not complete with a verified pass"));
                return new("inconclusive", reasons, reports);
            }
        }
        return ran ? new("pass", reasons, reports) : new("inconclusive", ["the score contained no movements"], reports);
    }

    private static string Details(JsonElement? report, string fallback)
    {
        if (report is not { ValueKind: JsonValueKind.Object } r || !r.TryGetProperty("reasons", out var reasons)
            || reasons.ValueKind != JsonValueKind.Array) return fallback;
        var lines = reasons.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToArray();
        return lines.Length == 0 ? fallback : string.Join("; ", lines);
    }

}
