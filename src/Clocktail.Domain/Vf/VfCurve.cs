using System.Buffers.Binary;
using System.Numerics;
using Clocktail.Hw;

namespace Clocktail.Domain.Vf;

/// <summary>Status +0x24: how the driver lays out the Core curve's records.</summary>
public enum VfMode : byte { Legacy = 0, Regional = 1 }

/// <summary>
/// The only four point-mask layouts accepted. Anything else is refused as
/// "The V/F mode and point mask do not match a supported layout": the editable range is a fact of the layout,
/// so a layout nobody has seen has no known editable range.
/// </summary>
public enum VfLayoutId
{
    /// <summary>Regional, points 0..131 (live RTX 5090, driver 617.42). 127 editable Core points.</summary>
    Regional132,
    /// <summary>Regional, points 0..128. 127 editable Core points.</summary>
    Regional129,
    /// <summary>Legacy, points 0..83. 80 editable Core points.</summary>
    Legacy84,
    /// <summary>Legacy, points 0..132. 128 editable Core points (mask bit 128 is the 128-point switch).</summary>
    Legacy133,
}

/// <summary>
/// One editable Core point, a 0x14-byte record: index, voltage, the frequency without
/// any offset, the frequency the driver reports now, and this point's own offset.
/// </summary>
public sealed record VfPoint(
    int Index,
    /// <summary>The voltage the point runs at, µV (regional status +0x08, legacy +0x38): its firmware voltage plus any
    /// Core voltage-demand offset in force. Strictly rising along a regional curve.</summary>
    uint VoltageUv,
    /// <summary>The point's frequency without offsets, kHz (regional status +0x0c; legacy: effective minus the point offset).</summary>
    uint BaseKhz,
    /// <summary>The frequency the driver reports for the point now, kHz (regional status +0x04, legacy +0x34). Status, not control.</summary>
    uint EffectiveKhz,
    /// <summary>This point's offset, kHz (control +0x14). The only field a V/F write changes.</summary>
    int OffsetKhz,
    /// <summary>The point's firmware voltage, µV, without the voltage-demand offset (regional status +0x10; a legacy
    /// curve reports one voltage, so there it equals <see cref="VoltageUv"/>). VoltageUv − this is the Core
    /// voltage-demand offset in force, equal on every point; the stock 5090 reports the two equal. The axis undervolt
    /// shapes are drawn on (<see cref="VfShapes"/>).</summary>
    uint ReferenceVoltageUv);

public sealed record VfCurveSnapshot(
    VfMode Mode,
    VfLayoutId Layout,
    /// <summary>The 256-bit point mask the driver advertised (Info +4..+0x23); Status and Control must echo it byte for byte.</summary>
    byte[] Mask,
    IReadOnlyList<VfPoint> Points,
    /// <summary>VoltageUv − ReferenceVoltageUv, identical on every editable point (0 on legacy curves).</summary>
    int DemandShiftUv,
    byte[] Info,
    byte[] Status,
    byte[] Control)
{
    public int EditableCount => Points.Count;
    public int MaskPoints => VfLayout.PopCount(Mask);

    /// <summary>The offsets of points 0..N−1, in index order: what a V/F write owns.</summary>
    public int[] Offsets() => Points.Select(p => p.OffsetKhz).ToArray();
}

/// <summary>
/// The three Core V/F structures (slots 11–14), all BOARDOBJGRP-shaped: version, a 256-bit point mask, a header
/// tail, then 255 fixed-stride entries indexed by point number. Sizes reproduce the driver's exactly:
/// 0x44 + 255×0x18 = 0x182c (info), 0x68 + 255×0x15c = 0x15b0c (status), 0x44 + 255×0x24 = 0x2420 (control).
/// </summary>
public static class VfLayout
{
    public const int MaskOffset = 4, MaskBytes = 0x20, MaxPoints = 255;

    public const int InfoEntries = 0x44, InfoStride = 0x18;          // +0 type, +4 byte: 1 = editable Core point
    public const int StatusMode = 0x24;                              // byte: 0 legacy, 1 regional
    public const int StatusEntries = 0x68, StatusStride = 0x15c;     // +0 type, then mode-dependent fields
    public const int ControlEntries = 0x44, ControlStride = 0x24;    // +0 type, +0x14 s32 offset kHz
    public const int ControlOffset = 0x14;

    public static int Info(int i) => InfoEntries + i * InfoStride;
    public static int Status(int i) => StatusEntries + i * StatusStride;
    public static int Control(int i) => ControlEntries + i * ControlStride;
    /// <summary>Where point <paramref name="i"/>'s offset lives in a control (and set-control) buffer.</summary>
    public static int OffsetField(int i) => Control(i) + ControlOffset;

    /// <summary>Points 0..n−1 as a 256-bit mask.</summary>
    public static byte[] FirstPoints(int n)
    {
        var m = new byte[MaskBytes];
        for (int i = 0; i < n; i++) m[i >> 3] |= (byte)(1 << (i & 7));
        return m;
    }

    public static bool Has(ReadOnlySpan<byte> mask, int i) => (mask[i >> 3] >> (i & 7) & 1) != 0;

    public static int PopCount(ReadOnlySpan<byte> mask)
    {
        int n = 0;
        foreach (var b in mask) n += BitOperations.PopCount(b);
        return n;
    }

    /// <summary>Copy a mask into a structure's +4..+0x23: the question a status or control read asks.</summary>
    public static void WriteMask(byte[] buffer, ReadOnlySpan<byte> mask) => mask.CopyTo(buffer.AsSpan(MaskOffset, MaskBytes));

    public static byte[] ReadMask(ReadOnlySpan<byte> buffer) => buffer.Slice(MaskOffset, MaskBytes).ToArray();

    /// <summary>The supported layouts and their editable Core point counts.</summary>
    public static (VfLayoutId Id, int Editable)? Identify(VfMode mode, ReadOnlySpan<byte> mask)
    {
        int n = PopCount(mask);
        if (!mask.SequenceEqual(FirstPoints(n))) return null;   // every supported layout is a solid run from point 0
        return (mode, n) switch
        {
            (VfMode.Regional, 132) => (VfLayoutId.Regional132, 127),
            (VfMode.Regional, 129) => (VfLayoutId.Regional129, 127),
            (VfMode.Legacy, 84) => (VfLayoutId.Legacy84, 80),
            (VfMode.Legacy, 133) => (VfLayoutId.Legacy133, 128),
            _ => null,
        };
    }
}

/// <summary>
/// Pure parsing of the three structures into the editable Core curve, with structural proofs.
/// Each failure has its own message, because each one names a
/// different way the driver can disagree with itself, and a write planned on such a curve would be a guess.
/// </summary>
public static class VfParser
{
    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);

    public static VfCurveSnapshot Parse(byte[] info, byte[] status, byte[] control)
    {
        Expect(info, Slot.ClockClientClkVfPointsGetInfo, "The driver returned an unsupported V/F info layout.");
        Expect(status, Slot.ClockClientClkVfPointsGetStatus, "The driver returned an unsupported V/F status layout.");
        Expect(control, Slot.ClockClientClkVfPointsGetControl, "The driver returned an unsupported V/F control layout.");

        byte modeByte = status[VfLayout.StatusMode];
        if (modeByte > 1) throw new InvalidDataException("The V/F mode and point mask do not match a supported layout.");
        var mode = (VfMode)modeByte;
        var mask = VfLayout.ReadMask(info);
        var layout = VfLayout.Identify(mode, mask) ?? throw new InvalidDataException("The V/F mode and point mask do not match a supported layout.");
        if (!mask.AsSpan().SequenceEqual(status.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes)) ||
            !mask.AsSpan().SequenceEqual(control.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes)))
            throw new InvalidDataException("The driver returned inconsistent V/F point masks.");

        int editable = layout.Editable;
        var points = new List<VfPoint>();
        int? shift = null;
        for (int i = 0; i < VfLayout.MaxPoints; i++)
        {
            if (!VfLayout.Has(mask, i)) continue;
            uint infoType = U(info, VfLayout.Info(i));
            bool coreFlag = info[VfLayout.Info(i) + 4] == 1;
            uint statusType = U(status, VfLayout.Status(i));
            uint controlType = U(control, VfLayout.Control(i));

            if (mode == VfMode.Legacy)
            {
                // Legacy curves advertise a fixed topology: N Core points, then 2 (80-point) or 3 (128-point) type-1 points.
                uint expectedType = i >= editable && i < editable + 2 + (editable != 80 ? 1 : 0) ? 1u : 0u;
                if (infoType != expectedType || statusType != infoType || controlType != infoType || coreFlag != (i < editable))
                    throw new InvalidDataException("The legacy V/F records do not match their advertised topology.");
            }
            if (infoType != 0 || statusType != 0 || controlType != 0 || !coreFlag) continue;

            int s = VfLayout.Status(i);
            int offset = I(control, VfLayout.OffsetField(i));
            uint effective, voltage, reference;
            long baseKhz;
            if (mode == VfMode.Regional)
            {
                effective = U(status, s + 4); voltage = U(status, s + 8); baseKhz = U(status, s + 0xc); reference = U(status, s + 0x10);
            }
            else
            {
                effective = U(status, s + 0x34); voltage = U(status, s + 0x38); reference = voltage;
                baseKhz = effective - (long)offset;
            }
            if (baseKhz < 1 || baseKhz > uint.MaxValue) throw new InvalidDataException("The V/F frequency baseline or stored offset is invalid.");
            if (effective == 0 || reference == 0) throw new InvalidDataException("The editable V/F curve failed structural validation.");
            if (points.Count > 0)
            {
                uint previous = points[^1].VoltageUv;
                if (mode == VfMode.Regional ? voltage <= previous : voltage < previous)
                    throw new InvalidDataException("The editable V/F curve failed structural validation.");
            }
            long d = (long)voltage - reference;
            if (d is < int.MinValue or > int.MaxValue) throw new InvalidDataException("The V/F curve carries a Core voltage-demand shift outside the supported range.");
            if (shift != null && shift != (int)d) throw new InvalidDataException("The driver returned an inconsistent Core voltage-demand shift across the editable V/F curve.");
            shift = (int)d;
            points.Add(new VfPoint(i, voltage, (uint)baseKhz, effective, offset, reference));
        }
        if (points.Count != editable) throw new InvalidDataException("The driver did not expose a supported editable GPU curve.");
        for (int k = 0; k < points.Count; k++)
            if (points[k].Index != k) throw new InvalidDataException("The editable GPU V/F point sequence is incomplete.");

        return new VfCurveSnapshot(mode, layout.Id, mask, points, shift ?? 0, info, status, control);
    }

    /// <summary>The offsets of points 0..n−1 from a control buffer alone (what the ownership record holds).</summary>
    public static int[] Offsets(ReadOnlySpan<byte> control, int n)
    {
        var o = new int[n];
        for (int i = 0; i < n; i++) o[i] = I(control, VfLayout.OffsetField(i));
        return o;
    }

    /// <summary>
    /// The offset of every Core point (type 0, info flag 1) from Info and Control alone, for a caller that needs
    /// nothing else of the curve (<see cref="VfBudget"/>). It proves only what the offsets' meaning rests on: both
    /// layouts, one mask, Info and Control agreeing on which points are Core entries. The Status proofs of
    /// <see cref="Parse"/> are about the curve's shape and do not apply.
    /// </summary>
    public static IReadOnlyList<(int Index, int OffsetKhz)> CoreOffsets(byte[] info, byte[] control)
    {
        Expect(info, Slot.ClockClientClkVfPointsGetInfo, "The driver returned an unsupported V/F info layout.");
        Expect(control, Slot.ClockClientClkVfPointsGetControl, "The driver returned an unsupported V/F control layout.");
        var mask = VfLayout.ReadMask(info);
        if (!mask.AsSpan().SequenceEqual(control.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes)))
            throw new InvalidDataException("The driver returned inconsistent V/F point masks.");
        var offsets = new List<(int, int)>();
        for (int i = 0; i < VfLayout.MaxPoints; i++)
        {
            if (!VfLayout.Has(mask, i) || U(info, VfLayout.Info(i)) != 0 || info[VfLayout.Info(i) + 4] != 1) continue;
            if (U(control, VfLayout.Control(i)) != 0)
                throw new InvalidDataException($"V/F point {i} is a Core entry in the info structure but not in the control structure.");
            offsets.Add((i, I(control, VfLayout.OffsetField(i))));
        }
        return offsets;
    }

    private static void Expect(byte[] raw, Slot slot, string message)
    {
        var shape = Slots.Info(slot).Shape!.Value;
        if (raw.Length != shape.Size || U(raw, 0) != shape.Word) throw new InvalidDataException(message);
    }
}

/// <summary>Validated Core control topology and offsets, independent of effective-frequency Status.
/// This snapshot can record and restore controls for a P0 failure; it cannot plan or verify a new V/F tune.</summary>
public sealed record VfControlSnapshot(VfLayoutId Layout, byte[] Info, byte[] Control, byte[] Mask,
    IReadOnlyList<(int Index, int OffsetKhz)> Points);

/// <summary>
/// The read order the driver requires: Info first (it answers with the point mask), then Status and Control,
/// each asked about exactly that mask. Status and Control are queries: with a zero mask the driver answers with
/// nothing but the mode byte (live 617.42 diagnose sweep), so the mask is the question, not decoration.
/// </summary>
public static class VfReader
{
    public static VfCurveSnapshot Read(NvClient hw, GpuSession gpu)
    {
        var info = Get(hw, gpu, Slot.ClockClientClkVfPointsGetInfo, null, "V/F point information read");
        var mask = VfLayout.ReadMask(info);
        var status = Get(hw, gpu, Slot.ClockClientClkVfPointsGetStatus, mask, "V/F point status read");
        var control = Get(hw, gpu, Slot.ClockClientClkVfPointsGetControl, mask, "V/F point control read");
        return VfParser.Parse(info, status, control);
    }

    /// <summary>Every Core point's offset (<see cref="VfParser.CoreOffsets"/>): Info and Control, no Status.</summary>
    public static IReadOnlyList<(int Index, int OffsetKhz)> ReadOffsets(NvClient hw, GpuSession gpu)
    {
        var info = Get(hw, gpu, Slot.ClockClientClkVfPointsGetInfo, null, "V/F point information read");
        var control = Get(hw, gpu, Slot.ClockClientClkVfPointsGetControl, VfLayout.ReadMask(info), "V/F point control read");
        return VfParser.CoreOffsets(info, control);
    }

    /// <summary>Read budget offsets and, when their topology is supported, an ownership snapshot from the same
    /// Info/Control query. Unsupported ownership topology does not discard readable budget data. No Status query.</summary>
    public static (IReadOnlyList<(int Index, int OffsetKhz)> Offsets, VfControlSnapshot? Original) ReadControls(NvClient hw, GpuSession gpu)
    {
        var info = Get(hw, gpu, Slot.ClockClientClkVfPointsGetInfo, null, "V/F point information read");
        var mask = VfLayout.ReadMask(info);
        var control = Get(hw, gpu, Slot.ClockClientClkVfPointsGetControl, mask, "V/F point control read");
        var points = VfParser.CoreOffsets(info, control);
        // The supported masks identify distinct topologies without interpreting a derived frequency baseline.
        var layout = VfLayout.Identify(VfMode.Regional, mask) ?? VfLayout.Identify(VfMode.Legacy, mask);
        if (layout == null || points.Count != layout.Value.Editable || points.Where((p, i) => p.Index != i).Any())
            return (points, null);
        return (points, new(layout.Value.Id, info, control, mask, points));
    }

    private static byte[] Get(NvClient hw, GpuSession gpu, Slot slot, byte[]? mask, string what)
    {
        var b = NvClient.NewBuffer(slot);
        if (mask != null) VfLayout.WriteMask(b, mask);
        var r = hw.Call(gpu, slot, b);
        if (!r.IsOk) throw new InvalidOperationException($"{what}: {r}");
        return b;
    }
}
