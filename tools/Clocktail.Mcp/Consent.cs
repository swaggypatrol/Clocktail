using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>
/// The person's own yes: for a session of trials (begin_session), and for a change that stays: a new daily profile
/// (keep_as_daily, load_preset), the daily profile back on (load_daily), stock, the daily power limit, the F8 power
/// request that cannot be deleted, a saved profile deleted. The instructions always said "ask the person
/// first", but an agent that runs every tool without asking (Freebuff, an auto-approve mode) and a model that skips the
/// rule made it a wish: a tester saw one change the card unasked (2026-10-24). So Clocktail asks the person itself.
/// By default (the user, 2026-10-27) in the agent's own chat: the first call changes nothing and answers with the
/// question and a one-time code, the agent puts the question to the person word for word, and only after their yes
/// calls again with the code. A code is good once, for that very question, for <see cref="CodeValid"/>. Until then it
/// was an MCP elicitation form, else a Windows dialog: the desktop app's Code tab declined every form unseen, so each
/// yes took a dialog on the desktop. CLOCKTAIL_CONFIRM: "chat" (the default), "form" (the elicitation form where the
/// client offers one, else the Windows dialog), "dialog" (always the Windows dialog); the old "off" (never ask) reads as
/// "chat" since 0.6.0. A session of trials is asked for too (begin_session); each trial is recorded and undone by
/// back_to_daily, end_session or a crash.
/// </summary>
public static class Consent
{
    public const string Variable = "CLOCKTAIL_CONFIRM";

    /// <summary>How long a chat question's code stays good.</summary>
    public static readonly TimeSpan CodeValid = TimeSpan.FromMinutes(10);

    /// <summary>How long the Windows dialog waits for an answer before it counts as a no.</summary>
    public static readonly TimeSpan DialogWait = TimeSpan.FromMinutes(2);

    /// <summary>How long an elicitation may wait for the person before it counts as no answer.</summary>
    public static readonly TimeSpan FormWait = TimeSpan.FromMinutes(5);

    public static string Mode => Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant() switch
    {
        "dialog" => "dialog",
        "form" or "auto" => "form",   // "auto" was the default's name until the chat question
        _ => "chat",
    };

    /// <summary>Why nothing was done, for the agent: the reason, a code for it, and in chat the question with its code.</summary>
    public sealed record Refusal(string Error, string Code, string? Question = null, string? ConfirmCode = null);

    /// <summary>Null when the person said yes; else why nothing may be done yet.</summary>
    public static async Task<Refusal?> Ask(McpServer? server, string what, string? confirm, CancellationToken ct) =>
        Mode == "chat" ? Chat(what, confirm, DateTimeOffset.UtcNow)
            : await Ask(Mode, Form(server, what), q => Dialog(q), what, ct) is { } no ? new Refusal(no, "not_confirmed") : null;

    private static readonly Dictionary<string, (string What, DateTimeOffset Until)> Codes = [];

    /// <summary>
    /// The question in the agent's chat: a call with no code (or one that is not this question's) is answered with the
    /// question and a new code; a call with the code the person's yes was given for goes ahead, once. The code is bound
    /// to the question's words, which name the change, so a yes to one change never lets another through.
    /// </summary>
    public static Refusal? Chat(string what, string? confirm, DateTimeOffset now)
    {
        lock (Codes)
        {
            foreach (var spent in Codes.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList()) Codes.Remove(spent);
            string? given = confirm?.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(given) && Codes.TryGetValue(given, out var asked) && asked.What == what)
            {
                Codes.Remove(given);
                return null;
            }
            string code = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6);
            Codes[code] = (what, now + CodeValid);
            string why = string.IsNullOrEmpty(given) ? "" : "that confirm code is not this change's (used, expired, or for another question): ";
            return new Refusal(
                $"{why}nothing was changed yet. Ask the person in chat, in these words, and wait for their answer: \"{what} Allow it?\" " +
                $"Only after they say yes, call this tool again with the same arguments and confirm=\"{code}\" (once, within " +
                $"{CodeValid.TotalMinutes:0} minutes). If they say no or do not answer, change nothing.",
                "confirm_in_chat", what, code);
        }
    }

    /// <summary>
    /// The decision outside the chat (<see cref="Mode"/> "form" or "dialog"; "chat" never comes here, and no mode skips
    /// the question: "off" reads as "chat"), with the two ways of asking passed in: <paramref name="form"/> (null when the client
    /// offers no elicitation) answers true for yes, false for no and null when it could not ask; <paramref name="dialog"/>
    /// the same for the Windows dialog. A form that could not ask falls back to the dialog; a no is final.
    /// </summary>
    public static async Task<string?> Ask(string mode, Func<CancellationToken, Task<bool?>>? form, Func<string, bool?> dialog, string what, CancellationToken ct)
    {
        if (mode == "form" && form != null)
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
                    $"Nothing was changed. Ask them in chat; {Variable}=chat (the default) asks in the agent's chat.",
        };
    }

    private static string Declined(string where) =>
        $"the person did not confirm it ({where}): nothing was changed. Ask them in chat whether they want it; calling again asks them again";

    /// <summary>The elicitation form, or null when the client offers none: one checkbox the person ticks, so an empty
    /// accept (Codex auto-accepts a form with no fields) is not a yes.</summary>
    private static Func<CancellationToken, Task<bool?>>? Form(McpServer? server, string what) =>
        server?.ClientCapabilities?.Elicitation is null ? null : async ct =>
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(FormWait);
            try
            {
                var r = await server.ElicitAsync(new ElicitRequestParams
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
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { return null; }
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
