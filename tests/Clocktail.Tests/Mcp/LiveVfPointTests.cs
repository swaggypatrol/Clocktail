using Clocktail.Mcp.CurveEditor;

namespace Clocktail.Tests.Mcp;

public sealed class LiveVfPointTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 2800u)]
    [InlineData(950000u, null)]
    [InlineData(0u, 2800u)]
    [InlineData(950000u, 0u)]
    public void Missing_or_zero_readings_cannot_make_a_live_crosshair(uint? uv, uint? mhz) =>
        Assert.Null(LiveVfPoint.From(At, uv, mhz));

    [Fact]
    public void Live_coordinates_preserve_actual_readings_including_idle()
    {
        var point = Assert.IsType<LiveVfPoint>(LiveVfPoint.From(At, 712345, 210));
        Assert.Equal(712.345, point.Mv, 3);
        Assert.Equal(210u, point.Mhz);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(2999, true)]
    [InlineData(3000, true)]
    [InlineData(3001, false)]
    public void Stalled_or_future_dated_samples_are_not_presented_as_live(int milliseconds, bool fresh)
    {
        var point = new LiveVfPoint(At, 950000, 2800);
        Assert.Equal(fresh, point.IsFresh(At.AddMilliseconds(milliseconds)));
    }
}
