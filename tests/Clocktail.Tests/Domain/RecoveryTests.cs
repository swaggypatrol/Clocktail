using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

[Collection("fake-driver")]
public class RecoveryTests : IDisposable
{
    private const int CoreDelta = 0x14 + 8 + 0xc;
    private const int CoreFreq = 0x14 + 8 + 0x18;    // data.range.minFreq_kHz: the driver moves it with the offset
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public RecoveryTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Table(int coreDelta)
    {
        var b = NvClient.NewBuffer(Slot.GetPstates20);
        void W(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        W(4, 1); W(8, 1); W(0xc, 1);
        int p = Pstates20Layout.Pstate(0); W(p, 0); W(p + 4, 1);
        int c = Pstates20Layout.Clock(0, 0); W(c, 0); W(c + 4, 1); W(c + 8, 1); I(c + 0xc, coreDelta); I(c + 0x10, -1_000_000); I(c + 0x14, 1_000_000); I(c + 0x18, 2_407_000 + coreDelta); I(c + 0x1c, 3_090_000 + coreDelta);
        return b;
    }

    private (Recovery rec, NvClient hw) Setup(int coreDelta)
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.GetPstates20).Shape!.Value.Word] = Table(coreDelta);
        FakeDriver.RequestApplier = (state, req) =>
        {
            var next = (byte[])state.Clone();
            int delta = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(0x28)), old = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(CoreDelta));
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreDelta), delta);
            BinaryPrimitives.WriteInt32LittleEndian(next.AsSpan(CoreFreq), BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(CoreFreq)) + delta - old);
            return next;
        };
        var hw = new NvClient(FakeDriver.ApiFor([Slot.GetPstates20], [Slot.SetPstates20]));
        return (new Recovery(hw, _journal, _sentinel, _host), hw);
    }

    /// <summary>A record left by pid 32316, which is gone, plus the sentinel it never cleared: the live 2026-10-17 case.</summary>
    private void LeaveOrphan(string original = "0", string kind = ClockOffsets.Kind, string key = "core")
    {
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(kind, key, original, _host.Now, "P0 clock offset, kHz"));
        _sentinel.Begin("manual", false);
        _host.Pid = 100;   // the recovering process
        Assert.Equal(RecoveryReason.OwnerDied, _journal.Inspect().Reason);
    }

    [Fact]
    public void Orphan_with_hardware_at_original_is_released_without_a_write()
    {
        var (rec, _) = Setup(coreDelta: 0);
        LeaveOrphan();
        var r = rec.Run(_gpu, canWrite: false);
        var item = Assert.Single(r.Items);
        Assert.Equal(RecoveryAction.Released, item.Action);
        Assert.Equal("0", item.Current);
        Assert.True(r.Clean, r.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Orphan_with_hardware_moved_is_written_back_through_the_transaction()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        LeaveOrphan();
        var r = rec.Run(_gpu, canWrite: true);
        var item = Assert.Single(r.Items);
        Assert.Equal(RecoveryAction.Written, item.Action);
        Assert.Equal("15000", item.Current);
        Assert.Equal(TxOutcome.Applied, item.Tx!.Outcome);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.Current(Slot.GetPstates20).AsSpan(CoreDelta)));
        Assert.True(r.Clean);
        Assert.Equal(1, FakeDriver.Sets);
    }

    [Fact]
    public void Orphan_with_hardware_moved_is_not_written_without_the_right_to_write()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        LeaveOrphan();
        var r = rec.Run(_gpu, canWrite: false);
        Assert.Equal(RecoveryAction.Refused, Assert.Single(r.Items).Action);
        Assert.True(r.NeedsWrite);
        Assert.False(r.Clean);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.NotNull(_journal.Load());              // the debt stays for an administrator run (or the next write) to pay
        Assert.False(_sentinel.IsBlocked(out _));     // but the hardware was looked at: 15000 kHz is a known state
    }

    [Fact]
    public void A_refused_give_back_keeps_the_record_and_knows_where_the_hardware_is()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        LeaveOrphan();
        FakeDriver.Mode = FakeDriver.SetMode.RefuseUntouched;
        var r = rec.Run(_gpu, canWrite: true);
        var item = Assert.Single(r.Items);
        Assert.Equal(RecoveryAction.Failed, item.Action);
        Assert.True(item.StateKnown);                 // the readback shows the write did not land: still 15000
        Assert.False(r.Clean);
        Assert.NotNull(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_give_back_that_ends_in_an_unknown_state_keeps_the_sentinel()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        LeaveOrphan();
        FakeDriver.OnSet = () => FakeDriver.FailNextGets = 1;   // the readback after the write fails
        var r = rec.Run(_gpu, canWrite: true);
        var item = Assert.Single(r.Items);
        Assert.Equal(RecoveryAction.Failed, item.Action);
        Assert.False(item.StateKnown);
        Assert.NotNull(_journal.Load());
        Assert.True(_sentinel.IsBlocked(out _));
    }

    /// <summary>A dead owner's record this version cannot give back (an unknown kind) and no window open: recovery opens
    /// one, writes nothing, and must not leave it up, or every later write is refused with nothing in flight.</summary>
    [Fact]
    public void An_item_recovery_cannot_prepare_leaves_the_debt_but_not_a_sentinel()
    {
        var (rec, _) = Setup(coreDelta: 0);
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem("from-a-newer-version", "x", "1", _host.Now, null));
        _host.Pid = 100;
        var r = rec.Run(_gpu, canWrite: true);
        var item = Assert.Single(r.Items);
        Assert.Equal(RecoveryAction.Refused, item.Action);
        Assert.False(item.StateKnown);
        Assert.NotNull(_journal.Load());             // the debt stays on record
        Assert.False(_sentinel.IsBlocked(out _));    // but writes are not locked out
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_debt_without_a_sentinel_is_paid_inside_one_sentinel_window()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        _host.Pid = 18812;   // the live 17:14 case: a clean +15 left a record and no sentinel
        _journal.Claim(new OwnedItem(ClockOffsets.Kind, "core", "0", _host.Now, "P0 clock offset, kHz"));
        _host.Pid = 100;
        Assert.False(_sentinel.IsBlocked(out _));
        bool openDuringWrite = false;
        FakeDriver.OnSet = () => openDuringWrite = _sentinel.IsBlocked(out _);
        var r = rec.Run(_gpu, canWrite: true);
        Assert.Equal(RecoveryAction.Written, Assert.Single(r.Items).Action);
        Assert.True(openDuringWrite);                 // the write window was covered
        Assert.True(r.Clean);
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_live_owner_is_left_alone()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        LeaveOrphan();
        _host.Alive.Add(32316);
        var r = rec.Run(_gpu, canWrite: true);
        Assert.Equal(RecoveryReason.OwnedByLiveProcess, r.Reason);
        Assert.Equal(RecoveryAction.Refused, Assert.Single(r.Items).Action);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.True(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_record_from_another_boot_is_dropped_without_a_write()
    {
        var (rec, _) = Setup(coreDelta: 15_000);   // the hardware value is irrelevant: the reboot reset it
        LeaveOrphan();
        _sentinel.Clear();                          // the window had closed; the reboot just outlived the record
        _host.BootId = "boot-B";
        var r = rec.Run(_gpu, canWrite: false);
        Assert.Equal(RecoveryReason.Rebooted, r.Reason);
        Assert.True(r.Clean);
        Assert.Null(r.CrashRecorded);
        Assert.False(File.Exists(CrashLog.PathBeside(_sentinel.Path)));
        Assert.Equal(0, FakeDriver.Sets);
    }

    /// <summary>
    /// The daily profile is held all day, so a curve that blue-screens a game leaves no window open (row 11d: 0x133,
    /// no driver-reset event). With the broker's start reporting an unclean end of the last boot, held cells from
    /// before it are a crash all the same: on record, then cleared, so the profile goes back on one notch lower.
    /// </summary>
    [Fact]
    public void Held_cells_across_an_unclean_shutdown_are_a_crash_though_no_window_was_open()
    {
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem("vf", "core", "0*127", _host.Now, "daily profile"));
        _host.Pid = 100;
        _host.BootId = "boot-B";
        Assert.False(_sentinel.IsBlocked(out _));

        var path = CrashLog.SettleReboot(_journal, _sentinel, _host, uncleanShutdown: "Kernel-Power 41, BugCheck 1001");
        Assert.Equal(CrashLog.PathBeside(_sentinel.Path), path);
        var crash = Assert.Single(CrashLog.ReadLast(path!, 5));
        Assert.Equal("boot-A", crash.BootId);
        Assert.Contains("uncleanly (Kernel-Power 41, BugCheck 1001)", crash.Sentinel);
        Assert.Equal("vf/core", Assert.Single(crash.Items).Id);
        Assert.Equal(RecoveryReason.Nothing, _journal.Inspect().Reason);
    }

    [Fact]
    public void A_clean_reboot_or_nothing_held_is_not_a_crash()
    {
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem("vf", "core", "0*127", _host.Now, "daily profile"));
        _host.Pid = 100;
        _host.BootId = "boot-B";
        Assert.Null(CrashLog.SettleReboot(_journal, _sentinel, _host));          // a clean shutdown: the record just outlived the boot
        Assert.Equal(RecoveryReason.Rebooted, _journal.Inspect().Reason);

        _journal.Clear();                                                        // the profile was off: nothing held
        Assert.Null(CrashLog.SettleReboot(_journal, _sentinel, _host, uncleanShutdown: "Kernel-Power 41"));
        Assert.False(File.Exists(CrashLog.PathBeside(_sentinel.Path)));
    }

    /// <summary>
    /// A write window still open across a reboot is the one evidence that a write took the machine down. Before
    /// the record and the sentinel are cleared as reset, what was in flight (the sentinel's line, with the cell and
    /// the value) and what was held go to the crash log, oldest first, so a person or the UI can see which write to
    /// avoid.
    /// </summary>
    [Fact]
    public void A_window_open_across_a_reboot_is_written_to_the_crash_log_before_it_is_cleared()
    {
        var (rec, _) = Setup(coreDelta: 15_000);
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(ClockOffsets.Kind, "core", "0", _host.Now, "P0 clock offset, kHz"));
        _sentinel.Begin("manual", false, "pstates20/core <- 300000 kHz");
        _host.Pid = 100;
        _host.BootId = "boot-B";

        var r = rec.Run(_gpu, canWrite: false);
        Assert.Equal(RecoveryReason.Rebooted, r.Reason);
        Assert.True(r.Clean);
        var path = CrashLog.PathBeside(_sentinel.Path);
        Assert.Equal(path, r.CrashRecorded);
        Assert.Contains("a write window was open when the machine went down", r.Detail);
        var crash = Assert.Single(CrashLog.ReadLast(path, 5));
        Assert.Equal("boot-A", crash.BootId);
        Assert.Contains("Clocktail manual apply in progress", crash.Sentinel);
        Assert.Contains("pstates20/core <- 300000 kHz", crash.Sentinel);
        Assert.Equal("pstates20/core", Assert.Single(crash.Items).Id);
        Assert.Equal("0", crash.Items[0].Original);
        Assert.Equal(0, FakeDriver.Sets);

        // A second one appends; the log keeps them oldest first and is read back in order.
        _journal.Claim(new OwnedItem(ClockOffsets.Kind, "core", "0", _host.Now, null));
        _sentinel.Begin("manual", false, "pstates20/core <- 450000 kHz");
        _host.BootId = "boot-C";
        rec.Run(_gpu, canWrite: false);
        var two = CrashLog.ReadLast(path, 5);
        Assert.Equal(2, two.Count);
        Assert.Contains("450000", two[1].Sentinel);
        Assert.Single(CrashLog.ReadLast(path, 1));
    }

    /// <summary>
    /// The first thing after a reboot is rarely recovery: a write comes first. Its take-over used to overwrite the
    /// sentinel, and its origin lookup used to drop the record (<see cref="OwnershipJournal.Adopt"/>) before anyone
    /// looked at the sentinel, so by the time recovery ran the one record of which write took the machine down was
    /// gone. Every door now settles the reboot first, and settling is idempotent.
    /// </summary>
    [Fact]
    public void The_first_write_after_a_reboot_puts_the_open_window_on_record_before_the_stale_marks_go()
    {
        var (rec, hw) = Setup(coreDelta: 15_000);
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(ClockOffsets.Kind, "core", "0", _host.Now, "P0 clock offset, kHz"));
        _sentinel.Begin("manual", false, "pstates20/core <- 300000 kHz");
        _host.Pid = 100;
        _host.BootId = "boot-B";

        var co = new ClockOffsets(hw, _journal, _sentinel, _host);
        var up = co.Apply(_gpu, ClockDomain.Core, 30_000, true);   // a take-over: it used to replace the stale sentinel's line
        Assert.Equal(TxOutcome.Applied, up.Tx.Outcome);
        var path = CrashLog.PathBeside(_sentinel.Path);
        var crash = Assert.Single(CrashLog.ReadLast(path, 5));
        Assert.Equal("boot-A", crash.BootId);
        Assert.Contains("pstates20/core <- 300000 kHz", crash.Sentinel);
        Assert.Equal("0", Assert.Single(crash.Items).Original);
        Assert.Equal(100, _journal.Load()!.Pid);                 // the new write's own record, in this boot
        Assert.Equal("15000", _journal.Load()!.Items[0].Original);

        Assert.Equal(RecoveryAction.Written, Assert.Single(rec.Run(_gpu, canWrite: true).Items).Action);
        Assert.Single(CrashLog.ReadLast(path, 5));               // nothing added by the write or the recovery
    }

    [Fact]
    public void A_writes_origin_lookup_after_a_reboot_records_the_open_window_too()
    {
        var (rec, hw) = Setup(coreDelta: 15_000);
        _host.Pid = 32316;
        _journal.Claim(new OwnedItem(ClockOffsets.Kind, "core", "0", _host.Now, "P0 clock offset, kHz"));
        _sentinel.Begin("manual", false, "pstates20/core <- 300000 kHz");
        _host.Pid = 100;
        _host.BootId = "boot-B";

        var writer = new OwnedWriter(hw, _journal, _sentinel, _host);
        Assert.Null(writer.AdoptedOriginal(ClockOffsets.Kind, "core"));   // what the broker asks before planning a write
        var path = CrashLog.PathBeside(_sentinel.Path);
        Assert.Contains("300000", Assert.Single(CrashLog.ReadLast(path, 5)).Sentinel);
        Assert.False(_sentinel.IsBlocked(out _));
        Assert.Null(_journal.Load());
        Assert.True(rec.Run(_gpu, canWrite: false).Clean);
        Assert.Single(CrashLog.ReadLast(path, 5));
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void An_unknown_kind_is_refused_and_nothing_is_cleared()
    {
        var (rec, _) = Setup(coreDelta: 0);
        LeaveOrphan(kind: "rails", key: "nvvdd");
        var r = rec.Run(_gpu, canWrite: true);
        Assert.Equal(RecoveryAction.Refused, Assert.Single(r.Items).Action);
        Assert.False(r.Clean);
        Assert.True(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_sentinel_without_a_record_is_simply_cleared()
    {
        var (rec, _) = Setup(coreDelta: 0);
        _sentinel.Begin("manual", false);
        var r = rec.Run(_gpu, canWrite: false);
        Assert.Equal(RecoveryReason.Nothing, r.Reason);
        Assert.True(r.Clean);
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void After_recovery_the_write_path_accepts_a_new_claim()
    {
        var (rec, hw) = Setup(coreDelta: 0);
        LeaveOrphan();
        Assert.True(rec.Run(_gpu, canWrite: false).Clean);
        var co = new ClockOffsets(hw, _journal, _sentinel, _host);
        var up = co.Apply(_gpu, ClockDomain.Core, 15_000);
        Assert.Equal(TxOutcome.Applied, up.Tx.Outcome);
        Assert.Equal(100, _journal.Load()!.Pid);
    }
}
