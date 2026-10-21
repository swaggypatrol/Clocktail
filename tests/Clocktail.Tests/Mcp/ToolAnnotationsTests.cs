using System.Reflection;
using System.Text.RegularExpressions;
using Clocktail.Mcp;
using ModelContextProtocol.Server;

namespace Clocktail.Tests.Mcp;

/// <summary>Every tool tells a client whether it only reads, and the ones that replace or delete what the person kept
/// are exactly the ones the instructions say to ask about first.</summary>
public class ToolAnnotationsTests
{
    private static readonly List<McpServerToolAttribute> Tools = typeof(Card).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToList();

    [Fact]
    public void The_tools_that_need_the_persons_yes_are_the_destructive_ones()
    {
        var rule = Regex.Match(ManualTools.Instructions, @"Ask the person before ([^.]+)\.").Groups[1].Value;
        var asked = Regex.Matches(rule, "[a-z_]+_[a-z_]+").Select(m => m.Value).ToHashSet();
        Assert.NotEmpty(asked);
        Assert.Equal(asked.Order(), Tools.Where(t => t.Destructive && !t.ReadOnly).Select(t => t.Name!).Order());
    }

    [Fact]
    public void Reading_tools_are_read_only_and_only_card_report_reaches_the_internet()
    {
        Assert.Equal(38, Tools.Count);
        var read = Tools.Where(t => t.ReadOnly).Select(t => t.Name!).ToHashSet();
        Assert.Subset(read, new HashSet<string> { "card_report", "card_settings", "gpu_status", "limiter", "manual", "read_log", "data_points", "preview_daily" });
        Assert.DoesNotContain("adjust_curve", read);
        Assert.DoesNotContain("flatten_curve", read);
        Assert.DoesNotContain("log_note", read);   // it writes the tuning log
        Assert.Equal(["card_report"], Tools.Where(t => t.OpenWorld).Select(t => t.Name!));
    }
}
