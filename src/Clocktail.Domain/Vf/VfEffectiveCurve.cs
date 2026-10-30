namespace Clocktail.Domain.Vf;

/// <summary>Effective-frequency proofs for coupled P0/V/F writes, independent of any additive control model.</summary>
public static class VfEffectiveCurve
{
    public static long Displacement(VfPoint p) => (long)p.EffectiveKhz - p.BaseKhz;

    public static void RequireSameCurve(VfCurveSnapshot before, VfCurveSnapshot after)
    {
        if (before.Layout != after.Layout || before.EditableCount != after.EditableCount || !before.Mask.AsSpan().SequenceEqual(after.Mask)
            || before.Points.Where((p, i) => p.VoltageUv != after.Points[i].VoltageUv || p.ReferenceVoltageUv != after.Points[i].ReferenceVoltageUv).Any())
            throw new InvalidDataException("the V/F layout or voltage axis changed during the P0 write");
    }

    /// <summary>
    /// Regional curves expose an independent base: preserve effective minus base, so thermal changes are not saved
    /// as tuning. A legacy base is derived from effective minus control: use direct frequency comparison instead.
    /// </summary>
    public static string? Mismatch(VfCurveSnapshot before, VfCurveSnapshot after, long deltaKhz = 0)
    {
        RequireSameCurve(before, after);
        foreach (var p in before.Points)
        {
            var actual = after.Points[p.Index];
            long expected = (before.Mode == VfMode.Regional ? Displacement(p) : p.EffectiveKhz) + deltaKhz;
            long observed = before.Mode == VfMode.Regional ? Displacement(actual) : actual.EffectiveKhz;
            // The driver floors half clock steps to whole MHz. Never absorb a full clock step.
            if (Math.Abs(expected - observed) >= 1_000)
                return $"point {p.Index}: effective Core change did not verify (expected {expected} kHz, read {observed} kHz)";
        }
        return null;
    }
}
