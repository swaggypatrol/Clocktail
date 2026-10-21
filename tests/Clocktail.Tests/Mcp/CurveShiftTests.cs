using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Vf;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>W16: a run says how far the curve moved at its point as the card warmed (re/tuning-results §26: the 82 °C
/// step took 8-15 MHz at 940-990 mV), so two runs at different temperatures are not compared as if equal.</summary>
public class CurveShiftTests
{
    private static VfCurveSnapshot Curve(uint khzAt80) =>
        new(default, default, new byte[32], [new VfPoint(79, 960_000, 2_900_000, 2_900_000, 0, 960_000), new VfPoint(80, 970_000, khzAt80, khzAt80, 0, 970_000)], 0, [], [], []);

    private static Card.Sample S(double t) => new(DateTimeOffset.Now,
        new TelemetrySnapshot(t, 590, 600, 575, 600, 400, 600, 2900, 15000, 3200, 15000, 99, 40, null, null, [], []), 970_000, null, null, null);

    private static JsonElement J(object? o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void A_run_that_warmed_past_a_step_reports_the_shift()
    {
        var j = J(TuningTools.CurveShift(Curve(2_925_000), Curve(2_915_000), 80, [S(70), S(78), S(83)]));
        Assert.Equal(-10, j.GetProperty("shift_mhz").GetDouble());
        Assert.Equal((70.0, 83.0), (j.GetProperty("temp_start_c").GetDouble(), j.GetProperty("temp_end_c").GetDouble()));
        Assert.Contains("compare runs at the same temperature", j.GetProperty("note").GetString());
    }

    [Fact]
    public void No_shift_near_a_step_still_warns_and_far_from_one_says_nothing()
    {
        Assert.Contains("82 °C", J(TuningTools.CurveShift(Curve(2_925_000), Curve(2_925_000), 80, [S(81)])).GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, J(TuningTools.CurveShift(Curve(2_925_000), Curve(2_925_000), 80, [S(66)])).GetProperty("note").ValueKind);
        Assert.Null(TuningTools.CurveShift(Curve(2_925_000), null, 80, []));
        Assert.Null(TuningTools.CurveShift(Curve(2_925_000), Curve(2_925_000), null, []));
    }
}
