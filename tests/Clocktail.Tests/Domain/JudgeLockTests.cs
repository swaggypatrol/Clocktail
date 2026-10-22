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
}
