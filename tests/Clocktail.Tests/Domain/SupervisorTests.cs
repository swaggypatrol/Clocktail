using Clocktail.Domain.Platform;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The supervisor the broker and the tray run under (2026-10-19 21:48:29Z: the broker died of an access violation inside
/// NVML during a device restart, and nothing started it again). Workers are exit codes and run times; the clock is fake.
/// </summary>
public class SupervisorTests
{
    private const int AccessViolation = unchecked((int)0xC0000005);

    private DateTimeOffset _now = new(2026, 10, 19, 21, 48, 0, TimeSpan.Zero);
    private readonly List<string> _log = [];
    private readonly List<TimeSpan> _waits = [];
    private readonly List<int> _attempts = [];

    /// <summary>Each worker in turn: how long it runs and how it exits.</summary>
    private Supervisor Over(int? giveUpAfter, params (TimeSpan Runs, int Exit)[] workers)
    {
        int i = 0;
        return new Supervisor(attempt =>
        {
            _attempts.Add(attempt);
            var (runs, exit) = workers[i++];
            _now += runs;
            return exit;
        }, _log.Add, () => _now, t => { _waits.Add(t); _now += t; }, giveUpAfter);
    }

    private static (TimeSpan, int) Ran(double seconds, int exit) => (TimeSpan.FromSeconds(seconds), exit);

    [Fact]
    public void A_worker_that_dies_is_started_again_and_one_asked_to_stop_ends_the_supervisor()
    {
        var s = Over(null, Ran(3 * 3600, AccessViolation), Ran(600, 0));
        Assert.Equal(0, s.Run());
        Assert.Equal([0, 1], _attempts);
        Assert.Equal([TimeSpan.FromSeconds(2)], _waits);
        Assert.Contains("0xC0000005 (access violation) after 3 h; starting a new one in 2 s", _log[0]);
        Assert.Contains("stopped cleanly", _log[^1]);
    }

    [Fact]
    public void Quick_deaths_back_off_and_a_worker_that_ran_a_while_starts_the_backoff_over()
    {
        var s = Over(null,
            Ran(1, AccessViolation), Ran(0.5, 1), Ran(0.5, 1), Ran(0.5, 1), Ran(0.5, 1), Ran(0.5, 1),
            Ran(Supervisor.Healthy.TotalSeconds, AccessViolation), Ran(1, 1), Ran(60, 0));
        Assert.Equal(0, s.Run());
        Assert.Equal(new[] { 2, 5, 15, 30, 60, 60, 2, 5 }.Select(x => TimeSpan.FromSeconds(x)), _waits);
    }

    [Fact]
    public void A_worker_whose_driver_went_away_is_always_started_again_and_never_counts_toward_giving_up()
    {
        var workers = Enumerable.Repeat(Ran(1, Supervisor.DriverLost), 20).Append(Ran(1, 0)).ToArray();
        Assert.Equal(0, Over(3, workers).Run());
        Assert.Equal(21, _attempts.Count);
        Assert.Contains("75 (the driver it opened went away)", _log[0]);
    }

    [Fact]
    public void With_a_limit_a_worker_that_keeps_dying_at_once_is_given_up_on()
    {
        var s = Over(3, Ran(1, AccessViolation), Ran(1, Supervisor.DriverLost), Ran(1, AccessViolation), Ran(1, AccessViolation), Ran(1, AccessViolation));
        Assert.Equal(AccessViolation, s.Run());
        Assert.Equal(5, _attempts.Count);
        Assert.Contains("3 quick exits in a row; giving up", _log[^1]);
    }

    [Fact]
    public void A_worker_that_cannot_be_started_is_tried_again_like_one_that_died()
    {
        int tries = 0;
        var s = new Supervisor(_ => ++tries < 3 ? throw new InvalidOperationException("no exe") : 0, _log.Add, () => _now, t => { _waits.Add(t); _now += t; });
        Assert.Equal(0, s.Run());
        Assert.Equal(3, tries);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _waits);
        Assert.Contains("could not run a worker: no exe", _log[0]);
    }

    [Fact]
    public void A_worker_process_hands_back_every_line_it_printed_and_its_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var said = new List<string>();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // Alive for about a second, so it is still there to be put in the job that dies with the supervisor.
        int code = new WorkerProcess(cmd).Run("/d /c \"echo out& echo err 1>&2& ping -n 2 127.0.0.1 >nul& exit /b 7\"", l => { lock (lines) lines.Add(l.Trim()); }, said.Add);
        Assert.Equal(7, code);
        Assert.Contains("out", lines);
        Assert.Contains("err", lines);
        Assert.Empty(said);   // started, and tied to the job

    }

    /// <summary>A worker that exits while a process it started still holds its output (PresentMon, for a capture of up to
    /// 4 h) is seen to exit at once, not when that process ends: the supervisor restarts the worker.</summary>
    [Fact]
    public void A_worker_that_exits_is_waited_for_not_a_process_holding_its_output()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var said = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // ping runs ~10 s with the worker's output inherited; the worker itself exits at once.
        int code = new WorkerProcess(cmd).Run("/d /c \"echo started& start \"\" /b ping -n 11 127.0.0.1 >nul& exit /b 3\"", _ => { }, said.Add);
        Assert.Equal(3, code);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 7);
        Assert.Contains(said, l => l.Contains("its output stayed open"));
    }

    [Fact]
    public void Exit_codes_read_as_a_person_reads_them()
    {
        Assert.Equal("0xC0000005 (access violation)", Supervisor.Describe(AccessViolation));
        Assert.Equal("0xE0434352 (unhandled .NET exception)", Supervisor.Describe(unchecked((int)0xE0434352)));
        Assert.Equal("0xC000013A", Supervisor.Describe(unchecked((int)0xC000013A)));
        Assert.Equal("1", Supervisor.Describe(1));
    }
}
