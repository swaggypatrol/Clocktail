using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bwt.Contracts;

/// <summary>
/// C01 report formatting preserves snake_case fields, enum strings, and explicit nulls for unavailable evidence.
/// </summary>
public static class BwtJson
{
    public static readonly JsonSerializerOptions Indented = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new SnakeEnumFactory());
        return o;
    }

    /// <summary>snake_case enums, except an enum type that names its own converter (e.g. <see cref="Outcome"/>).</summary>
    private sealed class SnakeEnumFactory : JsonConverterFactory
    {
        private readonly JsonStringEnumConverter _inner = new(JsonNamingPolicy.SnakeCaseLower);
        public override bool CanConvert(Type t) => t.IsEnum && !t.IsDefined(typeof(JsonConverterAttribute), false);
        public override JsonConverter? CreateConverter(Type t, JsonSerializerOptions o) => _inner.CreateConverter(t, o);
    }
}

/// <summary>Outcome-style enums serialize UPPER_SNAKE (PASS, COMPUTE_MISMATCH, NOT_RUN), as in the spec's tables.</summary>
public sealed class UpperSnakeEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseUpper) where T : struct, Enum;

/// <summary>
/// Units and clocks used everywhere (spec §4.3): integers in kHz, µV, mW, ns. UI may show MHz/mV; nothing rounded for
/// display is ever written back. Monotonic time is QueryPerformanceCounter converted to ns (<see cref="Mono.NowNs"/>):
/// on Windows QPC is system-wide, so worker and host timestamps are directly comparable.
/// </summary>
public static class Mono
{
    private static readonly double NsPerTick = 1e9 / System.Diagnostics.Stopwatch.Frequency;
    public static long NowNs() => TicksToNs(System.Diagnostics.Stopwatch.GetTimestamp());
    public static long TicksToNs(long qpcTicks) => (long)(qpcTicks * NsPerTick);
    public static long Qpc() => System.Diagnostics.Stopwatch.GetTimestamp();
}
