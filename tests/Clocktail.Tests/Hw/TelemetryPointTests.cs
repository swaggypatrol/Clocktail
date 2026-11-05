using Clocktail.Core.Gpu;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

[Collection("fake-nvml")]
public sealed class TelemetryPointTests : IDisposable
{
    private static GpuDevice Gpu(nint handle = 0x77) => new()
    {
        Identity = new(0, "fake GPU", "fake VBIOS", 0, 0, 0, 0, 1, 0),
        NvapiHandle = 1, NvmlHandle = handle,
    };

    public TelemetryPointTests() => FakeNvml.Reset();
    public void Dispose() => FakeNvml.Reset();

    [Fact]
    public void Residency_reads_only_its_three_nvml_fields_and_matches_the_full_snapshot()
    {
        using var nvml = FakeNvml.Open();
        var full = Telemetry.Read(Gpu(), nvml);
        int fullCalls = FakeNvml.Calls;
        FakeNvml.PointQueries.Clear();

        var point = Telemetry.ReadPoint(Gpu(), nvml);
        Assert.Equal(3, FakeNvml.Calls - fullCalls);
        Assert.Equal(["clock:0", "util", "power"], FakeNvml.PointQueries);
        Assert.Equal(full.GraphicsClockMhz, point.Mhz);
        Assert.Equal(full.GpuUtilPercent, point.Util);
        Assert.Equal(full.PowerWatts, point.W);
        Assert.Equal(148.5, point.W);
        Assert.True(fullCalls > 3);
    }

    [Theory]
    [InlineData("clock", 3)]
    [InlineData("util", 10)]
    [InlineData("power", 999)]
    public void An_unavailable_field_stays_missing_without_discarding_the_other_fields(string field, int code)
    {
        using var nvml = FakeNvml.Open();
        Fail(field, code);
        var point = Telemetry.ReadPoint(Gpu(), nvml);
        Assert.Equal(field == "clock" ? null : (uint?)2880, point.Mhz);
        Assert.Equal(field == "util" ? null : (uint?)100, point.Util);
        Assert.Equal(field == "power" ? null : (double?)148.5, point.W);
        Assert.Equal(3, FakeNvml.Calls);
        Assert.False(nvml.Lost);
    }

    [Theory]
    [InlineData("clock", 1)]
    [InlineData("util", 2)]
    [InlineData("power", 3)]
    public void Driver_loss_discards_the_point_and_stops_native_calls(string field, int calls)
    {
        using var nvml = FakeNvml.Open();
        Fail(field, NvmlApi.GpuIsLost);
        Assert.Equal(((uint?)null, (uint?)null, (double?)null), Telemetry.ReadPoint(Gpu(), nvml));
        Assert.Equal(calls, FakeNvml.Calls);
        Assert.True(nvml.Lost);

        Assert.Equal(((uint?)null, (uint?)null, (double?)null), Telemetry.ReadPoint(Gpu(), nvml));
        Assert.Equal(calls, FakeNvml.Calls);
    }

    [Fact]
    public void A_driver_already_known_lost_is_never_called()
    {
        using var nvml = FakeNvml.Open();
        nvml.MarkLost();
        Assert.Equal(((uint?)null, (uint?)null, (double?)null), Telemetry.ReadPoint(Gpu(), nvml));
        Assert.Equal(0, FakeNvml.Calls);
    }

    [Fact]
    public void A_card_without_an_nvml_handle_is_rejected_before_any_read()
    {
        using var nvml = FakeNvml.Open();
        Assert.Throws<InvalidOperationException>(() => Telemetry.ReadPoint(Gpu(0), nvml));
        Assert.Equal(0, FakeNvml.Calls);
    }

    private static void Fail(string field, int code)
    {
        if (field == "clock") FakeNvml.ClockAnswer = code;
        else if (field == "util") FakeNvml.UtilAnswer = code;
        else FakeNvml.PowerAnswer = code;
    }
}
