using System.Text.Json;
using Clocktail.Domain.Platform;

namespace Clocktail.Domain.Profiles;

/// <summary>
/// Daily profiles saved under a name, so a person can keep several and switch with a sentence. The name is whatever
/// they call it ("quiet", "CS2 / low noise", "Sunday 🌙", in any language): every saved profile lives in one file beside the daily profile
/// (saved-profiles.json, in this card's own state folder, so a saved profile is always this card's), keyed by its name,
/// so no name has to be a valid file name. Names compare without case. Loading one makes it the daily profile;
/// the daily profile stays the one the service puts back after every boot and driver reset.
/// </summary>
public static class SavedProfiles
{
    public const string FileName = "saved-profiles.json";
    public const int MaxNameLength = 64;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathBeside(string journalPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, FileName);

    /// <summary>The name as it is kept (trimmed), or why it cannot be one: empty, too long, a control character, or a
    /// invalid character. No former built-in tuning name is reserved.</summary>
    public static (string? Name, string? Error) Normalize(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return (null, "a saved profile needs a name");
        if (n.Length > MaxNameLength) return (null, $"a name is at most {MaxNameLength} characters");
        if (n.Any(char.IsControl)) return (null, "a name cannot hold line breaks or other control characters");
        return (n, null);
    }

    /// <summary>Every saved profile, in the order they were saved. A file that does not read is no saved profiles.</summary>
    public static IReadOnlyList<(string Name, DailyProfile Profile)> All(string path)
    {
        var bytes = AtomicFile.Read(path, 4 * 1024 * 1024);
        if (bytes == null) return [];
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var valid = new List<(string Name, DailyProfile Profile)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    var entry = element.Deserialize<Entry>(Json);
                    if (entry != null && Normalize(entry.Name).Name is { } name && DailyProfile.IsValid(entry.Profile))
                        valid.Add((name, entry.Profile!));
                }
                catch (JsonException) { } // one malformed entry must not hide the other saved profiles
            }
            return valid;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The saved profile with this name (case does not matter), or null.</summary>
    public static (string Name, DailyProfile Profile)? Find(string path, string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        foreach (var e in All(path))
            if (string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>Saves under the name, replacing one saved under the same name (whatever its case); true when it replaced one.</summary>
    public static bool Save(string path, string name, DailyProfile profile)
    {
        var all = All(path).ToList();
        int i = all.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) all[i] = (name, profile); else all.Add((name, profile));
        Write(path, all);
        return i >= 0;
    }

    /// <summary>Deletes the one saved under the name; the deleted profile, or null when there was none.</summary>
    public static (string Name, DailyProfile Profile)? Delete(string path, string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        var all = All(path).ToList();
        int i = all.FindIndex(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        var gone = all[i];
        all.RemoveAt(i);
        Write(path, all);
        return gone;
    }

    private static void Write(string path, List<(string Name, DailyProfile Profile)> all) =>
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(all.Select(e => new Entry(e.Name, e.Profile)).ToList(), Json));

    private sealed record Entry(string? Name, DailyProfile? Profile);
}
