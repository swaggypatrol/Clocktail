using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Clocktail.Domain.Profiles;

/// <summary>Host-owned receipts, never stored beside the user's editable profiles.</summary>
public interface IProfileApprovals
{
    bool Matches(string gpu, string slot, DailyProfile profile);
    void Remember(string gpu, string slot, DailyProfile profile);
    void Forget(string gpu, string slot);
    /// <summary>Why this store cannot approve anything (its protected folder is missing or not to be trusted), or null
    /// when it works. An unavailable store matches nothing, so no daily profile goes on; everything that needs no
    /// approval (trials, the way back to stock) goes on working.</summary>
    string? Unavailable => null;
}

public static class ProfileApproval
{
    public const string Daily = "daily";
    public static string Saved(string name) => "saved/" + name.Trim().ToUpperInvariant();
    public static string Fingerprint(string gpu, string slot, DailyProfile profile) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, gpu = gpu.ToUpperInvariant(), slot,
            profile.Writes, profile.Adopted, profile.Suspended, profile.Vbios, profile.Driver,
            // KeptAt/History are explanatory bookkeeping, not settings or permission to apply them.
        })));
}

/// <summary>Fail-closed standalone host default: only profiles explicitly kept in this process are approved.
/// A restarted host does not auto-trust disk. The real Windows broker supplies protected durable receipts.</summary>
public sealed class MemoryProfileApprovals : IProfileApprovals
{
    private readonly ConcurrentDictionary<string, string> _receipts = new();
    private static string Key(string gpu, string slot) => JsonSerializer.Serialize(new[] { gpu.ToUpperInvariant(), slot });
    public bool Matches(string gpu, string slot, DailyProfile profile) =>
        _receipts.TryGetValue(Key(gpu, slot), out var receipt) && receipt == ProfileApproval.Fingerprint(gpu, slot, profile);
    public void Remember(string gpu, string slot, DailyProfile profile) =>
        _receipts[Key(gpu, slot)] = ProfileApproval.Fingerprint(gpu, slot, profile);
    public void Forget(string gpu, string slot) => _receipts.TryRemove(Key(gpu, slot), out _);
}

/// <summary>The store a host falls back to when its protected one cannot be opened: nothing is approved and nothing can
/// be, with the reason for whoever asks. The host still starts, so trials, recovery and stock are not lost with it.</summary>
public sealed class UnavailableProfileApprovals(string reason) : IProfileApprovals
{
    public string? Unavailable { get; } = reason;
    public bool Matches(string gpu, string slot, DailyProfile profile) => false;
    public void Remember(string gpu, string slot, DailyProfile profile) =>
        throw new InvalidOperationException($"profile approvals are unavailable: {Unavailable}");
    public void Forget(string gpu, string slot) { }   // nothing is approved, so nothing is left to revoke
}
