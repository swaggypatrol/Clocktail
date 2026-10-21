using System.Text.Json;

namespace Clocktail.Domain.Profiles;

/// <summary>
/// A daily profile that ships with Clocktail. A fresh install keeps the card at stock; a preset is what a person may
/// choose to start from instead. Presets are embedded in this assembly, so the broker only ever loads its own: a client
/// names one, it never sends the writes. Each one names the card model it was tuned on and loads only on that model.
/// </summary>
public sealed record Preset(string Name, string Title, string Gpu, string Note, IReadOnlyList<ProfileWrite> Writes)
{
    /// <summary>Whether this card is the model the preset was tuned on: the GPU name is <see cref="Gpu"/> or ends with it
    /// as a whole word ("NVIDIA GeForce RTX 5090"), so "RTX 5090 D" and "RTX 5090 Laptop GPU", other chips with other
    /// power ranges, do not fit.</summary>
    public bool Fits(string gpuName)
    {
        string name = gpuName.Trim();
        return name.Equals(Gpu, StringComparison.OrdinalIgnoreCase) || name.EndsWith(" " + Gpu, StringComparison.OrdinalIgnoreCase);
    }
}

public static class Presets
{
    private const string Prefix = "preset.";

    private static readonly Lazy<IReadOnlyList<Preset>> Loaded = new(() =>
    {
        var asm = typeof(Presets).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                return JsonSerializer.Deserialize<Preset>(s) ?? throw new InvalidDataException($"{n} is empty");
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    });

    public static IReadOnlyList<Preset> All => Loaded.Value;

    public static Preset? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}
