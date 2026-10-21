using System.Text;

namespace Clocktail.Domain.Profiles;

/// <summary>
/// The tuning log: tuning-log.md beside the journal, one Markdown entry per change to the card or to its records (a
/// trial, a crash, a profile kept, loaded or put back, a driver reset, the agent's own notes), as detailed as the
/// writer can make it. Append-only, never rewritten, for the person and for the next agent to read. Only the broker
/// writes it, inside the ops that make the changes, so entries are in the order the card saw them.
/// </summary>
public static class TuningLog
{
    public const string FileName = "tuning-log.md";
    private const string Heading = "\n## ";

    public static string PathBeside(string journalPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, FileName);

    /// <summary>Appends one entry: a heading with the local time and <paramref name="title"/>, then one bullet per line.
    /// A log that cannot be written never fails the change it records.</summary>
    public static void Append(string path, DateTimeOffset at, string title, IEnumerable<string?> lines)
    {
        var sb = new StringBuilder();
        if (!File.Exists(path)) sb.Append("# Clocktail tuning log\n\nOne entry per change, newest last.\n");
        sb.Append(Heading).Append(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append(" · ").Append(title).Append('\n');
        foreach (var line in lines)
            if (!string.IsNullOrWhiteSpace(line)) sb.Append("- ").Append(line.Replace("\n", "\n  ")).Append('\n');
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.AppendAllText(path, sb.ToString());
                return;
            }
            catch (IOException) { Thread.Sleep(50); }   // a reader holding it open for a moment
            catch (UnauthorizedAccessException) { return; }
        }
    }

    /// <summary>The last <paramref name="entries"/> entries, oldest first; empty when there is no log yet.</summary>
    public static string Tail(string path, int entries)
    {
        if (!File.Exists(path)) return "";
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(fs)) text = r.ReadToEnd();
        var parts = text.Split(Heading);
        return parts.Length <= entries + 1 ? text.TrimStart() : "## " + string.Join(Heading, parts[^entries..]);
    }
}
