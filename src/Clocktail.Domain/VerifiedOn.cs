namespace Clocktail.Domain;

/// <summary>
/// The driver, card and VBIOS Clocktail's driver structures were read and its writes drilled on. Information, not a
/// gate: every write checks the structures it touches (sizes, version words, guard bytes, a full readback) and refuses
/// what it does not recognise, so another driver or card fails safe; this says how far the measurements reach, so the
/// person hears it before the first write on anything else. A driver update the reference card was measured on joins
/// <see cref="Drivers"/> after its stress run.
/// </summary>
public static class VerifiedOn
{
    public const string Gpu = "NVIDIA GeForce RTX 5090";
    public const string Vbios = "98.02.2E.00.03";
    /// <summary>NVAPI's driver version (61742 = 617.42).</summary>
    public static readonly uint[] Drivers = [61742];

    /// <summary>Drivers that take a voltage-rail write (NVVDD's and MSVDD's offsets) and change nothing: the call answers OK
    /// and the readback is the same. 621.01 on the reference card, 2026-10-22, both rails, up and down, after a reboot too;
    /// a report traces it to a flag the driver sets when it loads, which its rail setter honours by skipping the offsets.</summary>
    public static readonly uint[] RailWritesDropped = [62101];

    /// <summary>Why rail writes cannot go on with this driver; null where they can.</summary>
    public static string? RailWritesDroppedBy(uint driver) => RailWritesDropped.Contains(driver)
        ? $"driver {Driver(driver)} takes NVVDD's and MSVDD's offsets and changes nothing (the call answers OK, the readback is the same; measured on the reference card, 2026-10-22): they stay where they are; go back to driver {Driver(Drivers[^1])} to set them"
        : null;

    public static string Driver(uint version) => $"{version / 100}.{version % 100:00}";

    /// <summary>Each of the three, compared with what was tested (the VBIOS without regard to case).</summary>
    public static (bool Driver, bool Gpu, bool Vbios) Match(uint driver, string gpu, string vbios) =>
        (Drivers.Contains(driver), string.Equals(gpu, Gpu, StringComparison.Ordinal), string.Equals(vbios, Vbios, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line for the person: what matches, and what that means when something does not.</summary>
    public static string Describe(uint driver, string gpu, string vbios)
    {
        var (d, g, v) = Match(driver, gpu, vbios);
        string tested = $"driver {string.Join(" / ", Drivers.Select(Driver))} on an RTX 5090 (VBIOS {Vbios})";
        if (d && g && v) return "the driver, card and VBIOS Clocktail was measured on";
        var other = new List<string>();
        if (!d) other.Add($"driver {Driver(driver)}");
        if (!g) other.Add(gpu);
        if (!v && g) other.Add($"VBIOS {vbios}");
        return $"Clocktail was measured on {tested}; this {string.Join(", ", other)} is not one of them yet. Every write still checks each structure it touches and refuses what it does not recognise, so expect a refusal, not a wrong write";
    }
}
