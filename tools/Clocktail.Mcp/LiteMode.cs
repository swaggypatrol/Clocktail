namespace Clocktail.Mcp;

/// <summary>
/// The default MCP tool list covers reading the card, tuning the V/F curve and memory, testing, and keeping or
/// restoring a profile. Full mode lists every registered tool. Both modes can call tools by name.
/// </summary>
public static class LiteMode
{
    public const string Variable = "CLOCKTAIL_TOOLS";

    /// <summary>Resolve command-line flags before the environment. An unset or unrecognized value uses lite.</summary>
    public static bool On(IReadOnlyCollection<string> args, string? mode) =>
        !args.Contains("--full") && (args.Contains("--lite") || !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase));

    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
    {
        "card_report", "limiter", "curve_around", "manual",
        "begin_session", "adjust_curve", "flatten_curve", "set_memory_offset", "lab_test", "stress_test",
        "mark_stable", "mark_crash", "back_to_daily", "end_session", "data_points",
        "stock", "load_daily", "preview_daily", "keep_as_daily", "set_daily_power_limit",
        "list_presets", "load_preset", "install_service",
    };
}
