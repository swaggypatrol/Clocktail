namespace Clocktail.Mcp.CurveEditor;

/// <summary>An actual NVVDD/core-clock pair from one background read, including desktop/idle operation.
/// Never snapped to the curve, substituted from a requested voltage, or kept as stability evidence.</summary>
public sealed record LiveVfPoint(DateTimeOffset At, uint Uv, uint Mhz)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);
    public double Mv => Uv / 1000.0;

    public static LiveVfPoint? From(DateTimeOffset at, uint? uv, uint? mhz) =>
        uv is > 0 && mhz is > 0 ? new(at, uv.Value, mhz.Value) : null;

    /// <summary>A stalled sampler must not leave its last crosshair presented as current.</summary>
    public bool IsFresh(DateTimeOffset now) => now >= At && now - At <= MaxAge;
}
