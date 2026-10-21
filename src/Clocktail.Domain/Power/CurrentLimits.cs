using System.Buffers.Binary;
using System.Globalization;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Domain.Power;

/// <summary>
/// The raw PMGR policy table in its layout 1:
/// INFO 0xa70 rev 4 and CONTROL 0xa4c rev 1, both read live on the RTX 5090 (617.42, 2026-10-19). One record per
/// policy, selected by the bit mask at +4. An INFO record is {+0 class, +4 power-monitor channel, +8 unit (1 = mA,
/// 0 = mW), +0xc min, +0x10 default, +0x14 max}; a CONTROL record is {+0 class, +4 limit} and nothing else. CONTROL
/// is read as a query: the mask in the buffer picks the records the driver fills in, the rest come back zero.
/// </summary>
public static class PolicyTableLayout
{
    public const int InfoSize = 0xa70, InfoRev = 4, ControlSize = 0xa4c, ControlRev = 1;
    public const int Mask = 4, InfoBase = 0x3c, InfoStride = 0x58, ControlBase = 0x1c, ControlStride = 0x28, Records = 32;
    /// <summary>The class of a current-limit ("OCP") record. On the 5090 four records carry it: two in mW on 12 V
    /// inputs, and the two written here, in mA on the NVVDD and MSVDD regulator outputs.</summary>
    public const uint CurrentLimitClass = 0x13;
    public const uint UnitMilliamps = 1;

    public static int InfoRecord(int i) => InfoBase + i * InfoStride;
    public static int ControlRecord(int i) => ControlBase + i * ControlStride;
    public static int ControlLimit(int i) => ControlRecord(i) + 4;
}

/// <summary>
/// The power monitor: INFO 0xca8 rev 3 with the channel mask at +0x10 and, per channel, a provider
/// at 0x34 + i×0x3c and a channel type at 0x38 + i×0x3c (1 NVVDD output, 0x10 MSVDD, 0xf5 total board, …); STATUS
/// 0x59c rev 1 with the mask at +4, the total in mW at +8, and per channel {+0 mW, +0xc mA, +0x10 µV} at 0x1c + i×0x2c.
/// On the 5090 the rail outputs (provider 6, a regulator) are channels 12 (MSVDD) and 13/14 (NVVDD), read live.
/// </summary>
public static class PowerMonitorLayout
{
    public const int InfoSize = 0xca8, InfoRev = 3, InfoMask = 0x10, Provider = 0x34, Type = 0x38, ChannelStride = 0x3c;
    public const int StatusSize = 0x59c, StatusRev = 1, StatusMask = 4, StatusTotal = 8, StatusBase = 0x1c, StatusStride = 0x2c;
    public const uint Regulator = 6, NvvddOutput = 1, MsvddOutput = 0x10;
    /// <summary>Measured on the RTX 5090 FE (2026-10-21, idle and full load at 400/500/600 W): type 0xda read total board
    /// less the PCIe slot to 0.1 W in every state (the 16-pin connector), type 0xa9 reads current only and tracks the
    /// board's input current (+0.5 A; 49.6 A at the 600 W wall). Its policy record caps it at 60 A.</summary>
    public const uint ConnectorInput = 0xda, InputCurrent = 0xa9, PcieSlot = 0xff;

    public static uint TypeOf(Rail rail) => rail == Rail.Nvvdd ? NvvddOutput : MsvddOutput;

    /// <summary>The channel-type names; other types are "rail 0x…".</summary>
    public static string Name(uint type) => type switch
    {
        1 => "NVVDD output", 2 => "FBVDD output", 3 => "FBVDDQ output", 8 => "total GPU output", 0xb => "SRAM output",
        0x10 => "MSVDD output", 0xe8 => "misc 0 input", 0xf5 => "total board", 0xf6 => "NVVDD input", 0xf7 => "FBVDD input",
        0xf8 => "FBVDDQ input", 0xfa => "8-pin input 0", 0xfb => "8-pin input 1", 0xfe => "PCIe 3.3 V", 0xff => "PCIe 12 V",
        ConnectorInput => "16-pin input", InputCurrent => "board input current",
        _ => $"rail 0x{type:x}",
    };
}

public sealed record PolicyRecord(int Index, uint Class, uint Channel, uint Unit, uint Min, uint Default, uint Max);
public sealed record MonitorChannel(int Index, uint Provider, uint Type);

/// <summary>A rail's current limit as the driver states it: the record, its bounds and the limit in force, mA.</summary>
public sealed record CurrentLimit(Rail Rail, int Record, uint Channel, uint MinMa, uint DefaultMa, uint MaxMa, uint LimitMa);

/// <summary>
/// What comes into the board: the 16-pin connector's watts, the PCIe slot's, and the input current against the limit its
/// policy record states (60 A on the 5090 FE). Null fields where the card has no such channel.
/// </summary>
public sealed record BoardInput(double? ConnectorW, double? PcieSlotW, double? InputA, double? InputLimitA)
{
    /// <summary>The 16-pin connector is rated 600 W.</summary>
    public const double ConnectorRatedW = 600;

    public static BoardInput? From(CurrentLimits limits, IReadOnlyList<MonitorReading> readings)
    {
        MonitorReading? Of(uint type) => readings.FirstOrDefault(r => r.Type == type);
        var connector = Of(PowerMonitorLayout.ConnectorInput);
        var current = Of(PowerMonitorLayout.InputCurrent);
        if (connector == null && current == null) return null;
        var record = current == null ? null : limits.Records.FirstOrDefault(r => r.Channel == current.Channel && r.Unit == PolicyTableLayout.UnitMilliamps);
        return new BoardInput(connector?.Mw / 1000.0, Of(PowerMonitorLayout.PcieSlot)?.Mw / 1000.0, current?.Ma / 1000.0, record?.Default / 1000.0);
    }

    /// <summary>Why it needs a look, or null: the connector over its 600 W rating, or the input current within 10 % of its limit.</summary>
    public string? Concern() =>
        ConnectorW > ConnectorRatedW ? $"the 16-pin connector carries {ConnectorW:0} W, over its {ConnectorRatedW:0} W rating"
        : InputA is { } a && InputLimitA is { } l && l > 0 && a >= 0.9 * l ? $"input current {a:0.#} A is within 10 % of its {l:0} A limit"
        : null;
}

/// <summary>One power-monitor channel's reading.</summary>
public sealed record MonitorReading(int Channel, uint Type, uint Mw, int Ma, uint Uv);

public sealed record CurrentLimits(
    IReadOnlyList<PolicyRecord> Records,
    IReadOnlyList<MonitorChannel> Channels,
    CurrentLimit? Nvvdd,
    CurrentLimit? Msvdd,
    /// <summary>Why a rail has no limit (no record, or more than one), for the person reading it.</summary>
    IReadOnlyDictionary<Rail, string> Missing)
{
    public CurrentLimit? Get(Rail r) => r == Rail.Nvvdd ? Nvvdd : Msvdd;

    /// <summary>A rail's output as the monitor reads it: the channel its current limit watches, else the one regulator
    /// channel of the rail's type (two NVVDD channels report the same regulator on the 5090; the limit's is used).</summary>
    public MonitorReading? Output(Rail r, IReadOnlyList<MonitorReading> readings)
    {
        if (Get(r) is { } l) return readings.FirstOrDefault(x => x.Channel == l.Channel);
        uint type = PowerMonitorLayout.TypeOf(r);
        return readings.FirstOrDefault(x => x.Type == type && Channels.Any(c => c.Index == x.Channel && c.Provider == PowerMonitorLayout.Regulator));
    }
}

public static class CurrentLimitsParser
{
    public static IReadOnlyList<PolicyRecord> ParseInfo(ReadOnlySpan<byte> b)
    {
        uint mask = U(b, PolicyTableLayout.Mask);
        var list = new List<PolicyRecord>();
        for (int i = 0; i < PolicyTableLayout.Records; i++)
        {
            int o = PolicyTableLayout.InfoRecord(i);
            if ((mask >> i & 1) == 0 || o + PolicyTableLayout.InfoStride > b.Length) continue;
            list.Add(new PolicyRecord(i, U(b, o), U(b, o + 4), U(b, o + 8), U(b, o + 0xc), U(b, o + 0x10), U(b, o + 0x14)));
        }
        return list;
    }

    public static IReadOnlyList<MonitorChannel> ParseMonitorInfo(ReadOnlySpan<byte> b)
    {
        uint mask = U(b, PowerMonitorLayout.InfoMask);
        var list = new List<MonitorChannel>();
        for (int i = 0; i < 32; i++)
            if ((mask >> i & 1) != 0 && PowerMonitorLayout.Type + i * PowerMonitorLayout.ChannelStride + 4 <= b.Length)
                list.Add(new MonitorChannel(i, U(b, PowerMonitorLayout.Provider + i * PowerMonitorLayout.ChannelStride), U(b, PowerMonitorLayout.Type + i * PowerMonitorLayout.ChannelStride)));
        return list;
    }

    /// <summary>
    /// The identification (layout 1): the one current-limit record, in mA, whose monitor
    /// channel is a regulator output of the rail's type. None, or more than one, and the rail has no limit here: a
    /// write must never guess which record is the rail's.
    /// </summary>
    public static int? Identify(IReadOnlyList<PolicyRecord> records, IReadOnlyList<MonitorChannel> channels, Rail rail, out string? why)
    {
        uint type = PowerMonitorLayout.TypeOf(rail);
        var hits = records.Where(r => r.Class == PolicyTableLayout.CurrentLimitClass && r.Unit == PolicyTableLayout.UnitMilliamps
            && channels.Any(c => c.Index == r.Channel && c.Provider == PowerMonitorLayout.Regulator && c.Type == type)).ToList();
        why = hits.Count switch
        {
            0 => $"no current-limit record on a {rail} regulator output",
            1 => null,
            _ => $"{hits.Count} current-limit records on {rail} regulator outputs (records {string.Join(", ", hits.Select(h => h.Index))}); none is written",
        };
        return hits.Count == 1 ? hits[0].Index : null;
    }

    public static uint ControlLimit(ReadOnlySpan<byte> control, int record) => U(control, PolicyTableLayout.ControlLimit(record));
    public static uint ControlClass(ReadOnlySpan<byte> control, int record) => U(control, PolicyTableLayout.ControlRecord(record));

    public static IReadOnlyList<MonitorReading> ParseMonitorStatus(ReadOnlySpan<byte> b, IReadOnlyList<MonitorChannel> channels)
    {
        uint mask = U(b, PowerMonitorLayout.StatusMask);
        var list = new List<MonitorReading>();
        foreach (var c in channels)
        {
            int o = PowerMonitorLayout.StatusBase + c.Index * PowerMonitorLayout.StatusStride;
            if ((mask >> c.Index & 1) == 0 || o + 0x14 > b.Length) continue;
            list.Add(new MonitorReading(c.Index, c.Type, U(b, o), (int)U(b, o + 0xc), U(b, o + 0x10)));
        }
        return list;
    }

    private static uint U(ReadOnlySpan<byte> b, int o) => o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b[o..]) : 0;
}

/// <summary>
/// One record of the policy table as the inspector shows it: what it limits (named only for the classes measured on the
/// 5090 FE), the monitor channel it watches, the limit in force (CONTROL; null when the driver did not answer for it) and
/// that channel's reading now, both in the record's unit. There is no "binding now" flag in the table: a record whose
/// reading sits at its limit under load is one that can hold the clock.
/// </summary>
public sealed record PolicyRow(PolicyRecord Record, string What, string Watches, uint? Limit, long? Now)
{
    public bool Milliamps => Record.Unit == PolicyTableLayout.UnitMilliamps;

    /// <summary>The reading as a share of the limit in force, %; null without both.</summary>
    public double? UsePct => Limit is > 0 && Now is { } n ? Math.Round(100.0 * n / Limit.Value, 1) : null;
}

public static class PolicyTable
{
    /// <summary>The records measured on the 5090 FE (617.42): class 0x13 in mA the regulator current limits, 0x18 the board
    /// power limit (400 / 575 / 600 W), 0x19 the board input current (60 A). 0x00, 0x03, 0x12, 0x15, 0x17 and the two 0x13
    /// records in mW (on the 12 V inputs) are not identified.</summary>
    public static string? ClassName(PolicyRecord r) => r.Class switch
    {
        PolicyTableLayout.CurrentLimitClass when r.Unit == PolicyTableLayout.UnitMilliamps => "current limit",
        0x18 => "power limit",
        0x19 => "input current limit",
        _ => null,
    };

    /// <summary>CONTROL for every record INFO lists, in one GET: the mask picks the records the driver fills in.</summary>
    public static byte[] Control(NvClient hw, GpuSession gpu, IReadOnlyList<PolicyRecord> records)
    {
        var b = NvClient.NewBuffer(Slot.PowerPolicyGetControl);
        uint mask = 0;
        foreach (var r in records) mask |= 1u << r.Index;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PolicyTableLayout.Mask), mask);
        var res = hw.Call(gpu, Slot.PowerPolicyGetControl, b);
        if (!res.IsOk) throw new InvalidOperationException(res.ToString());
        return b;
    }

    /// <summary>Every record with bounds (an empty one, all zero, is left out). A CONTROL record whose class is not the
    /// INFO record's has no limit here: the two do not describe the same policy.</summary>
    public static IReadOnlyList<PolicyRow> Rows(CurrentLimits limits, ReadOnlySpan<byte> control, IReadOnlyList<MonitorReading> readings)
    {
        uint answered = control.Length >= PolicyTableLayout.Mask + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(control[PolicyTableLayout.Mask..]) : 0;
        var rows = new List<PolicyRow>();
        foreach (var r in limits.Records)
        {
            if (r.Max == 0) continue;
            var ch = limits.Channels.FirstOrDefault(c => c.Index == r.Channel);
            string watches = ch == null ? $"channel {r.Channel}" : $"channel {r.Channel}, {PowerMonitorLayout.Name(ch.Type)}";
            uint? limit = (answered >> r.Index & 1) != 0 && CurrentLimitsParser.ControlClass(control, r.Index) == r.Class
                ? CurrentLimitsParser.ControlLimit(control, r.Index) : null;
            var reading = readings.FirstOrDefault(x => x.Channel == r.Channel);
            long? now = reading == null ? null : r.Unit == PolicyTableLayout.UnitMilliamps ? (reading.Ma < 0 ? null : reading.Ma) : reading.Mw;
            rows.Add(new PolicyRow(r, ClassName(r) ?? $"class 0x{r.Class:x}, not identified", watches, limit, now));
        }
        return rows;
    }
}

/// <summary>Read-only: every call here is a GET, takes no write window and no lock.</summary>
public static class CurrentLimitsReader
{
    public static CurrentLimits Read(NvClient hw, GpuSession gpu)
    {
        var info = Get(hw, gpu, Slot.PowerPolicyGetInfo);
        var mon = Get(hw, gpu, Slot.PowerMonitorGetInfo);
        var records = CurrentLimitsParser.ParseInfo(info);
        var channels = CurrentLimitsParser.ParseMonitorInfo(mon);
        var missing = new Dictionary<Rail, string>();
        CurrentLimit? Limit(Rail rail)
        {
            if (CurrentLimitsParser.Identify(records, channels, rail, out var why) is not { } i) { missing[rail] = why!; return null; }
            var control = Control(hw, gpu, i);
            if (CurrentLimitsParser.ControlClass(control, i) != PolicyTableLayout.CurrentLimitClass)
            {
                missing[rail] = $"CONTROL record {i} is not a current-limit record";
                return null;
            }
            var r = records.First(x => x.Index == i);
            return new CurrentLimit(rail, i, r.Channel, r.Min, r.Default, r.Max, CurrentLimitsParser.ControlLimit(control, i));
        }
        return new CurrentLimits(records, channels, Limit(Rail.Nvvdd), Limit(Rail.Msvdd), missing);
    }

    /// <summary>Every monitor channel's reading, from one STATUS read with the INFO mask.</summary>
    public static IReadOnlyList<MonitorReading> Monitor(NvClient hw, GpuSession gpu, IReadOnlyList<MonitorChannel> channels)
    {
        var b = NvClient.NewBuffer(Slot.PowerMonitorGetStatus);
        uint mask = 0;
        foreach (var c in channels) mask |= 1u << c.Index;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PowerMonitorLayout.StatusMask), mask);
        var r = hw.Call(gpu, Slot.PowerMonitorGetStatus, b);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return CurrentLimitsParser.ParseMonitorStatus(b, channels);
    }

    /// <summary>CONTROL asked for one record, as the write's snapshot is.</summary>
    public static byte[] Control(NvClient hw, GpuSession gpu, int record)
    {
        var b = NvClient.NewBuffer(Slot.PowerPolicyGetControl);
        CurrentLimitWrites.Query(record)(b);
        var r = hw.Call(gpu, Slot.PowerPolicyGetControl, b);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return b;
    }

    private static byte[] Get(NvClient hw, GpuSession gpu, Slot slot)
    {
        var b = NvClient.NewBuffer(slot);
        var r = hw.Call(gpu, slot, b);
        if (!r.IsOk) throw new InvalidOperationException(r.ToString());
        return b;
    }
}

/// <summary>
/// A rail's current limit ("OCP") as one owned cell: CONTROL read for the
/// rail's record alone and written back with its limit changed (gateway kind 0x13). The firmware caps the rail's
/// current at it, so a lower limit caps that rail's power (P = V·I) where the board power request cannot go below
/// 400 W. The plan holds a value to the driver's [min, max] (1 mA..5001 A on the 5090). A limit above the VBIOS
/// default (NVVDD 600 A, MSVDD 180 A) is allowed (the user, 2026-10-21); at the 600 W wall NVVDD peaks near 430 A on
/// the reference card, so the board power limit binds first and a raise changes nothing there.
/// </summary>
public static class CurrentLimitWrites
{
    public const string Kind = "ocp";

    public static Rail ParseKey(string key) => key.ToLowerInvariant() switch
    {
        "nvvdd" or "core" => Rail.Nvvdd,
        "msvdd" or "memory" => Rail.Msvdd,
        _ => throw new ArgumentException($"'{key}' is not a rail with a current limit (nvvdd, msvdd)"),
    };

    public static string Key(Rail r) => r == Rail.Nvvdd ? "nvvdd" : "msvdd";

    public static Action<byte[]> Query(int record) => b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PolicyTableLayout.Mask), 1u << record);

    public static OwnedCell Cell(Rail rail, int record) => new(
        Kind, Key(rail), "mA", $"{rail} current limit (raw policy record {record}), mA",
        new TransactionSpec(Slot.PowerPolicyGetControl, Slot.PowerPolicySetControl, PrepareRead: Query(record)),
        raw =>
        {
            if (CurrentLimitsParser.ControlClass(raw, record) != PolicyTableLayout.CurrentLimitClass)
                throw new InvalidDataException($"CONTROL record {record} is not a current-limit record");
            return CurrentLimitsParser.ControlLimit(raw, record).ToString(CultureInfo.InvariantCulture);
        });

    /// <summary>Judged on the fresh snapshot: the record is still a current limit (its control identity has not changed),
    /// and the value is inside the driver's range.</summary>
    public static Func<ReadOnlyMemory<byte>, Mutation> Plan(CurrentLimit limit, uint ma) => raw =>
    {
        var s = raw.Span;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[PolicyTableLayout.Mask..]) != 1u << limit.Record)
            return new Mutation.Abort($"the driver answered for other records than {limit.Record}");
        if (CurrentLimitsParser.ControlClass(s, limit.Record) != PolicyTableLayout.CurrentLimitClass)
            return new Mutation.Abort($"the selected {limit.Rail} current-limit record changed identity; nothing written");
        if (ma < limit.MinMa || ma > limit.MaxMa)
            return new Mutation.Abort($"{ma / 1000.0:0.###} A is outside the {limit.MinMa / 1000.0:0.###}–{limit.MaxMa / 1000.0:0.###} A range the driver reports for {limit.Rail}");
        uint now = CurrentLimitsParser.ControlLimit(s, limit.Record);
        return now == ma ? new Mutation.Apply([]) : Mutation.Of(Edit.UInt32(PolicyTableLayout.ControlLimit(limit.Record), ma));
    };

    /// <summary>The write-path registry's entry. The record is identified afresh for every write.</summary>
    public static (OwnedCell Cell, Func<ReadOnlyMemory<byte>, Mutation>? Plan, string? CannotWrite) Prepare(NvClient hw, GpuSession gpu, string key, string value)
    {
        var rail = ParseKey(key);
        var limits = CurrentLimitsReader.Read(hw, gpu);
        var limit = limits.Get(rail) ?? throw new InvalidOperationException($"{rail} has no current limit on this GPU: {limits.Missing.GetValueOrDefault(rail)}");
        var cell = Cell(rail, limit.Record);
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ma))
            return (cell, null, $"'{value}' is not a whole number of milliamps");
        return (cell, Plan(limit, ma), null);
    }
}
