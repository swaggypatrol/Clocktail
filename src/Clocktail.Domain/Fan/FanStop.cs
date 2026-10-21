using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Fan;

/// <summary>
/// F03: a fan policy's zero-RPM band, the two words after its curve in the Control entry (+0x64 stop, +0x68 start, signed
/// 1/256 °C like the points): its fans stop once the filtered temperature (Status +0x28) falls under stop, and start again
/// when it rises past start. Read live on the 5090 FE (617.42): 43 / 50 °C on policies 0 and 1, 63/70 and 60/70 on the
/// backstops 4 and 5. Watched on 2026-10-21 with the fans stopped at idle: the filtered temperature reached 50.0 °C and
/// they started, fell back to 43.0 °C and they stopped. Written the same day: 38:45 landed on policies 0 and 1 alone (the
/// backstops kept theirs) and read back exactly, and 43:50 put it back. A write only starts the fans earlier: start no
/// later than the curve's first point, a band of at least 3 °C (so the fans do not toggle), stop no lower than 20 °C.
/// </summary>
public static class FanStopWrites
{
    public const string Kind = "fanstop";
    public const int StopAt = 0x64, StartAt = 0x68, MinBandC = 3, LowestStopC = 20;

    public static OwnedCell Cell(int index) => new(
        Kind, FanPolicyWrites.Key(index), "degC", $"fan policy {index}: the zero-RPM band, stop:start",
        new TransactionSpec(Slot.FanPolicyGetControl, Slot.FanPolicySetControl, PrepareRead: FanPolicyWrites.Query(index)),
        raw => Format(Read(raw, index)));

    /// <summary>The band in 1/256 °C.</summary>
    public static (int Stop, int Start) Read(ReadOnlySpan<byte> raw, int index)
    {
        FanPolicyParser.PointsIn(raw, index);   // the shape, the mask and the entry type checked
        int e = FanPolicyLayout.ControlEntry(index);
        return (BinaryPrimitives.ReadInt32LittleEndian(raw[(e + StopAt)..]), BinaryPrimitives.ReadInt32LittleEndian(raw[(e + StartAt)..]));
    }

    public static string Format((int Stop, int Start) b) => string.Create(CultureInfo.InvariantCulture, $"{b.Stop / 256m:0.##}:{b.Start / 256m:0.##}");

    public static (int Stop, int Start)? Parse(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 2 && decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var stop)
            && decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
            ? (FanCurvePoint.TempOf(stop), FanCurvePoint.TempOf(start)) : null;
    }

    /// <summary>Why a band cannot go on this policy, or null. Recovery (the band the card held) is never refused.</summary>
    public static string? Invalid((int Stop, int Start) want, IReadOnlyList<FanCurvePoint> points)
    {
        if (want.Start - want.Stop < MinBandC * 256) return $"the band is at least {MinBandC} °C wide (stop under start), or the fans toggle";
        if (want.Stop < LowestStopC * 256) return $"the fans stop at {LowestStopC} °C at the lowest";
        if (want.Start > points[0].Temp) return string.Create(CultureInfo.InvariantCulture, $"the fans start no later than the curve's first point ({points[0].TempC:0.#} °C): only an earlier start is written");
        return null;
    }

    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(int index, (int Stop, int Start) want, bool isRecovery) => raw =>
    {
        var b = raw.Span;
        IReadOnlyList<FanCurvePoint> points;
        (int Stop, int Start) now;
        try { points = FanPolicyParser.PointsIn(b, index); now = Read(b, index); }
        catch (InvalidDataException x) { return new Mutation.Abort(x.Message); }
        if (!isRecovery && Invalid(want, points) is { } why) return new Mutation.Abort(why);
        if (now == want) return new Mutation.Apply([]);
        int e = FanPolicyLayout.ControlEntry(index);
        return Mutation.Of(Edit.Int32(e + StopAt, want.Stop), Edit.Int32(e + StartAt, want.Start));
    };

    /// <summary>The registry's entry: key = policy index, value = "stop:start" in °C. Only an editable policy.</summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite) Prepare(NvClient hw, GpuSession gpu, string key, string value, bool isRecovery)
    {
        int index = FanPolicyWrites.ParseKey(key);
        var info = FanPolicyReader.ReadInfo(hw, gpu);
        var policy = info.Get(index) ?? throw new InvalidOperationException($"fan policy {index} does not exist");
        var cell = Cell(index);
        if (!policy.Editable) return (cell, null, $"fan policy {index} is not editable; a non-editable policy is the firmware's own backstop");
        return Parse(value) is { } want ? (cell, Plan(index, want, isRecovery), null) : (cell, null, $"'{value}' is not a band (stop:start, °C)");
    }
}
