using System.Text.Json;
using Clocktail.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Clocktail.Tests.Mcp;

public class MandatoryConsentTests
{
    [Theory]
    [InlineData("accept", "true", true)]
    [InlineData("accept", "false", false)]
    [InlineData("accept", null, false)]
    [InlineData("accept", "\"true\"", false)]
    [InlineData("accept", "null", false)]
    [InlineData("decline", "true", false)]
    [InlineData("cancel", "true", false)]
    public async Task Real_form_response_requires_an_accepted_checked_boolean(string action, string? value, bool allowed)
    {
        ElicitRequestParams? shown = null;
        var form = Consent.ElicitationForm((request, _) =>
        {
            shown = request;
            return Task.FromResult(new ElicitResult
            {
                Action = action,
                Content = value == null ? null : new Dictionary<string, JsonElement> { ["confirm"] = JsonDocument.Parse(value).RootElement.Clone() },
            });
        }, "GPU/session/ranges", mandatory: true);
        var refusal = await Consent.AskMandatory("off", form, _ => throw new Exception("form outcomes must not fall back"), "policy", default);
        Assert.Equal(allowed, refusal == null);
        Assert.NotNull(shown);
        var field = Assert.IsType<ElicitRequestParams.BooleanSchema>(shown.RequestedSchema!.Properties["confirm"]);
        Assert.False(field.Default);
        Assert.Contains("confirm", shown.RequestedSchema.Required!);
    }

    [Fact]
    public async Task Ambiguous_form_failure_never_opens_a_second_prompt()
    {
        var form = Consent.ElicitationForm((_, _) => throw new IOException("client disconnected after display"), "policy", mandatory: true);
        Assert.NotNull(await Consent.AskMandatory("auto", form, _ => throw new Exception("must not fall back"), "policy", default));
    }

    [Fact]
    public async Task Unsupported_elicitation_can_fall_back_but_existing_callers_keep_legacy_behavior()
    {
        int dialogs = 0;
        var unavailable = Consent.ElicitationForm((_, _) => throw new McpProtocolException("not supported", McpErrorCode.MethodNotFound),
            "policy", mandatory: true);
        Assert.Null(await Consent.AskMandatory("auto", unavailable, _ => { dialogs++; return true; }, "policy", default));
        Assert.Equal(1, dialogs);
        var legacy = Consent.ElicitationForm((_, _) => throw new IOException("legacy failure"), "old caller", mandatory: false);
        Assert.Null(await Consent.Ask("auto", legacy, _ => { dialogs++; return true; }, "old caller", default));
        Assert.Equal(2, dialogs);
    }

    [Fact]
    public async Task Form_timeout_is_a_final_no()
    {
        var form = Consent.ElicitationForm(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("timeout should cancel");
        }, "policy", mandatory: true, timeout: TimeSpan.FromMilliseconds(1));
        Assert.NotNull(await Consent.AskMandatory("auto", form, _ => throw new Exception("timeout must not fall back"), "policy", default));
    }

    [Fact]
    public async Task Cancellation_between_unavailable_form_and_fallback_does_not_open_a_dialog()
    {
        using var cts = new CancellationTokenSource();
        var result = await Consent.AskMandatory("auto", _ =>
        {
            cts.Cancel();
            return Task.FromResult<bool?>(null);
        }, _ => throw new Exception("cancelled call must not open a dialog"), "policy", cts.Token);
        Assert.NotNull(result);
    }
}
