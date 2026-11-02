using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;

namespace Clocktail.Tests.Domain;

/// <summary>The contract with StabilityLab: a live run blocks a write, a dead or garbled lock does not wedge one.</summary>
public class JudgeLockTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n") + ".json");
    private readonly FakeHost _host = new();   // pid 100 is alive

    public JudgeLockTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }

    [Fact]
    public void No_lock_is_not_busy() => Assert.Null(JudgeLock.BusyReason(_path, _host));

    [Fact]
    public void A_live_run_is_busy_and_names_its_label()
    {
        File.WriteAllText(_path, """{"Pid":100,"RunId":"r1","Label":"baseline","StartedUtc":"2026-10-17T00:00:00Z"}""");
        var why = JudgeLock.BusyReason(_path, _host);
        Assert.NotNull(why);
        Assert.Contains("100", why);
        Assert.Contains("baseline", why);
    }

    [Fact]
    public void A_lock_whose_pid_now_belongs_to_a_later_process_is_free()
    {
        // Row 11d: the run that wrote the lock died with the machine; after the reboot its pid can be anything.
        File.WriteAllText(_path, """{"Pid":100,"Label":"tune-a995mv-40","StartedUtc":"2026-10-18T02:44:10Z"}""");
        _host.StartedAt[100] = DateTimeOffset.Parse("2026-10-18T02:48:20Z");
        Assert.Null(JudgeLock.BusyReason(_path, _host));
    }

    [Fact]
    public void A_dead_runs_lock_is_stale_and_ignored()
    {
        File.WriteAllText(_path, """{"Pid":999,"Label":"baseline"}""");   // 999 is not in the host's alive set
        Assert.Null(JudgeLock.BusyReason(_path, _host));
    }

    [Fact]
    public void A_garbled_lock_does_not_wedge_writes()
    {
        File.WriteAllText(_path, "not json at all");
        Assert.Null(JudgeLock.BusyReason(_path, _host));
    }

    [Fact]
    public void Case_does_not_matter_in_the_field_names()
    {
        File.WriteAllText(_path, """{"pid":100,"label":"vf-sweep"}""");
        Assert.Contains("vf-sweep", JudgeLock.BusyReason(_path, _host));
    }

    /// <summary>
    /// The lock as StabilityLab writes it (CoexistenceGuard.HoldJudgeLock: one compact JSON object with these four
    /// fields, the pid of the process that lives for the whole run), read by the real host: our own pid is alive, so
    /// it is busy; a pid no process has is stale and ignored, whatever else the file says.
    /// </summary>
    [Fact]
    public void The_lock_StabilityLab_writes_is_busy_while_its_process_lives_and_stale_once_it_is_gone()
    {
        var host = new SystemHost();
        File.WriteAllText(_path, $$"""{"Pid":{{host.Pid}},"RunId":"20261017-211207-preheat-check2","Label":"preheat-check2","StartedUtc":"{{DateTimeOffset.UtcNow:o}}"}""");   // this process started before the lock, as StabilityLab's does
        var why = JudgeLock.BusyReason(_path, host);
        Assert.NotNull(why);
        Assert.Contains("preheat-check2", why);
        Assert.Contains(host.Pid.ToString(), why);

        File.WriteAllText(_path, $$"""{"Pid":{{int.MaxValue - 1}},"RunId":"20261017-211207-preheat-check2","Label":"preheat-check2","StartedUtc":"2026-10-17T21:12:07.7312345Z"}""");
        Assert.Null(JudgeLock.BusyReason(_path, host));
    }

    /// <summary>The 01:08 finding (drill 20): StabilityLab, started from the packaged Claude app, wrote its lock into the
    /// package's LocalCache; the elevated broker looked only at the real %LocalAppData% and saw nothing.</summary>
    [Fact]
    public void A_lock_redirected_into_a_packaged_apps_cache_is_seen()
    {
        var local = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var real = Path.Combine(local, "Clocktail", "judge.lock");
            var cached = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Local", "Clocktail", "judge.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            Directory.CreateDirectory(Path.Combine(local, "Packages", "Other_app"));
            File.WriteAllText(cached, """{"Pid":100,"Label":"burn"}""");

            var paths = JudgeLock.Candidates(real, local);
            Assert.Equal([real, cached], paths);
            Assert.Null(JudgeLock.BusyReason(real, _host));
            var why = JudgeLock.BusyReason(paths, _host, out var trace);
            Assert.Contains("burn", why);
            Assert.Contains("absent", trace);
            Assert.Contains("pid 100 alive", trace);
        }
        finally { try { Directory.Delete(local, true); } catch (IOException) { } }
    }

    /// <summary>The measurement lease: one holder at a time, its holder may take it again, only its holder gives it back, and
    /// a lock whose process is gone is taken over.</summary>
    [Fact]
    public void The_lease_is_held_once_given_back_by_its_holder_only_and_a_dead_ones_is_taken_over()
    {
        string local = Path.Combine(Path.GetTempPath(), "clocktail-tests", "no-local");
        Assert.True(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-1", "clocktail-lab run-1"), _host, local, out _));
        Assert.False(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-2", "lab_test"), _host, local, out var by));
        Assert.Contains("clocktail-lab run-1", by);
        Assert.True(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-1", "clocktail-lab run-1"), _host, local, out _));
        JudgeLock.Release(_path, "run-2");
        Assert.True(File.Exists(_path));
        JudgeLock.Release(_path, "run-1");
        Assert.False(File.Exists(_path));
        File.WriteAllText(_path, """{"Pid":999,"RunId":"old"}""");   // its process is gone
        Assert.True(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-3"), _host, local, out _));
        Assert.Equal("run-3", JudgeLock.Live(_path, _host, out _)!.RunId);
    }

    private static readonly string NoLocal = Path.Combine(Path.GetTempPath(), "clocktail-tests", "no-local");

    /// <summary>Several takers find the same stale lock at once. Each one checked the dead run's file and then deleted
    /// whatever was at the path, so a taker could delete the lock another had just written and both held the lease.</summary>
    [Fact]
    public void Only_one_of_many_takers_gets_a_stale_lock()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        const int takers = 6;
        for (int round = 0; round < 40; round++)
        {
            File.WriteAllText(_path, """{"Pid":999,"RunId":"old"}""");   // its process is gone
            using var start = new Barrier(takers);
            var won = new bool[takers];
            var threads = Enumerable.Range(0, takers).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                won[i] = JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, $"run-{i}"), _host, NoLocal, out _);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            int winner = Assert.Single(Enumerable.Range(0, takers), i => won[i]);
            Assert.Equal($"run-{winner}", JudgeLock.Live(_path, _host, out _)!.RunId);
            File.Delete(_path);
        }
    }

    /// <summary>A lock another taker still has open (it is writing it) cannot be read for a moment. That is not a stale
    /// lock: it is waited for, then named, never deleted.</summary>
    [Fact]
    public async Task A_lock_still_being_written_is_waited_for_not_taken_as_stale()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        File.WriteAllText(_path, """{"Pid":100,"RunId":"a","Label":"burn"}""");
        var writer = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var closer = Task.Run(async () => { await Task.Delay(100); writer.Dispose(); });
        try
        {
            Assert.False(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "b"), _host, NoLocal, out var by));
            Assert.Contains("burn", by);
        }
        finally { await closer; }
        Assert.Equal("a", JudgeLock.Live(_path, _host, out _)!.RunId);
    }

    /// <summary>A stale lock that cannot be deleted (here: read-only) is a lease not taken, not an exception out of
    /// TryAcquire.</summary>
    [Fact]
    public void A_stale_lock_that_cannot_be_deleted_is_reported_not_thrown()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        File.WriteAllText(_path, """{"Pid":999,"RunId":"old"}""");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-1"), _host, NoLocal, out var by));
            Assert.NotNull(by);
        }
        finally { File.SetAttributes(_path, FileAttributes.Normal); }
    }

    /// <summary>A crashed run's half-written lock (garbled, or empty) is still taken over, so a crash never wedges the lease.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    public void A_garbled_lock_is_taken_over(string content)
    {
        File.WriteAllText(_path, content);
        Assert.True(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-1"), _host, NoLocal, out _));
        Assert.Equal("run-1", JudgeLock.Live(_path, _host, out _)!.RunId);
    }

    /// <summary>Giving the lease back while someone reads the lock a moment: the read and the delete differed in what
    /// they let others do, so the delete failed quietly and the run's lock stayed behind with its live pid.</summary>
    [Fact]
    public async Task Release_waits_out_a_reader_and_deletes_its_own_lock()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        File.WriteAllText(_path, """{"Pid":100,"RunId":"run-1"}""");
        var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var closer = Task.Run(async () => { await Task.Delay(100); reader.Dispose(); });
        JudgeLock.Release(_path, "run-1");
        await closer;
        Assert.False(File.Exists(_path));
    }

    /// <summary>The handle a taker checks a lock through (<c>TakeOver</c>) and a holder gives it back through (<c>Release</c>):
    /// it lets others do nothing but delete.</summary>
    private static FileStream Checking(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Delete);

    /// <summary>While another process checks or gives back the lock, the broker cannot read it. Its judge read that as no
    /// lock, so a write landed under a live run. A lock that stays open is busy, never free, and names no run.</summary>
    [Fact]
    public void A_lock_another_process_keeps_open_is_busy_not_free()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        File.WriteAllText(_path, """{"Pid":100,"RunId":"r1","Label":"burn"}""");
        using (Checking(_path))
        {
            var info = JudgeLock.Live(_path, _host, out var trace);
            Assert.NotNull(info);
            Assert.False(JudgeLock.NamesRun(info));
            Assert.Null(info.RunId);
            Assert.Contains("open by another process", trace);
            Assert.Equal("judge.lock is open by another process", JudgeLock.BusyReason(_path, _host));
            Assert.Equal("judge.lock is open by another process", BrokerDispatcher.Judge(() => JudgeLock.Live([_path], _host, out _))());
        }
        Assert.Contains("burn", JudgeLock.BusyReason(_path, _host));
    }

    /// <summary>A lock open only a moment (a taker's check is far shorter than the tries) is read once it is free: the run in it.</summary>
    [Fact]
    public async Task A_lock_open_a_moment_is_read_once_it_is_free()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        File.WriteAllText(_path, """{"Pid":100,"RunId":"r1","Label":"burn"}""");
        var check = Checking(_path);
        var closer = Task.Run(async () => { await Task.Delay(30); check.Dispose(); });
        try { Assert.Contains("burn", JudgeLock.BusyReason(_path, _host)); }
        finally { await closer; }
    }

    /// <summary>A lock open elsewhere is not a holder to a taker: a stale one another taker is checking is still taken over
    /// once it is free, and a run's own fresh lock that a taker is checking is not given back by <c>Settle</c>.</summary>
    [Fact]
    public async Task A_lock_open_elsewhere_does_not_refuse_a_taker_or_make_a_run_give_its_own_back()
    {
        if (!OperatingSystem.IsWindows()) return;   // share modes are enforced on Windows only, where Clocktail runs
        File.WriteAllText(_path, """{"Pid":999,"RunId":"old"}""");   // its process is gone
        var check = Checking(_path);
        var closer = Task.Run(async () => { await Task.Delay(200); check.Dispose(); });
        try { Assert.True(JudgeLock.TryAcquire(_path, new JudgeLock.Info(100, "run-1"), _host, NoLocal, out var by), by); }
        finally { await closer; }
        Assert.Equal("run-1", JudgeLock.Live(_path, _host, out _)!.RunId);

        var mine = new JudgeLock.Info(100, "run-1");
        check = Checking(_path);
        closer = Task.Run(async () => { await Task.Delay(200); check.Dispose(); });
        try { Assert.True(JudgeLock.Settle(_path, mine, _host, NoLocal, out var by), by); }
        finally { await closer; }
        Assert.Equal("run-1", JudgeLock.Live(_path, _host, out _)!.RunId);
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    /// <summary>A real %LocalAppData% and one packaged app's cache of it, as <see cref="A_lock_redirected_into_a_packaged_apps_cache_is_seen"/>.</summary>
    private static void WithPackagedCopy(Action<string, string, string> test)
    {
        var local = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var real = Path.Combine(local, "Clocktail", "judge.lock");
            var cached = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Local", "Clocktail", "judge.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(real)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            test(local, real, cached);
        }
        finally { try { Directory.Delete(local, true); } catch (IOException) { } }
    }

    private static void Write(string path, JudgeLock.Info info) => File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(info));

    /// <summary>A packaged and an unpackaged taker both found nothing and both created their lock, each in its own file. The
    /// later run gives its own back at once and is told who holds the card.</summary>
    [Fact]
    public void Of_two_locks_written_in_different_copies_the_later_gives_its_own_back()
    {
        WithPackagedCopy((local, real, cached) =>
        {
            var mine = new JudgeLock.Info(100, "run-b", "lab_test", T0.AddSeconds(1));
            Write(real, mine);
            Write(cached, new JudgeLock.Info(100, "run-a", "burn", T0));
            Assert.False(JudgeLock.Settle(real, mine, _host, local, out var by));
            Assert.Contains("burn", by);
            Assert.False(File.Exists(real));
            Assert.True(File.Exists(cached));
        });
    }

    /// <summary>The earlier run waits for the later one to give its lock back, and keeps the lease once it has.</summary>
    [Fact]
    public void The_earlier_run_keeps_the_lease_once_the_later_gives_its_lock_back()
    {
        WithPackagedCopy((local, real, cached) =>
        {
            var mine = new JudgeLock.Info(100, "run-a", "burn", T0);
            Write(real, mine);
            Write(cached, new JudgeLock.Info(100, "run-b", "lab_test", T0.AddSeconds(1)));
            // The delete is tried again while the file is open for a read (Settle's look, a scanner): one refused delete
            // left the later run's lock in place and the test failed now and then.
            var later = Task.Run(async () =>
            {
                await Task.Delay(100);
                for (int i = 0; ; i++)
                {
                    try { File.Delete(cached); return; }
                    catch (IOException) when (i < 50) { await Task.Delay(20); }
                }
            });
            Assert.True(JudgeLock.Settle(real, mine, _host, local, out var by, TimeSpan.FromSeconds(5)));
            later.Wait();
            Assert.Null(by);
            Assert.True(File.Exists(real));
        });
    }

    /// <summary>A later run whose lock stays looked before ours was written and holds the lease (or does not settle at all,
    /// like StabilityLab): the earlier one gives its own back rather than hold the card with it.</summary>
    [Fact]
    public void The_earlier_run_gives_its_lock_back_when_the_later_ones_stays()
    {
        WithPackagedCopy((local, real, cached) =>
        {
            var mine = new JudgeLock.Info(100, "run-a", "burn", T0);
            Write(real, mine);
            Write(cached, new JudgeLock.Info(100, "run-b", "lab_test", T0.AddSeconds(1)));
            Assert.False(JudgeLock.Settle(real, mine, _host, local, out var by, TimeSpan.FromMilliseconds(100)));
            Assert.Contains("lab_test", by);
            Assert.False(File.Exists(real));
        });
    }

    /// <summary>A host whose first liveness question runs <paramref name="first"/>: the moment between a taker's first look
    /// and its create.</summary>
    private sealed class HookedHost(Action first) : IHost
    {
        private readonly FakeHost _inner = new();
        private Action? _first = first;
        public int Pid => _inner.Pid;
        public string BootId => _inner.BootId;
        public DateTimeOffset Now => _inner.Now;
        public bool IsProcessAlive(int pid, DateTimeOffset? startedBy = null)
        {
            Interlocked.Exchange(ref _first, null)?.Invoke();
            return _inner.IsProcessAlive(pid, startedBy);
        }
    }

    /// <summary>
    /// A packaged taker (the Claude app's MCP server) writes its lock into its package's copy just after an unpackaged one
    /// (Claude Code's) looked and found only a dead run's lock. The unpackaged one then takes that over and creates its
    /// own in the real folder: a different file, so the create alone let both hold the lease. The look after the create
    /// sees the packaged run's lock, and the later run gives its own back.
    /// </summary>
    [Fact]
    public void A_packaged_lock_written_between_the_look_and_the_create_is_seen()
    {
        WithPackagedCopy((local, real, cached) =>
        {
            File.WriteAllText(real, """{"Pid":999,"RunId":"old"}""");   // its process is gone
            var host = new HookedHost(() => Write(cached, new JudgeLock.Info(100, "packaged", "burn", T0)));
            Assert.False(JudgeLock.TryAcquire(real, new JudgeLock.Info(100, "unpackaged", "lab_test", T0.AddSeconds(1)), host, local, out var by));
            Assert.Contains("burn", by);
            Assert.False(File.Exists(real));
            Assert.Equal("packaged", JudgeLock.Live(cached, _host, out _)!.RunId);
        });
    }
}
