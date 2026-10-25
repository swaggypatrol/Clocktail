using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Clocktail.Mcp;
using ModelContextProtocol.Server;

namespace Clocktail.Tests.Mcp;

/// <summary>
/// 0.4.0, from a tester's run with five agents (2026-10-24): another tool's settings are named as such and never as stock,
/// an overclocking program running is said, the lite tool set is real, and the person's own yes is asked for the changes
/// that stay.
/// </summary>
public class NotStockTests
{
    private static List<(string Name, string Mark, string Note)> Rows(string settings) =>
        [.. SettingsTools.ReportRows(JsonDocument.Parse(settings).RootElement, JsonDocument.Parse("{}").RootElement)];

    private const string Afterburner = """
        "not_set_by_clocktail":[
          {"setting":"V/F curve","cell":"vf/core","stock":"stock","found":"67 of 127 points offset, -150 to +90 MHz","now":"67 of 127 points offset, -150 to +90 MHz","clocktail_holds":false},
          {"setting":"memory offset","cell":"pstates20/memory","stock":"0 MHz","found":"+1000 MHz","now":"+1000 MHz","clocktail_holds":false}],
        "vf_curve":{"offset_points":67,"min_offset_mhz":-150,"max_offset_mhz":90},
        "clock_offsets":{"memory":{"offset_mhz":1000,"stock_mhz":0,"min_mhz":-1000,"max_mhz":3000}}
        """;

    [Fact]
    public void Another_tools_settings_are_a_row_of_their_own_and_the_card_is_not_called_stock()
    {
        var rows = Rows("{" + Afterburner + "}");
        var other = Assert.Single(rows, r => r.Name == "Not set by Clocktail");
        Assert.Equal(SettingsTools.Look, other.Mark);
        Assert.Contains("NOT at stock", other.Note);
        Assert.Contains("keep_as_daily adopt=true", other.Note);
        Assert.Contains("set by another tool", Assert.Single(rows, r => r.Name == "V/F curve").Note);
        Assert.Contains("set by another tool", Assert.Single(rows, r => r.Name == "Memory offset").Note);
        var daily = Assert.Single(Now("{" + Afterburner + "}"), r => r.Name == "Daily profile");
        Assert.Equal("none: the card runs what another tool set, not stock (see 'Not set by Clocktail')", daily.Now);
        Assert.Equal("none: the card runs stock", Assert.Single(Now("{}"), r => r.Name == "Daily profile").Now);
    }

    private static IReadOnlyList<(string Name, string Now)> Now(string settings) =>
        SettingsTools.ReportNow(JsonDocument.Parse(settings).RootElement, JsonDocument.Parse("{}").RootElement);

    [Fact]
    public void Without_them_there_is_no_such_row()
    {
        var rows = Rows("""{"vf_curve":{"offset_points":0},"clock_offsets":{"memory":{"offset_mhz":0,"stock_mhz":0}}}""");
        Assert.DoesNotContain(rows, r => r.Name is "Not set by Clocktail" or "Overclocking tool running");
        Assert.DoesNotContain("set by another tool", Assert.Single(rows, r => r.Name == "V/F curve").Note);
    }

    [Fact]
    public void A_running_overclocking_program_is_a_row_and_named_in_the_other_tools_note()
    {
        var rows = Rows("{" + Afterburner + ""","other_oc_tools":["MSI Afterburner"]}""");
        var running = Assert.Single(rows, r => r.Name == "Overclocking tool running");
        Assert.Equal(SettingsTools.Look, running.Mark);
        Assert.Contains("Apply overclocking at system startup", running.Note);
        Assert.StartsWith("MSI Afterburner left these", Assert.Single(rows, r => r.Name == "Not set by Clocktail").Note);
    }

    [Fact]
    public void Running_programs_are_named_once_each()
    {
        var running = OtherTools.Running(p => p is "MSIAfterburner" or "GPU Tweak III");
        Assert.Equal(["MSI Afterburner", "ASUS GPU Tweak III"], running);
        Assert.Empty(OtherTools.Running(_ => throw new InvalidOperationException("no access")));
        Assert.DoesNotContain(OtherTools.Known, k => k.Process is "RTSS" or "GPU-Z" or "HWiNFO64");   // they only read
    }

    private static readonly List<McpServerToolAttribute> Tools = typeof(Card).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToList();

    [Fact]
    public void The_lite_set_names_real_tools_and_its_instructions_name_them()
    {
        var names = Tools.Select(t => t.Name!).ToHashSet();
        Assert.Subset(names, LiteMode.Tools.ToHashSet());
        Assert.InRange(LiteMode.Tools.Count, 15, 25);
        foreach (var t in new[] { "card_report", "stock", "keep_as_daily", "stress_test", "begin_session" })
            Assert.Contains(t, LiteMode.Tools);
        foreach (Match m in Regex.Matches(ManualTools.LiteInstructions, @"\b[a-z]+_[a-z_]+\b"))
            if (names.Contains(m.Value)) Assert.Contains(m.Value, LiteMode.Tools);   // it names no tool it does not list
        Assert.Contains("NOT at stock", ManualTools.LiteInstructions);
        Assert.True(ManualTools.LiteInstructions.Length < ManualTools.Instructions.Length / 2);
        Assert.DoesNotContain("instructions", ManualTools.Manual(null));   // neither is a manual topic
    }

    [Theory]
    [InlineData("🩺 Card report · ✅ 45 °C → 600 W")]
    [InlineData(@"a path C:\uD83E\uDE7A stays text")]
    [InlineData("\\\\🧠 after an escaped backslash")]
    public void Answers_carry_emoji_as_themselves_and_stay_the_same_json(string text)
    {
        string json = JsonText.Serialize(new { text }, SettingsTools.Json);
        Assert.Equal(text, JsonDocument.Parse(json).RootElement.GetProperty("text").GetString());
        Assert.DoesNotMatch(@"(?<!\\)\\u[dD][89abAB]", json.Replace(@"\\", ""));   // no escaped surrogate left
    }

    [Fact]
    public void Lite_is_an_argument_or_the_variable()
    {
        Assert.True(LiteMode.On(["--lite"]));
        Assert.False(LiteMode.On(["--worker"]));
    }

    private static Func<CancellationToken, Task<bool?>> Form(bool? answer) => _ => Task.FromResult(answer);

    [Fact]
    public async Task The_persons_yes_comes_from_the_agents_form_or_else_a_windows_dialog()
    {
        bool asked = false;
        bool? Dialog(bool? answer) { asked = true; return answer; }
        const string what = "Make the card's settings your daily profile.";

        Assert.Null(await Consent.Ask("auto", Form(true), _ => Dialog(false), what, default));
        Assert.False(asked);                                                                   // the form answered
        Assert.Contains("did not confirm", await Consent.Ask("auto", Form(false), _ => Dialog(true), what, default));
        Assert.False(asked);                                                                   // a no in the form is final

        Assert.Null(await Consent.Ask("auto", Form(null), _ => Dialog(true), what, default)); // no form shown: the dialog
        Assert.True(asked);
        Assert.Null(await Consent.Ask("auto", null, _ => true, what, default));                // no elicitation (Freebuff)
        Assert.Contains("Windows dialog", await Consent.Ask("auto", null, _ => false, what, default));
        Assert.Contains(Consent.Variable + "=off", await Consent.Ask("auto", null, _ => null, what, default));   // nobody to ask

        Assert.Contains("Windows dialog", await Consent.Ask("dialog", Form(true), _ => false, what, default));  // dialog only
        Assert.Null(await Consent.Ask("off", Form(false), _ => false, what, default));         // the person opted out
        Assert.Contains("stopped waiting", await Consent.Ask("auto", null, _ => true, what, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void The_tools_that_stay_ask_the_person_themselves()
    {
        var asking = typeof(Card).Assembly.GetTypes().SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is { } a && a.Destructive && !a.ReadOnly && a.Name != "install_service")   // UAC asks for that one
            .ToList();
        Assert.Equal(7, asking.Count);   // stock asks only when it would clear another tool's settings
        foreach (var m in asking)
        {
            Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(McpServer));     // to put the question to the client
            Assert.Contains("asks the person itself", m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description);
        }
    }
}
