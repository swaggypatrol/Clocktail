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

    public static string Driver(uint version) => $"{version / 100}.{version % 100:00}";

    /// <summary>Each of the three, compared with what was tested (the VBIOS without regard to case).</summary>
    public static (bool Driver, bool Gpu, bool Vbios) Match(uint driver, string gpu, string vbios) =>
        (Drivers.Contains(driver), string.Equals(gpu, Gpu, StringComparison.Ordinal), string.Equals(vbios, Vbios, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line for the person: what matches, and what that means when something does not.</summary>
    public static string Describe(uint driver, string gpu, string vbios)
    {
        var (d, g, v) = Match(driver, gpu, vbios);
        string history = d && g && v ? "this combination has recorded hardware validation"
            : "this combination has no recorded hardware-validation result";
        return $"RTX 50 series; detected {gpu}, driver {Driver(driver)}, VBIOS {vbios}; {history}. " +
            "Validation history is not a model whitelist. Available controls and ranges are queried on this GPU; " +
            "writes require recognized layouts and matching readback. No tuning profile or default daily settings are supplied";
    }
}
