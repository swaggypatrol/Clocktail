using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Clocks;

/// <summary>
/// A clock domain's voltage-demand offset (<c>--xbar/sys/video-vdemand MV</c>): µV added to what every
/// point of the domain's V/F curve asks its rail for, so the domain runs the same clock at a higher (or lower) voltage.
/// Not the rail's REL/ALT limits: those only move the ceiling. Two slots follow the clock offset in the domain's control
/// entry, NVVDD's then MSVDD's; a domain uses the one its topology entry names. The core's (graphics, NVVDD's slot of
/// entry 0) moves every Core V/F point's voltage by the same µV: the reader shows it as the curve's demand shift
/// (VfPoint.VoltageUv less its firmware voltage), so the same curve runs at a higher or lower voltage. XBAR's was written
/// live on 2026-10-20 (+20 mV moved MSVDD 825 → 845 mV); the core's on 2026-10-21 (+5 mV read back as the curve's +5 mV
/// demand shift, 0 put it back).
/// </summary>
public static class ClockVdemand
{
    public const string Kind = "vdemand";

    /// <summary>The standard range; an XOC mode would take any int32, and this program has no XOC.</summary>
    public const int MinUv = -25_000, MaxUv = 50_000;

    public static bool Writable(ClockDomainId d) => d is ClockDomainId.Graphics or ClockDomainId.Xbar or ClockDomainId.Sys or ClockDomainId.Video;

    public static string Key(ClockDomainId d) => d.ToString().ToLowerInvariant();

    /// <summary>Where a slot sits: right after the clock offset, NVVDD's (slot 0) then MSVDD's (slot 1).</summary>
    public static int SlotAt(ClockTopologyEntry t, int slot) => ClockDomainsLayout.OffsetAt(t.Index, t.Type) + 4 + 4 * slot;

    /// <summary>
    /// The probe: which slot (0 NVVDD, 1 MSVDD) the domain's demand goes to, or why it has none. The topology
    /// entry is type 10 or 0xf, its rail (+0x78) is 1 or 2, its capability byte for that slot (+0x80 for NVVDD, +0xc4 for
    /// MSVDD) is 1, and the rail is present with VOLT info kind 0 or 3 and type slot×2+1.
    /// </summary>
    public static int Slot(ClockTopologyEntry t, Func<int, (uint Kind, uint Type)?> railInfo)
    {
        if (t.Type is not (10 or 0xf)) throw new InvalidDataException($"clock domain 0x{t.DomainId:x} has topology type {t.Type}: no voltage demand");
        if (t.Rail is not (1 or 2)) throw new InvalidDataException($"clock domain 0x{t.DomainId:x} names rail {t.Rail}: no voltage demand");
        int slot = t.Rail == 1 ? 0 : 1;
        uint cap = slot == 0 ? t.Caps & 0xff : t.Caps >> 16 & 0xff;
        if (cap != 1) throw new InvalidDataException($"clock domain 0x{t.DomainId:x} does not offer a voltage demand on its rail (capability byte {cap})");
        if (railInfo(slot) is not { } info) throw new InvalidDataException($"the rail clock domain 0x{t.DomainId:x} demands from is not present");
        if (info.Kind is not (0 or 3) || info.Type != slot * 2 + 1)
            throw new InvalidDataException($"the rail clock domain 0x{t.DomainId:x} demands from reports kind {info.Kind} / type {info.Type}, not the expected rail");
        return slot;
    }

    public static Func<int, (uint Kind, uint Type)?> RailInfo(RailsSnapshot rails) =>
        slot => rails.Get((Rail)slot) is { } r ? (r.Kind, r.Type) : null;

    /// <summary>The demand in force for <paramref name="slot"/>, from a single-entry control read.</summary>
    public static int Read(byte[] control, ClockTopologyEntry t, int slot)
    {
        var c = ClockDomainsParser.ParseControl(control, t.Index);
        return slot == 0 ? c.VdemandNvvddUv : c.VdemandMsvddUv;
    }

    public static Mutation Plan(ClockTopologyEntry t, int slot, byte[] control, int uv)
    {
        ClockControlEntry c;
        try { c = ClockDomainsParser.ParseControl(control, t.Index); }
        catch (InvalidDataException e) { return new Mutation.Abort(e.Message); }
        if (c.Type != t.Type) return new Mutation.Abort($"clock control entry {t.Index} reports type {c.Type}, the topology says {t.Type}: the identity changed");
        if (uv < MinUv || uv > MaxUv)
            return new Mutation.Abort(string.Create(CultureInfo.InvariantCulture, $"A voltage-demand offset of {uv / 1000.0} mV is outside the supported {MinUv / 1000} to +{MaxUv / 1000} mV range."));
        if (c.Reserved0 != 0 || c.Reserved1 != 0)
            return new Mutation.Abort("an unexpected rail offset is active in a slot this program does not know; it will not overwrite it");
        if ((slot == 0 ? c.VdemandNvvddUv : c.VdemandMsvddUv) == uv) return new Mutation.Apply([]);
        return Mutation.Of(Edit.Int32(SlotAt(t, slot), uv));
    }

    /// <summary>The cell is keyed by domain; the entry index and slot are where the driver keeps it today.</summary>
    public static OwnedCell Cell(ClockDomainId domain, ClockTopologyEntry t, int slot) => new(
        Kind, Key(domain), "µV", $"clock-domain 0x{(uint)domain:x} voltage demand on {(slot == 0 ? "NVVDD" : "MSVDD")}, µV ({t.Identity})",
        ClockDomainOffsets.Spec(t.Index),
        raw =>
        {
            var c = ClockDomainsParser.ParseControl(raw, t.Index);
            return c.Type == t.Type ? (slot == 0 ? c.VdemandNvvddUv : c.VdemandMsvddUv).ToString(CultureInfo.InvariantCulture) : null;
        });

    /// <summary>
    /// The write-path registry's entry: the cell for <paramref name="key"/> (xbar | sys | video) and the plan that sets
    /// it to <paramref name="value"/>, µV. Serves a write and recovery alike; the plan is null when the value is not an
    /// integer. The probe runs on a fresh topology and rails read, so a domain that offers no demand is refused here.
    /// </summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        var domain = Enum.GetValues<ClockDomainId>().Where(Writable).Cast<ClockDomainId?>().FirstOrDefault(d => Key(d!.Value) == key)
            ?? throw new ArgumentException($"'{key}' is not a clock domain whose voltage demand is written here (graphics, xbar, sys, video)");
        var t = ClockDomainsParser.Find(ClockDomainOffsets.ReadTopology(hw, gpu), (uint)domain);
        int slot = Slot(t, RailInfo(RailsReader.Read(hw, gpu)));
        Func<ReadOnlyMemory<byte>, Mutation>? plan = int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var uv)
            ? raw => Plan(t, slot, raw.ToArray(), uv) : null;
        return (Cell(domain, t, slot), plan);
    }
}
