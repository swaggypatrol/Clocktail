using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Clocktail.Domain.Platform;

namespace Clocktail.Domain.Profiles;

/// <summary>One curve tried in a game: what changed, where the card ran when it went on, and since when.</summary>
public sealed record ExperimentTrial(
    DateTimeOffset At,
    /// <summary>The whole Core curve as written, in the vf cell's encoding.</summary>
    string Curve,
    /// <summary>What this trial changed against the one before it, in words ("+15 MHz on points 74-78 (930-950 mV)").</summary>
    string Change,
    /// <summary>The operating point when it was written: NVVDD µV and core MHz under the game's load.</summary>
    uint? AtUv,
    uint? AtMhz,
    /// <summary>Marked stable: that data point is on record, so closing it records nothing more but a crash.</summary>
    bool Marked = false,
    /// <summary>The board power limit the trial runs under, mW, when a trial set one (lowered to move the operating
    /// point down the curve); null: the daily profile's.</summary>
    uint? PowerMw = null,
    /// <summary>The NVVDD / MSVDD current limits the trial runs under, mA, when a trial set them (a current limit caps
    /// the rail's power below the 400 W the board request cannot go under); null: the VBIOS default.</summary>
    uint? NvvddMa = null,
    uint? MsvddMa = null,
    /// <summary>The MSVDD rail's REL / VMIN offsets the trial runs under, µV, when a trial set them (MSVDD follows the
    /// core's demand minus its REL offset, so a lower one is a straight undervolt of the rail); null: the stock ones.</summary>
    int? MsvddRelUv = null,
    int? MsvddVminUv = null,
    /// <summary>The fan curve the trial runs under (both curve policies, °C:duty%:rpm), when a trial set one; null: the
    /// daily profile's.</summary>
    string? FanCurve = null,
    /// <summary>A fixed fan duty the trial holds, %, when a trial set one (to hold a temperature for a test); null: the
    /// fan curve drives the fans. Never kept.</summary>
    uint? FanPct = null,
    /// <summary>The memory offset the trial runs under, kHz (P0), when a trial set one; null: the daily profile's.</summary>
    int? MemoryKhz = null,
    /// <summary>The XBAR clock offset (kHz) and voltage demand (µV, on MSVDD) the trial runs under, when a trial set
    /// them; null: the daily profile's.</summary>
    int? XbarKhz = null,
    int? XbarVdemandUv = null,
    /// <summary>Every other write the trial runs under, through its own write path (the core P0 offset, NVVDD's offsets,
    /// SYS/Video, the boost, a clock lock…), when a trial set them; null: none.</summary>
    IReadOnlyList<ProfileWrite>? Writes = null,
    /// <summary>How the trial ended while the card had not let go of it yet (its put-back held off by a busy card), a
    /// crash over anything else: on record, so nothing more is recorded for it but a crash after another ending (asked
    /// again, mark_stable, a driver reset after mark_crash, a hand-back or the next trial: none adds a point), and a crash
    /// is never kept. Null: it has not ended.</summary>
    string? Ending = null,
    /// <summary>The trial's identity, given when it was written ("try") and kept while it stays in force (a hand-back
    /// writes a new <see cref="At"/>, not a new identity): a run that measured this trial records its crash against this
    /// identity, so a trial written after the run cannot take it. Null: written by a service that gave none.</summary>
    string? Id = null,
    /// <summary>The automatic job that wrote this trial. Retained across hand-backs, so an old job's cleanup cannot
    /// end a trial another agent wrote, even when the job lost the response to its own write.</summary>
    string? JobId = null);

/// <summary>One data point: how a trial ended in a game.</summary>
public sealed record DataPoint(
    DateTimeOffset At,
    string Game,
    /// <summary><see cref="Experiment.Stable"/>, <see cref="Experiment.Survived"/> or <see cref="Experiment.Crash"/>.</summary>
    string Outcome,
    /// <summary>How long the trial ran before this outcome.</summary>
    double Minutes,
    string Change,
    uint? AtUv,
    uint? AtMhz,
    string Curve,
    string? Note = null,
    /// <summary>For a crash: the core voltage the flight recorder found at the hang, µV, when it found one.</summary>
    uint? CrashUv = null,
    /// <summary>The trial's board power limit, mW; null: the daily profile's.</summary>
    uint? PowerMw = null,
    /// <summary>The trial's NVVDD / MSVDD current limits, mA; null: the VBIOS default.</summary>
    uint? NvvddMa = null,
    uint? MsvddMa = null,
    /// <summary>The trial's MSVDD REL / VMIN offsets, µV; null: the stock ones.</summary>
    int? MsvddRelUv = null,
    int? MsvddVminUv = null,
    /// <summary>The trial's fan curve, fixed fan duty (%), memory offset (kHz), XBAR offset (kHz) and XBAR voltage
    /// demand (µV); null: the daily profile's (for the fan duty: the fan curve's).</summary>
    string? FanCurve = null,
    uint? FanPct = null,
    int? MemoryKhz = null,
    int? XbarKhz = null,
    int? XbarVdemandUv = null,
    /// <summary>The trial's other writes (<see cref="ExperimentTrial.Writes"/>).</summary>
    IReadOnlyList<ProfileWrite>? Writes = null,
    DateTimeOffset? TrialAt = null);

/// <summary>
/// An agent tuning the curve live while the person plays (the MCP server): the session and the trial in force, in
/// experiment.json beside the journal, and every trial's outcome as one line of datapoints.jsonl. A crash during a
/// trial belongs to the trial, not to the daily profile: the broker records it here and puts the daily profile back
/// unchanged instead of stepping it down (the user's decision, 2026-10-18). Without a trial in force a crash is the
/// daily profile's, as before.
/// </summary>
public sealed record Experiment(string Game, string? Note, DateTimeOffset StartedAt, ExperimentTrial? Trial = null,
    /// <summary>The session's crash budget, when it was begun unattended: after this many crashes no new trial is written
    /// until a new session begins (the daily profile stays on). Null: no budget, the person is at the machine.</summary>
    int? CrashBudget = null)
{
    public const string FileName = "experiment.json";
    public const string DataFileName = "datapoints.jsonl";
    /// <summary>The person or the agent judged the trial good in the game.</summary>
    public const string Stable = "stable";
    /// <summary>The trial ended (another one replaced it, or the session or the profile put the card back) without a crash.</summary>
    public const string Survived = "survived";
    /// <summary>A driver reset or an unclean shutdown while the trial was on the card.</summary>
    public const string Crash = "crash";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Line = new() { WriteIndented = false };

    public static string PathBeside(string journalPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, FileName);

    public static string DataPathBeside(string journalPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, DataFileName);

    public static Experiment? Load(string path)
    {
        var bytes = AtomicFile.Read(path, 1024 * 1024);
        if (bytes == null) return null;
        try { return JsonSerializer.Deserialize<Experiment>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    public void Save(string path) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(AtomicFile.BackupPath(path))) File.Delete(AtomicFile.BackupPath(path));
    }

    /// <summary>The crashes recorded in this session so far.</summary>
    public int CrashesSince(string dataPath) =>
        ReadLast(dataPath, 1000, includeDeleted: true).Count(p => p.Outcome == Crash && p.At >= StartedAt && p.Game == Game);

    /// <summary>The trial's data point with this outcome; null when there is no trial.</summary>
    public DataPoint? Point(string outcome, DateTimeOffset at, string? note = null, uint? crashUv = null) =>
        Trial is { } t
            ? new DataPoint(at, Game, outcome, Math.Round((at - t.At).TotalMinutes, 1), t.Change, t.AtUv, t.AtMhz, t.Curve, note, crashUv, t.PowerMw, t.NvvddMa, t.MsvddMa, t.MsvddRelUv, t.MsvddVminUv,
                t.FanCurve, t.FanPct, t.MemoryKhz, t.XbarKhz, t.XbarVdemandUv, t.Writes, t.At)
            : null;

    /// <summary>Whether a trial's change was the curve's (adjust_curve, flatten_curve, set_curve, shift_curve, the curve
    /// editor): its crash is evidence about the curve. Under a trial that moved XBAR, the memory, the wall, a limit or the
    /// fans the curve was merely on the card. By the change's words: a curve trial's data point also carries the session's
    /// earlier writes and wall, so the other fields do not tell. A core P0 offset trial moves every point too, but its data
    /// point's curve does not carry the offset, so it is left out rather than pinned at the wrong clock.</summary>
    public static bool CurveChange(string change) =>
        System.Text.RegularExpressions.Regex.IsMatch(change, @"MHz on points|flat top at|whole curve written|curve shifted|curve editor");

    /// <summary>When the data point's trial went on the card: <see cref="DataPoint.TrialAt"/>, or for an older record
    /// without it, the moment less its minutes (rounded to 0.1 min, so up to 3 s off).</summary>
    public static DateTimeOffset StartOf(DataPoint point) => point.TrialAt ?? point.At - TimeSpan.FromMinutes(point.Minutes);

    public static void Append(string dataPath, DataPoint point)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataPath))!);
        File.AppendAllText(dataPath, JsonSerializer.Serialize(point, Line) + "\n");
    }

    /// <summary>A stable identity for a complete test record, not just its timestamp or curve point.</summary>
    public static string PointId(DataPoint point) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(point, Line)));

    public static string DeletedDirectory(string dataPath) => dataPath + ".deleted";

    /// <summary>Delete a test from history without rewriting the broker's append-only journal. One independent marker
    /// per record makes concurrent deletes and appends safe across servers. The original remains for audit/recovery;
    /// safety accounting explicitly reads it, so removing history cannot replenish a session's crash budget.</summary>
    public static void DeletePoint(string dataPath, DataPoint point)
    {
        string id = PointId(point);
        if (!ReadLast(dataPath, int.MaxValue, includeDeleted: true).Any(p => PointId(p) == id))
            throw new InvalidOperationException("This test record no longer exists; reopen the window.");
        string dir = DeletedDirectory(dataPath);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, id + ".json");
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(path)) { return; } // already deleted by another window
        using (file)
        {
            // Existence is the commit: even an interrupted metadata write still records the deletion.
            file.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { deletedAt = DateTimeOffset.UtcNow, point }, Line)));
            file.Flush(flushToDisk: true);
        }
    }

    /// <summary>The last <paramref name="count"/> visible data points, oldest first; a line that does not parse is skipped.
    /// Safety/recovery callers can include deleted records; ordinary readers see the same history as the editor.</summary>
    public static IReadOnlyList<DataPoint> ReadLast(string dataPath, int count, bool includeDeleted = false)
    {
        if (!File.Exists(dataPath)) return [];
        string dir = DeletedDirectory(dataPath);
        var deleted = !includeDeleted && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.Ordinal)
            : [];
        var points = new List<DataPoint>();
        // The broker may append while an editor reads or deletes history. Never lock it out of its journal.
        using var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<DataPoint>(line, Line) is { } p && (deleted.Count == 0 || !deleted.Contains(PointId(p))))
                    points.Add(p);
            }
            catch (JsonException) { }
        }
        return points.Count <= count ? points : points.GetRange(points.Count - count, count);
    }
}
