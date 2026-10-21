using System.Globalization;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Core.Native;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Hw;

namespace Clocktail.Cli;

internal static class Program
{
    private static JsonSerializerOptions Json => CliContext.Json;

    private const string Usage = """

        Clocktail.exe [options]

        Read-only (no elevation required):
          --list-gpus              list adapters (index, name, stable id) as JSON
          --status                 JSON: identity, live telemetry and voltage rails
          --capabilities           JSON: what this driver and GPU expose (P1 probe)
          --rails                  JSON: voltage rails through the new reader (offsets, limits, domains)
          --pstates                JSON: P-state clock offsets (public Pstates20 layout)
          --power                  JSON: client power requests (FE ordinary / F8 additional, mW) and the board range, plus NVML cross-read
          --fans                   JSON: fan channels (mode, level, firmware [min,max], tach reading), raw blocks, NVML fan %
          --fan-policy             JSON: the firmware's own fan curves: per policy type, editable, three points (degC, duty %, rpm),
                                   and Status (temperatures, target rpm; inferred); the channels' modes and NVML temperature beside them
          --diagnose               JSON: GET-only sweep over every readable slot with a known size
          --clock-ratio            JSON: the XBAR/core clock propagation ratio and its control block's non-zero words (GET only)
          --probe-size SLOT        JSON: which structure sizes the driver accepts for a readable slot
            --probe-max HEX        up to this size (default 2000; PerfPerfLimits needs ~12200)
            --probe-rev N          only this structure version (default 1..4)
          --version                print version and exit
          --help                   this text

        Tuning (applied immediately; performed by Clocktail.Broker when one is running, else by this process if it is elevated;
        every write is read back and verified, holds the firmware-reported limits as its ceiling, and records the original):
          --rail RAIL:SLOT=mV[,SLOT+=mV]
                                   voltage-rail offsets: RAIL nvvdd|msvdd, SLOT rel|alt|ov|vmin, signed mV, slots not named stay;
                                   SLOT=mV sets the offset (stock MSVDD REL is already -50, so rel=-25 is a raise), SLOT+=mV adds
                                   to the current one; each slot is printed to stderr as before -> after (delta);
                                   a raised limit must stay under min(domain max, 1.15 V), and MAX is re-read after the write
          --allow-raise            with --rail: let an offset go up; without it a request that raises any slot is refused
          --core-offset MHz        P0 core clock offset through the transaction template (journal + sentinel)
          --memory-offset MHz      P0 memory clock offset, same path
          --power-limit W          FE (ordinary) watt request, bounded by the board's [min, max]; same path
          --power-cap W            F8 (additional) watt request, upper bound only; same path
          --allow-new-request      required when the request does not exist yet (F8 on the 5090): the driver cannot delete one
          --allow-power-write      accepted and ignored: the power setter is on since its live drill (2026-10-17)
          --fan ID=LEVEL|auto      fan channel ID (or 'all') to a fixed level inside the firmware [min,max], or back to the firmware; same path
          --fan-curve T:D,T:D,T:D|stock
                                   every editable firmware fan policy to three points (degC:duty%, e.g. 50:20,79:60,86:100; each point's
                                   rpm is the policy's own pairing for that duty, or name it as T:D:R), or back to the curve the card had
                                   before this program first wrote it; same path. Nothing stays running: the firmware runs the curve.
                                   Temperatures rise, duty <= 100 % and never falls; a channel in manual mode (--fan) overrides it
          --take-over              proceed despite a stale apply.lock from an application that did not complete
          --ownership              JSON: what the ownership journal says this process (or a dead one) still owes
          --profile show|keep|off|apply|load|preset:NAME
                                   the daily profile the broker holds: keep saves what the broker holds now; it goes on when the
                                   broker starts (GPU idle) and goes back on unchanged after a display driver reset; off suspends it
                                   (stock at the next start or reset); apply writes it unless suspended; load ends a suspension and
                                   writes it. Every change is an entry in tuning-log.md beside the profile
          --recover                give back what a dead process still owed (writes only what differs, through the broker or elevated), clear the sentinel

        Adapter selection:
          --gpu N                  select adapter N (default 0)
          --gpu-id ID              select the adapter with this stable id; wins over --gpu

        Rail arguments are offsets, not absolute voltages, with up to 0.001 mV precision. ALT is ALT/OP (Vop).
        Exit code 0 on success, 1 on CLI failure.
        """ + ClockCommands.Usage + VfCommands.Usage + SessionCommands.Usage + DumpCommands.Usage + PowerSplitCommands.Usage + CurrentLimitCommands.Usage;

    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException
                                      or DllNotFoundException or EntryPointNotFoundException or InvalidDataException
                                      or NvApiException or NvmlException or GuardViolationException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = e.Message }));
            return 1;
        }
    }

    private static int ParseMicrovolts(string s)
    {
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv))
            throw new ArgumentException($"'{s}' is not a number of millivolts.");
        var uv = decimal.Round(mv * 1000m, 0, MidpointRounding.AwayFromZero);
        if (Math.Abs(uv) > int.MaxValue) throw new ArgumentException($"'{s}' mV is out of range.");
        return (int)uv;
    }

    /// <summary>Watts with up to 3 decimals, at least 1 W, to mW.</summary>
    private static uint ParseMilliwatts(string text)
    {
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var w) || w < 1 || w > 10_000)
            throw new ArgumentException($"'{text}' is not a wattage between 1 and 10000.");
        if (decimal.Round(w, 3) != w) throw new ArgumentException("Wattage has at most 3 decimals.");
        return (uint)(w * 1000);
    }

    private static int ParseKilohertz(string s)
    {
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
            throw new ArgumentException($"'{s}' is not a number of megahertz.");
        var khz = decimal.Round(mhz * 1000m, 0, MidpointRounding.AwayFromZero);
        if (Math.Abs(khz) > 2_000_000_000m) throw new ArgumentException($"'{s}' MHz is out of range.");
        return (int)khz;
    }

    /// <summary>"nvvdd:rel=-5,vmin+=10" → rail nvvdd, REL set to −5000 µV and VMIN raised by 10000 µV (unnamed slots stay).</summary>
    private static (Clocktail.Domain.Rails.Rail Rail, List<RailEdit> Edits) ParseRail(string text)
    {
        var p = text.Split(':', 2);
        if (p.Length != 2 || !RailWrites.TryParseKey(p[0], out var rail))
            throw new ArgumentException($"--rail needs RAIL:SLOT=mV[,SLOT=mV] with RAIL nvvdd or msvdd, not '{text}'.");
        var parts = p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("--rail names at least one slot (rel, alt, ov, vmin).");
        var edits = parts.Select(part =>
        {
            var kv = part.Split('=', 2);
            var name = kv[0].Trim().ToLowerInvariant();
            bool relative = name.EndsWith('+');
            name = name.TrimEnd('+').Trim();
            if (kv.Length != 2 || !Enum.TryParse<RailSlot>(name, ignoreCase: true, out var slot) || !Enum.IsDefined(slot) || name.Any(char.IsDigit))
                throw new ArgumentException($"'{part}' is not SLOT=mV or SLOT+=mV (rel, alt, ov, vmin).");
            return new RailEdit(slot, ParseMicrovolts(kv[1]), relative);
        }).ToList();
        return (rail, edits);
    }

    /// <summary>
    /// Each --rail resolved against the rail's offsets now: shown as before → after on stderr, refused when it raises a
    /// slot without --allow-raise, and sent with absolute values for the named slots only.
    /// </summary>
    private static List<BrokerRequest> RailRequests(CliContext ctx, List<(Clocktail.Domain.Rails.Rail Rail, List<RailEdit> Edits)> rails, bool allowRaise, bool takeOver)
    {
        var now = RailsReader.Read(ctx.Hw, ctx.Session);
        var reqs = new List<BrokerRequest>();
        foreach (var (rail, edits) in rails)
        {
            var before = now.Rails.FirstOrDefault(r => r.Rail == rail)?.Offsets
                ?? throw new InvalidOperationException($"{RailWrites.Key(rail)} is not among this GPU's rails.");
            var after = RailEdits.Apply(before, edits);
            foreach (var line in RailEdits.Describe(before, after, edits))
                Console.Error.WriteLine($"{RailWrites.Key(rail)} {line}");
            if (RailEdits.Raised(before, after) is { Count: > 0 } up && !allowRaise)
                throw new ArgumentException($"{RailWrites.Key(rail)} {string.Join(", ", up.Select(s => s.ToString().ToLowerInvariant()))} would go up " +
                    "(SLOT=mV is the offset itself, not a change: use SLOT+=mV for a change); add --allow-raise if the raise is meant.");
            reqs.Add(ctx.WriteRequest(RailWrites.Kind, RailWrites.Key(rail), RailEdits.Encode(after, edits), takeOver));
        }
        return reqs;
    }

    /// <summary>The kind of write each option belongs to. One command line does one kind: each kind is dispatched on its
    /// own and the first one found returned, so a second kind given beside it was silently not written.</summary>
    private static readonly Dictionary<string, string> WriteGroup = new(StringComparer.Ordinal)
    {
        ["--power-limit"] = "power", ["--power-cap"] = "power",
        ["--core-offset"] = "P0 clock offsets", ["--memory-offset"] = "P0 clock offsets",
        ["--rail"] = "rails", ["--fan"] = "fans", ["--fan-curve"] = "fan curve", ["--profile"] = "profile", ["--recover"] = "recover",
        ["--xbar-offset"] = "clock domains", ["--xbar-vdemand"] = "clock domains", ["--sys-offset"] = "clock domains",
        ["--sys-vdemand"] = "clock domains", ["--video-offset"] = "clock domains", ["--video-vdemand"] = "clock domains", ["--boost"] = "voltage boost",
        ["--vf-offsets"] = "V/F curve", ["--vf-undervolt"] = "V/F curve", ["--current-limit"] = "current limits", ["--session"] = "session",
    };

    /// <summary>The kinds of write a command line asks for, in the order given.</summary>
    internal static IReadOnlyList<string> WriteGroups(string[] args) =>
        [.. args.Select(a => WriteGroup.GetValueOrDefault(a)).OfType<string>().Distinct()];

    private static int Run(string[] args)
    {
        if (WriteGroups(args) is { Count: > 1 } groups)
            throw new ArgumentException($"one kind of write per command, and this one asks for {string.Join(", ", groups)}: run them one after another, or put them in one --session file.");
        if (ClockCommands.TryRun(args, out var clockCode)) return clockCode;
        if (VfCommands.TryRun(args, out var vfCode)) return vfCode;
        if (SessionCommands.TryRun(args, out var sessionCode)) return sessionCode;
        if (DumpCommands.TryRun(args, out var dumpCode)) return dumpCode;
        if (PowerSplitCommands.TryRun(args, out var splitCode)) return splitCode;
        if (CurrentLimitCommands.TryRun(args, out var limitCode)) return limitCode;

        var cmd = new HashSet<string>();
        int? gpuIndex = null; string? gpuId = null;
        var rails = new List<(Clocktail.Domain.Rails.Rail Rail, List<RailEdit> Edits)>();
        string? probeSize = null;
        int probeMax = 0x2000, probeMinRev = 1, probeMaxRev = 4;
        var clocks = new List<(ClockDomain Domain, int Khz)>();
        var power = new List<(PowerRequestKind Kind, uint Mw)>();
        var fans = new List<(uint? Id, FanState State)>();
        string? fanCurve = null;
        bool takeOver = false, allowCreate = false, allowRaise = false;
        string? profileVerb = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu":
                    if (!int.TryParse(Next(), out var n)) throw new ArgumentException("--gpu needs an integer.");
                    gpuIndex = n; break;
                case "--gpu-id": gpuId = Next(); break;
                case "--rail": rails.Add(ParseRail(Next())); break;
                case "--fan": fans.Add(FanCommands.ParseRequest(Next())); break;
                case "--fan-curve": fanCurve = FanCommands.ParseCurve(Next()); break;
                case "--probe-size": probeSize = Next(); break;
                case "--probe-max":
                    // A structure larger than 0x2000 (PerfPerfLimits' names run to ~0x12200): every size up to it, 4 bytes apart.
                    probeMax = int.TryParse(Next().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pm) && pm is >= 8 and <= 0x40000
                        ? pm : throw new ArgumentException("--probe-max is a hex size, 8..40000."); break;
                case "--probe-rev":
                    probeMinRev = probeMaxRev = int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var pr) && pr is >= 1 and <= 15
                        ? pr : throw new ArgumentException("--probe-rev is a version 1..15."); break;
                case "--core-offset": clocks.Add((ClockDomain.Core, ParseKilohertz(Next()))); break;
                case "--memory-offset": clocks.Add((ClockDomain.Memory, ParseKilohertz(Next()))); break;
                case "--take-over": takeOver = true; break;
                case "--power-limit": power.Add((PowerRequestKind.Ordinary, ParseMilliwatts(Next()))); break;
                case "--power-cap": power.Add((PowerRequestKind.Additional, ParseMilliwatts(Next()))); break;
                case "--allow-new-request": allowCreate = true; break;
                case "--allow-raise": allowRaise = true; break;
                case "--profile": profileVerb = Next(); break;
                case "--allow-power-write": break;   // scripts written before the power setter was enabled keep working
                case "--version" or "--help" or "--list-gpus" or "--status" or "--probe-volt" or "--probe-rails"
                    or "--capabilities" or "--rails" or "--pstates" or "--power" or "--fans" or "--fan-policy" or "--diagnose" or "--ownership" or "--recover"
                    or "--clock-ratio":
                    cmd.Add(args[i]); break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        bool nothingAsked = profileVerb == null && cmd.Count == 0 && rails.Count == 0 && probeSize == null && clocks.Count == 0 && power.Count == 0 && fans.Count == 0 && fanCurve == null;
        if (nothingAsked) { Console.WriteLine(Usage); return 0; }
        if (cmd.Contains("--help")) { Console.WriteLine(Usage); return 0; }
        if (cmd.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            return 0;
        }

        using var mgr = GpuManager.Open();

        if (cmd.Contains("--list-gpus"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                driver = new { version = mgr.Driver.Version, branch = mgr.Driver.Branch },
                gpus = mgr.Gpus.Select(g => new
                {
                    index = g.Identity.Index, name = g.Identity.Name, stable_id = g.Identity.StableId,
                    vbios = g.Identity.Vbios, has_nvml = g.HasNvml,
                }),
            }, Json));
            return 0;
        }

        var gpu = mgr.Select(gpuIndex, gpuId);
        var ctx = new CliContext(mgr, gpu);
        var hw = ctx.Hw;
        var session = ctx.Session;

        if (cmd.Contains("--capabilities"))
        {
            var c = Capabilities.Probe(hw, session);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                gpu = gpu.Identity.Name,
                volt_read = c.VoltRead, volt_write = c.VoltWrite, boost = c.Boost.ToString(),
                pstates = c.Pstates, pstates_write = c.PstatesWrite,
                clock_domains = c.ClockDomains, propagation_ratio = c.PropagationRatio, boost_lock = c.BoostLock,
                power_percent = c.PowerPercent, power_policy = c.PowerPolicy, pwr_policies = c.PwrPolicies,
                thermal_input = c.ThermalInput, fan_coolers = c.FanCoolers, vf_points = c.VfPoints, domain_vf = c.DomainVf,
                missing_slots = c.Presence.Where(kv => !kv.Value).Select(kv => kv.Key.ToString()).ToArray(),
            }, Json));
            return 0;
        }

        if (cmd.Contains("--rails"))
        {
            var s = RailsReader.Read(hw, session);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mask = $"0x{s.Mask:x}", legacy_boost_percent = s.LegacyBoostPercent,
                rails = s.Rails.Select(r => new
                {
                    rail = r.Rail.ToString().ToLowerInvariant(), ordinal = r.Ordinal, kind = r.Kind, type = r.Type,
                    domain_index = r.DomainIndex, domain_mask = $"0x{r.DomainMask:x}",
                    offsets_uv = r.Offsets, limits_uv = r.Limits, domain_uv = r.Domain,
                    ceiling_uv = new { standard = RailRules.Ceiling(r, false), xoc = RailRules.Ceiling(r, true) }, floor_uv = RailRules.Floor(r),
                    writable = r.Writable, not_writable_reason = r.NotWritableReason,
                }),
            }, Json));
            return 0;
        }

        if (cmd.Contains("--pstates"))
        {
            var s = Pstates20Reader.Read(hw, session);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                editable = s.Editable, pstates = s.NumPstates, clocks_per_pstate = s.NumClocks, base_voltages = s.NumBaseVoltages,
                entries = s.Clocks.Select(c => new
                {
                    pstate = $"P{c.PstateId}", domain = c.DomainId switch { 0 => "core", 4 => "memory", var d => $"domain{d}" },
                    type = c.TypeId, editable = c.Editable, delta_khz = c.DeltaKhz, min_khz = c.MinKhz, max_khz = c.MaxKhz, freq_khz = c.FreqKhz, freq_max_khz = c.FreqMaxKhz,
                }),
            }, Json));
            return 0;
        }

        if (cmd.Contains("--power"))
        {
            var s = PowerRequestsReader.Read(hw, session);
            var nvml = mgr.Nvml != null && gpu.HasNvml ? Telemetry.Read(gpu, mgr.Nvml) : null;
            object Req(PowerRequest r) => new { command = $"0x{(uint)r.Kind:x}", present = r.Present, mw = r.Mw, raw = $"0x{r.RawValue:x8}", value_offset = $"0x{r.ValueOffset:x}" };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                board = s.Board.Board, flag = s.Board.Flag, directly_readable = s.Board.DirectlyReadable,
                min_mw = s.Board.MinMw, default_mw = s.Board.DefaultMw, max_mw = s.Board.MaxMw,
                ordinary_fe = Req(s.Ordinary), additional_f8 = Req(s.Additional), cap_mw = s.CapMw,
                nvml = nvml == null ? null : new { requested_w = nvml.PowerLimitWatts, enforced_w = nvml.PowerEnforcedWatts },
                info_head = Convert.ToHexString(s.InfoRaw.AsSpan(0, 0x28)),
                board_info_entry = Convert.ToHexString(s.InfoRaw.AsSpan(PowerRequestsLayout.InfoEntry(s.Board.Board), PowerRequestsLayout.InfoStride)),
            }, Json));
            return 0;
        }

        if (cmd.Contains("--fans")) return FanCommands.Read(ctx);
        if (cmd.Contains("--fan-policy")) return FanCommands.ReadPolicy(ctx);

        if (cmd.Contains("--clock-ratio"))
        {
            var cr = Clocktail.Domain.Clocks.ClockRatio.Read(hw, session);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                relations = $"0x{cr.Relations:x}", ratio = cr.Ratio, raw = $"0x{cr.Raw:x}",
                non_zero = cr.NonZero.ToDictionary(kv => $"+0x{kv.Key:x}", kv => $"0x{kv.Value:x}"),
            }, Json));
            return 0;
        }

        if (cmd.Contains("--diagnose"))
        {
            var probes = Diagnostics.GetSweep(hw, session);
            Console.WriteLine(JsonSerializer.Serialize(probes.Select(p => new
            {
                slot = p.Slot.ToString(), id = $"0x{p.Id:x8}", shape = p.Shape, outcome = p.Outcome.ToString(), status = p.Status,
                version_echoed = p.VersionEchoed, non_zero_dwords = p.NonZeroDwords, head = p.Head,
            }), Json));
            return 0;
        }

        // Every privileged op is one broker request through the context: a running broker performs it, else the
        // identical dispatcher runs here and says what an unelevated process may do.
        if (cmd.Contains("--ownership"))
            return CliContext.Print(ctx.Perform(new BrokerRequest(BrokerRequest.OpOwnership, ctx.StableId)));

        if (profileVerb != null)
        {
            // "preset:NAME" loads a shipped preset as the daily profile.
            var (verb, value) = profileVerb.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) ? ("preset", profileVerb[7..]) : (profileVerb, (string?)null);
            return CliContext.Print(ctx.Perform(new BrokerRequest(BrokerRequest.OpProfile, ctx.StableId, Kind: verb, Value: value)));
        }

        if (cmd.Contains("--recover"))
            return CliContext.Print(ctx.Perform(new BrokerRequest(BrokerRequest.OpRecover, ctx.StableId)));

        if (power.Count > 0)
            return ctx.PrintAll(power.Select(p => new BrokerRequest(BrokerRequest.OpPower, ctx.StableId, Kind: PowerCap.Key(p.Kind), Mw: p.Mw, AllowCreate: allowCreate, TakeOver: takeOver)));

        if (clocks.Count > 0)
            return ctx.PrintAll(clocks.Select(c => new BrokerRequest(BrokerRequest.OpClock, ctx.StableId, Domain: c.Domain.ToString().ToLowerInvariant(), Khz: c.Khz, TakeOver: takeOver)));

        if (rails.Count > 0)
            return ctx.PrintAll(RailRequests(ctx, rails, allowRaise, takeOver));

        if (fans.Count > 0)
            return FanCommands.Apply(ctx, fans, takeOver);

        if (fanCurve != null)
            return FanCommands.ApplyCurve(ctx, fanCurve, takeOver);

        if (probeSize != null)
        {
            if (!Enum.TryParse<Slot>(probeSize, ignoreCase: true, out var slot))
                throw new ArgumentException($"'{probeSize}' is not a slot name; see nvapi_slots.csv.");
            var probes = Diagnostics.ProbeSize(hw, session, slot, probeMax, probeMinRev, probeMaxRev);
            Console.WriteLine(JsonSerializer.Serialize(probes.Select(p => new
            {
                slot = p.Slot.ToString(), rev = p.Rev, accepted_sizes = p.AcceptedSizes.Select(x => $"0x{x:x}").ToArray(), tried = p.Tried, guard_broken = p.GuardBroken,
            }), Json));
            return 0;
        }

        if (cmd.Contains("--probe-rails"))
        {
            // Read-only: ask the driver about every possible rail bit, one at a time and all together.
            foreach (var m in Enumerable.Range(0, 32).Select(b => 1u << b).Append(0xFFFFFFFFu))
            {
                var c = VoltProbe.Control(mgr.NvApi, gpu.NvapiHandle, m);
                var s = VoltProbe.Status(mgr.NvApi, gpu.NvapiHandle, m);
                uint cm = c.Data == null ? 0 : BitConverter.ToUInt32(c.Data, 4);
                uint sm = s.Data == null ? 0 : BitConverter.ToUInt32(s.Data, 4);
                Console.WriteLine($"mask=0x{m:x8} control: status={c.Status} echoed=0x{cm:x}  status: status={s.Status} echoed=0x{sm:x}");
            }
            return 0;
        }

        if (cmd.Contains("--probe-volt"))
        {
            var info = VoltProbe.Info(mgr.NvApi, gpu.NvapiHandle);
            Console.WriteLine($"{info.Name} id=0x{info.Id:x8} status={info.Status}");
            if (info.Data == null) return 0;
            foreach (var l in VoltProbe.NonZeroDwords(info.Data)) Console.WriteLine("  " + l);
            uint mask = BitConverter.ToUInt32(info.Data, 4);
            foreach (var r in new[] { VoltProbe.Control(mgr.NvApi, gpu.NvapiHandle, mask), VoltProbe.Status(mgr.NvApi, gpu.NvapiHandle, mask) })
            {
                Console.WriteLine($"{r.Name} id=0x{r.Id:x8} status={r.Status}");
                if (r.Data == null) continue;
                foreach (var l in VoltProbe.NonZeroDwords(r.Data)) Console.WriteLine("  " + l);
            }
            return 0;
        }

        var t = mgr.Nvml != null && gpu.HasNvml ? Telemetry.Read(gpu, mgr.Nvml) : null;
        object? volt = null;
        try
        {
            var v = VoltRails.Read(mgr.NvApi, gpu.NvapiHandle);
            volt = v.Rails.Select(r => new { rail = r.Rail.ToString().ToLowerInvariant(), kind = r.ControlKind, offsets_uv = r.Offsets, limits_uv = r.Limits });
        }
        catch (Exception e) when (e is NvApiException or NotSupportedException or InvalidDataException)
        {
            volt = new { unavailable = e.Message };
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            gpu = new
            {
                index = gpu.Identity.Index, name = gpu.Identity.Name, stable_id = gpu.Identity.StableId,
                vbios = gpu.Identity.Vbios,
                pci = new
                {
                    device_id = $"0x{gpu.Identity.DeviceId:x8}", subsystem_id = $"0x{gpu.Identity.SubSystemId:x8}",
                    revision = gpu.Identity.RevisionId, bus = gpu.Identity.Bus, slot = gpu.Identity.Slot,
                },
            },
            telemetry = t,
            voltage_rails = volt,
        }, Json));
        return 0;
    }
}
