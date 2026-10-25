using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// The person's own yes for a change that stays: a new daily profile (keep_as_daily, load_preset), the daily power limit,
/// the F8 power request that cannot be deleted, a saved profile deleted. The instructions always said "ask the person
/// first", but an agent that runs every tool without asking (Freebuff, an auto-approve mode) and a model that skips the
/// rule made it a wish: a tester saw one change the card unasked (2026-10-24), and after the one UAC prompt at install
/// nothing else stands between an agent and the service. So Clocktail asks the person itself: in the agent, as an MCP
/// elicitation form, where the client offers one (Claude Code, Cursor, VS Code, Codex, Copilot CLI); else in a Windows
/// dialog on the desktop, which no model can answer (only when the agent's question could not be shown at all: a declined
/// or dismissed one is a no). Each caller's question says how long its change lasts. Trials need no yes: each is one bounded step, recorded, and undone
/// by back_to_daily, end_session or a crash. CLOCKTAIL_CONFIRM: "auto" (the default), "dialog" (always the Windows
/// dialog), "off" (skip for existing callers). Device/override policy approval always uses mandatory confirmation.
/// </summary>
public static class Consent
{
    public const string Variable = "CLOCKTAIL_CONFIRM";

    /// <summary>How long the Windows dialog waits for an answer before it counts as a no.</summary>
    public static readonly TimeSpan DialogWait = TimeSpan.FromMinutes(2);

    /// <summary>How long an elicitation may wait for the person before it counts as no answer.</summary>
    public static readonly TimeSpan FormWait = TimeSpan.FromMinutes(5);

    public static string Mode => Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant() switch
    {
        "off" or "0" or "false" or "no" => "off",
        "dialog" => "dialog",
        _ => "auto",
    };

    /// <summary>Null when the person said yes (or asking is off); else why nothing may be done, for the agent.</summary>
    public static Task<string?> Ask(McpServer? server, string what, CancellationToken ct) =>
        Ask(Mode, Form(server, what), q => Dialog(q), what, ct);

    public static Task<string?> AskMandatory(McpServer? server, string what, CancellationToken ct) =>
        AskMandatory(Mode, Form(server, what, mandatory: true), q => Dialog(q), what, ct);

    public static async Task<string?> AskMandatory(string mode, Func<CancellationToken, Task<bool?>>? form,
        Func<string, bool?> dialog, string what, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return "Confirmation was cancelled: nothing was changed";
        Func<CancellationToken, Task<bool?>>? guardedForm = form == null ? null : async token =>
        {
            var answer = await form(token);
            return token.IsCancellationRequested ? false : answer;
        };
        var refusal = await Ask(mode == "dialog" ? "dialog" : "auto", guardedForm,
            question => ct.IsCancellationRequested ? null : dialog(question), what, ct);
        if (ct.IsCancellationRequested) return "Confirmation was cancelled: nothing was changed";
        return refusal == null ? null : "Mandatory confirmation was not granted. Call set_tuning_policy explicitly to ask again; CLOCKTAIL_CONFIRM=off cannot approve this policy.";
    }

    /// <summary>
    /// The decision, with the two ways of asking passed in: <paramref name="form"/> (null when the client offers no
    /// elicitation) answers true for yes, false for no and null when it could not ask; <paramref name="dialog"/> the same
    /// for the Windows dialog. A form that could not ask falls back to the dialog; a no is final.
    /// </summary>
    public static async Task<string?> Ask(string mode, Func<CancellationToken, Task<bool?>>? form, Func<string, bool?> dialog, string what, CancellationToken ct)
    {
        if (mode == "off") return null;
        if (mode == "auto" && form != null)
        {
            switch (await form(ct))
            {
                case true: return null;
                case false: return Declined("in the agent's question");
            }
        }
        return dialog(what) switch
        {
            // The dialog cannot see the call being cancelled: an agent that stopped waiting gets nothing done behind its back.
            true when ct.IsCancellationRequested => "the agent stopped waiting before the person answered: nothing was changed",
            true => null,
            false => Declined($"in the Windows dialog, or did not answer it within {DialogWait.TotalMinutes:0} minutes"),
            null => "Clocktail could not ask the person: the question could not be shown in the agent and no Windows desktop answered. " +
                    $"Nothing was changed. Ask them in chat; if they want these changes without a question, they can set {Variable}=off in this " +
                    $"server's environment ({Variable}=dialog where a desktop is there to answer).",
        };
    }

    private static string Declined(string where) =>
        $"the person did not confirm it ({where}): nothing was changed. Ask them in chat whether they want it; calling again asks them again";

    /// <summary>The elicitation form, or null when the client offers none: one checkbox the person ticks, so an empty
    /// accept (Codex auto-accepts a form with no fields) is not a yes.</summary>
    private static Func<CancellationToken, Task<bool?>>? Form(McpServer? server, string what, bool mandatory = false) =>
        server?.ClientCapabilities?.Elicitation is null ? null
            : ElicitationForm(async (request, token) => await server.ElicitAsync(request, token), what, mandatory);

    // The same request/response path is exercised by software tests without a client or Windows dialog.
    internal static Func<CancellationToken, Task<bool?>> ElicitationForm(
        Func<ElicitRequestParams, CancellationToken, Task<ElicitResult>> elicit, string what,
        bool mandatory, TimeSpan? timeout = null) => async ct =>
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout ?? FormWait);
            try
            {
                var r = await elicit(new ElicitRequestParams
                {
                    Message = $"Clocktail: {what}",
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties =
                        {
                            ["confirm"] = new ElicitRequestParams.BooleanSchema
                            {
                                Title = "Yes, do this", Description = "Your AI agent asked for it. Tick and accept to allow it.", Default = false,
                            },
                        },
                        Required = ["confirm"],
                    },
                }, wait.Token);
                // Declined or dismissed (cancel: Esc in Claude Code) is the person's no, not a question that could not be shown.
                if (!r.IsAccepted) return false;
                return r.Content is { } c && c.TryGetValue("confirm", out var v) && v.ValueKind == JsonValueKind.True;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }   // nobody answered
            catch (McpProtocolException e) when (e.ErrorCode == McpErrorCode.MethodNotFound) { return null; }
            // A transport or decoding failure may happen after the form was shown. Mandatory consent must not
            // open a second prompt when its display/outcome is uncertain. Existing callers retain their fallback.
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            { return mandatory ? false : null; }
        };

    /// <summary>A Yes/No dialog on the desktop, on top and No by default, answered within <see cref="DialogWait"/>; null
    /// when there is no desktop to show it on.</summary>
    private static bool? Dialog(string what)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return null;
        const uint YesNo = 0x4, Warning = 0x30, SecondDefault = 0x100, Foreground = 0x10000, TopMost = 0x40000;
        const int Yes = 6;
        try
        {
            int answer = MessageBoxTimeout(IntPtr.Zero,
                $"{what}\n\nYour AI agent asked Clocktail for this.\n\nAllow it?",
                "Clocktail asks for your OK", YesNo | Warning | SecondDefault | Foreground | TopMost, 0, (uint)DialogWait.TotalMilliseconds);
            return answer == 0 ? null : answer == Yes;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>user32's MessageBox with a timeout (exported since Windows XP): 32000 when it timed out, 0 when it could
    /// not be shown.</summary>
    [DllImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeout(IntPtr hWnd, string text, string caption, uint type, ushort language, uint milliseconds);
}
