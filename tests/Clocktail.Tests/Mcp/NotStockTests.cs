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
    public void The_persons_yes_is_asked_in_the_chat_with_a_code_for_that_question()
    {
        const string what = "Make the card's settings your daily profile.";
        var now = new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);

        var first = Consent.Chat(what, null, now);                                             // no code: nothing done, the question
        Assert.NotNull(first);
        Assert.Equal("confirm_in_chat", first!.Code);
        Assert.Equal(what, first.Question);
        Assert.Contains(what, first.Error);
        Assert.Matches("^[A-Z2-9]{6}$", first.ConfirmCode!);

        Assert.NotNull(Consent.Chat("Delete the profile saved as 'quiet'.", first.ConfirmCode, now));   // another question's code
        Assert.Null(Consent.Chat(what, first.ConfirmCode!.ToLowerInvariant(), now.AddMinutes(1)));     // the yes, any case
        Assert.Contains("not this change's", Consent.Chat(what, first.ConfirmCode, now.AddMinutes(1))!.Error);   // once only

        var late = Consent.Chat(what, null, now)!;
        Assert.NotNull(Consent.Chat(what, late.ConfirmCode, now + Consent.CodeValid));         // expired
        Assert.NotNull(Consent.Chat(what, "", now));                                           // an empty code asks
    }

    [Fact]
    public async Task Outside_the_chat_the_yes_comes_from_the_agents_form_or_else_a_windows_dialog()
    {
        bool asked = false;
        bool? Dialog(bool? answer) { asked = true; return answer; }
        const string what = "Make the card's settings your daily profile.";

        Assert.Null(await Consent.Ask("form", Form(true), _ => Dialog(false), what, default));
        Assert.False(asked);                                                                   // the form answered
        Assert.Contains("did not confirm", await Consent.Ask("form", Form(false), _ => Dialog(true), what, default));
        Assert.False(asked);                                                                   // a no in the form is final

        Assert.Null(await Consent.Ask("form", Form(null), _ => Dialog(true), what, default)); // no form shown: the dialog
        Assert.True(asked);
        Assert.Null(await Consent.Ask("form", null, _ => true, what, default));                // no elicitation (Freebuff)
        Assert.Contains("Windows dialog", await Consent.Ask("form", null, _ => false, what, default));
        Assert.Contains(Consent.Variable + "=chat", await Consent.Ask("form", null, _ => null, what, default));   // nobody to ask

        Assert.Contains("Windows dialog", await Consent.Ask("dialog", Form(true), _ => false, what, default));  // dialog only
        Assert.Contains("stopped waiting", await Consent.Ask("form", null, _ => true, what, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Chat_is_the_default_and_auto_still_means_the_form()
    {
        string? old = Environment.GetEnvironmentVariable(Consent.Variable);
        try
        {
            Environment.SetEnvironmentVariable(Consent.Variable, null);
            Assert.Equal("chat", Consent.Mode);
            Environment.SetEnvironmentVariable(Consent.Variable, "auto");
            Assert.Equal("form", Consent.Mode);
            Environment.SetEnvironmentVariable(Consent.Variable, "off");
            Assert.Equal("chat", Consent.Mode); // Explicit approval cannot be disabled.
        }
        finally { Environment.SetEnvironmentVariable(Consent.Variable, old); }
    }

    [Fact]
    public void The_tools_that_stay_ask_the_person_themselves()
    {
        var asking = typeof(Card).Assembly.GetTypes().SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is { } a && a.Destructive && !a.ReadOnly && a.Name != "install_service")   // UAC asks for that one
            .ToList();
        Assert.Equal(6, asking.Count);   // stock asks only when it would clear another tool's settings
        foreach (var m in asking)
        {
            Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(McpServer));     // to put the question to the client
            Assert.Contains(m.GetParameters(), p => p.Name == "confirm" && p.ParameterType == typeof(string) && p.HasDefaultValue);   // the chat's code
            Assert.Contains("asks the person itself", m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description);
        }
    }
}
