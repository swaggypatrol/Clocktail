namespace Clocktail.Domain.Rails;

/// <summary>
/// A rail write's rules as pure functions, µV throughout: the structure's and the firmware's own, no tuning envelope.
/// <list type="bullet">
/// <item>new evaluated value = new offset − current offset + current evaluated value;</item>
/// <item>a slot that moves evaluates inside the voltage domain's range as the firmware reports it (min..max), or inside
/// what the driver can represent when the board reports no valid domain table;</item>
/// <item>a slot that moves goes in whole steps of the domain's voltage step: a change off the grid is rounded to the
/// nearest step before it is written, since the driver would round it itself and the readback would then differ from
/// what was sent, so the write would land and be taken back;</item>
/// <item>a slot asked to go back to its origin (the offsets the hardware held before this program owned the cell: the
/// journal's original, or the value itself on recovery) is written exactly, never rounded or judged: the firmware held it;</item>
/// <item>ALT/OP changes only on a rail that reports an operating limit.</item>
/// </list>
/// </summary>
public static class RailRules
{
    /// <summary>The domain maximum the firmware reports; the representable top when it reports no valid domain.</summary>
    public static uint Ceiling(RailRecord rail) => rail.Domain is { IsValid: true } d ? d.MaxUv : uint.MaxValue;

    /// <summary>The domain minimum the firmware reports; 0 when it reports no valid domain.</summary>
    public static uint Floor(RailRecord rail) => rail.Domain is { IsValid: true } d ? d.MinUv : 0;

    /// <summary>The domain's voltage step; 0 when the board reports no valid domain (nothing to round to).</summary>
    public static uint Step(RailRecord rail) => rail.Domain is { IsValid: true } d ? d.StepUv : 0;

    /// <summary>A requested change: null slots are left as they are.</summary>
    public readonly record struct Request(int? VminUv, int? RelUv, int? AltUv, int? OvUv)
    {
        public int? this[RailSlot s] => s switch { RailSlot.Rel => RelUv, RailSlot.Alt => AltUv, RailSlot.Ov => OvUv, _ => VminUv };
    }

    /// <summary>
    /// The request with every slot that moves put on the domain's step grid, counted from where the slot is now (the
    /// nearest whole step, half a step away from zero); a slot equal to its <paramref name="origin"/> stays as asked.
    /// </summary>
    public static Request OnStep(RailRecord rail, Request req, RailOffsets? origin = null)
    {
        uint step = Step(rail);
        if (step == 0) return req;
        int? Snap(RailSlot s)
        {
            if (req[s] is not { } want || want == rail.Offsets[s] || origin is { } o && want == o[s]) return req[s];
            long now = rail.Offsets[s];
            long steps = (long)Math.Round((want - now) / (double)step, MidpointRounding.AwayFromZero);
            return (int)Math.Clamp(now + steps * step, int.MinValue, int.MaxValue);
        }
        return new Request(Snap(RailSlot.Vmin), Snap(RailSlot.Rel), Snap(RailSlot.Alt), Snap(RailSlot.Ov));
    }

    /// <summary>
    /// Validate a request against the fresh snapshot and turn it into control-structure edits (on the step grid, see
    /// <see cref="OnStep"/>). Returns the edits (possibly empty when nothing changes) or a reason to abort.
    /// </summary>
    /// <param name="origin">The rail's offsets before this program owned the cell; only ever read from the hardware or
    /// the journal, never a user's request. A slot going back to it is written exactly.</param>
    public static Mutation Plan(RailRecord rail, Request req, RailOffsets? origin = null)
    {
        if (!rail.Writable) return new Mutation.Abort($"{rail.Rail} is not writable: {rail.NotWritableReason}");
        bool altReported = rail.Limits.AltOpUv != 0;
        if (!altReported && req.AltUv is { } a && a != rail.Offsets.AltUv)
            return new Mutation.Abort("the driver does not report an operating limit (ALT/OP) for this rail, so ALT cannot be changed");

        var snapped = OnStep(rail, req, origin);
        long floor = Floor(rail), ceiling = Ceiling(rail);
        var edits = new List<Edit>();
        foreach (var s in new[] { RailSlot.Vmin, RailSlot.Rel, RailSlot.Alt, RailSlot.Ov })
        {
            if (snapped[s] is not { } want || want == rail.Offsets[s]) continue;
            bool back = origin is { } o && want == o[s];
            long e = Eval(rail, s, want);
            if (!back && e < floor) return new Mutation.Abort($"{Name(s)} would evaluate to {Mv(e)} mV, below the reported device minimum of {Mv(floor)} mV");
            if (!back && e > ceiling)
                return new Mutation.Abort(ceiling == uint.MaxValue
                    ? $"{Name(s)} would evaluate to {Mv(e)} mV, which the driver cannot represent"
                    : $"{Name(s)} would evaluate to {Mv(e)} mV, above the reported device maximum of {Mv(ceiling)} mV");
            edits.Add(Edit.Int32(rail.ControlFieldOffset(s), want));
        }
        return new Mutation.Apply(edits);
    }

    private static long Eval(RailRecord rail, RailSlot s, int newOffset) => (long)newOffset - rail.Offsets[s] + rail.Eval(s);
    internal static string Name(RailSlot s) => s switch { RailSlot.Rel => "REL", RailSlot.Alt => "ALT/OP", RailSlot.Ov => "OV", _ => "VMIN" };
    internal static string Mv(long uv) => (uv / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
