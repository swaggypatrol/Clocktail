using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

/// <summary>
/// NVML bound to managed stand-ins. Every device call answers <see cref="Answer"/> and counts itself, so a test can see
/// whether a call reached the "library" at all. No GPU, no nvml.dll.
/// </summary>
internal static unsafe class FakeNvml
{
    public static int Answer, Calls, Shutdowns, FieldAnswer;
    public static int ClockAnswer, UtilAnswer, PowerAnswer;
    public static readonly List<string> PointQueries = [];

    public static void Reset()
    {
        Answer = 0; Calls = 0; Shutdowns = 0; FieldAnswer = 0;
        ClockAnswer = 0; UtilAnswer = 0; PowerAnswer = 0; PointQueries.Clear();
    }

    public static NvmlApi Open() => NvmlApi.FromExports(name => name switch
    {
        "nvmlInit_v2" => (nint)(delegate* unmanaged[Cdecl]<int>)&Init,
        "nvmlShutdown" => (nint)(delegate* unmanaged[Cdecl]<int>)&Shutdown,
        "nvmlDeviceGetCount_v2" => (nint)(delegate* unmanaged[Cdecl]<uint*, int>)&Count,
        "nvmlDeviceGetHandleByIndex_v2" => (nint)(delegate* unmanaged[Cdecl]<uint, nint*, int>)&ByIndex,
        "nvmlDeviceGetName" or "nvmlDeviceGetVbiosVersion" => (nint)(delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)&Text,
        "nvmlDeviceGetPciInfo_v3" => (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int>)&Pci,
        "nvmlDeviceGetClockInfo" => (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)&Clock,
        "nvmlDeviceGetUtilizationRates" => (nint)(delegate* unmanaged[Cdecl]<nint, uint*, int>)&Util,
        "nvmlDeviceGetPowerUsage" => (nint)(delegate* unmanaged[Cdecl]<nint, uint*, int>)&Power,
        "nvmlDeviceGetTemperature" or "nvmlDeviceGetMaxClockInfo" or "nvmlDeviceGetFanSpeed_v2"
            => (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)&IndexedValue,
        "nvmlDeviceGetPowerManagementLimit" or "nvmlDeviceGetPowerManagementDefaultLimit"
            or "nvmlDeviceGetEnforcedPowerLimit" or "nvmlDeviceGetNumFans"
            => (nint)(delegate* unmanaged[Cdecl]<nint, uint*, int>)&Value,
        "nvmlDeviceGetMemoryInfo" => (nint)(delegate* unmanaged[Cdecl]<nint, uint*, int>)&Memory,
        "nvmlDeviceGetPowerManagementLimitConstraints" => (nint)(delegate* unmanaged[Cdecl]<nint, uint*, uint*, int>)&TwoValues,
        "nvmlDeviceGetFieldValues" => (nint)(delegate* unmanaged[Cdecl]<nint, int, byte*, int>)&Fields,
        "nvmlDeviceGetMarginTemperature" => (nint)(delegate* unmanaged[Cdecl]<nint, uint*, int>)&Margin,
        "nvmlDeviceGetCurrentClocksThrottleReasons" => (nint)(delegate* unmanaged[Cdecl]<nint, ulong*, int>)&Reasons,
        _ => 0,
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Init() => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Shutdown() { Shutdowns++; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Count(uint* n) { Calls++; *n = 1; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ByIndex(uint i, nint* h) { Calls++; *h = 0x77; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Text(nint h, byte* b, uint n) { Calls++; b[0] = (byte)'G'; b[1] = 0; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Pci(nint h, byte* b) { Calls++; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int IndexedValue(nint h, uint i, uint* v) { Calls++; *v = 2880; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Clock(nint h, uint i, uint* v)
    { Calls++; PointQueries.Add($"clock:{i}"); *v = 2880; return Answer != 0 ? Answer : ClockAnswer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Util(nint h, uint* v)
    { Calls++; PointQueries.Add("util"); v[0] = 100; v[1] = 50; return Answer != 0 ? Answer : UtilAnswer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Power(nint h, uint* v)
    { Calls++; PointQueries.Add("power"); *v = 148_500; return Answer != 0 ? Answer : PowerAnswer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Value(nint h, uint* v) { Calls++; *v = 1; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Memory(nint h, uint* v)
    { Calls++; var m = (ulong*)v; m[0] = 8UL << 30; m[1] = 6UL << 30; m[2] = 2UL << 30; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int TwoValues(nint h, uint* a, uint* b) { Calls++; *a = 400_000; *b = 600_000; return Answer; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Margin(nint h, uint* m) { Calls++; m[1] = 39; return Answer; }

    /// <summary>nvmlFieldValue_t as the 5090 answered (40 bytes each): instantaneous power as a uint in mW, a policy
    /// counter as an unsigned long long in ns, each field with <see cref="FieldAnswer"/> as its own status.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Fields(nint h, int n, byte* v)
    {
        Calls++;
        for (int i = 0; i < n; i++)
        {
            byte* e = v + i * 40;
            bool power = *(uint*)e == NvmlField.PowerInstantMw;
            *(int*)(e + 24) = power ? 1 : 3;
            *(int*)(e + 28) = FieldAnswer;
            *(ulong*)(e + 32) = power ? 48_102ul : 927_153_308_736ul;
        }
        return Answer;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Reasons(nint h, ulong* v) { Calls++; *v = 0x4; return Answer; }
}

/// <summary>
/// The loss latch (2026-10-19 21:48:29Z: a device restart killed three processes inside NVML reads). Once NVML says the
/// driver it was opened against is gone, nothing goes into it again: not the next read, not nvmlShutdown.
/// </summary>
[Collection("fake-nvml")]
public sealed class NvmlApiTests : IDisposable
{
    private const nint H = 0x77;

    public NvmlApiTests() => FakeNvml.Reset();
    public void Dispose() => FakeNvml.Reset();

    [Fact]
    public void A_healthy_nvml_reads_through_and_shuts_down_on_dispose()
    {
        var nvml = FakeNvml.Open();
        Assert.Equal(2880u, nvml.ClockMhz(H, NvmlClock.Graphics));
        Assert.Equal(100u, nvml.Utilization(H).Gpu);
        Assert.Equal(0x4ul, nvml.ThrottleReasons(H));
        Assert.False(nvml.Lost);
        nvml.Dispose();
        Assert.Equal(1, FakeNvml.Shutdowns);
    }

    [Theory]
    [InlineData(NvmlApi.GpuIsLost)]
    [InlineData(NvmlApi.Uninitialized)]
    [InlineData(NvmlApi.DriverNotLoaded)]
    [InlineData(NvmlApi.ResetRequired)]
    [InlineData(NvmlApi.LibRmVersionMismatch)]
    public void An_answer_that_the_driver_is_gone_latches_and_nothing_enters_nvml_again(int code)
    {
        var nvml = FakeNvml.Open();
        FakeNvml.Answer = code;
        var first = Assert.Throws<NvmlException>(() => nvml.ClockMhz(H, NvmlClock.Graphics));
        Assert.Equal(code, first.Code);
        Assert.True(nvml.Lost);
        Assert.Equal(code, nvml.LostCode);
        int calls = FakeNvml.Calls;

        FakeNvml.Answer = 0;   // even a driver that would answer again is not asked through the old state
        Assert.Equal(code, Assert.Throws<NvmlException>(() => nvml.Utilization(H)).Code);
        Assert.Equal(code, Assert.Throws<NvmlException>(() => nvml.PowerUsageMw(H)).Code);
        Assert.Equal(0ul, nvml.ThrottleReasons(H));
        Assert.Equal(0u, nvml.FanCount(H));
        Assert.Null(nvml.FanPercent(H, 0));
        Assert.Null(nvml.VbiosVersion(H));
        Assert.Equal(calls, FakeNvml.Calls);

        nvml.Dispose();
        Assert.Equal(0, FakeNvml.Shutdowns);
    }

    [Fact]
    public void Field_values_come_back_by_their_type_and_a_field_that_says_lost_latches()
    {
        var nvml = FakeNvml.Open();
        var v = nvml.FieldValues(H, NvmlField.PowerInstantMw, 74)!;   // 74: a counter, unsigned long long
        Assert.Equal(48_102.0, v[0].Value);
        Assert.Equal(927_153_308_736.0, v[1].Value);
        Assert.Equal(39, nvml.MarginTemperatureC(H));

        FakeNvml.FieldAnswer = 3;   // one field not supported: that field is unread, nothing latches
        Assert.Null(nvml.FieldValues(H, NvmlField.PowerInstantMw)![0].Value);
        Assert.False(nvml.Lost);

        FakeNvml.FieldAnswer = NvmlApi.GpuIsLost;
        Assert.Null(nvml.FieldValues(H, NvmlField.PowerInstantMw)![0].Value);
        Assert.True(nvml.Lost);
        int calls = FakeNvml.Calls;
        Assert.Null(nvml.FieldValues(H, NvmlField.PowerInstantMw));
        Assert.Null(nvml.MarginTemperatureC(H));
        Assert.Equal(calls, FakeNvml.Calls);
    }

    [Fact]
    public void A_read_that_does_not_throw_latches_too()
    {
        var nvml = FakeNvml.Open();
        FakeNvml.Answer = NvmlApi.GpuIsLost;
        Assert.Equal(0ul, nvml.ThrottleReasons(H));
        Assert.True(nvml.Lost);
    }

    [Theory]
    [InlineData(3)]     // not supported
    [InlineData(10)]    // timeout
    [InlineData(999)]   // unknown: also said for one failed call, so it does not latch
    public void Other_failures_fail_that_call_only(int code)
    {
        var nvml = FakeNvml.Open();
        FakeNvml.Answer = code;
        Assert.Equal(code, Assert.Throws<NvmlException>(() => nvml.TemperatureC(H)).Code);
        Assert.False(nvml.Lost);
        FakeNvml.Answer = 0;
        Assert.Equal(2880u, nvml.TemperatureC(H));
        nvml.Dispose();
        Assert.Equal(1, FakeNvml.Shutdowns);
    }

    [Fact]
    public void An_nvapi_handle_loss_latches_nvml_before_it_is_read_through_the_same_dead_driver()
    {
        var nvml = FakeNvml.Open();
        var healthy = new GpuSession(1, "fake 5090");
        Assert.Null(DriverLoss.Why(nvml, [healthy]));
        Assert.False(nvml.Lost);

        var gone = new GpuSession(1, "fake 5090");
        gone.Poison(NvStatus.HandleInvalidated);
        Assert.Equal("fake 5090: NVAPI handle lost (status -10)", DriverLoss.Why(nvml, [healthy, gone]));
        Assert.True(nvml.Lost);
        Assert.Throws<NvmlException>(() => nvml.ClockMhz(H, NvmlClock.Graphics));
        Assert.Equal(0, FakeNvml.Calls);
        Assert.Contains("gpu is lost", DriverLoss.Why(nvml, [healthy]));
        Assert.Null(DriverLoss.Why(null, [healthy]));
    }
}
