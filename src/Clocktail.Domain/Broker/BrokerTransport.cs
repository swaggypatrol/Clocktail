using System.Buffers.Binary;
using System.Text.Json;

namespace Clocktail.Domain.Broker;

/// <summary>
/// One request, one response, length-prefixed. A 4-byte little-endian length then that many UTF-8 bytes of
/// compact JSON. Length framing, not a newline, so nothing in a payload can be mistaken for a boundary, and a
/// truncated or oversized message is caught instead of hanging the reader. The client and the broker are the
/// only two speakers, so there is no versioning handshake: both ship from this one assembly.
/// </summary>
public static class BrokerTransport
{
    public const string PipeName = "Clocktail.Broker";

    /// <summary>A message never legitimately approaches this; past it, assume a desynchronized or hostile peer.</summary>
    public const int MaxMessageBytes = 1 << 20;

    private static readonly JsonSerializerOptions Wire = new();   // compact

    public static void WriteRequest(Stream s, BrokerRequest req) => Write(s, JsonSerializer.SerializeToUtf8Bytes(req, Wire));
    public static void WriteResponse(Stream s, BrokerResponse resp) => Write(s, JsonSerializer.SerializeToUtf8Bytes(resp, Wire));

    public static BrokerRequest ReadRequest(Stream s)
        => JsonSerializer.Deserialize<BrokerRequest>(Read(s), Wire) ?? throw new InvalidDataException("empty request");
    public static BrokerResponse ReadResponse(Stream s)
    {
        try { return JsonSerializer.Deserialize<BrokerResponse>(Read(s), Wire) ?? throw new InvalidDataException("empty response"); }
        catch (JsonException e) { throw new InvalidDataException("invalid broker response JSON", e); }
    }

    private static void Write(Stream s, byte[] payload)
    {
        if (payload.Length > MaxMessageBytes) throw new InvalidDataException($"message of {payload.Length} bytes exceeds the {MaxMessageBytes}-byte limit");
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        s.Write(len);
        s.Write(payload);
        s.Flush();
    }

    private static byte[] Read(Stream s)
    {
        var len = ReadExactly(s, 4);
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n < 0 || n > MaxMessageBytes) throw new InvalidDataException($"framed length {n} is out of range");
        return ReadExactly(s, n);
    }

    private static byte[] ReadExactly(Stream s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(buf, off, n - off);
            if (r == 0) throw new EndOfStreamException("the connection closed mid-message");
            off += r;
        }
        return buf;
    }
}
