using System.Buffers.Binary;
using System.Numerics;
using Clocktail.Hw;

namespace Clocktail.Domain.Rails;

public enum Rail { Nvvdd = 0, Msvdd = 1 }

/// <summary>The four writable offsets of a rail, in microvolts, in the order the control structure stores them.</summary>
public readonly record struct RailOffsets(int RelUv, int AltUv, int OvUv, int VminUv)
{
    public int this[RailSlot s] => s switch { RailSlot.Rel => RelUv, RailSlot.Alt => AltUv, RailSlot.Ov => OvUv, _ => VminUv };
}

/// <summary>Evaluated (post-offset) limits the driver reports, in microvolts.</summary>
public readonly record struct RailLimits(uint RelUv, uint AltOpUv, uint OvUv, uint MaxUv, uint VminUv);

/// <summary>Control-structure slot order: +4 REL, +8 ALT/OP, +0xC OV, +0x10 VMIN (field map {3,0,1,2}).</summary>
public enum RailSlot { Rel = 0, Alt = 1, Ov = 2, Vmin = 3 }

/// <summary>Hardware voltage bounds for the domain a rail maps to (from VoltVoltDevicesGetInfo).</summary>
public readonly record struct VoltDomain(uint MinUv, uint MaxUv, uint StepUv)
{
    /// <summary>The validity test; an invalid table falls back to constant ceilings.</summary>
    public bool IsValid => MinUv != 0 && MaxUv < 0x80000000 && MinUv <= MaxUv && StepUv != 0 && (MinUv == MaxUv || MaxUv - MinUv >= StepUv);
}

public sealed record RailRecord(
    Rail Rail,
    /// <summary>Position among present rails (popcount of lower mask bits); indexes control and status entries.</summary>
    int Ordinal,
    uint Kind,
    uint Type,
    byte DomainIndex,
    uint DomainMask,
    RailOffsets Offsets,
    RailLimits Limits,
    VoltDomain? Domain,
    bool Writable,
    string? NotWritableReason)
{
    /// <summary>Offset of this rail's control entry inside the control structure.</summary>
    public int ControlEntry => RailsLayout.ControlBase + Ordinal * RailsLayout.ControlEntry;
    public int ControlFieldOffset(RailSlot s) => ControlEntry + 4 + 4 * (int)s;
    /// <summary>The driver's own base for each slot: evaluated value minus the offset that produced it.</summary>
    public long Base(RailSlot s) => (long)Eval(s) - Offsets[s];
    public uint Eval(RailSlot s) => s switch { RailSlot.Rel => Limits.RelUv, RailSlot.Alt => Limits.AltOpUv, RailSlot.Ov => Limits.OvUv, _ => Limits.VminUv };
}

public sealed record RailsSnapshot(
    uint Mask,
    byte LegacyBoostPercent,
    IReadOnlyList<RailRecord> Rails,
    byte[] RawInfo,
    byte[] RawControl,
    byte[] RawStatus,
    byte[]? RawDevices)
{
    public RailRecord? Get(Rail r) => Rails.FirstOrDefault(x => x.Rail == r);
}

public static class RailsLayout
{
    public const int InfoBase = 0x4c, InfoEntry = 0xc0;
    public const int ControlBase = 0x48, ControlEntry = 0x54, ControlBoostByte = 8;
    public const int StatusBase = 0xa4, StatusEntry = 0xac;
    public const int DevicesBase = 0x48, DevicesEntry = 0x78, DeviceCount = 32;
}

/// <summary>Pure parsing of the three VOLT structures plus the voltage-domain table. No driver access.</summary>
public static class RailsParser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    /// <param name="setAvailable">Whether the driver exposes VoltVoltRailsSetControl; part of the writable rule.</param>
    public static RailsSnapshot Parse(byte[] info, byte[] control, byte[] status, byte[]? devices, bool setAvailable)
    {
        Check(info, Slot.VoltVoltRailsGetInfo);
        Check(control, Slot.VoltVoltRailsGetControl);
        Check(status, Slot.VoltVoltRailsGetStatus);
        if (devices != null) Check(devices, Slot.VoltVoltDevicesGetInfo);

        uint mask = U(info, 4);
        if ((mask & 1) == 0) throw new InvalidDataException("The NVIDIA driver did not expose NVVDD.");
        if (U(control, 4) != mask) throw new InvalidDataException("The voltage control read returned a different rail mask.");
        if (U(status, 4) != mask) throw new InvalidDataException("The voltage status read returned a different rail mask.");
        byte boost = control[RailsLayout.ControlBoostByte];
        if (boost > 100) throw new InvalidDataException("The voltage percentage delta is out of range.");

        var rails = new List<RailRecord>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            int n = BitOperations.PopCount(mask & ((1u << bit) - 1));
            int ib = RailsLayout.InfoBase + bit * RailsLayout.InfoEntry;
            int cb = RailsLayout.ControlBase + n * RailsLayout.ControlEntry;
            int sb = RailsLayout.StatusBase + n * RailsLayout.StatusEntry;
            if (ib + RailsLayout.InfoEntry > info.Length || cb + RailsLayout.ControlEntry > control.Length || sb + RailsLayout.StatusEntry > status.Length)
                throw new InvalidDataException("The returned NVIDIA rail data exceeded its bounds.");

            uint kind = U(info, ib), type = U(info, ib + 4);
            byte domainIndex = info[ib + 8];
            uint domainMask = U(info, ib + 0x18);
            var offsets = new RailOffsets(I(control, cb + 4), I(control, cb + 8), I(control, cb + 12), I(control, cb + 16));
            var limits = new RailLimits(U(status, sb + 4), U(status, sb + 8), U(status, sb + 12), U(status, sb + 16), U(status, sb + 20));

            // Refuse to continue when a present rail reports implausible (zero) evaluated limits; ALT may be 0.
            if (limits.RelUv == 0 || limits.OvUv == 0 || limits.MaxUv == 0 || limits.VminUv == 0)
                throw new InvalidDataException("NVVDD or MSVDD returned implausible evaluated limits.");

            VoltDomain? domain = null;
            if (devices != null && domainIndex < RailsLayout.DeviceCount)
            {
                int db = RailsLayout.DevicesBase + domainIndex * RailsLayout.DevicesEntry;
                if (db + RailsLayout.DevicesEntry <= devices.Length && devices[db + 6] == 1 && devices[db + 4] == type)
                    domain = new VoltDomain(U(devices, db + 8), U(devices, db + 12), U(devices, db + 16));
            }

            string? why = !setAvailable ? "the driver does not expose the private voltage write interface"
                : kind is not (0 or 3) ? $"rail kind {kind} is not one this program writes (0 or 3)"
                : U(control, cb) != kind ? "control and info disagree on the rail kind"
                : mask is not (1 or 3) ? $"rail mask 0x{mask:x} is not NVVDD or NVVDD+MSVDD"
                : bit > 1 ? "rail beyond MSVDD"
                : type != (bit == 0 ? 1u : 3u) ? $"rail type {type} does not match its position"
                : null;

            rails.Add(new RailRecord((Rail)bit, n, kind, type, domainIndex, domainMask, offsets, limits, domain, why == null, why));
        }
        return new RailsSnapshot(mask, boost, rails, info, control, status, devices);
    }

    private static void Check(byte[] b, Slot slot)
    {
        var shape = Slots.Info(slot).Shape!.Value;
        if (b.Length != shape.Size) throw new InvalidDataException($"{slot}: buffer is 0x{b.Length:x} bytes, structure is {shape}.");
        uint word = U(b, 0);
        if (word != shape.Word) throw new InvalidDataException($"{slot}: the read returned an unexpected version (0x{word:x}).");
    }
}

/// <summary>Reads the four structures through the hardware layer and parses them.</summary>
public static class RailsReader
{
    public static RailsSnapshot Read(NvClient hw, GpuSession gpu)
    {
        var info = NvClient.NewBuffer(Slot.VoltVoltRailsGetInfo);
        Require(hw.Call(gpu, Slot.VoltVoltRailsGetInfo, info));
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4));

        var control = NvClient.NewBuffer(Slot.VoltVoltRailsGetControl);
        BinaryPrimitives.WriteUInt32LittleEndian(control.AsSpan(4), mask);
        Require(hw.Call(gpu, Slot.VoltVoltRailsGetControl, control));

        var status = NvClient.NewBuffer(Slot.VoltVoltRailsGetStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(status.AsSpan(4), mask);
        Require(hw.Call(gpu, Slot.VoltVoltRailsGetStatus, status));

        byte[]? devices = NvClient.NewBuffer(Slot.VoltVoltDevicesGetInfo);
        if (!hw.Call(gpu, Slot.VoltVoltDevicesGetInfo, devices).IsOk) devices = null;

        bool setAvailable = hw.Api.Resolve(Slot.VoltVoltRailsSetControl) != 0;
        return RailsParser.Parse(info, control, status, devices, setAvailable);
    }

    /// <summary>The rails' mask (info +4), which every status read must echo. Read once; it does not change.</summary>
    public static uint? Mask(NvClient hw, GpuSession gpu)
    {
        var info = NvClient.NewBuffer(Slot.VoltVoltRailsGetInfo);
        return hw.Call(gpu, Slot.VoltVoltRailsGetInfo, info).IsOk ? BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4)) : null;
    }

    /// <summary>
    /// A rail's present voltage, µV, from the status structure alone (its entry +0), one driver call: cheap enough for a
    /// look every quarter second. Null when unreadable, the mask is not echoed, or the value is implausible.
    /// </summary>
    public static uint? PresentUv(NvClient hw, GpuSession gpu, uint mask, Rail rail)
    {
        if ((mask >> (int)rail & 1) == 0) return null;
        var st = ReadStatus(hw, gpu, mask);
        return st == null ? null : ExtractPresentUv(st, mask, rail);
    }

    /// <summary>Read both NVVDD and MSVDD present voltages from a single status driver call.</summary>
    public static (uint? Nvvdd, uint? Msvdd) PresentUvPair(NvClient hw, GpuSession gpu, uint mask)
    {
        if ((mask >> (int)Rail.Nvvdd & 1) == 0 && (mask >> (int)Rail.Msvdd & 1) == 0) return (null, null);
        var st = ReadStatus(hw, gpu, mask);
        return st == null ? (null, null) : (ExtractPresentUv(st, mask, Rail.Nvvdd), ExtractPresentUv(st, mask, Rail.Msvdd));
    }

    private static byte[]? ReadStatus(NvClient hw, GpuSession gpu, uint mask)
    {
        var st = NvClient.NewBuffer(Slot.VoltVoltRailsGetStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(st.AsSpan(4), mask);
        return hw.Call(gpu, Slot.VoltVoltRailsGetStatus, st).IsOk && BinaryPrimitives.ReadUInt32LittleEndian(st.AsSpan(4)) == mask ? st : null;
    }

    /// <summary>Extract a rail's present voltage, µV, from an already-read rails status buffer.</summary>
    public static uint? ExtractPresentUv(ReadOnlySpan<byte> status, uint mask, Rail rail)
    {
        int bit = (int)rail;
        if ((mask >> bit & 1) == 0 || status.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(status[4..]) != mask) return null;
        int at = RailsLayout.StatusBase + BitOperations.PopCount(mask & ((1u << bit) - 1)) * RailsLayout.StatusEntry;
        if (at + 4 > status.Length) return null;
        uint uv = BinaryPrimitives.ReadUInt32LittleEndian(status[at..]);
        return uv is > 0 and < 2_000_000 ? uv : null;
    }

    private static void Require(NvResult r)
    {
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
    }
}
