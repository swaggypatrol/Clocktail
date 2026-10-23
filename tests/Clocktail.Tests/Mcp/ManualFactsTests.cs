using System.Reflection;
using Clocktail.Mcp;
using ModelContextProtocol.Server;

namespace Clocktail.Tests.Mcp;

/// <summary>The measured facts the agent acts on are in the manual it is served (the embedded pages, as the manual tool
/// returns them): S-3, S-6, S-9 and S-10 from the 0.2.1 review's sweep of the reference card's measurements.</summary>
public class ManualFactsTests
{
    private static readonly List<McpServerToolAttribute> Tools = typeof(Card).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToList();

    /// <summary>Every tool that writes a trial is in controls.md's table of what it changes and what keep takes; a new
    /// set_/adjust_ tool fails here until it is.</summary>
    [Fact]
    public void The_controls_table_names_every_trial_tool()
    {
        string controls = ManualTools.Manual("controls");
        var trials = Tools.Where(t => !t.ReadOnly && t.Name is { } n && (n.StartsWith("set_") || n.StartsWith("adjust_") || n is "flatten_curve" or "shift_curve")
                                      && n != "set_daily_power_limit").Select(t => t.Name!).ToList();
        Assert.Equal(17, trials.Count);
        foreach (var tool in trials)
            Assert.Contains($"| `{tool}` |", controls);
        Assert.Contains("the lower of FE and F8 holds", controls);
        Assert.Contains("`manual controls`", ManualTools.Manual("tuning"));
    }

    /// <summary>An agent learns every tool when it connects (the instructions name each one) and finds it in start's
    /// table, so a tool added without a word in the manual fails here.</summary>
    [Fact]
    public void The_instructions_and_start_name_every_tool()
    {
        string start = ManualTools.Manual("start");
        foreach (var tool in Tools.Select(t => t.Name!))
        {
            Assert.Matches($@"\b{tool}\b", ManualTools.Instructions);
            if (tool != "manual") Assert.Contains($"`{tool}`", start);
        }
    }

    [Theory]
    [InlineData("tuning", "loosen the wall or the current limit after each raise")]          // S-3
    [InlineData("tuning", "480 → 490 W, or 350 → 360 A")]
    [InlineData("stability", "were pinned with `nvidia-smi -lgc` clock locks (`set_clock_lock`")]                 // S-6
    [InlineData("stability", "55-64 965-985 mV at 430-550 W")]
    [InlineData("method", "To measure this card's own")]                                        // S-9
    [InlineData("xbar-msvdd", "SYS 2182 / 2017 / 1912 / 1830 MHz")]                              // S-10
    [InlineData("xbar-msvdd", "Runs of one setting more than 2 % apart were disturbed")]           // UncoreSearch.Disturbed (0.3.4)
    [InlineData("xbar-msvdd", "A search that ends early (lab_stop, disturbed twice) names nothing")]
    [InlineData("memory", "marks nothing stable")]                                                 // MemorySweep.End (0.3.4)
    public void The_manual_carries_it(string topic, string fact) => Assert.Contains(fact, ManualTools.Manual(topic));
}
