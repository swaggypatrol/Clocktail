using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Domain;

public sealed record GetProbe(Slot Slot, uint Id, string Shape, NvOutcome Outcome, int Status, bool VersionEchoed, int NonZeroDwords, string Head);

public sealed record SizeProbe(Slot Slot, int Rev, IReadOnlyList<int> AcceptedSizes, int Tried, string? GuardBroken);

/// <summary>
/// GET-only exploration of the driver: a compatibility report. Never calls a setter,
/// never resolves the forbidden slot.
/// </summary>
public static class Diagnostics
{
    /// <summary>Read every readable slot whose shape is known and report how the driver answered.</summary>
    public static List<GetProbe> GetSweep(NvClient hw, GpuSession gpu)
    {
        var result = new List<GetProbe>();
        uint railMask = 0, adcMask = 0;
        foreach (var s in Slots.All)
        {
            if (s.Access != SlotAccess.Read || s.Shape is not { } shape) continue;
            if (s.Signature is Signature.GpuScalar or Signature.GpuIndexStruct) continue;
            if (s.Signature == Signature.GpuDomainSentinel)
            {
                var d = hw.ReadDomain(gpu, s.Slot, 6, out uint v);
                result.Add(new GetProbe(s.Slot, s.Id, shape.ToString(), d.Outcome, d.Status, d.IsOk, d.IsOk ? 1 : 0, d.IsOk ? $"domain 6 = {v}" : ""));
                continue;
            }
            var b = NvClient.NewBuffer(s.Slot);
            // Inputs some structures need.
            switch (s.Slot)
            {
                case Slot.VoltVoltRailsGetStatus or Slot.VoltVoltRailsGetControl: BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), railMask); break;
                case Slot.ClockAdcDevicesGetStatus: BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), adcMask); break;
                case Slot.ClockClkDomainsGetControl: BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 2); break;
                case Slot.ClockClkPropTopRelsGetControl: BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x3fff); break;
                case Slot.ThermChannelGetControl or Slot.ThermChannelGetStatus: BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0xffff); break;
                case Slot.ClientPwrPoliciesGetControl:   // board 0, ordinary request: the usual query shape
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x28 + 4), 0xfe); break;
            }
            var r = hw.Call(gpu, s.Slot, b);
            if (r.IsOk)
            {
                if (s.Slot == Slot.VoltVoltRailsGetInfo) railMask = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
                if (s.Slot == Slot.ClockAdcDevicesGetInfo) adcMask = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
            }
            int nz = 0;
            for (int o = 4; o + 4 <= b.Length; o += 4) if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) != 0) nz++;
            result.Add(new GetProbe(s.Slot, s.Id, shape.ToString(), r.Outcome, r.Status,
                r.IsOk && BinaryPrimitives.ReadUInt32LittleEndian(b) == shape.Word, r.IsOk ? nz : 0,
                r.IsOk ? Convert.ToHexString(b.AsSpan(0, Math.Min(64, b.Length))) : ""));
        }
        return result;
    }

    /// <summary>
    /// The size sweep: for one readable slot, try (size, rev) pairs from 8 bytes upward in steps of 4
    /// and record which sizes the driver accepts. This is how an unknown or disputed structure size is settled.
    /// A guard violation stops the sweep for that revision, since the driver wrote past what it was given.
    /// </summary>
    public static List<SizeProbe> ProbeSize(NvClient hw, GpuSession gpu, Slot slot, int maxSize = 0x2000, int minRev = 1, int maxRev = 4, int keep = 4)
    {
        var info = Slots.Info(slot);
        if (info.Access != SlotAccess.Read) throw new ArgumentException($"{slot} is not a readable slot.", nameof(slot));
        if (info.Signature != Signature.GpuStruct) throw new ArgumentException($"{slot} is not a plain structure call.", nameof(slot));
        var probes = new List<SizeProbe>();
        for (int rev = minRev; rev <= maxRev; rev++)
        {
            var accepted = new List<int>();
            string? broken = null;
            int tried = 0;
            for (int size = 8; size <= maxSize && accepted.Count < keep; size += 4)
            {
                var b = new byte[size];
                BinaryPrimitives.WriteUInt32LittleEndian(b, new Versioned(size, rev).Word);
                var r = hw.CallRaw(gpu, slot, b);
                tried++;
                if (r.Outcome == NvOutcome.GuardBroken) { broken = $"size 0x{size:x}: {r.Detail}"; break; }
                if (r.Outcome is NvOutcome.Missing or NvOutcome.Poisoned) break;
                if (r.IsOk) accepted.Add(size);
            }
            probes.Add(new SizeProbe(slot, rev, accepted, tried, broken));
        }
        return probes;
    }
}
