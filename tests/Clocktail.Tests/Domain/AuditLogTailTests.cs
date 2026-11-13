using System.Text;
using System.Text.Json;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

public sealed class AuditLogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tail-tests-" + Guid.NewGuid().ToString("n"));
    private string DataPath => Path.Combine(_dir, Experiment.DataFileName);
    private string LogPath => Path.Combine(_dir, TuningLog.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static DataPoint Point(int i, string outcome = Experiment.Survived, string? note = null) =>
        new(
            new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(i),
            "Cyberpunk 2077",
            outcome,
            1.5,
            $"+15 MHz on points {60 + (i % 20)}-{65 + (i % 20)}",
            915_000,
            2950,
            "15000*40,0*87",
            Note: note,
            TrialAt: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(i).AddSeconds(-90));

    private static IReadOnlyList<DataPoint> ReferenceReadLast(string dataPath, int count, bool includeDeleted = false)
    {
        if (count <= 0 || !File.Exists(dataPath)) return [];
        string dir = Experiment.DeletedDirectory(dataPath);
        var deleted = !includeDeleted && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.Ordinal)
            : [];
        var points = new List<DataPoint>();
        using var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<DataPoint>(line) is { } p && (deleted.Count == 0 || !deleted.Contains(Experiment.PointId(p))))
                    points.Add(p);
            }
            catch (JsonException) { }
        }
        return points.Count <= count ? points : points.GetRange(points.Count - count, count);
    }

    private static string ReferenceTail(string path, int entries)
    {
        if (entries <= 0 || !File.Exists(path)) return "";
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(fs)) text = r.ReadToEnd();
        var parts = text.Split("\n## ");
        return parts.Length <= entries + 1 ? text.TrimStart() : "## " + string.Join("\n## ", parts[^entries..]);
    }

    [Fact]
    public void ReadLast_handles_missing_empty_and_zero_count()
    {
        Assert.Empty(Experiment.ReadLast(DataPath, 20));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(DataPath, "");
        Assert.Empty(Experiment.ReadLast(DataPath, 20));
        Experiment.Append(DataPath, Point(1));
        Assert.Empty(Experiment.ReadLast(DataPath, 0));
    }

    [Fact]
    public void ReadLast_matches_full_read_across_chunks_bom_crlf_utf8_malformed_and_deleted()
    {
        Directory.CreateDirectory(_dir);
        var points = new List<DataPoint>();
        var sb = new StringBuilder();
        sb.Append("\r\n");
        // Write > 200 KB of records spanning multiple 64 KB chunks with multi-byte UTF-8 and CRLF/LF mix.
        for (int i = 0; i < 650; i++)
        {
            var p = Point(i, i % 15 == 0 ? Experiment.Crash : Experiment.Stable,
                note: $"中文备注-{i}-" + new string('测', 10));
            points.Add(p);
            sb.Append(JsonSerializer.Serialize(p));
            sb.Append(i % 2 == 0 ? "\r\n" : "\n");
            if (i is 100 or 645 or 648) sb.Append("not json at all\n   \r\n");
        }
        // Final line intentionally without a trailing newline.
        var last = Point(650, Experiment.Survived, "no trailing newline");
        points.Add(last);
        sb.Append(JsonSerializer.Serialize(last));

        // Include UTF-8 BOM at byte 0.
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(DataPath, sb.ToString(), utf8Bom);

        // Delete points near the tail and in the middle.
        Experiment.DeletePoint(DataPath, points[649]);
        Experiment.DeletePoint(DataPath, points[647]);
        Experiment.DeletePoint(DataPath, points[10]);

        foreach (int k in new[] { 1, 5, 20, 200, 1000, int.MaxValue })
        {
            Assert.Equal(ReferenceReadLast(DataPath, k, includeDeleted: false), Experiment.ReadLast(DataPath, k, includeDeleted: false));
            Assert.Equal(ReferenceReadLast(DataPath, k, includeDeleted: true), Experiment.ReadLast(DataPath, k, includeDeleted: true));
        }
    }

    [Fact]
    public void ReadLast_handles_single_line_larger_than_chunk_buffer()
    {
        var p0 = Point(0, note: "small-before");
        var huge = Point(1, note: new string('x', 90_000));
        var p2 = Point(2, note: "small-after");
        Experiment.Append(DataPath, p0);
        Experiment.Append(DataPath, huge);
        Experiment.Append(DataPath, p2);

        Assert.Equal([p2], Experiment.ReadLast(DataPath, 1));
        Assert.Equal([huge, p2], Experiment.ReadLast(DataPath, 2));
        Assert.Equal([p0, huge, p2], Experiment.ReadLast(DataPath, 10));
    }

    [Fact]
    public void CrashesSince_counts_matching_game_and_session_including_deleted_on_large_file()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 600; i++)
        {
            var outcome = i is 590 or 595 ? Experiment.Crash : Experiment.Survived;
            var game = i == 592 ? "Other Game" : "Cyberpunk 2077";
            Experiment.Append(DataPath, Point(i, outcome) with { Game = game, At = t0.AddMinutes(i) });
        }
        // Delete one of the crashes: safety budget must still count it.
        var deletedCrash = Point(595, Experiment.Crash) with { At = t0.AddMinutes(595) };
        Experiment.DeletePoint(DataPath, deletedCrash);

        var session = new Experiment("Cyberpunk 2077", null, t0.AddMinutes(580), CrashBudget: 2);
        Assert.Equal(2, session.CrashesSince(DataPath));
    }

    [Fact]
    public void TuningLog_Tail_matches_full_split_across_chunk_boundaries_and_entry_counts()
    {
        Assert.Equal("", TuningLog.Tail(LogPath, 15));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "");
        Assert.Equal("", TuningLog.Tail(LogPath, 15));

        var baseTime = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        // Write > 180 KB so headings fall before, across, and after 64 KB chunk boundaries.
        for (int i = 0; i < 900; i++)
        {
            TuningLog.Append(LogPath, baseTime.AddMinutes(i), $"Trial {i} · 中文",
                [$"Change: +{i} MHz", new string('a', 140)]);
        }

        foreach (int entries in new[] { 1, 2, 15, 300, 900, 901, 2000 })
        {
            Assert.Equal(ReferenceTail(LogPath, entries), TuningLog.Tail(LogPath, entries));
        }
    }

    [Fact]
    public void TuningLog_Tail_matches_when_heading_spans_exact_64kb_boundary()
    {
        Directory.CreateDirectory(_dir);
        // Construct a file where "\n## " starts at byte 65534 (spanning the 65536 chunk boundary).
        var sb = new StringBuilder("# Clocktail tuning log\n\n");
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < 65534)
        {
            sb.Append("\n## 2026-11-01 12:00:00 · Pad\n- ").Append(new string('p', 200)).Append('\n');
        }
        int need = 65534 - (Encoding.UTF8.GetByteCount(sb.ToString()) - 65534);
        // Easier: build exact prefix length before "\n## "
        sb.Clear();
        sb.Append("# Header\n\n## 2026-11-01 12:00:00 · E1\n- ");
        int pad = 65534 - Encoding.UTF8.GetByteCount(sb.ToString());
        sb.Append(new string('x', pad));
        sb.Append("\n## 2026-11-01 12:01:00 · E2\n- boundary entry\n");
        sb.Append("\n## 2026-11-01 12:02:00 · E3\n- last entry\n");
        File.WriteAllText(LogPath, sb.ToString(), new UTF8Encoding(false));

        Assert.Equal(ReferenceTail(LogPath, 1), TuningLog.Tail(LogPath, 1));
        Assert.Equal(ReferenceTail(LogPath, 2), TuningLog.Tail(LogPath, 2));
        Assert.Equal(ReferenceTail(LogPath, 3), TuningLog.Tail(LogPath, 3));
        Assert.Equal(ReferenceTail(LogPath, 4), TuningLog.Tail(LogPath, 4));
    }
}
