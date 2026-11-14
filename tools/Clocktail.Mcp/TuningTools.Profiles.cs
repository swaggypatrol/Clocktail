using System.ComponentModel;
using System.Text.Json;
using Clocktail.Domain.Broker;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

public sealed partial class TuningTools
{
    [McpServerTool(Name = "back_to_daily", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Restore this user's kept daily settings. If no daily profile exists, return Clocktail-owned changes to their recorded baseline; no bundled settings are applied and another utility's settings are not implicitly cleared.")]
    public string BackToDaily([Description("Why, for the tuning log.")] string? reason = null) =>
        Applied(card.Broker(BrokerRequest.OpProfile, "apply", reason ?? "back_to_daily (agent)"), null);

    [McpServerTool(Name = "stock", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description(
        "Explicitly return the GPU to its own known NVIDIA/board stock values, clearing known settings left by other utilities as well. Some originals are available only from recorded snapshots. Suspend the user's daily profile without deleting it; no default profile is installed." + " " + AsksItself)]
    public async Task<string> Stock(McpServer server, [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default)
    {
        // Another tool's settings cleared are gone for good (the broker lets go of them): the person says yes to that.
        IReadOnlyList<Clocktail.Domain.FactoryCell> others;
        try { others = new SettingsTools(card).NotStock(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or Clocktail.Hw.NvApiException) { others = []; }
        string question = others.Count == 0
            ? "Put your graphics card to NVIDIA's stock settings: what Clocktail set goes back. Your daily profile is kept, not applied, until load_daily puts it back."
            : $"Put your graphics card to NVIDIA's stock settings. This clears what another tool set ({string.Join(", ", others.Select(o => $"{o.Name} {o.Describe(o.Found)}"))}); " +
              "Clocktail keeps no copy of it, so only that tool can put it back. Your daily profile is kept and load_daily puts it back.";
        return await Asked(server, question, confirm, cancellationToken, Stock);
    }

    private string Stock()
    {
        // Since 0.4.0 "factory": what "stock" did (the profile suspended, what the service holds given back), then what
        // another tool left written to NVIDIA's default. A service from 0.3.7 on knows "stock" alone (the card as Clocktail
        // found it), one from before "off" then a recovery: the answer says the other tool's settings stayed.
        var factory = card.Broker(BrokerRequest.OpProfile, "factory");
        if (factory.Code != BrokerCode.BadRequest)
        {
            var cleared = factory.Result is { } fr && fr.TryGetProperty("cleared", out var cl) && cl.ValueKind == JsonValueKind.Array && cl.GetArrayLength() > 0 ? cl : (JsonElement?)null;
            return Merge(Applied(factory, null), new { cleared });
        }
        const string older = "the service is older than this server: only Clocktail's own settings went back, and anything another tool set stays on the card; call install_service (with the person's OK) to update it, then stock again";
        var stock = card.Broker(BrokerRequest.OpProfile, "stock");
        if (stock.Code != BrokerCode.BadRequest)
            return Merge(Applied(stock.Result is { } res && res.TryGetProperty("recover", out var rec) && rec.ValueKind == JsonValueKind.Object ? stock with { Result = rec } : stock, null), new { note = older });
        var off = card.Broker(BrokerRequest.OpProfile, "off");
        return Merge(off.Ok ? Applied(card.Broker(BrokerRequest.OpRecover), null) : Applied(off, null), new { note = older });
    }

    /// <summary>One answer's JSON object with more fields; a field that is null is left out.</summary>
    private static string Merge(string answer, object more)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(answer)!.AsObject();
        if (JsonSerializer.SerializeToNode(more, Json) is System.Text.Json.Nodes.JsonObject extra)
        {
            foreach (var p in extra.ToList())
            {
                if (p.Value == null) continue;
                extra.Remove(p.Key);
                node[p.Key] = p.Value;
            }
        }
        return JsonText.Unescape(node.ToJsonString(Json));
    }

    [McpServerTool(Name = "load_daily", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Apply a daily profile this user previously kept for this GPU, with explicit approval, and enable its automatic reapplication. If no profile exists, fail without writing; there is no supplied starter profile." + " " + AsksItself)]
    public Task<string> LoadDaily(McpServer server, [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default) =>
        Asked(server, "Put your kept daily profile on the graphics card now and enable automatic reapplication after starts and driver resets.",
            confirm, cancellationToken, () => Applied(card.Broker(BrokerRequest.OpProfile, "load"), null));

    [McpServerTool(Name = "preview_daily", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Read-only preview of what keep_as_daily would retain, including exclusions, or what a named user-saved profile would load. adopt=true previews eligible settings left by another utility. Its values_fingerprint is the one keep_as_daily and load_preset name in their question. No built-in tuning profile exists.")]
    public string PreviewDaily(
        [Description("A preset's or saved profile's name; omit for keep_as_daily.")] string? name = null,
        [Description("Preview keep_as_daily adopt=true: another tool's settings kept too.")] bool adopt = false) =>
        adopt ? Older(card.Broker(BrokerRequest.OpProfile, "preview-adopt"), Previewed) : Previewed(card.Broker(BrokerRequest.OpProfile, "preview", name));

    /// <summary>A reply, unless the service is too old for the op (0.4.0's adopt): then that, and how to update it.</summary>
    private static string Older(BrokerAnswer r, Func<BrokerAnswer, string>? reply = null) => r.Code == BrokerCode.BadRequest
        ? Out(new { success = false, error = "the service is older than this server and cannot adopt another tool's settings: call install_service (with the person's OK) to update it", code = r.Code })
        : (reply ?? Reply)(r);

    [McpServerTool(Name = "keep_as_daily", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "After explicit approval, make eligible settings measured on this GPU the user's own daily profile. Show preview_daily first. Temporary trial controls are excluded as shown in that preview. adopt=true can include eligible settings left by another utility. Service startup and reset recovery reapply only this user-kept profile." + " " + AsksItself)]
    public async Task<string> KeepAsDaily(
        McpServer server,
        [Description("Why this becomes the daily profile (games and minutes it survived).")] string note,
        [Description("Keep another tool's settings on the card too (show preview_daily adopt=true first).")] bool adopt = false,
        [Description(ConfirmCode)] string? confirm = null,
        CancellationToken cancellationToken = default)
    {
        var preview = card.Broker(BrokerRequest.OpProfile, adopt ? "preview-adopt" : "preview");
        if (adopt && preview.Code == BrokerCode.BadRequest) return Older(preview);
        if (!preview.Ok) return Reply(preview);
        var expected = FullFingerprint(preview);
        if (expected == null) return Out(new { success = false, error = "update the Clocktail service before keeping a profile: it cannot verify the confirmed values" });
        return await Asked(server, $"Make what is on your graphics card now your daily profile ({Changes(preview)}). Clocktail puts it back after every start and driver reset.", confirm, cancellationToken, () =>
        {
            var r = card.Broker(BrokerRequest.OpProfile, adopt ? "adopt" : "keep", note, expectedProfileFingerprint: expected);
            if (!r.Ok || r.Result is not { } res) return Reply(r);
            var writes = res.GetProperty("profile").GetProperty("Writes").EnumerateArray()
                .Select(w => $"{w.GetProperty("Kind").GetString()}/{w.GetProperty("Key").GetString()} = {w.GetProperty("Value").GetString()}");
            return Out(new { success = true, daily = writes });
        });
    }

    /// <summary>For the description of each tool the person confirms (<see cref="Consent"/>).</summary>
    private const string AsksItself = "Clocktail asks the person itself before it does this, in the chat: the first call changes nothing " +
        "and answers with the question and a confirm code (code confirm_in_chat); put the question to the person word for word, and only " +
        "after their yes call again with the same arguments and confirm set to that code. When they do not confirm, nothing is changed.";

    /// <summary>The <c>confirm</c> parameter of each tool the person confirms.</summary>
    private const string ConfirmCode = "The code from this tool's first answer, given only after the person said yes to its question in chat; omit it to ask.";

    /// <summary>The person's yes (<see cref="Consent"/>) before <paramref name="then"/>; without it, why not, and nothing done.</summary>
    private static async Task<string> Asked(McpServer server, string what, string? confirm, CancellationToken ct, Func<string> then) =>
        await Consent.Ask(server, what, confirm, ct) is { } no
            ? Out(new { success = false, error = no.Error, code = no.Code, question = no.Question, confirm = no.ConfirmCode, written = WriteOutcome.None })
            : Guard(then);

    /// <summary>
    /// A profile preview's changes for the person's question: the settings in a few words, then the values' fingerprint
    /// ("V/F curve, memory offset; values fingerprint 3FA9C1"). The confirm code is bound to the question's words, so
    /// with the fingerprint a yes given for these values is not a yes for others: a curve or offset written to the card
    /// (or a profile saved again under the name) between the asking call and the confirmed call makes another question,
    /// and the old code no longer matches.
    /// </summary>
    public static string Changes(BrokerAnswer preview)
    {
        var names = preview.Result is { } r && r.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => Setting(Str(x, "kind"), Str(x, "key"))).Distinct().ToList() : [];
        return $"{(names.Count == 0 ? "no change from the daily profile" : string.Join(", ", names))}; values fingerprint {Fingerprint(preview)}";
    }

    /// <summary>
    /// Six hex characters of a SHA-256 over what a profile preview would make the daily profile: its source (keep, adopt
    /// or a saved profile's name), each change's kind/key=becomes and what it drops, sorted. preview_daily shows it as
    /// values_fingerprint, and keep_as_daily and load_preset put it in their question.
    /// </summary>
    public static string Fingerprint(BrokerAnswer preview)
    {
        if (FullFingerprint(preview) is { } full) return full[..6];
        List<string> lines = [];
        if (preview.Result is { } r)
        {
            lines.Add($"source={Raw(r, "source")}");
            if (r.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array)
                lines.AddRange(c.EnumerateArray().Select(x => $"{Raw(x, "kind")}/{Raw(x, "key")}={Raw(x, "becomes")}"));
            if (r.TryGetProperty("dropped", out var d) && d.ValueKind == JsonValueKind.Array)
                lines.AddRange(d.EnumerateArray().Select(x => $"dropped {x.GetRawText()}"));
        }
        lines.Sort(StringComparer.Ordinal);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(hash, 0, 3);
    }

    private static string? FullFingerprint(BrokerAnswer preview) =>
        preview.Result is { } r && r.TryGetProperty("values_fingerprint", out var f) && f.ValueKind == JsonValueKind.String
            && f.GetString() is { Length: 64 } hash ? hash : null;

    /// <summary>A field's JSON text as it came (any kind of value), or empty when it is missing.</summary>
    private static string Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.GetRawText() : "";

    /// <summary>A profile preview's answer with its <see cref="Fingerprint"/>, so the person can match the question to it.</summary>
    private static string Previewed(BrokerAnswer r) => r.Ok ? Merge(Reply(r), new { values_fingerprint = Fingerprint(r) }) : Reply(r);

    private static string Setting(string? kind, string? key) => (kind, key) switch
    {
        ("vf", _) => "V/F curve",
        ("pstates20", "memory") => "memory offset",
        ("pstates20", _) => "core offset",
        ("clockdomain", var k) => $"{k?.ToUpperInvariant()} offset",
        ("vdemand", var k) => $"{(k == "graphics" ? "core" : k?.ToUpperInvariant())} voltage demand",
        ("boost", _) => "voltage boost",
        ("power", _) => "power limit",
        ("fancurve", _) => "fan curve",
        ("fanstop", _) => "fan stop band",
        ("rails", var k) => $"{k?.ToUpperInvariant()} offsets",
        ("clockratio", _) => "XBAR/core ratio",
        ("clocklimit", _) => "clock cap",
        _ => $"{kind}/{key}",
    };

    [McpServerTool(Name = "list_presets", ReadOnly = true, OpenWorld = false), Description(
        "List profiles this user saved for the current GPU. The compatible 'presets' field is empty: Clocktail ships no tuning preset or default daily profile. 'saved' contains user-owned named profiles; load_preset loads one after approval.")]
    public string ListPresets() => Guard(() =>
    {
        var saved = card.Broker(BrokerRequest.OpProfile, "saved");
        return Out(new
        {
            gpu = card.Name,
            presets = Array.Empty<object>(),   // the field stays for clients that read it: nothing ships
            saved = saved.Ok && saved.Result is { } res && res.TryGetProperty("profiles", out var list) ? list : (JsonElement?)null,
            saved_error = saved.Ok ? null : saved.Error,
        });
    });

    [McpServerTool(Name = "save_profile", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Save this GPU's existing user-kept daily profile under a chosen name. No daily profile is manufactured when none exists. A reused name is replaced; names are case-insensitive.")]
    public string SaveProfile([Description("The name to save it under.")] string name) =>
        Guard(() => Reply(card.Broker(BrokerRequest.OpProfile, "save", name)));

    [McpServerTool(Name = "delete_profile", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Delete a named profile this user saved, after explicit approval. The daily profile already kept on the card is not deleted. There are no bundled profiles." + " " + AsksItself)]
    public Task<string> DeleteProfile(McpServer server, [Description("The saved profile's name, from list_presets.")] string name,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default) =>
        Asked(server, $"Delete the profile saved as '{name}'. It cannot be undone; the settings on your graphics card do not change.", confirm, cancellationToken,
            () => Reply(card.Broker(BrokerRequest.OpProfile, "forget", name)));

    [McpServerTool(Name = "load_preset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Compatible tool name for loading a profile this user saved for the current GPU and making it daily after approval. Show preview_daily with the name first. Missing names write nothing; no starter preset is supplied." + " " + AsksItself)]
    public async Task<string> LoadPreset(McpServer server, [Description("The user-saved profile's name, from list_presets.")] string name,
        [Description(ConfirmCode)] string? confirm = null, CancellationToken cancellationToken = default)
    {
        // Validate a user-saved name before asking; there are no built-in tuning profiles.
        var preview = card.Broker(BrokerRequest.OpProfile, "preview", name);
        if (!preview.Ok) return Reply(preview);
        var expected = FullFingerprint(preview);
        if (expected == null) return Out(new { success = false, error = "update the Clocktail service before loading a profile: it cannot verify the confirmed values" });
        return await Asked(server, $"Load '{name}' as your daily profile and put it on your graphics card now ({Changes(preview)}); Clocktail puts it back after every start.",
            confirm, cancellationToken, () => Applied(card.Broker(BrokerRequest.OpProfile, "preset", name, expectedProfileFingerprint: expected), null));
    }
}
