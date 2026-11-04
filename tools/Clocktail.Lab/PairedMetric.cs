namespace Clocktail.Lab;

/// <summary>One positive metric measured A B B A. The paired geometric mean cancels steady multiplicative drift;
/// agreement of both pairs and a limit on repeated-setting spread keep a single reading from deciding a gain.</summary>
public sealed record PairedMetric(double ChangePct, double FirstRatio, double SecondRatio, double Spread)
{
    public bool Increased(double minimum) => FirstRatio > 1 + minimum / 2 && SecondRatio > 1 + minimum / 2 && ChangePct >= minimum * 100;
    public bool Decreased(double minimum) => FirstRatio < 1 - minimum / 2 && SecondRatio < 1 - minimum / 2 && ChangePct <= -minimum * 100;
    public bool Disturbed(double maximum) => Spread > maximum;

    public static bool Valid(double? value) => value is > 0 && double.IsFinite(value.Value);

    public static PairedMetric? Of(double? a1, double? b1, double? b2, double? a2)
    {
        if (!Valid(a1) || !Valid(b1) || !Valid(b2) || !Valid(a2)) return null;
        double r1 = b1!.Value / a1!.Value, r2 = b2!.Value / a2!.Value;
        return new((Math.Sqrt(r1 * r2) - 1) * 100, r1, r2,
            Math.Max(Math.Abs(a2.Value / a1.Value - 1), Math.Abs(b2.Value / b1.Value - 1)));
    }
}
