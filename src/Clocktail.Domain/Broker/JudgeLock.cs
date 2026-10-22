using System.Text.Json;
using Clocktail.Domain.Ownership;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The other half of a contract with StabilityLab, and the lab's own measurement lease. While a run measures a state it
/// holds a lock file <c>{Pid, RunId, Label, StartedUtc}</c>; the broker reads it before opening a write window, so an
/// automatic write, or another agent's, never moves the hardware out from under a run in progress. The holder's own writes
/// carry the RunId as their lease (<see cref="BrokerRequest.Lease"/>) and go through: the lab writes its next candidate
/// without letting anyone else in between. It is the mirror of StabilityLab refusing to start while the broker's own
/// apply.lock exists. A lock whose process is gone is stale and ignored, so a crashed run never wedges every future
/// write; a lock we cannot parse names no live process, so it is ignored too rather than blocking forever.
/// </summary>
public static class JudgeLock
{
    public sealed record Info(int Pid, string? RunId = null, string? Label = null, DateTimeOffset? StartedUtc = null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Where the broker looks: the per-user state root (the broker runs elevated as the user, so its
    /// %LocalAppData% is the user's).</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", "judge.lock");

    /// <summary>
    /// Where a lock may sit. A program started from an MSIX-packaged app (the Claude desktop app runs StabilityLab
    /// this way) has its new files under %LocalAppData% redirected to
    /// <c>%LocalAppData%\Packages\&lt;family&gt;\LocalCache\Local\…</c>. It sees the merged view, so to it the lock
    /// is at the usual path; the elevated broker runs outside the package and sees only the real folder, where the
    /// lock is absent. So the broker looks at the real path and at the same relative path in every package's cache.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string path, string localAppData)
    {
        var list = new List<string> { path };
        var root = Path.GetFullPath(localAppData).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return list;
        var rel = full[(root.Length + 1)..];
        var packages = Path.Combine(root, "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.EnumerateDirectories(packages))
                {
                    var p = Path.Combine(pkg, "LocalCache", "Local", rel);
                    if (File.Exists(p)) list.Add(p);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Busy when any of <paramref name="paths"/> names a live run; the trace covers every path looked at.</summary>
    public static string? BusyReason(IEnumerable<string> paths, IHost host, out string trace) =>
        Live(paths, host, out trace) is { } info ? Reason(info) : null;

    /// <summary>The first live run any of <paramref name="paths"/> names, or null; the trace covers every path looked at.</summary>
    public static Info? Live(IEnumerable<string> paths, IHost host, out string trace)
    {
        var traces = new List<string>();
        Info? live = null;
        foreach (var p in paths)
        {
            var info = Live(p, host, out var t);
            traces.Add(t);
            live ??= info;
        }
        trace = string.Join("; ", traces);
        return live;
    }

    /// <summary>A sentence saying why a write must wait, or null when the GPU is free.</summary>
    public static string? BusyReason(string path, IHost host) => BusyReason(path, host, out _);

    /// <summary>As <see cref="BusyReason(string, IHost)"/>, with <paramref name="trace"/> saying how the verdict was
    /// reached (file present, pid parsed, liveness, any exception), so a wrong "free" can be read off the broker log.</summary>
    public static string? BusyReason(string path, IHost host, out string trace) => Live(path, host, out trace) is { } info ? Reason(info) : null;

    /// <summary>The run <paramref name="path"/> names while its process is alive, or null.</summary>
    public static Info? Live(string path, IHost host, out string trace)
    {
        Info? info;
        try
        {
            if (!File.Exists(path)) { trace = $"{path}: absent"; return null; }
            info = JsonSerializer.Deserialize<Info>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            trace = $"{path}: unreadable ({e.GetType().Name}: {e.Message})";
            return null;
        }

        if (info == null || info.Pid <= 0) { trace = $"{path}: no pid"; return null; }
        bool alive = host.IsProcessAlive(info.Pid, info.StartedUtc);   // a lock left by a crash must not come back to life through a reused pid
        trace = $"{path}: pid {info.Pid}{(info.RunId != null ? $" run {info.RunId}" : "")} {(alive ? "alive" : "not running")}";
        return alive ? info : null;
    }

    public static string Reason(Info info) =>
        $"pid {info.Pid}{(string.IsNullOrWhiteSpace(info.Label) ? "" : $" '{info.Label}'")}";

    /// <summary>
    /// Takes the measurement lease: writes <paramref name="mine"/> to <paramref name="path"/> unless a live run holds it
    /// there or in a packaged copy (<paramref name="heldBy"/> names it). A stale lock (its process gone) is taken over.
    /// Created exclusively, so two takers that both found it free cannot both hold it.
    /// </summary>
    public static bool TryAcquire(string path, Info mine, IHost host, string localAppData, out string? heldBy)
    {
        heldBy = null;
        if (Live(Candidates(path, localAppData), host, out _) is { } other && other.RunId != mine.RunId)
        {
            heldBy = Reason(other);
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(fs, mine);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                if (Live(path, host, out _) is { } holder)
                {
                    if (holder.RunId == mine.RunId) return true;
                    heldBy = Reason(holder);
                    return false;
                }
                try { File.Delete(path); } catch (IOException) { }   // stale: its process is gone
            }
        }
        heldBy = "judge.lock could not be created";
        return false;
    }

    /// <summary>Gives the lease back: deletes the lock when it is still <paramref name="runId"/>'s.</summary>
    public static void Release(string path, string runId)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Info>(File.ReadAllText(path), Json)?.RunId == runId) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
}
