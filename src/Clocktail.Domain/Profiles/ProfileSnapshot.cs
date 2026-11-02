using System.Security.Cryptography;
using System.Text.Json;

namespace Clocktail.Domain.Profiles;

/// <summary>A canonical fingerprint of the full target profile, including values unchanged from the current daily.</summary>
public static class ProfileSnapshot
{
    public static string Fingerprint(string source, IEnumerable<ProfileWrite> writes) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            source,
            writes = writes.OrderBy(w => w.Kind, StringComparer.Ordinal).ThenBy(w => w.Key, StringComparer.Ordinal)
                .Select(w => new { w.Kind, w.Key, w.Value }),
        })));
}
