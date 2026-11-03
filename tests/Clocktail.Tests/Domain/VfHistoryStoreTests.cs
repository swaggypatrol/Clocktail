using System.Buffers.Binary;
using Clocktail.Domain.Vf;

namespace Clocktail.Tests.Domain;

/// <summary>The always-on V/F history's day files: the per-look gate, the format and its version, UTC day rotation,
/// a torn tail, a restart, a file another process holds, and a reader beside the writer. Temp files only.</summary>
public sealed class VfHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-vfh-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day0 = new(2026, 11, 1);

    private static VfObservation Obs(DateTimeOffset at, uint uv = 912_345, uint mhz = 2617) => new(at, uv, mhz, VfSource.Lightweight);

    private static List<VfObservation> Stream(DateTimeOffset from, int n, int everyMs = 250, uint uv = 900_000) =>
        [.. Enumerable.Range(0, n).Select(i => Obs(from.AddMilliseconds(i * everyMs), uv + (uint)i, 2500 + (uint)(i % 7)))];

    private string DayPath(DateOnly day, int part = 0) => Path.Combine(_dir, VfHistoryFile.DayFileName(day, part));

    [Theory]
    [InlineData(825_000u, 2500u, false)]   // equal is not above
    [InlineData(825_001u, 2500u, true)]
    [InlineData(824_999u, 2500u, false)]
    [InlineData(900_000u, 0u, false)]      // no clock
    [InlineData(2_000_000u, 2500u, false)] // the rail reader's own implausible bound
    public void Only_a_measured_voltage_strictly_above_825_mV_with_a_clock_is_admitted(uint uv, uint mhz, bool admitted)
    {
        Assert.Equal(admitted, VfHistoryFile.Admits(uv, mhz));
    }

    [Fact]
    public void Missing_reads_are_not_admitted()
    {
        Assert.False(VfHistoryFile.Admits(null, 2500));
        Assert.False(VfHistoryFile.Admits(900_000, null));
        Assert.False(VfHistoryFile.Admits(null, null));
    }

    [Fact]
    public void A_batch_round_trips_with_its_measured_precision_in_a_versioned_file()
    {
        var batch = Stream(T0, 40);
        VfHistoryStore.Append(_dir, batch);
        var bytes = File.ReadAllBytes(DayPath(Day0));
        Assert.Equal(VfHistoryFile.HeaderSize + 40 * VfHistoryFile.RecordSize, bytes.Length);
        Assert.Equal("CTVF"u8.ToArray(), bytes[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(20261101u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        // First record: 12:00:00.000 is 43 200 000 ms after the UTC midnight; µV exactly as measured, MHz as read.
        Assert.Equal(43_200_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(900_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(2500u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));

        var back = VfHistoryStore.ReadDay(_dir, Day0);
        Assert.Equal(batch, back);
        Assert.All(back, o => Assert.Null(o.UtilPercent));   // nothing beside the voltage and clock is made up
        Assert.All(back, o => Assert.Null(o.Watts));
    }

    [Fact]
    public void Each_observation_goes_to_its_UTC_day_whatever_offset_it_was_taken_in()
    {
        var beforeMidnight = new DateTimeOffset(2026, 11, 1, 23, 59, 59, 900, TimeSpan.Zero);
        var afterMidnight = new DateTimeOffset(2026, 11, 2, 0, 0, 0, 100, TimeSpan.Zero);
        // Taken in UTC+8 at 07:59:59.950 on Nov 2 local: still Nov 1 in UTC.
        var local = new DateTimeOffset(2026, 11, 2, 7, 59, 59, 950, TimeSpan.FromHours(8));
        // Taken in UTC-5 at 19:00:01 on Nov 1 local: already Nov 2 in UTC.
        var west = new DateTimeOffset(2026, 11, 1, 19, 0, 1, 0, TimeSpan.FromHours(-5));
        VfHistoryStore.Append(_dir, [Obs(beforeMidnight), Obs(local), Obs(afterMidnight), Obs(west)]);

        Assert.True(File.Exists(DayPath(new DateOnly(2026, 11, 1))));
        Assert.True(File.Exists(DayPath(new DateOnly(2026, 11, 2))));
        Assert.Equal([beforeMidnight, local], VfHistoryStore.ReadDay(_dir, new DateOnly(2026, 11, 1)).Select(o => o.At));
        Assert.Equal([afterMidnight, west], VfHistoryStore.ReadDay(_dir, new DateOnly(2026, 11, 2)).Select(o => o.At));
        var across = VfHistoryStore.Read(_dir, beforeMidnight, west).ToList();
        Assert.Equal([beforeMidnight, local, afterMidnight, west], across.Select(o => o.At));
        Assert.True(across.Zip(across.Skip(1)).All(p => p.First.At <= p.Second.At));
    }

    [Fact]
    public void A_torn_last_record_is_ignored_by_a_reader_and_cut_before_the_next_append()
    {
        VfHistoryStore.Append(_dir, Stream(T0, 10));
        using (var fs = new FileStream(DayPath(Day0), FileMode.Append)) fs.Write([1, 2, 3, 4, 5]);   // a write cut off by a crash
        var stats = new VfReadStats();
        Assert.Equal(10, VfHistoryStore.ReadDay(_dir, Day0, stats).Count);
        Assert.Equal(5, stats.TornBytes);

        VfHistoryStore.Append(_dir, Stream(T0.AddSeconds(10), 3));   // the broker started again
        Assert.Equal(VfHistoryFile.HeaderSize + 13 * VfHistoryFile.RecordSize, new FileInfo(DayPath(Day0)).Length);
        var back = VfHistoryStore.ReadDay(_dir, Day0);
        Assert.Equal(13, back.Count);
        Assert.Equal(Stream(T0, 10).Concat(Stream(T0.AddSeconds(10), 3)), back);
    }

    [Fact]
    public void A_header_cut_off_as_it_was_first_written_is_written_again()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(DayPath(Day0), VfHistoryFile.Header(Day0)[..7]);
        VfHistoryStore.Append(_dir, Stream(T0, 2));
        Assert.Equal(2, VfHistoryStore.ReadDay(_dir, Day0).Count);
        Assert.False(File.Exists(DayPath(Day0, 1)));
    }

    [Fact]
    public void Another_versions_file_is_left_alone_and_the_day_goes_on_in_a_part_of_its_own()
    {
        Directory.CreateDirectory(_dir);
        var foreign = VfHistoryFile.Header(Day0);
        BinaryPrimitives.WriteUInt16LittleEndian(foreign.AsSpan(4), 2);   // a newer writer's
        byte[] content = [.. foreign, .. new byte[24]];
        File.WriteAllBytes(DayPath(Day0), content);

        VfHistoryStore.Append(_dir, Stream(T0, 4));
        Assert.Equal(content, File.ReadAllBytes(DayPath(Day0)));        // not deleted, not rewritten
        Assert.True(File.Exists(DayPath(Day0, 1)));
        var stats = new VfReadStats();
        Assert.Equal(4, VfHistoryStore.ReadDay(_dir, Day0, stats).Count);
        Assert.Equal(1, stats.UnreadableFiles);
    }

    [Fact]
    public void A_file_of_another_day_under_this_days_name_is_not_read_or_appended_to()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(DayPath(Day0), VfHistoryFile.Header(Day0.AddDays(1)));
        VfHistoryStore.Append(_dir, Stream(T0, 1));
        Assert.Equal(VfHistoryFile.HeaderSize, new FileInfo(DayPath(Day0)).Length);
        Assert.Single(VfHistoryStore.ReadDay(_dir, Day0));
    }

    [Fact]
    public void Zeroed_or_implausible_records_are_skipped_not_believed()
    {
        VfHistoryStore.Append(_dir, Stream(T0, 3));
        var junk = new byte[2 * VfHistoryFile.RecordSize];   // a power loss can leave a zeroed block
        BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(12), 1_000);
        BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(16), 800_000);   // at or under the gate: never written by us
        BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(20), 2500);
        using (var fs = new FileStream(DayPath(Day0), FileMode.Append)) fs.Write(junk);
        var stats = new VfReadStats();
        Assert.Equal(3, VfHistoryStore.ReadDay(_dir, Day0, stats).Count);
        Assert.Equal(2, stats.SkippedRecords);
    }

    [Fact]
    public void A_batch_appended_again_is_not_written_twice()
    {
        var batch = Stream(T0, 5);
        VfHistoryStore.Append(_dir, batch);
        VfHistoryStore.Append(_dir, batch[3..]);
        VfHistoryStore.Append(_dir, batch);
        Assert.Equal(VfHistoryFile.HeaderSize + 5 * VfHistoryFile.RecordSize, new FileInfo(DayPath(Day0)).Length);
        Assert.Equal(batch, VfHistoryStore.ReadDay(_dir, Day0));
    }

    [Fact]
    public void A_retry_with_looks_queued_since_writes_only_the_new_ones()
    {
        var first = Stream(T0, 8);
        var more = Stream(T0.AddSeconds(2), 4);   // 2.00 … 2.75 s, after the first batch
        VfHistoryStore.Append(_dir, first);
        VfHistoryStore.Append(_dir, [.. first, .. more]);
        Assert.Equal(VfHistoryFile.HeaderSize + 12 * VfHistoryFile.RecordSize, new FileInfo(DayPath(Day0)).Length);
        Assert.Equal([.. first, .. more], VfHistoryStore.ReadDay(_dir, Day0));
    }

    [Fact]
    public void A_three_day_batch_whose_middle_day_is_held_is_retried_without_duplicating_any_day()
    {
        var a = Stream(T0, 4);
        var b = Stream(T0.AddDays(1), 4);
        var c = Stream(T0.AddDays(2), 4);
        List<VfObservation> batch = [.. a, .. b, .. c];
        Directory.CreateDirectory(_dir);
        using (new FileStream(DayPath(Day0.AddDays(1)), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => VfHistoryStore.Append(_dir, batch));
            Assert.ThrowsAny<IOException>(() => VfHistoryStore.Append(_dir, batch));
        }
        Assert.False(File.Exists(DayPath(Day0.AddDays(2))));    // days are written in order; the batch stays the caller's
        var later = Stream(T0.AddDays(2).AddSeconds(5), 2);     // looks queued while it waited
        VfHistoryStore.Append(_dir, [.. batch, .. later]);
        long Len(int d) => new FileInfo(DayPath(Day0.AddDays(d))).Length;
        Assert.Equal(VfHistoryFile.HeaderSize + 4 * VfHistoryFile.RecordSize, Len(0));
        Assert.Equal(VfHistoryFile.HeaderSize + 4 * VfHistoryFile.RecordSize, Len(1));
        Assert.Equal(VfHistoryFile.HeaderSize + 6 * VfHistoryFile.RecordSize, Len(2));
        Assert.Equal(a, VfHistoryStore.ReadDay(_dir, Day0));
        Assert.Equal(b, VfHistoryStore.ReadDay(_dir, Day0.AddDays(1)));
        Assert.Equal([.. c, .. later], VfHistoryStore.ReadDay(_dir, Day0.AddDays(2)));
    }

    [Fact]
    public void A_retry_after_a_torn_tail_rewrites_only_the_record_that_was_cut()
    {
        var batch = Stream(T0, 6);
        VfHistoryStore.Append(_dir, batch);
        // A crash mid-append of the next batch: its first record whole, its second cut off.
        var next = Stream(T0.AddSeconds(5), 3);
        var torn = new byte[VfHistoryFile.RecordSize + 5];
        VfHistoryFile.WriteRecord(torn, next[0]);
        using (var fs = new FileStream(DayPath(Day0), FileMode.Append)) fs.Write(torn);
        VfHistoryStore.Append(_dir, next);
        Assert.Equal(VfHistoryFile.HeaderSize + 9 * VfHistoryFile.RecordSize, new FileInfo(DayPath(Day0)).Length);
        Assert.Equal([.. batch, .. next], VfHistoryStore.ReadDay(_dir, Day0));
    }

    [Fact]
    public void A_recorder_retry_through_the_real_store_leaves_each_day_once()
    {
        var log = new List<string>();
        var rec = new Clocktail.Broker.VfHistoryRecorder(new Dictionary<string, string> { ["G"] = _dir }, log.Add);
        var lastOfDay = new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero);
        var at = lastOfDay;
        for (int i = 0; i < 4 * 180; i++, at = at.AddMilliseconds(250))   // three minutes across midnight, 4 a second
            rec.Look("G", at, 900_000, 2500, 10, null);
        Directory.CreateDirectory(_dir);
        // The quarantine released the first two minutes (day 1's last minute, day 2's first); day 2's file is held.
        using (new FileStream(DayPath(Day0.AddDays(1)), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.False(rec.FlushOnce(at, force: true));
        Assert.True(rec.FlushOnce(at, force: true));
        var day1 = VfHistoryStore.ReadDay(_dir, Day0);
        var day2 = VfHistoryStore.ReadDay(_dir, Day0.AddDays(1));
        Assert.Equal(240, day1.Count);   // 23:59:00.000 … 23:59:59.750
        Assert.Equal(240, day2.Count);   // 00:00:00.000 … 00:00:59.750
        Assert.Equal(VfHistoryFile.HeaderSize + day1.Count * VfHistoryFile.RecordSize, new FileInfo(DayPath(Day0)).Length);
        Assert.Equal(VfHistoryFile.HeaderSize + day2.Count * VfHistoryFile.RecordSize, new FileInfo(DayPath(Day0.AddDays(1))).Length);
        Assert.Equal(rec.Status.Written, day1.Count + day2.Count);
    }

    [Fact]
    public void A_file_another_process_holds_fails_the_append_without_writing_part_of_it()
    {
        VfHistoryStore.Append(_dir, Stream(T0, 2));
        using (new FileStream(DayPath(Day0), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => VfHistoryStore.Append(_dir, Stream(T0.AddSeconds(1), 2)));
        VfHistoryStore.Append(_dir, Stream(T0.AddSeconds(1), 2));    // tried again once the lock let go
        Assert.Equal(4, VfHistoryStore.ReadDay(_dir, Day0).Count);
    }

    [Fact]
    public async Task A_reader_beside_the_writer_only_ever_sees_whole_records()
    {
        var writer = Task.Run(() =>
        {
            for (int b = 0; b < 200; b++) VfHistoryStore.Append(_dir, Stream(T0.AddSeconds(b * 3), 10));
        });
        int last = 0;
        var errors = new List<Exception>();
        while (!writer.IsCompleted)
        {
            try
            {
                var stats = new VfReadStats();
                int n = VfHistoryStore.ReadDay(_dir, Day0, stats).Count;
                Assert.True(n >= last);
                Assert.Equal(0, stats.SkippedRecords);
                last = n;
            }
            catch (IOException e) { errors.Add(e); }   // never expected: reads share the file with the writer
        }
        await writer;
        Assert.Empty(errors);
        Assert.Equal(2000, VfHistoryStore.ReadDay(_dir, Day0).Count);
    }

    [Fact]
    public void Windows_read_only_what_falls_inside_them()
    {
        VfHistoryStore.Append(_dir, Stream(T0, 400));   // 100 s
        var inside = VfHistoryStore.ReadWindows(_dir, [(T0.AddSeconds(10), T0.AddSeconds(20)), (T0.AddSeconds(15), T0.AddSeconds(30)), (T0.AddSeconds(90), T0.AddSeconds(91))]);
        Assert.Equal(81 + 5, inside.Count);   // 10.00 … 30.00 s, 90.00 … 91.00 s, four a second, both ends included
        Assert.All(inside, o => Assert.True(o.At >= T0.AddSeconds(10) && o.At <= T0.AddSeconds(30) || o.At >= T0.AddSeconds(90) && o.At <= T0.AddSeconds(91)));
        Assert.Empty(VfHistoryStore.ReadWindows(null, [(T0, T0.AddSeconds(5))]));
        Assert.Empty(VfHistoryStore.Read(Path.Combine(_dir, "absent"), T0, T0.AddDays(1)));
    }

    [Fact]
    public void The_history_directory_sits_beside_the_gpus_records()
    {
        var data = Path.Combine(_dir, "GPU-1", "datapoints.jsonl");
        Assert.Equal(Path.Combine(_dir, "GPU-1", "vf-history"), VfHistoryFile.DirBeside(data));
        Assert.Equal("20261101.vfh", VfHistoryFile.DayFileName(Day0));
        Assert.Equal("20261101-2.vfh", VfHistoryFile.DayFileName(Day0, 2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
