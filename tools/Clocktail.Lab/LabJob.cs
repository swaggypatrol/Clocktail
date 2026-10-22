using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Domain.Broker;

namespace Clocktail.Lab;

/// <summary>
/// What the lab's background jobs (the memory sweep, the uncore tuner) share: requests to the service for the card, a
/// measurement in a child lab process (a hang or a driver reset ends that child, not the job), the stop file lab_stop
/// writes, the heartbeat that keeps the agent's trial lease while nobody else is there, and the status written whole.
/// </summary>
public abstract class LabJob(string stableId, string pci, int graceSeconds, string jobDir, string labExe)
{
    protected static readonly JsonSerializerOptions StatusJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string StatusPath => Path.Combine(jobDir, "status.json");
    public string StopPath => Path.Combine(jobDir, "stop");
    protected bool StopAsked => File.Exists(StopPath);
    protected string JobDir => jobDir;

    /// <summary>"alive" once a minute: the service hands back trial-only settings when no agent says it is there.</summary>
    protected Timer Heartbeat() => new(_ => Broker("alive"), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

    protected BrokerResponse? Broker(string kind, string? value = null, string op = BrokerRequest.OpExperiment)
    {
        try { return BrokerClient.TryCall(new BrokerRequest(op, stableId, Kind: kind, Value: value)); }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or TimeoutException) { return null; }
    }

    /// <summary>One measurement in a child lab process; a child that hangs past its time and the grace, dies, or writes no
    /// result is a failed measurement.</summary>
    protected LabResult Measure(int seconds, string phases, ulong? capMib)
    {
        string json = Path.Combine(jobDir, $"run-{DateTime.UtcNow:HHmmssfff}.json");
        var psi = new ProcessStartInfo(labExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "--phases", phases, "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--json", json, "--gpu-pci", pci })
            psi.ArgumentList.Add(a);
        if (capMib is { } c) { psi.ArgumentList.Add("--mem-cap-mib"); psi.ArgumentList.Add(c.ToString(CultureInfo.InvariantCulture)); }
        var started = DateTime.UtcNow;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the lab did not start");
        var deadline = started.AddSeconds(seconds + graceSeconds + 30);
        while (!p.WaitForExit(1000))
        {
            if (StopAsked || DateTime.UtcNow > deadline)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return Failed(started, StopAsked ? "stopped" : $"the lab did not finish within {seconds + graceSeconds + 30} s: a hang");
            }
        }
        try { return JsonSerializer.Deserialize<LabResult>(File.ReadAllText(json), LabJson.Options) ?? Failed(started, "an empty result"); }
        catch (Exception e) when (e is IOException or JsonException) { return Failed(started, $"the lab ended without a result (exit code {p.ExitCode})"); }
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

    protected static LabResult Failed(DateTime started, string why) =>
        new("", "", 0, 0, started, DateTime.UtcNow, [], LabResult.Fail, [why]);

    protected void SaveStatus(object status)
    {
        var tmp = StatusPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(status, StatusJson));
        File.Move(tmp, StatusPath, overwrite: true);
    }

    protected static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
