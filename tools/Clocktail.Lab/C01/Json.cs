using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bwt.Contracts;

/// <summary>
/// The one JSON dialect every file and pipe uses: snake_case properties, enums as strings, nulls written (a null is
/// "unknown", which must stay visible; it is never dropped and never turned into 0).
/// </summary>
public static class BwtJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = indented,
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

    public static string Serialize<T>(T value, bool indented = false) => JsonSerializer.Serialize(value, indented ? Indented : Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"null {typeof(T).Name}");
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

/// <summary>Process exit codes kept from Clocktail's Lab contract (spec §14.1). Structured results carry the detail.</summary>
public static class ExitCodes
{
    public const int Pass = 0;
    public const int Infrastructure = 2;
    public const int Fail = 4;
    public const int Inconclusive = 5;
    public const int Usage = 64;
}
