using System.Globalization;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Hw;

namespace Clocktail.Cli;

/// <summary>
/// Clock-domain (XBAR/SYS/Video) offsets and voltage boost. Owns its own flags; Program.Run hands over when any of
/// them is present. Reads happen here; every write is one broker request through <see cref="CliContext"/>.
/// </summary>
internal static class ClockCommands
{
    public const string Usage = """

        Clock domains and voltage boost:
          --clock-domains          JSON: the driver's clock-domain table (id, range, offset, vdemand) (read-only)
          --boost-read             JSON: both boost percentages, every rail's MAX, the default path rule (read-only)
          --xbar-offset MHz        XBAR clock offset, bounded by the driver's own range; journal + sentinel
          --sys-offset MHz         SYS clock offset, same path
          --video-offset MHz       clock domain 0x15 offset, same path
          --xbar-vdemand mV        XBAR voltage demand: mV added to every XBAR V/F point's voltage (not the rail's limit),
                                   up to ±1000 mV (a units check; the driver decides inside it); also --sys-vdemand, --video-vdemand
          --allow-clock-domain-write
                                   no longer needed: the clock-domain setter is on since its live drill (2026-10-20); accepted for old scripts
          --boost PCT              voltage boost 0..100 %, read back exactly; what it moves is the driver's
          --boost-path P           independent | legacy (default: the rule for this GPU)
          --boost-prove-equal P    write the current boost back unchanged through path P and show nothing moved
        """;

    private static readonly string[] Flags =
        ["--clock-domains", "--boost-read", "--xbar-offset", "--sys-offset", "--video-offset", "--xbar-vdemand", "--sys-vdemand", "--video-vdemand",
         "--boost", "--boost-prove-equal"];

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (!args.Any(a => Flags.Contains(a))) return false;

        int? gpuIndex = null; string? gpuId = null;
        bool takeOver = false, allowDomainWrite = false;
        var offsets = new List<(ClockDomainId Domain, int Khz)>();
        var demands = new List<(ClockDomainId Domain, int Uv)>();
        uint? boost = null; BoostPath? boostPath = null, prove = null;
        var read = new HashSet<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu": gpuIndex = int.TryParse(Next(), out var n) ? n : throw new ArgumentException("--gpu needs an integer."); break;
                case "--gpu-id": gpuId = Next(); break;
                case "--take-over": takeOver = true; break;
                case "--allow-clock-domain-write": allowDomainWrite = true; break;
                case "--xbar-offset": offsets.Add((ClockDomainId.Xbar, Khz(Next()))); break;
                case "--sys-offset": offsets.Add((ClockDomainId.Sys, Khz(Next()))); break;
                case "--video-offset": offsets.Add((ClockDomainId.Video, Khz(Next()))); break;
                case "--xbar-vdemand": demands.Add((ClockDomainId.Xbar, Uv(Next()))); break;
                case "--sys-vdemand": demands.Add((ClockDomainId.Sys, Uv(Next()))); break;
                case "--video-vdemand": demands.Add((ClockDomainId.Video, Uv(Next()))); break;
                case "--boost": boost = uint.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p <= VoltBoost.MaxPercent ? p : throw new ArgumentException("--boost must be between 0 and 100."); break;
                case "--boost-path": boostPath = Path(Next()); break;
                case "--boost-prove-equal": prove = Path(Next()); break;
                case "--clock-domains" or "--boost-read": read.Add(args[i]); break;
                default: throw new ArgumentException($"'{args[i]}' cannot be combined with the clock-domain / boost options");
            }
        }

        using var mgr = GpuManager.Open();
        var ctx = new CliContext(mgr, mgr.Select(gpuIndex, gpuId));
        var hw = ctx.Hw;
        var session = ctx.Session;

        if (read.Contains("--clock-domains"))
        {
            var topo = ClockDomainOffsets.ReadTopology(hw, session);
            Print(new
            {
                entries = topo.Select(t =>
                {
                    var c = ReadControl(hw, session, t);
                    return new
                    {
                        index = t.Index, type = t.Type, domain = $"0x{t.DomainId:x}", name = Enum.IsDefined((ClockDomainId)t.DomainId) ? ((ClockDomainId)t.DomainId).ToString().ToLowerInvariant() : null,
                        min_mhz = t.MinOffsetMhz, max_mhz = t.MaxOffsetMhz, adjustable = t.Adjustable, rail = t.Rail, caps = $"0x{t.Caps:x8}",
                        writable_here = Enum.IsDefined((ClockDomainId)t.DomainId) && ClockDomainOffsets.Writable((ClockDomainId)t.DomainId),
                        offset_khz = c?.OffsetKhz, vdemand_nvvdd_uv = c?.VdemandNvvddUv, vdemand_msvdd_uv = c?.VdemandMsvddUv, reserved = c == null ? null : new[] { c.Reserved0, c.Reserved1 },
                    };
                }),
            });
            return Done(out code, 0);
        }

        if (read.Contains("--boost-read"))
        {
            var o = BoostReader.Observe(hw, session);
            bool railsWritable = hw.Api.Resolve(Slot.VoltVoltRailsSetControl) != 0;
            Print(new
            {
                independent_percent = o.IndependentPercent, legacy_percent = o.LegacyPercent, nvvdd_kind = o.NvvddKind,
                default_rule_path = o.DefaultRule(railsWritable)?.ToString(),
                rails = o.Rails.Select(r => new { rail = r.Rail.ToString().ToLowerInvariant(), max_uv = r.MaxUv }),
            });
            return Done(out code, 0);
        }

        // Writes: the broker (or the elevated in-process dispatcher) performs them; this process only asks.
        if (prove is { } pp)
            return Done(out code, CliContext.Print(ctx.Perform(new BrokerRequest(BrokerRequest.OpProve, ctx.StableId, Kind: VoltBoost.Kind, Key: Key(pp)))));

        string[]? enable = allowDomainWrite ? [nameof(Slot.ClockClkDomainsSetControl)] : null;
        // The voltage first, then the clock it carries.
        var reqs = demands.Select(d => ctx.WriteRequest(ClockVdemand.Kind, ClockVdemand.Key(d.Domain), d.Uv.ToString(CultureInfo.InvariantCulture), takeOver, enableSlots: enable))
            .Concat(offsets.Select(o => ctx.WriteRequest(ClockDomainOffsets.Kind, ClockDomainOffsets.Key(o.Domain), o.Khz.ToString(CultureInfo.InvariantCulture), takeOver, enableSlots: enable))).ToList();
        if (boost is { } pct)
        {
            var path = boostPath ?? BoostReader.Observe(hw, session).DefaultRule(hw.Api.Resolve(Slot.VoltVoltRailsSetControl) != 0)
                ?? throw new InvalidOperationException("this GPU exposes no boost path");
            reqs.Add(ctx.WriteRequest(VoltBoost.Kind, Key(path), pct.ToString(CultureInfo.InvariantCulture), takeOver));
        }
        return Done(out code, ctx.PrintAll(reqs));
    }

    private static ClockControlEntry? ReadControl(NvClient hw, GpuSession gpu, ClockTopologyEntry topo)
    {
        var b = ClockDomainOffsets.Spec(topo.Index).ReadBuffer();
        if (!hw.Call(gpu, Slot.ClockClkDomainsGetControl, b).IsOk) return null;
        try { return ClockDomainsParser.ParseControl(b, topo.Index); }
        catch (InvalidDataException) { return null; }
    }

    private static bool Done(out int code, int value) { code = value; return true; }

    private static void Print(object o) => Console.WriteLine(JsonSerializer.Serialize(o, CliContext.Json));

    private static string Key(BoostPath p) => p.ToString().ToLowerInvariant();

    private static BoostPath Path(string s) => s.ToLowerInvariant() switch
    {
        "independent" => BoostPath.Independent,
        "legacy" => BoostPath.Legacy,
        _ => throw new ArgumentException("boost path is independent or legacy"),
    };

    private static int Khz(string s)
    {
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
            throw new ArgumentException($"'{s}' is not a number of megahertz.");
        var khz = decimal.Round(mhz * 1000m, 0, MidpointRounding.AwayFromZero);
        if (Math.Abs(khz) > 2_000_000_000m) throw new ArgumentException($"'{s}' MHz is out of range.");
        return (int)khz;
    }

    private static int Uv(string s)
    {
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv))
            throw new ArgumentException($"'{s}' is not a number of millivolts.");
        var uv = decimal.Round(mv * 1000m, 0, MidpointRounding.AwayFromZero);
        if (Math.Abs(uv) > 2_000_000_000m) throw new ArgumentException($"'{s}' mV is out of range.");
        return (int)uv;
    }
}
