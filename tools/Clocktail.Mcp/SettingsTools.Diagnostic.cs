using System.ComponentModel;
using Clocktail.Domain;
using Clocktail.Hw;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

public sealed partial class SettingsTools
{
    [McpServerTool(Name = "diagnostic_report", ReadOnly = true, OpenWorld = false), Description(
        "A report to attach to a bug report or a question for Clocktail's maintainers, when something does not work on " +
        "this card or driver: Clocktail's and Windows' versions, the card (name, VBIOS, PCI ids), the driver and branch, " +
        "Windows' driver timeout, the capability map (card_settings controls), how the driver answered every read " +
        "Clocktail knows, and what Clocktail holds on the card. GET only: nothing is written. It holds no user names, " +
        "paths or files. It is saved as plain JSON the person can read first; tell them where, and offer to draft a GitHub " +
        "issue (github.com/swaggypatrol/Clocktail/issues) for them to post with it attached. Nothing is sent from here.")]
    public string DiagnosticReport() => Guard(() =>
    {
        var at = DateTimeOffset.Now;
        object? held = null, lastCrash = null;
        try { Held(out held, out lastCrash); } catch (Exception e) when (Soft(e)) { held = new { error = e.Message }; }
        var sweep = Section(() => Reads(card.WithDriver(Diagnostics.GetSweep)));
        string json = Out(new
        {
            report = "clocktail-diagnostic",
            get_only = true,
            note = "Clocktail changed nothing to make this. It holds the card's identity, the driver and how the driver answered each read; no user names, paths or files.",
            at,
            clocktail = UpdateCheck.Current,
            windows = Environment.OSVersion.Version.ToString(),
            gpu = card.Name,
            vbios = card.Vbios,
            pci = card.StableId,
            driver = VerifiedOn.Driver(card.DriverVersion),
            branch = card.DriverBranch,
            tested_on = VerifiedOn.Describe(card.DriverVersion, card.Name, card.Vbios),
            tdr = Section(() => OperatingSystem.IsWindows() && Clocktail.Domain.Platform.TdrSettings.Read() is { } tdr ? tdr.Describe() : null),
            controls = Section(() => Controls.Map(card.WithDriver(Capabilities.Probe), WritePolicySet.Default, card.DriverVersion)
                .Select(x => new { control = x.Name, tool = x.Tool, can = x.Can, why = x.Why }).ToList()),
            reads = sweep,
            journal_holds = held,
            last_crash = lastCrash,
        });
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", "diagnostics");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"diagnostic-{at:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, json);
        return Out(new
        {
            success = true,
            saved = path,
            size_kb = Math.Round(json.Length / 1024.0, 1),
            driver = VerifiedOn.Driver(card.DriverVersion),
            gpu = card.Name,
            vbios = card.Vbios,
            reads = sweep is ReadsSection r ? $"{r.Answered} of {r.Items.Count} reads answered" : "the reads could not run",
            next = "Tell the person where the file is and that it is plain JSON they can read first; offer to draft the GitHub issue text for them to post with it attached.",
        });
    });

    public sealed record ReadsSection(int Answered, IReadOnlyList<object> Items);

    /// <summary>The GET sweep as the report shows it: one line per read, how the driver answered and the first 64 bytes.</summary>
    public static ReadsSection Reads(IReadOnlyList<GetProbe> sweep) => new(
        sweep.Count(p => p.Outcome == NvOutcome.Ok),
        sweep.Select(p => (object)new
        {
            slot = p.Slot.ToString(), id = $"0x{p.Id:x8}", shape = p.Shape, outcome = p.Outcome.ToString(), status = p.Status,
            version_echoed = p.VersionEchoed, nonzero_dwords = p.NonZeroDwords, head = p.Head.Length > 0 ? p.Head : null,
        }).ToList());
}
