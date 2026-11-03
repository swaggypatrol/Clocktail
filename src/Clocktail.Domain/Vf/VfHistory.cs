using System.Buffers.Binary;
using System.Globalization;

namespace Clocktail.Domain.Vf;

/// <summary>
/// The always-on V/F history's day files: vf-history\yyyyMMdd.vfh beside a GPU's journal, one per UTC day (Gregorian
/// whatever the person's calendar). Little-endian throughout.
/// <code>
/// header, 16 bytes:  "CTVF" | u16 version (1) | u16 header size (16) | u16 record size (12) | u16 reserved (0)
///                    | u32 UTC day as yyyymmdd
/// record, 12 bytes:  u32 milliseconds since that UTC midnight | u32 NVVDD µV as measured | u32 core clock MHz as measured
/// </code>
/// A record is only what was measured: no requested voltage, no curve clock, no utilization or power. A day file a
/// newer or broken writer left that this version cannot append to is never rewritten: the writer goes on in
/// yyyyMMdd-1.vfh (up to <see cref="MaxParts"/>), and readers read every part of a day.
/// </summary>
public static class VfHistoryFile
{
    public const string DirName = "vf-history";
    public const string Extension = ".vfh";
    /// <summary>The per-look gate: an observation is kept only when NVVDD is strictly above this (µV).</summary>
    public const uint MinUvExclusive = 825_000;
    public const ushort Version = 1;
    public const int HeaderSize = 16;
    public const int RecordSize = 12;
    public const int MaxParts = 16;
    private const uint MsPerDay = 86_400_000;
    /// <summary>What a read accepts: the rail reader's own plausibility bound, and a clock no GPU reaches.</summary>
    private const uint MaxUv = 2_000_000, MaxMhz = 20_000;

    public static ReadOnlySpan<byte> Magic => "CTVF"u8;

    /// <summary>vf-history\ beside a GPU's journal or any other file of its record directory (datapoints.jsonl, experiment.json).</summary>
    public static string DirBeside(string fileInRecordDir) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fileInRecordDir))!, DirName);

    /// <summary>Whether a look is kept: a measured NVVDD strictly above 825 mV and a measured core clock.</summary>
    public static bool Admits(uint? uv, uint? mhz) => uv is > MinUvExclusive and < MaxUv && mhz is > 0 and < MaxMhz;

    public static DateOnly UtcDay(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    public static string DayFileName(DateOnly utcDay, int part = 0) =>
        utcDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (part == 0 ? "" : "-" + part.ToString(CultureInfo.InvariantCulture)) + Extension;

    private static uint DayNumber(DateOnly d) => (uint)(d.Year * 10000 + d.Month * 100 + d.Day);

    public static void WriteHeader(Span<byte> into, DateOnly utcDay)
    {
        into[..HeaderSize].Clear();
        Magic.CopyTo(into);
        BinaryPrimitives.WriteUInt16LittleEndian(into[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(into[6..], HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(into[8..], RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(into[12..], DayNumber(utcDay));
    }

    public static byte[] Header(DateOnly utcDay)
    {
        var b = new byte[HeaderSize];
        WriteHeader(b, utcDay);
        return b;
    }

    /// <summary>Whether this is a version-1 header for that day.</summary>
    public static bool ValidHeader(ReadOnlySpan<byte> h, DateOnly utcDay) =>
        h.Length >= HeaderSize && h[..4].SequenceEqual(Magic)
        && BinaryPrimitives.ReadUInt16LittleEndian(h[4..]) == Version
        && BinaryPrimitives.ReadUInt16LittleEndian(h[6..]) == HeaderSize
        && BinaryPrimitives.ReadUInt16LittleEndian(h[8..]) == RecordSize
        && BinaryPrimitives.ReadUInt32LittleEndian(h[12..]) == DayNumber(utcDay);

    public static void WriteRecord(Span<byte> into, VfObservation o)
    {
        var utc = o.At.UtcDateTime;
        uint ms = (uint)((utc - utc.Date).Ticks / TimeSpan.TicksPerMillisecond);
        BinaryPrimitives.WriteUInt32LittleEndian(into, ms);
        BinaryPrimitives.WriteUInt32LittleEndian(into[4..], o.Uv);
        BinaryPrimitives.WriteUInt32LittleEndian(into[8..], o.Mhz);
    }

    /// <summary>A record of that day, or null when it cannot be one (a block a power loss left as zeros, a torn write).</summary>
    public static VfObservation? ReadRecord(ReadOnlySpan<byte> r, DateOnly utcDay)
    {
        uint ms = BinaryPrimitives.ReadUInt32LittleEndian(r), uv = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
            mhz = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
        if (ms >= MsPerDay || uv <= MinUvExclusive || uv >= MaxUv || mhz == 0 || mhz >= MaxMhz) return null;
        var at = new DateTimeOffset(utcDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddMilliseconds(ms);
        return new VfObservation(at, uv, mhz, VfSource.Lightweight);
    }
}

/// <summary>What a read of the day files met, for the record and the tests.</summary>
public sealed class VfReadStats
{
    public int Files, UnreadableFiles;
    public long Records, SkippedRecords, TornBytes;
}

/// <summary>
/// Appends to and reads the day files. One writer (the broker) appends; any number of readers (MCP servers, the curve
/// editor) read at the same time: a reader takes whole records up to the length it found and ignores a record still
/// being written.
/// </summary>
public static class VfHistoryStore
{
    /// <summary>
    /// Appends a batch, each observation to its UTC day's file. Throws what the file system throws (disk full, access
    /// denied, the file held by another process), having put the file back to the whole records it had: the caller keeps
    /// the batch and tries again later. Appending is idempotent: a day an earlier attempt already wrote (a batch spanning
    /// days whose later day failed) is not written again (<see cref="NotYetIn"/>). A torn record at the end of a file (a write cut
    /// off by a crash or power loss) is cut away before appending, so every later record stays aligned.
    /// </summary>
    public static void Append(string dir, IReadOnlyList<VfObservation> batch)
    {
        if (batch.Count == 0) return;
        Directory.CreateDirectory(dir);
        foreach (var day in batch.GroupBy(o => VfHistoryFile.UtcDay(o.At)).OrderBy(g => g.Key))
        {
            var items = day.ToList();
            var bytes = new byte[items.Count * VfHistoryFile.RecordSize];
            for (int i = 0; i < items.Count; i++) VfHistoryFile.WriteRecord(bytes.AsSpan(i * VfHistoryFile.RecordSize), items[i]);
            AppendDay(dir, day.Key, bytes);
        }
    }

    private static void AppendDay(string dir, DateOnly day, byte[] records)
    {
        var header = VfHistoryFile.Header(day);
        for (int part = 0; part < VfHistoryFile.MaxParts; part++)
        {
            string path = Path.Combine(dir, VfHistoryFile.DayFileName(day, part));
            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            long len = fs.Length, start;
            if (len < VfHistoryFile.HeaderSize)
            {
                // Empty, or a header cut off as it was first written: only then is it ours to (re)write.
                var have = new byte[len];
                fs.ReadExactly(have);
                if (!header.AsSpan(0, (int)len).SequenceEqual(have)) continue;
                fs.SetLength(0);
                fs.Write(header);
                start = VfHistoryFile.HeaderSize;
            }
            else
            {
                var have = new byte[VfHistoryFile.HeaderSize];
                fs.ReadExactly(have);
                if (!VfHistoryFile.ValidHeader(have, day)) continue;   // another version's, or damaged: left as it is
                start = VfHistoryFile.HeaderSize + (len - VfHistoryFile.HeaderSize) / VfHistoryFile.RecordSize * VfHistoryFile.RecordSize;
                if (start != len) fs.SetLength(start);
            }
            var fresh = NotYetIn(fs, start, records);
            if (fresh.Length == 0) return;   // all on disk already: a batch an earlier, partly failed attempt wrote
            fs.Position = start;
            try
            {
                fs.Write(fresh);
                // A plain flush into the OS cache, no forced disk write: every 2 s around the clock, an fsync each time is
                // needless wear and scanner churn. A power loss can cost the last seconds; a torn tail is cut on the next append.
                fs.Flush();
            }
            catch
            {
                try { fs.SetLength(start); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return;
        }
        throw new IOException($"no day file of {day:yyyy-MM-dd} in {dir} can be appended to (every part is another version's or damaged)");
    }

    /// <summary>
    /// The records of <paramref name="records"/> whose millisecond is not among the file's last records yet. A batch that
    /// spans days is written a day at a time; when a later day fails, the caller retries the whole batch (with any looks
    /// queued since), and the days already written find their records at the end of their file: those are not written
    /// again. Only the file's last <c>records.Length / RecordSize</c> whole records can hold them (one writer appends, in
    /// time order, and a failed append is rolled back), so only those are read.
    /// </summary>
    private static byte[] NotYetIn(FileStream fs, long end, byte[] records)
    {
        int n = records.Length / VfHistoryFile.RecordSize;
        long have = (end - VfHistoryFile.HeaderSize) / VfHistoryFile.RecordSize;
        int tail = (int)Math.Min(have, n);
        if (tail == 0) return records;
        var buf = new byte[tail * VfHistoryFile.RecordSize];
        fs.Position = end - buf.Length;
        fs.ReadExactly(buf);
        var present = new HashSet<uint>();
        for (int i = 0; i < tail; i++) present.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * VfHistoryFile.RecordSize)));
        var keep = new List<int>(n);
        for (int i = 0; i < n; i++)
            if (!present.Contains(BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(i * VfHistoryFile.RecordSize)))) keep.Add(i);
        if (keep.Count == n) return records;
        var fresh = new byte[keep.Count * VfHistoryFile.RecordSize];
        for (int j = 0; j < keep.Count; j++)
            records.AsSpan(keep[j] * VfHistoryFile.RecordSize, VfHistoryFile.RecordSize).CopyTo(fresh.AsSpan(j * VfHistoryFile.RecordSize));
        return fresh;
    }

    /// <summary>Every observation of one UTC day, in time order, each millisecond once.</summary>
    public static List<VfObservation> ReadDay(string dir, DateOnly day, VfReadStats? stats = null)
    {
        var all = new List<VfObservation>();
        for (int part = 0; part < VfHistoryFile.MaxParts; part++)
        {
            string path = Path.Combine(dir, VfHistoryFile.DayFileName(day, part));
            if (!File.Exists(path)) continue;
            try { ReadFile(path, day, all, stats); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (stats != null) stats.UnreadableFiles++;
            }
        }
        all.Sort((a, b) => a.At.UtcTicks.CompareTo(b.At.UtcTicks));
        var once = new List<VfObservation>(all.Count);
        foreach (var o in all)
            if (once.Count == 0 || once[^1].At.UtcTicks != o.At.UtcTicks) once.Add(o);
        return once;
    }

    private static void ReadFile(string path, DateOnly day, List<VfObservation> into, VfReadStats? stats)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        var header = new byte[VfHistoryFile.HeaderSize];
        if (len < VfHistoryFile.HeaderSize || fs.Read(header) < VfHistoryFile.HeaderSize || !VfHistoryFile.ValidHeader(header, day))
        {
            if (stats != null) stats.UnreadableFiles++;
            return;
        }
        if (stats != null) stats.Files++;
        long whole = (len - VfHistoryFile.HeaderSize) / VfHistoryFile.RecordSize;
        if (stats != null) stats.TornBytes += (len - VfHistoryFile.HeaderSize) % VfHistoryFile.RecordSize;
        var buf = new byte[VfHistoryFile.RecordSize * 4096];
        while (whole > 0)
        {
            int n = (int)Math.Min(whole, 4096);
            int want = n * VfHistoryFile.RecordSize, got = 0;
            while (got < want)
            {
                int r = fs.Read(buf, got, want - got);
                if (r == 0) break;   // shorter than when it was opened: whatever is whole is read
                got += r;
            }
            n = got / VfHistoryFile.RecordSize;
            for (int i = 0; i < n; i++)
            {
                if (VfHistoryFile.ReadRecord(buf.AsSpan(i * VfHistoryFile.RecordSize, VfHistoryFile.RecordSize), day) is { } o) into.Add(o);
                else if (stats != null) stats.SkippedRecords++;
            }
            if (stats != null) stats.Records += n;
            if (got < want) break;
            whole -= n;
        }
    }

    /// <summary>The observations from <paramref name="from"/> to <paramref name="to"/> (both included) in time order, a UTC
    /// day at a time: a span of months is never all in memory at once. Nothing when <paramref name="dir"/> is null or absent.</summary>
    public static IEnumerable<VfObservation> Read(string? dir, DateTimeOffset from, DateTimeOffset to, VfReadStats? stats = null)
    {
        if (dir == null || !Directory.Exists(dir) || to < from) yield break;
        for (var day = VfHistoryFile.UtcDay(from); day <= VfHistoryFile.UtcDay(to); day = day.AddDays(1))
            foreach (var o in ReadDay(dir, day, stats))
                if (o.At >= from && o.At <= to) yield return o;
    }

    /// <summary>The observations inside any of the windows (trials, mostly), each day file read once; for the span from the
    /// first trial to now, which can be months, without loading the months between trials.</summary>
    public static List<VfObservation> ReadWindows(string? dir, IEnumerable<(DateTimeOffset From, DateTimeOffset To)> windows, VfReadStats? stats = null)
    {
        var result = new List<VfObservation>();
        if (dir == null || !Directory.Exists(dir)) return result;
        var merged = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var w in windows.Where(w => w.To >= w.From).OrderBy(w => w.From))
        {
            if (merged.Count > 0 && w.From <= merged[^1].To) merged[^1] = (merged[^1].From, w.To > merged[^1].To ? w.To : merged[^1].To);
            else merged.Add(w);
        }
        if (merged.Count == 0) return result;
        var days = new SortedSet<DateOnly>();
        foreach (var (f, t) in merged)
            for (var d = VfHistoryFile.UtcDay(f); d <= VfHistoryFile.UtcDay(t); d = d.AddDays(1)) days.Add(d);
        foreach (var d in days)
            foreach (var o in ReadDay(dir, d, stats))
                if (Inside(merged, o.At)) result.Add(o);
        return result;
    }

    private static bool Inside(List<(DateTimeOffset From, DateTimeOffset To)> merged, DateTimeOffset at)
    {
        int lo = 0, hi = merged.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (at < merged[mid].From) hi = mid - 1;
            else if (at > merged[mid].To) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
