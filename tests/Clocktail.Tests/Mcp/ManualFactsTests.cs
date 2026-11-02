using System.Reflection;
using Clocktail.Mcp;
using ModelContextProtocol.Server;

namespace Clocktail.Tests.Mcp;

/// <summary>The embedded manual teaches per-GPU measurement and user-owned profiles, not one board's settings.</summary>
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
    [InlineData("tuning", "No fixed voltage band, offset, power target or stability margin is assumed.")]
    [InlineData("stability", "there is no universal stable offset or margin")]
    [InlineData("method", "To measure this card's own")]                                        // S-9
    [InlineData("xbar-msvdd", "There is no mandatory demand at any particular MHz offset.")]
    [InlineData("xbar-msvdd", "Runs of one setting more than 2 % apart were disturbed")]           // UncoreSearch.Disturbed (0.3.4)
    [InlineData("xbar-msvdd", "A search that ends early (lab_stop, disturbed twice) names nothing")]
    [InlineData("memory", "marks nothing stable")]                                                 // MemorySweep.End (0.3.4)
    [InlineData("controls", "stop only when the card comes within 2 °C of NVIDIA's slowdown point")]   // Nvgwls.MarginStopC (0.6.0)
    [InlineData("stability", "stop only when the card comes within 2 °C of NVIDIA's slowdown point")]
    [InlineData("power-thermal", "stop only when the card comes within 2 °C of NVIDIA's slowdown point")]
    [InlineData("controls", "stop under start, both within 0..120 °C")]                            // FanStopWrites.Invalid (0.6.0)
    [InlineData("why", "Clocktail does not write that lock")]                                      // LACT #1177: no hard P-state lock
    [InlineData("controls", "is a request, not a hard P-state lock")]                               // ClockLockCheck
    [InlineData("controls", "a layout that is not the confirmed one is refused, not guessed")]
    [InlineData("status", "A held core clock says nothing about the other domains")]
    public void The_manual_carries_it(string topic, string fact) => Assert.Contains(fact, ManualTools.Manual(topic));

    [Theory]
    [InlineData("start")]
    [InlineData("method")]
    [InlineData("controls")]
    [InlineData("blackwell")]
    [InlineData("status")]
    [InlineData("tuning")]
    [InlineData("stability")]
    [InlineData("memory")]
    [InlineData("xbar-msvdd")]
    [InlineData("power-thermal")]
    [InlineData("presets")]
    [InlineData("troubleshooting")]
    [InlineData("why")]
    public void No_manual_topic_prescribes_the_developers_board_settings(string topic)
    {
        string text = ManualTools.Manual(topic);
        Assert.DoesNotContain("5090", text);
        Assert.DoesNotContain("Founders", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reference card", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("30-60 MHz", text);
    }

    /// <summary>stress_test and lab_test keep their margin stop (Nvgwls.MarginStopC): nothing the agent reads may say
    /// there is no thermal stop by default.</summary>
    [Fact]
    public void Nothing_says_there_is_no_default_thermal_stop()
    {
        Assert.Contains($"within {Nvgwls.MarginStopC} °C of NVIDIA's slowdown point", ManualTools.Instructions);
        Assert.Contains($"within {Nvgwls.MarginStopC} °C of NVIDIA's slowdown point", ManualTools.LiteInstructions);
        foreach (var text in new[] { ManualTools.Instructions, ManualTools.LiteInstructions, ManualTools.Manual("controls"), ManualTools.Manual("method"),
                     ManualTools.Manual("stability"), ManualTools.Manual("power-thermal") })
        {
            Assert.DoesNotContain("No default software thermal stop", text);
            Assert.DoesNotContain("no software thermal ceiling", text);
            Assert.DoesNotContain("software thermal stop applies by default", text);
        }
    }

    [Fact]
    public void Startup_instructions_and_profile_help_have_no_starter_daily()
    {
        foreach (string text in new[] { ManualTools.Instructions, ManualTools.LiteInstructions, ManualTools.Manual("presets") })
        {
            Assert.Contains("no", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("daily profile", text);
            Assert.DoesNotContain("founders", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("5090", text);
        }
        Assert.Contains("no built-in tuning preset", ManualTools.Instructions);
        Assert.Contains("no built-in tuning preset", ManualTools.LiteInstructions);
        Assert.Contains("built-in `presets` list is empty", ManualTools.Manual("presets"));
    }

    [Fact]
    public void Tool_descriptions_do_not_prescribe_one_boards_tuning_settings()
    {
        var descriptions = typeof(Card).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "");
        foreach (var description in descriptions)
        {
            Assert.DoesNotContain("5090", description);
            Assert.DoesNotContain("founders", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reference card", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("575 W", description);
            Assert.DoesNotContain("600 W", description);
        }
    }
}
