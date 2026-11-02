using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>A confirm code is bound to the question's words: a yes for one set of values must not let other values
/// through. keep_as_daily and load_preset name the profile's values by fingerprint, the power tools to the milliwatt.</summary>
// Consent keeps its codes in one static table and drops the expired ones by the time each call passes in, so
// tests with different clocks would drop each other's codes if they ran at once.
[Collection("Consent codes")]
public class ConsentValuesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static BrokerAnswer Preview(string source, params (string Kind, string Key, string Becomes)[] changes) =>
        new(true, JsonSerializer.SerializeToElement(new
        {
            success = true, source,
            changes = changes.Select(c => new { kind = c.Kind, key = c.Key, becomes = c.Becomes, daily_now = (string?)null, card_now = c.Becomes }),
            dropped = Array.Empty<string>(),
        }), null);

    [Fact]
    public void Same_settings_with_other_values_make_another_question()
    {
        var asked = TuningTools.Changes(Preview("keep_as_daily", ("vf", "curve", "A1"), ("pstates20", "memory", "1500000")));
        var since = TuningTools.Changes(Preview("keep_as_daily", ("vf", "curve", "A1"), ("pstates20", "memory", "2000000")));
        Assert.StartsWith("V/F curve, memory offset; values fingerprint ", asked);
        Assert.StartsWith("V/F curve, memory offset; values fingerprint ", since);
        Assert.NotEqual(asked, since);
    }

    [Fact]
    public void A_code_given_for_one_set_of_values_does_not_keep_another()
    {
        string Question(string memory) =>
            $"Make what is on your graphics card now your daily profile ({TuningTools.Changes(Preview("keep_as_daily", ("pstates20", "memory", memory)))}).";
        var first = Consent.Chat(Question("1500000"), null, Now);
        Assert.NotNull(first);
        var refused = Consent.Chat(Question("2000000"), first!.ConfirmCode, Now.AddMinutes(1));
        Assert.NotNull(refused);
        Assert.Equal("confirm_in_chat", refused!.Code);
        Assert.NotEqual(first.ConfirmCode, refused.ConfirmCode);
        // The new question's own code goes through.
        Assert.Null(Consent.Chat(Question("2000000"), refused.ConfirmCode, Now.AddMinutes(2)));
    }

    [Fact]
    public void Fingerprint_is_the_values_not_their_order()
    {
        var a = Preview("keep_as_daily", ("vf", "curve", "A1"), ("pstates20", "memory", "1500000"));
        var b = Preview("keep_as_daily", ("pstates20", "memory", "1500000"), ("vf", "curve", "A1"));
        Assert.Equal(TuningTools.Fingerprint(a), TuningTools.Fingerprint(b));
        Assert.Matches("^[0-9A-F]{6}$", TuningTools.Fingerprint(a));
        Assert.NotEqual(TuningTools.Fingerprint(a), TuningTools.Fingerprint(Preview("keep_as_daily (adopt)", ("vf", "curve", "A1"), ("pstates20", "memory", "1500000"))));
    }

    [Fact]
    public void A_profile_saved_again_under_the_name_makes_another_question()
    {
        var before = TuningTools.Changes(Preview("'quiet'", ("power", "ordinary", "300000")));
        var after = TuningTools.Changes(Preview("'quiet'", ("power", "ordinary", "450000")));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void No_change_still_names_the_fingerprint()
    {
        Assert.StartsWith("no change from the daily profile; values fingerprint ", TuningTools.Changes(Preview("keep_as_daily")));
    }

    [Fact]
    public void Power_questions_name_the_milliwatts_written()
    {
        Assert.Contains("to 300 W,", TuningTools.DailyPowerLimitQuestion(300_000));
        Assert.Contains("to 300.04 W,", TuningTools.DailyPowerLimitQuestion(300_040));
        Assert.NotEqual(TuningTools.DailyPowerLimitQuestion(300_000), TuningTools.DailyPowerLimitQuestion(300_040));
        Assert.Contains("(F8) to 450.001 W for a test", TuningTools.PowerCapQuestion(450_001));
        Assert.NotEqual(TuningTools.PowerCapQuestion(450_000), TuningTools.PowerCapQuestion(450_040));
    }
}
