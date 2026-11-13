using System.Text.Json;
using System.Text.Json.Serialization;
using Clocktail.Domain.Platform;

namespace Clocktail.Domain.Ownership;

/// <summary>A piece of hardware state this process changed and must give back: what it was before.</summary>
public sealed record OwnedItem(
    string Kind,
    string Key,
    /// <summary>The original state, as the domain that owns <see cref="Kind"/> chooses to encode it.</summary>
    string Original,
    DateTimeOffset ClaimedAt,
    string? Note = null)
{
    [JsonIgnore] public string Id => $"{Kind}/{Key}";
}

public sealed record OwnershipState(
    int Version,
    string Identity,
    int Pid,
    string BootId,
    IReadOnlyList<OwnedItem> Items,
    DateTimeOffset? AdoptedAt = null)
{
    public const int CurrentVersion = 1;
}

public enum RecoveryReason
{
    /// <summary>No record, or an empty one.</summary>
    Nothing,
    /// <summary>The record belongs to this very process (re-entrant use).</summary>
    OwnedByThisProcess,
    /// <summary>Another live process holds these items; leave them alone.</summary>
    OwnedByLiveProcess,
    /// <summary>The owning process is gone; the items must be given back.</summary>
    OwnerDied,
    /// <summary>The machine rebooted since the record was written; hardware already reset, the record is stale.</summary>
    Rebooted,
    /// <summary>The record is for a different GPU or unreadable; it is reported, not acted on.</summary>
    Foreign,
}

public sealed record RecoveryPlan(RecoveryReason Reason, IReadOnlyList<OwnedItem> Items, string Detail);

/// <summary>
/// Write-ahead record of hardware state this process owns. The rule is the database rule: the record of
/// "what it was" is on disk before the driver is told "what it should be". Then a crash at any point leaves
/// enough on disk for the next start to put things back.
/// </summary>
public sealed class OwnershipJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IHost _host;
    private readonly string _identity;

    public OwnershipJournal(string path, IHost host, string identity)
    {
        _path = path;
        _host = host;
        _identity = identity;
    }

    public string Path => _path;

    public OwnershipState? Load()
    {
        var bytes = AtomicFile.Read(_path, 1024 * 1024);
        if (bytes == null) return null;
        try { return JsonSerializer.Deserialize<OwnershipState>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Record an item before touching the hardware it describes. Throws when the record cannot be written,
    /// in which case the caller must not write to the driver.</summary>
    public void Claim(OwnedItem item)
    {
        var current = Load();
        var items = new List<OwnedItem>();
        DateTimeOffset? adoptedAt = null;
        if (current != null && current.Pid == _host.Pid && BootIdentity.SameBoot(current.BootId, _host.BootId) && current.Identity == _identity)
        {
            items.AddRange(current.Items.Where(i => i.Id != item.Id));
            adoptedAt = current.AdoptedAt;
        }
        else if (current != null && current.Items.Count > 0 && !(current.Pid == _host.Pid))
            throw new InvalidOperationException($"an ownership record from pid {current.Pid} still exists; run recovery before claiming hardware");
        items.Add(item);
        Save(items, adoptedAt);
    }

    /// <summary>Forget an item after its state was given back and verified.</summary>
    public void Release(string kind, string key)
    {
        var current = Load();
        if (current == null) return;
        var rest = current.Items.Where(i => i.Id != $"{kind}/{key}").ToList();
        if (rest.Count == 0) Clear();
        else Save(rest, current.AdoptedAt);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(AtomicFile.BackupPath(_path))) File.Delete(AtomicFile.BackupPath(_path));
    }

    /// <summary>What a starting process should do about an existing record.</summary>
    public RecoveryPlan Inspect()
    {
        var s = Load();
        if (s == null)
            return new RecoveryPlan(File.Exists(_path) ? RecoveryReason.Foreign : RecoveryReason.Nothing, [],
                File.Exists(_path) ? "ownership record is unreadable" : "no ownership record");
        if (s.Items.Count == 0) return new RecoveryPlan(RecoveryReason.Nothing, [], "empty ownership record");
        if (s.Identity != _identity) return new RecoveryPlan(RecoveryReason.Foreign, s.Items, $"record belongs to GPU '{s.Identity}', not '{_identity}'");
        if (!BootIdentity.SameBoot(s.BootId, _host.BootId)) return new RecoveryPlan(RecoveryReason.Rebooted, s.Items, "record predates the current boot; hardware was reset by the reboot");
        if (s.Pid == _host.Pid) return new RecoveryPlan(RecoveryReason.OwnedByThisProcess, s.Items, "owned by this process");
        // An adopted record carries the dead predecessor's ClaimedAt on its items; check pid reuse against when this owner took over.
        DateTimeOffset ownerSince = s.AdoptedAt ?? s.Items.Min(i => i.ClaimedAt);
        if (_host.IsProcessAlive(s.Pid, ownerSince)) return new RecoveryPlan(RecoveryReason.OwnedByLiveProcess, s.Items, $"pid {s.Pid} is still running");
        return new RecoveryPlan(RecoveryReason.OwnerDied, s.Items, $"pid {s.Pid} is gone (or now a later process); {s.Items.Count} item(s) must be given back");
    }

    /// <summary>
    /// What a process about to write should do about an existing record. The original value belongs to the
    /// user, not to the pid that first wrote it down: a record whose owner is gone (every earlier CLI run, or a
    /// crashed UI) is adopted, so the next write still gives back the state the hardware had before the first
    /// of them. A record from before a reboot describes state the reboot already reset and is dropped. A live
    /// owner, or a record for another GPU, is left alone and reported.
    /// </summary>
    public RecoveryPlan Adopt()
    {
        var plan = Inspect();
        switch (plan.Reason)
        {
            case RecoveryReason.OwnerDied:
                Save(plan.Items, _host.Now);
                return new RecoveryPlan(RecoveryReason.OwnedByThisProcess, plan.Items, $"adopted {plan.Items.Count} item(s) left by a process that is gone");
            case RecoveryReason.Rebooted:
                Clear();
                return new RecoveryPlan(RecoveryReason.Nothing, [], "a record from before the current boot was dropped; the reboot reset the hardware");
            default:
                return plan;
        }
    }

    private void Save(IReadOnlyList<OwnedItem> items, DateTimeOffset? adoptedAt = null)
    {
        var state = new OwnershipState(OwnershipState.CurrentVersion, _identity, _host.Pid, _host.BootId, items, adoptedAt);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(state, Json));
    }
}
