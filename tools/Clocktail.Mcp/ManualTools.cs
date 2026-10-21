using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// The agent's manual: what an RTX 50 card is made of, how to read it, and how to tune it, in topics embedded in this
/// server (Manual/*.md). The server's instructions are the short version every session starts with; this is the rest,
/// read on demand. Each topic's first line is its title and its second line the one-line summary the index shows.
/// </summary>
[McpServerToolType]
public sealed class ManualTools
{
    private const string Prefix = "manual.";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Topics = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .ToDictionary(n => n[Prefix.Length..^3], n =>
            {
                using var r = new StreamReader(asm.GetManifestResourceStream(n)!);
                return r.ReadToEnd().Replace("\r\n", "\n");
            }, StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>The server instructions: Manual/instructions.md, sent to every agent when it connects.</summary>
    public static string Instructions => Topics.Value.TryGetValue("instructions", out var t) ? t : "";

    [McpServerTool(Name = "manual", ReadOnly = true, OpenWorld = false), Description(
        "The Clocktail manual for the agent: the tuning method phase by phase and why each step comes where it does, " +
        "answers to the questions people ask, Blackwell (voltage rails, clock domains, the V/F curve, what limits the clock), " +
        "how to read every status field, every control and how to combine them, stability testing, presets and troubleshooting. " +
        "Call it with no topic for the index. Read 'start', 'method' and 'controls' before the first tuning session, 'why' before answering a why, and the matching topic " +
        "before answering a question about the card you are not sure of.")]
    public static string Manual([Description("The topic, from the index; omit for the index.")] string? topic = null)
    {
        var topics = Topics.Value;
        if (!string.IsNullOrWhiteSpace(topic) && topics.TryGetValue(topic.Trim(), out var text) && !topic.Trim().Equals("instructions", StringComparison.OrdinalIgnoreCase))
            return text;
        var index = topics.Where(t => !t.Key.Equals("instructions", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => Order(t.Key)).ThenBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => $"- {t.Key}: {Summary(t.Value)}");
        var head = string.IsNullOrWhiteSpace(topic) ? "" : $"No topic named '{topic}'.\n\n";
        return $"{head}# Clocktail manual\n\nCall manual with one of these topics:\n\n{string.Join("\n", index)}\n";
    }

    private static readonly string[] Ordered = ["start", "method", "controls", "why", "blackwell", "status", "tuning", "stability", "memory", "xbar-msvdd", "power-thermal", "presets", "troubleshooting"];
    private static int Order(string key) { int i = Array.IndexOf(Ordered, key.ToLowerInvariant()); return i < 0 ? Ordered.Length : i; }

    private static string Summary(string text) =>
        text.Split('\n').Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
}
