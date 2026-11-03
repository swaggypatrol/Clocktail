using System.Buffers.Binary;
using System.Globalization;

namespace Clocktail.Domain.Vf;

/// <summary>
/// The always-on V/F history's day files: vf-history\yyyyMMdd.vfh beside a GPU's journal, one per UTC day (Gregorian
/// whatever the person's calendar). Little-endian throughout.
/// <code>
/// header, 16 bytes:  "CTVF" | u16 version (1) | u16 header size (16) | u16 record size (12) | u16 flags
///                    | u32 UTC day as yyyymmdd
/// record, 12 bytes:  u32 milliseconds since that UTC midnight | u32 NVVDD µV as measured | u32 core clock MHz as measured
/// </code>
/// A record is only what was measured: no requested voltage, no curve clock, no utilization or power. A day file a
/// newer or broken writer left that this version cannot append to is never rewritten: the writer goes on in
/// yyyyMMdd-1.vfh (up to <see cref="MaxParts"/>), and readers read every part of a day.
/// <para>
/// Flags (0.8.8): bit 0, <see cref="FlagOrdered"/>, says every record in the file is valid and later than the one before
/// it, so a reader can find a time by binary search instead of decoding the day. A file a 0.8.7 writer began has the
/// field 0 (it was reserved) and is read in full; 0.8.7 readers ignore the field.
/// </para>
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
    /// <summary>Header flag: records strictly increasing in time, every one valid (the 0.8.8 writer's files).</summary>
    public const ushort FlagOrdered = 1;
    public const uint MsPerDay = 86_400_000;
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
        BinaryPrimitives.WriteUInt16LittleEndian(into[10..], FlagOrdered);
        BinaryPrimitives.WriteUInt32LittleEndian(into[12..], DayNumber(utcDay));
    }

    public static byte[] Header(DateOnly utcDay)
    {
        var b = new byte[HeaderSize];
        WriteHeader(b, utcDay);
        return b;
    }

    /// <summary>Whether a valid header says its records are in strict time order (<see cref="FlagOrdered"/>).</summary>
    public static bool Ordered(ReadOnlySpan<byte> h) => (BinaryPrimitives.ReadUInt16LittleEndian(h[10..]) & FlagOrdered) != 0;

    /// <summary>Milliseconds since that UTC midnight, as a record stores it.</summary>
    public static uint MsOfDay(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return (uint)((utc - utc.Date).Ticks / TimeSpan.TicksPerMillisecond);
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
        BinaryPrimitives.WriteUInt32LittleEndian(into, MsOfDay(o.At));
        BinaryPrimitives.WriteUInt32LittleEndian(into[4..], o.Uv);
        BinaryPrimitives.WriteUInt32LittleEndian(into[8..], o.Mhz);
    }

    /// <summary>A record of that day, or null when it cannot be one (a block a power loss left as zeros, a torn write).</summary>
    public static VfObservation? ReadRecord(ReadOnlySpan<byte> r, DateOnly utcDay) =>
        TryRead(r, out var rec) ? rec.At(utcDay) : null;

    /// <summary>A record's three fields, or false when it cannot be one (as <see cref="ReadRecord"/>).</summary>
    public static bool TryRead(ReadOnlySpan<byte> r, out VfRecord rec)
    {
        uint ms = BinaryPrimitives.ReadUInt32LittleEndian(r), uv = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
            mhz = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
        rec = new VfRecord(ms, uv, mhz);
        return !(ms >= MsPerDay || uv <= MinUvExclusive || uv >= MaxUv || mhz == 0 || mhz >= MaxMhz);
    }
}

/// <summary>One stored record as it is on disk: milliseconds since its UTC midnight, µV and MHz.</summary>
public readonly record struct VfRecord(uint Ms, uint Uv, uint Mhz)
{
    public VfObservation At(DateOnly utcDay) =>
        new(new DateTimeOffset(utcDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddMilliseconds(Ms), Uv, Mhz, VfSource.Lightweight);
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
    /// the batch and tries again later. A day file only ever grows in time: a record no later than the file's last one is
    /// not written. That makes a retry idempotent (a day an earlier attempt already wrote, in a batch spanning days whose
    /// later day failed, is not written again), and a clock set back leaves a gap rather than records out of order. A
    /// torn record, or whole records a power loss left as zeros, at the end of a file are cut away before appending, so
    /// every later record stays aligned and the file stays in order (<see cref="VfHistoryFile.FlagOrdered"/>).
    /// </summary>
    public static void Append(string dir, IReadOnlyList<VfObservation> batch)
    {
        if (batch.Count == 0) return;
        Directory.CreateDirectory(dir);
        foreach (var day in batch.GroupBy(o => VfHistoryFile.UtcDay(o.At)).OrderBy(g => g.Key))
            AppendDay(dir, day.Key, [.. day]);
    }

    private static void AppendDay(string dir, DateOnly day, VfObservation[] items)
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
                start = WithoutInvalidTail(fs, start);
                if (start != len) fs.SetLength(start);
            }
            long lastMs = -1;
            if (start > VfHistoryFile.HeaderSize)
            {
                var last = new byte[VfHistoryFile.RecordSize];
                fs.Position = start - VfHistoryFile.RecordSize;
                fs.ReadExactly(last);
                lastMs = BinaryPrimitives.ReadUInt32LittleEndian(last);
            }
            var fresh = new byte[items.Length * VfHistoryFile.RecordSize];
            int n = 0;
            foreach (var o in items)
            {
                uint ms = VfHistoryFile.MsOfDay(o.At);
                if (ms <= lastMs) continue;   // on disk already (a retry), or a clock set back
                VfHistoryFile.WriteRecord(fresh.AsSpan(n++ * VfHistoryFile.RecordSize), o);
                lastMs = ms;
            }
            if (n == 0) return;
            fs.Position = start;
            try
            {
                fs.Write(fresh, 0, n * VfHistoryFile.RecordSize);
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

    /// <summary>Where the file's whole records end once the invalid ones at its end (zeros a power loss left) are cut:
    /// they hold nothing, and a later record after them would break the file's time order.</summary>
    private static long WithoutInvalidTail(FileStream fs, long end)
    {
        var rec = new byte[VfHistoryFile.RecordSize];
        for (int i = 0; i < 4096 && end > VfHistoryFile.HeaderSize; i++)
        {
            fs.Position = end - VfHistoryFile.RecordSize;
            fs.ReadExactly(rec);
            if (VfHistoryFile.TryRead(rec, out _)) break;
            end -= VfHistoryFile.RecordSize;
        }
        return end;
    }

    /// <summary>Every observation of one UTC day, in time order, each millisecond once.</summary>
    public static List<VfObservation> ReadDay(string dir, DateOnly day, VfReadStats? stats = null)
    {
        var recs = new List<VfRecord>();
        ReadDayRecords(dir, day, 0, VfHistoryFile.MsPerDay - 1, recs, stats);
        return [.. recs.Select(r => r.At(day))];
    }

    /// <summary>
    /// The valid records of one UTC day from <paramref name="fromMs"/> to <paramref name="toMs"/> (both included), into
    /// <paramref name="into"/> (cleared first), in time order, each millisecond once. A file in time order
    /// (<see cref="VfHistoryFile.FlagOrdered"/>) is searched for the range; any other is read in full.
    /// </summary>
    public static void ReadDayRecords(string dir, DateOnly day, uint fromMs, uint toMs, List<VfRecord> into, VfReadStats? stats = null)
    {
        into.Clear();
        int parts = 0;
        bool inOrder = true;
        for (int part = 0; part < VfHistoryFile.MaxParts; part++)
        {
            string path = Path.Combine(dir, VfHistoryFile.DayFileName(day, part));
            if (!File.Exists(path)) continue;
            int before = into.Count;
            try
            {
                if (!ReadFile(path, day, fromMs, toMs, into, stats)) inOrder = false;
                parts++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (stats != null) stats.UnreadableFiles++;
                into.RemoveRange(before, into.Count - before);
            }
        }
        if (parts > 1 || !inOrder)
        {
            for (int i = 1; i < into.Count; i++)
                if (into[i].Ms <= into[i - 1].Ms) { into.Sort((a, b) => a.Ms.CompareTo(b.Ms)); break; }
        }
        // Each millisecond once (a record an older writer's retry put down twice, or one in two parts).
        int w = 0;
        for (int i = 0; i < into.Count; i++)
            if (w == 0 || into[i].Ms != into[w - 1].Ms) into[w++] = into[i];
        into.RemoveRange(w, into.Count - w);
    }

    /// <summary>Adds a file's valid records in the range; true when they came in strict time order.</summary>
    private static bool ReadFile(string path, DateOnly day, uint fromMs, uint toMs, List<VfRecord> into, VfReadStats? stats, bool search = true)
    {
        int added = into.Count;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        var header = new byte[VfHistoryFile.HeaderSize];
        if (len < VfHistoryFile.HeaderSize || fs.Read(header) < VfHistoryFile.HeaderSize || !VfHistoryFile.ValidHeader(header, day))
        {
            if (stats != null) stats.UnreadableFiles++;
            return true;
        }
        if (stats != null) stats.Files++;
        long whole = (len - VfHistoryFile.HeaderSize) / VfHistoryFile.RecordSize;
        if (stats != null) stats.TornBytes += (len - VfHistoryFile.HeaderSize) % VfHistoryFile.RecordSize;
        // Only a file that says it is in order is searched, and only while every record it meets bears that out.
        bool ordered = search && VfHistoryFile.Ordered(header);
        long first = 0;
        if (ordered && fromMs > 0)
        {
            if (LowerBound(fs, whole, fromMs) is { } lo) first = lo;
            else ordered = false;   // a probe met a record that cannot be one: the whole file is read
        }
        const int Chunk = 4096;
        var buf = new byte[VfHistoryFile.RecordSize * Chunk];
        fs.Position = VfHistoryFile.HeaderSize + first * VfHistoryFile.RecordSize;
        long left = whole - first;
        uint prev = 0;
        bool any = false, inOrder = true;
        while (left > 0)
        {
            int n = (int)Math.Min(left, Chunk);
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
                if (stats != null) stats.Records++;
                if (!VfHistoryFile.TryRead(buf.AsSpan(i * VfHistoryFile.RecordSize, VfHistoryFile.RecordSize), out var rec))
                {
                    if (stats != null) stats.SkippedRecords++;
                    ordered = false;
                    continue;
                }
                if (any && rec.Ms <= prev) { inOrder = false; ordered = false; }
                if (!ordered && first > 0)
                {
                    // The file is not in the order it claims (damage): what the search skipped may be in range.
                    into.RemoveRange(added, into.Count - added);
                    fs.Dispose();
                    return ReadFile(path, day, fromMs, toMs, into, stats, search: false);
                }
                prev = rec.Ms;
                any = true;
                if (rec.Ms > toMs)
                {
                    if (ordered) return inOrder;   // the rest is later still
                    continue;
                }
                if (rec.Ms >= fromMs) into.Add(rec);
            }
            if (got < want) break;
            left -= n;
        }
        return inOrder;
    }

    /// <summary>The first whole record at or after <paramref name="ms"/> in a file in time order; null when a probe meets a
    /// record that cannot be one.</summary>
    private static long? LowerBound(FileStream fs, long whole, uint ms)
    {
        var rec = new byte[VfHistoryFile.RecordSize];
        long lo = 0, hi = whole;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            fs.Position = VfHistoryFile.HeaderSize + mid * VfHistoryFile.RecordSize;
            if (fs.Read(rec) < rec.Length || !VfHistoryFile.TryRead(rec, out var r)) return null;
            if (r.Ms < ms) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>The observations from <paramref name="from"/> to <paramref name="to"/> (both included) in time order, a UTC
    /// day at a time and only that day's range of it: a span of months is never all in memory at once. Nothing when
    /// <paramref name="dir"/> is null or absent.</summary>
    public static IEnumerable<VfObservation> Read(string? dir, DateTimeOffset from, DateTimeOffset to, VfReadStats? stats = null)
    {
        if (dir == null || !Directory.Exists(dir) || to < from) yield break;
        var recs = new List<VfRecord>();
        DateOnly first = VfHistoryFile.UtcDay(from), last = VfHistoryFile.UtcDay(to);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            uint lo = day == first ? VfHistoryFile.MsOfDay(from) : 0, hi = day == last ? VfHistoryFile.MsOfDay(to) : VfHistoryFile.MsPerDay - 1;
            ReadDayRecords(dir, day, lo, hi, recs, stats);
            foreach (var r in recs)
            {
                var o = r.At(day);
                if (o.At >= from && o.At <= to) yield return o;
            }
        }
    }

    /// <summary>The observations inside any of the windows (trials, mostly), in time order: only each window's range of
    /// its day files is read, not the days or months between trials.</summary>
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
        foreach (var (f, t) in merged) result.AddRange(Read(dir, f, t, stats));
        return result;
    }
}
