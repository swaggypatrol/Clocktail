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
        if (entries <= 0 || !File.Exists(path)) return "";
        ReadOnlySpan<byte> headingBytes = "\n## "u8;
        const int ChunkSize = 64 * 1024;
        int overlap = headingBytes.Length - 1;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        if (length == 0) return "";

        byte[] buf = new byte[(int)Math.Min(ChunkSize + overlap, length)];
        long searchEnd = length;
        int found = 0;
        long? targetOffset = null;

        while (searchEnd > 0)
        {
            long chunkStart = Math.Max(0, searchEnd - ChunkSize);
            int toRead = (int)(Math.Min(length, searchEnd + overlap) - chunkStart);
            fs.Position = chunkStart;
            int read = ReadChunk(fs, buf, toRead);
            if (read < headingBytes.Length) break;

            int maxStartInBuf = (int)(searchEnd - chunkStart) - 1;
            int limit = Math.Min(maxStartInBuf, read - headingBytes.Length);
            if (limit >= 0)
            {
                var searchSpan = buf.AsSpan(0, limit + headingBytes.Length);
                while (searchSpan.Length >= headingBytes.Length)
                {
                    int idx = searchSpan.LastIndexOf(headingBytes);
                    if (idx < 0) break;
                    found++;
                    if (found == entries) targetOffset = chunkStart + idx;
                    else if (found > entries)
                    {
                        fs.Position = targetOffset!.Value + 1; // skip leading '\n' so result starts at "## "
                        using var tailReader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                        return tailReader.ReadToEnd();
                    }
                    searchSpan = searchSpan[..idx];
                }
            }
            searchEnd = chunkStart;
        }

        fs.Position = 0;
        using var fullReader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return fullReader.ReadToEnd().TrimStart();
    }

    private static int ReadChunk(FileStream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
