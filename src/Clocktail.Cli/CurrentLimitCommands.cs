using System.Globalization;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Power;
using Clocktail.Hw;
using Rail = Clocktail.Domain.Rails.Rail;

namespace Clocktail.Cli;

/// <summary>
/// The rails' current limits (NVVDD/MSVDD OCP) and the power monitor that measures each rail's output.
/// Reading takes no write window; a write goes through the write path like every other cell (journal, readback,
/// rollback), with the setter enabled for that request only.
/// </summary>
internal static class CurrentLimitCommands
{
    public const string Usage = """

        Rail current limits (OCP; the raw policy table, read live 2026-10-19):
          --current-limits         JSON: NVVDD/MSVDD current limit (in force, default, driver range) and every power-monitor channel
          --current-limit RAIL=A   set a rail's current limit in amps (RAIL nvvdd|msvdd; 'default' puts the VBIOS default back);
                                   never above the default. A lower limit caps that rail's power, below the 400 W the board
                                   request stops at; the journal records the original and recovery gives it back
        """;

    public static readonly string[] Setter = [nameof(Slot.PowerPolicySetControl)];

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (!args.Contains("--current-limits") && !args.Contains("--current-limit")) return false;

        int? gpuIndex = null; string? gpuId = null; bool takeOver = false;
        var writes = new List<(Rail Rail, string Amps)>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu": gpuIndex = int.TryParse(Next(), out var n) ? n : throw new ArgumentException("--gpu needs an integer."); break;
                case "--gpu-id": gpuId = Next(); break;
                case "--take-over": takeOver = true; break;
                case "--current-limits": break;
                case "--current-limit":
                {
                    var kv = Next().Split('=', 2);
                    if (kv.Length != 2) throw new ArgumentException("--current-limit needs RAIL=AMPS (nvvdd=350) or RAIL=default.");
                    writes.Add((CurrentLimitWrites.ParseKey(kv[0].Trim()), kv[1].Trim()));
                    break;
                }
                default: throw new ArgumentException($"'{args[i]}' cannot be combined with --current-limit(s)");
            }
        }

        using var mgr = GpuManager.Open();
        var ctx = new CliContext(mgr, mgr.Select(gpuIndex, gpuId));
        var limits = CurrentLimitsReader.Read(ctx.Hw, ctx.Session);
        if (writes.Count == 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(Describe(limits, CurrentLimitsReader.Monitor(ctx.Hw, ctx.Session, limits.Channels)), CliContext.Json));
            return true;
        }

        bool ok = true;
        foreach (var (rail, amps) in writes)
        {
            var limit = limits.Get(rail) ?? throw new InvalidOperationException($"{rail} has no current limit here: {limits.Missing.GetValueOrDefault(rail)}");
            uint ma = amps.Equals("default", StringComparison.OrdinalIgnoreCase) ? limit.DefaultMa
                : decimal.TryParse(amps, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a) && a > 0 && a < 2_000_000
                    ? (uint)decimal.Round(a * 1000m, 0, MidpointRounding.AwayFromZero)
                    : throw new ArgumentException($"'{amps}' is not a number of amps (or 'default').");
            Console.Error.WriteLine($"{CurrentLimitWrites.Key(rail)} {limit.LimitMa / 1000.0:0.###} A -> {ma / 1000.0:0.###} A (default {limit.DefaultMa / 1000.0:0.###} A)");
            var r = ctx.Perform(ctx.WriteRequest(CurrentLimitWrites.Kind, CurrentLimitWrites.Key(rail), ma.ToString(CultureInfo.InvariantCulture), takeOver, enableSlots: Setter));
            Console.WriteLine(r.Result is { } res ? res.GetRawText() : JsonSerializer.Serialize(new { success = false, error = r.Error, code = r.Code }));
            ok &= r.Ok;
        }
        code = ok ? 0 : 1;
        return true;
    }

    public static object Describe(CurrentLimits limits, IReadOnlyList<MonitorReading> readings)
    {
        object Rail(Rail r)
        {
            var l = limits.Get(r);
            var o = limits.Output(r, readings);
            return new
            {
                limit_a = l?.LimitMa / 1000.0,
                default_a = l?.DefaultMa / 1000.0,
                driver_min_a = l?.MinMa / 1000.0,
                driver_max_a = l?.MaxMa / 1000.0,
                record = l?.Record,
                monitor_channel = l?.Channel,
                missing = limits.Missing.GetValueOrDefault(r),
                now = o == null ? null : new { mv = o.Uv / 1000.0, a = o.Ma / 1000.0, w = o.Mw / 1000.0 },
            };
        }
        return new
        {
            nvvdd = Rail(Clocktail.Domain.Rails.Rail.Nvvdd),
            msvdd = Rail(Clocktail.Domain.Rails.Rail.Msvdd),
            monitor = readings.Select(x => new
            {
                channel = x.Channel, type = $"0x{x.Type:x}", name = PowerMonitorLayout.Name(x.Type),
                w = x.Mw / 1000.0, a = x.Ma < 0 ? (double?)null : x.Ma / 1000.0, v = x.Uv == 0 ? (double?)null : x.Uv / 1e6,
            }),
        };
    }
}
