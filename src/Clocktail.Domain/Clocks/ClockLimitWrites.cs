using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

/// <summary>
/// The client clock-limit record (<see cref="ClientLimitsLayout"/>, slots 26/27) as three owned cells: the maximum clock
/// (entry 0, mode 2, kHz: a cap), the minimum clock (entry 1, mode 2, kHz: a floor; the two are NVML's locked clocks,
/// nvidia-smi -lgc), and the voltage lock (entry 6, mode 3, µV: a V/F point lock, or <see cref="ClientLimitsLayout.BoostLockUv"/>
/// for Boost Lock). 0 releases an entry. A write is the original's request form [observed]: the record cleared, +8 = 1,
/// one entry at +0xc {domain, 0, mode, 0, value, 0}, gateway kind 8; the readback must be the record before with that
/// entry changed and nothing else. Entry 0 and 1's direction was measured with -lgc (2026-10-21), and the writes in their
/// drill the same day (617.42): Boost Lock held 3052 MHz in P0 at idle, a 2000 MHz cap held 1980 MHz at full load, a
/// 900 mV point lock held 2775 MHz at 890 mV at load 50, and 0 released each.
/// </summary>
public static class ClockLimitWrites
{
    public const string Kind = "clocklimit";
    public const string MaxKey = "max", MinKey = "min", VoltageKey = "voltage";


    public static (int Entry, uint Mode) Entry(string key) => key.ToLowerInvariant() switch
    {
        MaxKey => (0, 2),
        MinKey => (1, 2),
        VoltageKey => (6, 3),
        _ => throw new ArgumentException($"'{key}' is not a clock limit (max, min, voltage)"),
    };

    public static TransactionSpec Spec { get; } = new(Slot.PerfClientLimitsGetStatus, Slot.PerfClientLimitsSetStatus);

    public static OwnedCell Cell(string key)
    {
        var (entry, _) = Entry(key);
        return new(Kind, key.ToLowerInvariant(), entry == 6 ? "µV" : "kHz", $"clock limit {key} (client limit entry {entry})", Spec,
            raw => Value(raw, entry).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The entry's value, 0 when the entry is off; throws on a layout other than the confirmed one.</summary>
    public static uint Value(byte[] raw, int entry)
    {
        var held = ClientLimitsLayout.Parse(raw).FirstOrDefault(l => l.Domain == entry);
        return held?.Value ?? 0;
    }

    /// <summary>The one-entry request: the record cleared, +8 = 1, the entry at +0xc.</summary>
    public static byte[] Request(int entry, uint mode, uint value)
    {
        var b = new byte[ClientLimitsLayout.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(ClientLimitsLayout.Size, ClientLimitsLayout.Rev).Word);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0xc), (uint)entry);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x14), value == 0 ? 0 : mode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1c), value);
        return b;
    }

    /// <summary>Validate the entry identity only; value acceptance is NVIDIA's decision.</summary>
    public static string? Invalid(string key, uint value, IReadOnlyList<ClientLimit> now)
    {
        Entry(key); // Validate the key; NVIDIA decides which uint32 values it accepts.
        return null;
    }

    /// <summary>Decided on the fresh record: the request for the entry, the readback it must give (that entry's mode and
    /// value changed, nothing else), and the request that puts the entry back as it was.</summary>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(string key, uint value) => raw =>
    {
        var (entry, mode) = Entry(key);
        var b = raw.ToArray();
        IReadOnlyList<ClientLimit> now;
        try { now = ClientLimitsLayout.Parse(b); } catch (InvalidDataException x) { return new Mutation.Abort(x.Message); }
        if (Invalid(key, value, now) is { } why) return new Mutation.Abort(why);
        var old = now.FirstOrDefault(l => l.Domain == entry);
        if ((old?.Value ?? 0) == value && (value == 0 || old!.Mode == mode)) return new Mutation.Apply([]);
        int e = ClientLimitsLayout.First + entry * ClientLimitsLayout.Stride;
        return new Mutation.Request(Request(entry, mode, value),
            [Edit.UInt32(e + 8, value == 0 ? 0 : mode), Edit.UInt32(e + 0x10, value)],
            Request(entry, old?.Mode ?? 0, old?.Value ?? 0));
    };

    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite) Prepare(string key, string value)
    {
        var cell = Cell(key);
        string v = value.Trim();
        if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) v = "0";
        return uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (cell, Plan(key, n), null)
            : (cell, null, $"'{value}' is not a value (kHz for max/min, µV for voltage, 0 or 'off' to release)");
    }
}
