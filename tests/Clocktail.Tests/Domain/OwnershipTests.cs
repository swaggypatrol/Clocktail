using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;

namespace Clocktail.Tests.Domain;

internal sealed class FakeHost : IHost
{
    public int Pid { get; set; } = 100;
    public string BootId { get; set; } = "boot-A";
    public HashSet<int> Alive { get; } = [100];
    public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    public Dictionary<int, DateTimeOffset> StartedAt { get; } = [];
    public bool IsProcessAlive(int pid, DateTimeOffset? startedBy = null) =>
        Alive.Contains(pid) && !(startedBy is { } by && StartedAt.TryGetValue(pid, out var at) && at > by);
}

public class OwnershipJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private string P => Path.Combine(_dir, "gpu.state.json");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static OwnedItem Fan(string id = "1") => new("fan", id, "{\"level\":40,\"mode\":0}", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Claim_writes_before_the_caller_touches_hardware_and_release_forgets()
    {
        var host = new FakeHost();
        var j = new OwnershipJournal(P, host, "gpu-A");
        Assert.Equal(RecoveryReason.Nothing, j.Inspect().Reason);

        j.Claim(Fan());
        Assert.True(File.Exists(P));
        var s = j.Load()!;
        Assert.Equal(100, s.Pid);
        Assert.Equal("boot-A", s.BootId);
        Assert.Single(s.Items);

        j.Claim(Fan("2"));
        Assert.Equal(2, j.Load()!.Items.Count);
        j.Claim(Fan("1") with { Original = "{\"level\":55}" });   // re-claim replaces, never duplicates
        Assert.Equal(2, j.Load()!.Items.Count);

        j.Release("fan", "1");
        Assert.Single(j.Load()!.Items);
        j.Release("fan", "2");
        Assert.False(File.Exists(P));
        Assert.Equal(RecoveryReason.Nothing, j.Inspect().Reason);
    }

    [Fact]
    public void A_dead_owner_means_the_items_must_be_given_back()
    {
        var host = new FakeHost();
        new OwnershipJournal(P, host, "gpu-A").Claim(Fan());

        var later = new FakeHost { Pid = 200 };      // new process, same boot, pid 100 gone
        later.Alive.Remove(100);
        var plan = new OwnershipJournal(P, later, "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.OwnerDied, plan.Reason);
        Assert.Single(plan.Items);
    }

    [Fact]
    public void A_pid_reused_by_a_later_process_is_not_the_owner()
    {
        var host = new FakeHost();
        new OwnershipJournal(P, host, "gpu-A").Claim(Fan());   // claimed at the epoch

        var later = new FakeHost { Pid = 200 };   // pid 100 is running again, but it started after the claim
        later.StartedAt[100] = DateTimeOffset.UnixEpoch.AddMinutes(4);
        Assert.Equal(RecoveryReason.OwnerDied, new OwnershipJournal(P, later, "gpu-A").Inspect().Reason);

        later.StartedAt[100] = DateTimeOffset.UnixEpoch.AddMinutes(-1);   // the same process that claimed: still the owner
        Assert.Equal(RecoveryReason.OwnedByLiveProcess, new OwnershipJournal(P, later, "gpu-A").Inspect().Reason);
    }

    [Fact]
    public void A_live_owner_is_left_alone_and_cannot_be_overridden_by_a_claim()
    {
        var host = new FakeHost();
        new OwnershipJournal(P, host, "gpu-A").Claim(Fan());

        var other = new FakeHost { Pid = 200 };
        other.Alive.Add(100);
        var j2 = new OwnershipJournal(P, other, "gpu-A");
        Assert.Equal(RecoveryReason.OwnedByLiveProcess, j2.Inspect().Reason);
        Assert.Throws<InvalidOperationException>(() => j2.Claim(Fan("9")));
    }

    [Fact]
    public void A_reboot_makes_the_record_stale_rather_than_something_to_restore()
    {
        var host = new FakeHost();
        new OwnershipJournal(P, host, "gpu-A").Claim(Fan());
        var afterReboot = new FakeHost { Pid = 100, BootId = "boot-B" };
        Assert.Equal(RecoveryReason.Rebooted, new OwnershipJournal(P, afterReboot, "gpu-A").Inspect().Reason);
    }

    [Fact]
    public void Another_gpus_record_is_reported_not_acted_on()
    {
        var host = new FakeHost();
        new OwnershipJournal(P, host, "gpu-A").Claim(Fan());
        Assert.Equal(RecoveryReason.Foreign, new OwnershipJournal(P, host, "gpu-B").Inspect().Reason);
    }

    [Fact]
    public void A_record_left_only_as_its_backup_is_still_owed()
    {
        var host = new FakeHost();
        var j = new OwnershipJournal(P, host, "gpu-A");
        j.Claim(Fan("1"));
        j.Claim(Fan("2"));                                   // the first generation is now the .bak
        File.Delete(P);                                      // a replacement cut off between its two renames

        var later = new FakeHost { Pid = 200 };
        later.Alive.Remove(100);
        var plan = new OwnershipJournal(P, later, "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.OwnerDied, plan.Reason);
        Assert.Single(plan.Items);
    }

    [Fact]
    public void Corrupt_record_is_foreign_and_never_crashes_startup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(P, "{ not json");
        var plan = new OwnershipJournal(P, new FakeHost(), "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.Foreign, plan.Reason);
    }
}

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Replacement_keeps_one_backup_and_no_temp_file()
    {
        string p = Path.Combine(_dir, "a", "f.json");
        AtomicFile.WriteText(p, "one");
        Assert.Equal("one", File.ReadAllText(p));
        Assert.False(File.Exists(AtomicFile.BackupPath(p)));

        AtomicFile.WriteText(p, "two");
        Assert.Equal("two", File.ReadAllText(p));
        Assert.Equal("one", File.ReadAllText(AtomicFile.BackupPath(p)));

        AtomicFile.WriteText(p, "three");
        Assert.Equal("two", File.ReadAllText(AtomicFile.BackupPath(p)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.tmp.*"));
    }

    [Fact]
    public void Writes_of_one_path_at_once_take_turns_instead_of_colliding_on_the_temporary_file()
    {
        // Two crash traces in the same second shared "<path>.tmp.<pid>" and one failed (2026-10-19 21:48:29Z).
        string p = Path.Combine(_dir, "trace-20261019-214829.json");
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => AtomicFile.WriteText(p, $"write {i}"));
        Assert.StartsWith("write ", File.ReadAllText(p));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.*"));
    }

    [Fact]
    public void Read_falls_back_to_the_backup_when_the_file_is_gone()
    {
        string p = Path.Combine(_dir, "f.json");
        AtomicFile.WriteText(p, "one");
        AtomicFile.WriteText(p, "two");
        File.Delete(p);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(AtomicFile.Read(p)!));
        File.Delete(AtomicFile.BackupPath(p));
        Assert.Null(AtomicFile.Read(p));
    }

    [Fact]
    public void Oversized_files_are_refused_on_read()
    {
        string p = Path.Combine(_dir, "big");
        AtomicFile.Write(p, new byte[64]);
        Assert.Throws<InvalidDataException>(() => AtomicFile.Read(p, maxBytes: 32));
        Assert.Null(AtomicFile.Read(p + ".missing"));
    }
}

public class ApplySentinelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Completed_application_leaves_no_sentinel_and_an_abandoned_one_blocks_automatic_apply()
    {
        var s = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
        Assert.False(s.IsBlocked(out _));

        var scope = s.Begin("automatic", takeOver: false)!;
        Assert.True(s.IsBlocked(out var content));
        Assert.Contains("automatic", content);
        scope.Complete();
        Assert.False(s.IsBlocked(out _));

        // Simulate a crash: begin and never complete.
        _ = s.Begin("automatic", takeOver: false);
        Assert.True(s.IsBlocked(out _));
        Assert.Null(s.Begin("automatic", takeOver: false));        // the loop is cut here
        var manual = s.Begin("manual", takeOver: true);             // a person can take over
        Assert.NotNull(manual);
        manual.Complete();
        Assert.False(s.IsBlocked(out _));
    }
}
