using System.Text.Json;
using Clocktail.Domain.Ownership;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The other half of a contract with StabilityLab, and the lab's own measurement lease. While a run measures a state it
/// holds a lock file <c>{Pid, RunId, Label, StartedUtc, Waits}</c>; the broker reads it before opening a write window, so an
/// automatic write, or another agent's, never moves the hardware out from under a run in progress. The holder's own writes
/// carry the RunId as their lease (<see cref="BrokerRequest.Lease"/>) and go through: the lab writes its next candidate
/// without letting anyone else in between. It is the mirror of StabilityLab refusing to start while the broker's own
/// apply.lock exists. A lock whose process is gone is stale and ignored, so a crashed run never wedges every future
/// write; a lock we cannot parse names no live process, so it is ignored too rather than blocking forever.
/// </summary>
public static class JudgeLock
{
    /// <param name="Waits">The run asks the service to finish the operation in flight before it measures ("quiet", Clocktail's
    /// runs since 0.3.7), so an operation the judge let through before the lock was taken finishes under it rather than stop
    /// part-way (<see cref="BrokerDispatcher.Judge"/>). Without it (StabilityLab, a run from before 0.3.7)
    /// every write after the lock stands down, as before.</param>
    public sealed record Info(int Pid, string? RunId = null, string? Label = null, DateTimeOffset? StartedUtc = null, bool Waits = false);

    /// <summary>
    /// What <see cref="Live(string, IHost, out string)"/> answers for a lock another process keeps open past its tries: a
    /// taker checking it (<see cref="TakeOver"/>) or its holder giving it back (<see cref="Release"/>), both of which let
    /// no one else read it. The run in it cannot be read, so it is busy, and it names no run (pid 0, no RunId): no lease
    /// matches it. Every Clocktail run waits for the operation in flight, so this one is taken to wait as well.
    /// </summary>
    public static readonly Info OpenElsewhere = new(0, Label: "judge.lock is open by another process", Waits: true);

    /// <summary>Whether <paramref name="info"/> is a run read from a lock, not <see cref="OpenElsewhere"/>.</summary>
    public static bool NamesRun(Info info) => info.Pid > 0;

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

    /// <summary>The first live run any of <paramref name="paths"/> names, or null; the trace covers every path looked at. A
    /// lock open by another process (<see cref="OpenElsewhere"/>) is answered only when no path names a run.</summary>
    public static Info? Live(IEnumerable<string> paths, IHost host, out string trace)
    {
        var traces = new List<string>();
        Info? live = null;
        foreach (var p in paths)
        {
            var info = Live(p, host, out var t);
            traces.Add(t);
            if (info != null && (live == null || !NamesRun(live) && NamesRun(info))) live = info;
        }
        trace = string.Join("; ", traces);
        return live;
    }

    /// <summary>A sentence saying why a write must wait, or null when the GPU is free.</summary>
    public static string? BusyReason(string path, IHost host) => BusyReason(path, host, out _);

    /// <summary>As <see cref="BusyReason(string, IHost)"/>, with <paramref name="trace"/> saying how the verdict was
    /// reached (file present, pid parsed, liveness, any exception), so a wrong "free" can be read off the broker log.</summary>
    public static string? BusyReason(string path, IHost host, out string trace) => Live(path, host, out trace) is { } info ? Reason(info) : null;

    /// <summary>The run <paramref name="path"/> names while its process is alive, or null. A lock another process has open
    /// so that it cannot be read (a taker checking it, its holder giving it back) is tried again a few times, and if it
    /// stays open it is <see cref="OpenElsewhere"/>: busy, never free, since the run in it may be live.</summary>
    public static Info? Live(string path, IHost host, out string trace)
    {
        Info? info;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { trace = $"{path}: absent"; return null; }
                // Read letting others write and delete: a look never stops a holder from giving its lock back, nor a
                // later run's Settle from deleting its own. A taker's check (TakeOver) still shuts this read out.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                    info = JsonSerializer.Deserialize<Info>(reader.ReadToEnd(), Json);
                break;
            }
            catch (IOException e) when (InUse(e))
            {
                if (attempt < ReadAttempts) { Thread.Sleep(ReadRetryDelay); continue; }
                trace = $"{path}: open by another process after {ReadAttempts} tries ({e.Message}), so busy";
                return OpenElsewhere;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                trace = $"{path}: unreadable ({e.GetType().Name}: {e.Message})";
                return null;
            }
        }

        if (info == null || info.Pid <= 0) { trace = $"{path}: no pid"; return null; }
        bool alive = host.IsProcessAlive(info.Pid, info.StartedUtc);   // a lock left by a crash must not come back to life through a reused pid
        trace = $"{path}: pid {info.Pid}{(info.RunId != null ? $" run {info.RunId}" : "")} {(alive ? "alive" : "not running")}";
        return alive ? info : null;
    }

    public static string Reason(Info info) => !NamesRun(info) ? info.Label ?? OpenElsewhere.Label! :
        $"pid {info.Pid}{(string.IsNullOrWhiteSpace(info.Label) ? "" : $" '{info.Label}'")}";

    /// <summary>How often <see cref="Live(string, IHost, out string)"/> tries a lock another process has open, and how long
    /// apart: far longer than a taker or a holder keeps it open to check or delete it.</summary>
    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>A sharing or lock violation: another process has the file open and lets no one read it.</summary>
    private static bool InUse(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    /// <summary>How often a lock another process has open is tried again, and how long apart: about half a second in all,
    /// far longer than any taker keeps it open to write or check it.</summary>
    private const int Attempts = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>How long <see cref="Settle"/> waits for a later run to give back the lock it wrote in a packaged copy.</summary>
    public static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Takes the measurement lease: writes <paramref name="mine"/> to <paramref name="path"/> unless a live run holds it
    /// there or in a packaged copy (<paramref name="heldBy"/> names it). The file is created exclusively, so two takers
    /// cannot both create it at one path. A stale lock (its process gone, or no pid in it) is taken over through a handle
    /// no other taker can open at the same time (<see cref="TakeOver"/>), so the file deleted is always the one found
    /// stale, never a lock another taker wrote in between. A lock another process has open (a taker still writing it) is
    /// tried again, not taken as stale, and is not a holder either (<see cref="OpenElsewhere"/>: the loop below finds out
    /// what is in it). Once written, the packaged copies are looked at again (<see cref="Settle"/>):
    /// a packaged and an unpackaged taker create different files, so creating ours alone does not make it the only one.
    /// </summary>
    public static bool TryAcquire(string path, Info mine, IHost host, string localAppData, out string? heldBy)
    {
        heldBy = null;
        if (Live(Candidates(path, localAppData), host, out _) is { } other && NamesRun(other) && other.RunId != mine.RunId)
        {
            heldBy = Reason(other);
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(fs, mine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                switch (TakeOver(path, mine, host, out var holder))
                {
                    case Found.Mine: return true;
                    case Found.Live: heldBy = Reason(holder!); return false;
                    case Found.InUse: Thread.Sleep(RetryDelay); break;
                }
                continue;   // stale and deleted: create ours now
            }
            return Settle(path, mine, host, localAppData, out heldBy);
        }
        heldBy = "judge.lock could not be created or taken over: another process keeps it open, or it cannot be deleted";
        return false;
    }

    private enum Found { Mine, Live, Taken, InUse }

    /// <summary>
    /// The lock at <paramref name="path"/>, read through a handle that lets others do nothing but delete: while it is open
    /// no other taker can open, rewrite or replace the file, so when it names no live run the file deleted (while the
    /// handle is still open) is the one just read. A file that cannot be opened (another taker has it open, or it is
    /// being deleted) or deleted (read-only) is in use, not stale: nothing is deleted then.
    /// </summary>
    private static Found TakeOver(string path, Info mine, IHost host, out Info? holder)
    {
        holder = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            holder = Parse(fs);
            if (holder is { Pid: > 0 } && host.IsProcessAlive(holder.Pid, holder.StartedUtc))
                return holder.RunId == mine.RunId ? Found.Mine : Found.Live;
            File.Delete(path);   // stale: its process is gone, or it names none
            return Found.Taken;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Found.InUse; }
    }

    private static Info? Parse(Stream s)
    {
        try { return JsonSerializer.Deserialize<Info>(s, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// After <paramref name="mine"/> is written at <paramref name="path"/>: whether it keeps the lease. A packaged and an
    /// unpackaged taker write different files (<see cref="Candidates"/>), so both can find nothing live and both create
    /// theirs. Each looks again once its own is written. The one that creates second sees the first one's: a packaged
    /// taker cannot create its copy once the real file is there (it sees that file at its own path), so the second is the
    /// unpackaged one, and it looks in every package's copy. A packaged taker may not see the real file from its copy, so
    /// only one side may see the other. Of two runs the later (by StartedUtc, then RunId) gives its own lock back at once.
    /// The earlier waits up to <paramref name="wait"/> (<see cref="SettleWait"/>) for the later's lock to go, and gives its
    /// own back if it stays: that run saw nothing and holds the lease. Either way at most one run keeps it. A lock another
    /// process has open a moment (<see cref="OpenElsewhere"/>: often a taker checking ours) names no run and is passed over.
    /// </summary>
    public static bool Settle(string path, Info mine, IHost host, string localAppData, out string? heldBy, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? SettleWait);
        while (true)
        {
            var other = Candidates(path, localAppData).Select(p => Live(p, host, out _)).FirstOrDefault(i => i != null && NamesRun(i) && i.RunId != mine.RunId);
            if (other == null) { heldBy = null; return true; }
            if (!Earlier(mine, other) || DateTime.UtcNow >= until)
            {
                Release(path, mine.RunId!);
                heldBy = Reason(other);
                return false;
            }
            Thread.Sleep(RetryDelay);
        }
    }

    /// <summary>The order both sides of <see cref="Settle"/> agree on: earlier StartedUtc first (none counts as earliest),
    /// then RunId.</summary>
    private static bool Earlier(Info a, Info b)
    {
        int c = Nullable.Compare(a.StartedUtc, b.StartedUtc);
        return c != 0 ? c < 0 : string.CompareOrdinal(a.RunId, b.RunId) < 0;
    }

    /// <summary>Gives the lease back: deletes the lock when it is still <paramref name="runId"/>'s. The check and the delete go
    /// through one handle, as in <see cref="TakeOver"/>, so a lock another run took over in between is never deleted. A lock
    /// another process has open a moment (a reader, a taker looking at it) is tried again, so the run's own lock is not
    /// left behind with its live pid.</summary>
    public static void Release(string path, string runId)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
                if (Parse(fs)?.RunId == runId) File.Delete(path);
                return;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(RetryDelay); }
        }
    }
}
