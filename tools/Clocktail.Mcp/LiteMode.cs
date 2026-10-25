namespace Clocktail.Mcp;

/// <summary>
/// The lite tool set, for an agent with a small context window (a local model in Ollama, or llama.cpp at 32k): the full
/// server is about 18k tokens of tools and instructions before the person says a word (0.3.8: 52 tools, 59.5k characters),
/// more than such a window holds next to the agent's own prompt, and Ollama drops the start of a prompt that overflows
/// without a word, the instructions first (a tester, 2026-10-24: Claude Code on a local model "borked"). <c>--lite</c> or
/// CLOCKTAIL_TOOLS=lite lists only what reading the card, tuning the curve and the memory, judging and keeping need, with
/// short instructions (Manual/instructions-lite.md). A tool left out still answers when called by name.
/// </summary>
public static class LiteMode
{
    public const string Variable = "CLOCKTAIL_TOOLS";

    public static bool On(IReadOnlyCollection<string> args) =>
        args.Contains("--lite") || string.Equals(Environment.GetEnvironmentVariable(Variable), "lite", StringComparison.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
    {
        "card_report", "card_settings", "limiter", "curve_around", "manual",
        "set_tuning_policy", "begin_session", "adjust_curve", "flatten_curve", "set_memory_offset", "stress_test",
        "mark_stable", "mark_crash", "back_to_daily", "end_session", "data_points",
        "stock", "load_daily", "preview_daily", "keep_as_daily", "set_daily_power_limit",
        "list_presets", "load_preset", "install_service",
    };
}
