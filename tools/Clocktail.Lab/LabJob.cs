using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;

namespace Clocktail.Lab;

/// <summary>
/// What the lab's background jobs (the memory sweep, the uncore tuner) share: requests to the service for the card, a
/// measurement in a child lab process (a hang or a driver reset ends that child, not the job), the stop file lab_stop
/// writes, the heartbeat that keeps the agent's trial lease and says this job is alive, and the status written whole.
/// Two boundaries hold the evidence together. The card: one job at a time per card (<see cref="ClaimCard"/>). The
/// measurement: while a candidate is written and measured the job holds judge.lock (<see cref="Hold"/>), so no other
/// write lands under it, and its own writes carry the lease; a result is kept only when the trial on the card is still the
/// one the job wrote (<see cref="TrialAt"/>).
/// </summary>
public abstract class LabJob(string stableId, string pci, int graceSeconds, string jobDir, string labExe)
{
    protected static readonly JsonSerializerOptions StatusJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string StatusPath => Path.Combine(jobDir, "status.json");
    public string StopPath => Path.Combine(jobDir, "stop");
    /// <summary>Who runs this job and since when, rewritten every minute: lab_status and lab_stop find a live job by it, not
    /// by how fresh its status is (a measurement can wait 10 minutes for a screen saver without a new step).</summary>
    public string AlivePath => Path.Combine(jobDir, "alive.json");
    protected bool StopAsked => File.Exists(StopPath);
    protected string JobDir => jobDir;
    /// <summary>The job's identity: its folder's name, unique per start. The measurement lease's RunId too.</summary>
    protected string RunId => Path.GetFileName(jobDir);

    private readonly KillOnCloseJob? _children = OperatingSystem.IsWindows() ? new KillOnCloseJob() : null;
    private static readonly SystemHost Host = new();

    /// <summary>Takes the card for this job, or null when another job holds it. Held until the holder is disposed or the
    /// process ends (an abandoned mutex is taken over: its job is gone).</summary>
    protected IDisposable? ClaimCard()
    {
        var m = new Mutex(false, LabCard.JobMutex(stableId));
        try
        {
            if (m.WaitOne(0)) return new Held(m);
        }
        catch (AbandonedMutexException) { return new Held(m); }
        m.Dispose();
        return null;
    }

    private sealed class Held(Mutex m) : IDisposable
    {
        public void Dispose() { try { m.ReleaseMutex(); } catch (ApplicationException) { } m.Dispose(); }
    }

    /// <summary>"alive" once a minute: the service hands back trial-only settings when no agent says it is there; and
    /// alive.json, this process and its start, for whoever asks whether the job still runs.</summary>
    protected Timer Heartbeat() => new(_ =>
    {
        Broker("alive");
        try
        {
            var me = Process.GetCurrentProcess();
            File.WriteAllText(AlivePath, JsonSerializer.Serialize(new { pid = Environment.ProcessId, started_utc = me.StartTime.ToUniversalTime(), at = DateTime.UtcNow }, StatusJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

    /// <summary>A request to the service, carrying this job's measurement lease: while the job holds judge.lock its own
    /// writes go through and nobody else's do.</summary>
    protected BrokerResponse? Broker(string kind, string? value = null, string op = BrokerRequest.OpExperiment)
    {
        try { return BrokerClient.TryCall(new BrokerRequest(op, stableId, Kind: kind, Value: value, Lease: RunId)); }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or TimeoutException) { return null; }
    }

    /// <summary>
    /// Holds the card for one measurement (a candidate written and measured, a comparison's four runs): judge.lock with this
    /// job's RunId, so every write but this job's own waits until it is given back. Waits for another run that holds it (a
    /// lab_test, StabilityLab) up to <see cref="LeaseWaitMinutes"/>; null when it never came free or a stop was asked.
    /// </summary>
    protected IDisposable? Hold(Action<string>? log)
    {
        string path = JudgeLock.DefaultPath(), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var until = DateTime.UtcNow.AddMinutes(LeaseWaitMinutes);
        bool told = false;
        while (!StopAsked)
        {
            if (JudgeLock.TryAcquire(path, new JudgeLock.Info(Environment.ProcessId, RunId, $"clocktail-lab {RunId}", DateTimeOffset.UtcNow), Host, local, out var heldBy))
                return new Lease(path, RunId);
            if (DateTime.UtcNow > until) return null;
            if (!told) { log?.Invoke($"another run holds the card ({heldBy}): waiting for it"); told = true; }
            for (int i = 0; i < 5 && !StopAsked; i++) Thread.Sleep(1000);
        }
        return null;
    }

    public const int LeaseWaitMinutes = 10;

    private sealed class Lease(string path, string runId) : IDisposable
    {
        public void Dispose() => JudgeLock.Release(path, runId);
    }

    /// <summary>When the trial in force was written (the service's record), or null when none is: taken right after this job's
    /// write and again after its measurement, the two must match, or the result is not the setting's.</summary>
    protected string? TrialAt()
    {
        var r = Broker("show");
        return r is { Ok: true, Result: { } res } && res.TryGetProperty("experiment", out var exp) && exp.ValueKind == JsonValueKind.Object
            && exp.TryGetProperty("Trial", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("At", out var at)
            ? at.ToString() : null;
    }

    /// <summary>One measurement in a child lab process. The child is tied to this process (it never outlives the job) and is
    /// stopped and waited for on every way out, so nothing that follows (the next write, the daily profile going back) runs
    /// under its load. A child that hangs past its time and the grace, dies, or writes no result is a measurement that could
    /// not be made (inconclusive, the runner's), not a result about the card: a driver reset, which a GPU hang brings within
    /// the TDR delay, is counted apart by the caller.</summary>
    protected LabResult Measure(int seconds, string phases, ulong? capMib)
    {
        string json = Path.Combine(jobDir, $"run-{DateTime.UtcNow:HHmmssfff}.json");
        var psi = new ProcessStartInfo(labExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "--phases", phases, "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--json", json, "--gpu-pci", pci })
            psi.ArgumentList.Add(a);
        if (capMib is { } c) { psi.ArgumentList.Add("--mem-cap-mib"); psi.ArgumentList.Add(c.ToString(CultureInfo.InvariantCulture)); }
        var started = DateTime.UtcNow;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the lab did not start");
        try
        {
            if (OperatingSystem.IsWindows()) _children?.Add(p);
            var deadline = started.AddSeconds(seconds + graceSeconds + 30);
            while (!p.WaitForExit(1000))
            {
                if (StopAsked) return Failed(started, LabResult.ByStop, "stopped (lab_stop)");
                if (DateTime.UtcNow > deadline) return Failed(started, LabResult.ByRunner, $"the lab did not finish within {seconds + graceSeconds + 30} s");
            }
        }
        finally { ChildProcess.Stop(p); }
        try { return JsonSerializer.Deserialize<LabResult>(File.ReadAllText(json), LabJson.Options) ?? Failed(started, LabResult.ByRunner, "an empty result"); }
        catch (Exception e) when (e is IOException or JsonException) { return Failed(started, LabResult.ByRunner, $"the lab ended without a result (exit code {p.ExitCode})"); }
    }

    /// <summary>A measurement the GPU was not shared in: a run that passed while other work shared the GPU (a screen saver
    /// drawing, a window, a download in a game launcher) is measured again every <see cref="SharedRetrySeconds"/> s until it
    /// runs alone or <see cref="SharedWaitMinutes"/> have passed; the last run is returned, shared or not. A screen saver
    /// draws until the display sleeps, so an unattended job waits it out.</summary>
    protected LabResult MeasureAlone(int seconds, string phases, ulong? capMib, Action<string>? log)
    {
        var until = DateTime.UtcNow.AddMinutes(SharedWaitMinutes);
        while (true)
        {
            var r = Measure(seconds, phases, capMib);
            if (!(r.Shared && r.Verdict == LabResult.Pass) || DateTime.UtcNow > until || StopAsked) return r;
            log?.Invoke(Inv($"other work shared the GPU ({r.BusyBeforePct:0} % busy before, {r.BusyAfterPct:0} % after, others hold {r.OthersVramMib} MiB): measuring again in {SharedRetrySeconds} s"));
            for (int i = 0; i < SharedRetrySeconds && !StopAsked; i++) Thread.Sleep(1000);
        }
    }

    public const int SharedRetrySeconds = 30, SharedWaitMinutes = 10;

    /// <summary>A measurement that could not be made: inconclusive, with why (<see cref="LabResult"/>'s By*).</summary>
    protected static LabResult Failed(DateTime started, string by, string why) =>
        new("", "", 0, 0, started, DateTime.UtcNow, [], LabResult.Inconclusive, [why], Interrupted: by);

    /// <summary>The daily profile back on at a job's end; the reason when it did not go on, for the status (the agent's
    /// back_to_daily tries again), or null.</summary>
    protected string? DailyBack(string why)
    {
        var r = Broker("apply", why, BrokerRequest.OpProfile);
        if (r is { Ok: true }) return null;
        // Back on without a group the driver will not take (621.01: MSVDD): the rest is on, and trying again changes nothing.
        if (r?.Result is { } res && res.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True)
            return $"the daily profile is back on, except: {r.Error}";
        return $"the daily profile did not go back on ({r?.Error ?? "no answer from the service"}): back_to_daily";
    }

    protected void SaveStatus(object status)
    {
        var tmp = StatusPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(status, StatusJson));
        File.Move(tmp, StatusPath, overwrite: true);
    }

    protected static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
