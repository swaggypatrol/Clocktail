using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Power;
using Clocktail.Domain.Vf;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

public sealed partial class TuningTools
{
    private readonly AsyncLocal<TuningPolicySnapshot?> _policyCall = new();
    private TuningPolicySnapshot Limits => _policyCall.Value ?? throw new InvalidOperationException("No explicit policy evaluation is in progress");

    internal static TuningScope? ReadPolicyScope(Card card)
    {
        if (card.DriverGone() != null) return null;
        var r = card.Broker(BrokerRequest.OpExperiment, "policy-scope");
        if (!r.Ok || r.Result is not { ValueKind: JsonValueKind.Object } a
            || !a.TryGetProperty("gpu", out var gpu) || gpu.ValueKind != JsonValueKind.String || gpu.GetString() != card.StableId
            || !a.TryGetProperty("generation", out var generation) || generation.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(generation.GetString())
            || !a.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(session.GetString())) return null;
        string game = a.TryGetProperty("game", out var gameValue) && gameValue.ValueKind == JsonValueKind.String
            ? gameValue.GetString() ?? "" : "";
        return new(card.StableId, session.GetString()!, generation.GetString()!, game);
    }

    private string PolicyGuard(Func<string> call) => Guard(() =>
    {
        lock (tuningPolicy.Gate)
        {
            _policyCall.Value = tuningPolicy.Snapshot(ReadPolicyScope(card));
            try { return call(); }
            catch (Exception e) when (e is InvalidDataException or Clocktail.Hw.NvApiException or Clocktail.Hw.NvmlException
                || e is InvalidOperationException and not TuningPolicyException)
            { throw new TuningPolicyException("device_limit", e.Message); }
            finally { _policyCall.Value = null; }
        }
    });

    // Expected scope is compared by the broker under the same lock as crash/session/recovery changes and the write.
    private BrokerAnswer PolicyTrial(string json)
    {
        if (_policyCall.Value is not { } p) return card.Broker(BrokerRequest.OpExperiment, "try", json);
        var current = tuningPolicy.Snapshot(ReadPolicyScope(card));
        if (p.Scope != current.Scope || p.Approved != current.Approved)
            throw new TuningPolicyException("consent_required", "Policy approval expired before the write; call set_tuning_policy explicitly again");
        var args = JsonNode.Parse(json)!.AsObject();
        var context = args["context"] as JsonArray ?? new JsonArray();
        if (args["context"] == null) args["context"] = context;
        context.Add("MCP tuning policy: " + JsonSerializer.Serialize(p.Describe()));
        var result = card.Broker(BrokerRequest.OpExperiment, p.Scope == null ? "try" : BrokerRequest.ScopedTrial,
            args.ToJsonString(), p.Scope?.Generation);
        if (p.Scope != null && !result.Ok
            && result.Code is "consent_required" or BrokerCode.BadRequest or BrokerCode.BrokerUnreachable or "no_answer")
            tuningPolicy.Revoke("scoped broker request refused or broker connection changed");
        return result;
    }

    private void CheckCurveDevice(Card.Look look, int[] next)
    {
        var limits = VfLimits.From(card.WithDriver(Pstates20Reader.Read));
        if (limits.MinKhz > limits.MaxKhz)
            throw new TuningPolicyException("device_limit", "The core offset range is inverted; read diagnostic_report");
        if (VfRules.Check(look.Curve, limits, look.Curve.Offsets(), next, true) is { } why)
            throw new TuningPolicyException("device_limit", why);
    }

    private void CheckDaily(int[] next, int[] daily)
    {
        for (int i = 0; i < next.Length; i++)
            Limits.Ceiling("MAX_ABOVE_DAILY_MHZ", ((long)next[i] - daily.ElementAtOrDefault(i)) / 1000.0);
    }

    private static object PolicyCapabilities(Card card)
    {
        object Read(Func<object> f)
        {
            try { return f(); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            { return new { error = e.Message }; }
        }
        return new
        {
            gpu = card.Name,
            core_memory = Read(() => card.WithDriver(Pstates20Reader.Read).Clocks.Where(p => p.PstateId == 0)
                .Select(p => new { domain = p.DomainId, editable = p.Editable, min_mhz = p.MinKhz / 1000.0, max_mhz = p.MaxKhz / 1000.0 }).ToArray()),
            xbar = Read(() => ClockDomainsParser.Find(card.WithDriver(ClockDomainOffsets.ReadTopology), (uint)ClockDomainId.Xbar)),
            power = Read(() => card.WithDriver(PowerRequestsReader.ReadBoard)),
            curve = Read(() =>
            {
                var look = card.Read();
                return new { points = look.Curve.Points.Select(p => new { p.Index, mv = p.VoltageUv / 1000.0 }).ToArray(),
                    nvvdd_max_mv = look.NvvddMaxUv / 1000.0, shift_available = look.NvvddMaxUv is > 0 };
            }),
        };
    }

    // Consent is a decision summary, not a diagnostic dump. Keep the full structures and curve in reports.
    internal static string PolicyConsentSummary(Card card)
    {
        string Read(Func<string> read)
        {
            try { return read(); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            { return "unavailable (see card_settings)"; }
        }
        static string Range(double min, double max, string unit) =>
            string.Create(CultureInfo.InvariantCulture, $"{min:0.###}..{max:0.###} {unit}");
        string Offset(ClockDomain domain) => Read(() =>
        {
            var p = card.WithDriver(Pstates20Reader.Read).P0(domain)
                ?? throw new InvalidOperationException("P0 bounds unavailable");
            TuningValues.DeviceRange(p.MinKhz, p.Editable ? p.MinKhz : null,
                p.Editable ? p.MaxKhz : null, "P0 offset");
            return Range(p.MinKhz / 1000.0, p.MaxKhz / 1000.0, "MHz");
        });
        string power = Read(() =>
        {
            var p = card.WithDriver(PowerRequestsReader.ReadBoard);
            return Range(p.MinMw / 1000.0, p.MaxMw / 1000.0, "W");
        });
        string xbar = Read(() =>
        {
            var p = ClockDomainsParser.Find(card.WithDriver(ClockDomainOffsets.ReadTopology), (uint)ClockDomainId.Xbar);
            TuningValues.DeviceRange(p.MinOffsetMhz, p.Adjustable ? p.MinOffsetMhz : null,
                p.Adjustable ? p.MaxOffsetMhz : null, "XBAR offset");
            return Range(p.MinOffsetMhz, p.MaxOffsetMhz, "MHz");
        });
        return $"GPU: {card.Name}\nPower limit: {power}\nCore offset: {Offset(ClockDomain.Core)}\n" +
            $"Memory offset: {Offset(ClockDomain.Memory)}\nXBAR offset: {xbar}";
    }

    /// <summary>The core P0 interval consistent with the domain's existing relative global-plus-point budget.</summary>
    internal static (double Min, double Max) CoreBudgetRange(ClockOffset core, VfBudget budget)
    {
        long min = core.MinKhz, max = core.MaxKhz;
        if (budget.Offsets == null)
        {
            min = Math.Max(min, Math.Min(0, core.DeltaKhz));
            max = Math.Min(max, Math.Max(0, core.DeltaKhz));
        }
        else foreach (var (_, offset) in budget.Offsets)
        {
            if (offset == 0) continue;
            long total = (long)core.DeltaKhz + offset;
            long outside = Math.Max(0, Math.Max(core.MinKhz - total, total - core.MaxKhz));
            min = Math.Max(min, (long)core.MinKhz - outside - offset);
            max = Math.Min(max, (long)core.MaxKhz + outside - offset);
        }
        return (min / 1000.0, max / 1000.0);
    }

    internal static object EffectivePolicyRanges(Card card, TuningPolicySnapshot p)
    {
        object Read(Func<object> f)
        {
            try { return f(); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            { return new { available = false, error = e.Message }; }
        }
        object Offset(ClockDomain domain, string stepKey)
        {
            var snap = card.WithDriver(Pstates20Reader.Read);
            var c = snap.P0(domain) ?? throw new InvalidOperationException("P0 offset bounds unreadable");
            TuningValues.DeviceRange(c.MinKhz, c.MinKhz, c.MaxKhz, "P0 offset");
            double min = c.MinKhz / 1000.0, max = c.MaxKhz / 1000.0;
            VfBudget? budget = null;
            if (domain == ClockDomain.Core)
            {
                budget = card.WithDriver(VfBudget.Read);
                (min, max) = CoreBudgetRange(c, budget);
            }
            if (p.Bound(stepKey) is { } step)
            {
                min = Math.Max(min, c.DeltaKhz / 1000.0 - step);
                max = Math.Min(max, c.DeltaKhz / 1000.0 + step);
            }
            if (domain == ClockDomain.Core && p.Bound("MAX_ABOVE_DAILY_MHZ") is { } allowance)
            {
                int daily = 0;
                var r = card.Broker(BrokerRequest.OpProfile, "show");
                if (r.Ok && r.Result is { } a && a.TryGetProperty("profile", out var profile)
                    && profile.ValueKind == JsonValueKind.Object && !profile.GetProperty("Suspended").GetBoolean())
                    foreach (var w in profile.GetProperty("Writes").EnumerateArray())
                        if (w.GetProperty("Kind").GetString() == ClockOffsets.Kind && w.GetProperty("Key").GetString() == ClockOffsets.Key(domain))
                            int.TryParse(w.GetProperty("Value").GetString(), System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out daily);
                max = Math.Min(max, daily / 1000.0 + allowance);
            }
            return new { min_mhz = min, max_mhz = max, available = snap.Editable && c.Editable && min <= max,
                point_budget_readable = budget == null ? (bool?)null : budget.Offsets != null };
        }
        return new
        {
            core_offset = Read(() => Offset(ClockDomain.Core, "STEP_MHZ")),
            memory_offset = Read(() => Offset(ClockDomain.Memory, "MEMORY_STEP_MHZ")),
            xbar_offset = Read(() =>
            {
                var t = ClockDomainsParser.Find(card.WithDriver(ClockDomainOffsets.ReadTopology), (uint)ClockDomainId.Xbar);
                double min = t.MinOffsetMhz, max = t.MaxOffsetMhz;
                var (now, _) = SettingsTools.Domain(card, ClockDomainId.Xbar);
                if (p.Bound("XBAR_STEP_MHZ") is { } step)
                {
                    min = Math.Max(min, now / 1000.0 - step);
                    max = Math.Min(max, now / 1000.0 + step);
                }
                if (p.Bound("MAX_XBAR_MHZ") is { } cap) max = Math.Min(max, cap);
                return new { min_mhz = min, max_mhz = max, available = t.Adjustable && min <= max,
                    demand_heuristic = p.Device ? "reference-card advisory" : "conservative veto" };
            }),
            ordinary_trial_power = Read(() =>
            {
                var b = card.WithDriver(PowerRequestsReader.ReadBoard);
                double min = Math.Max(b.MinMw / 1000.0, p.Bound("MIN_POWER_W") ?? 0);
                double max = Math.Min(b.MaxMw / 1000.0, p.Bound("MAX_POWER_W") ?? double.PositiveInfinity);
                return new { min_w = min, max_w = max, available = min <= max };
            }),
            curve = Read(() =>
            {
                var b = VfLimits.From(card.WithDriver(Pstates20Reader.Read));
                if (b.MinKhz > b.MaxKhz) throw new InvalidDataException("The core offset range is inverted");
                return new { point_min_mhz = b.LowestKhz / 1000.0, point_max_mhz = b.HighestKhz / 1000.0,
                    explicit_window_min_mv = p.Bound("MIN_MV"), implicit_window_min_mv = p.Bound("MIN_MV") ?? 850,
                    daily_allowance_mhz = p.Bound("MAX_ABOVE_DAILY_MHZ"), adjust_step_mhz = p.Bound("STEP_MHZ"),
                    shift_step_mv = p.Bound("SHIFT_STEP_MV") };
            }),
        };
    }

    internal static object PolicyReport(Card card, TuningPolicy? state)
    {
        if (state == null) return new { effective_policy = "conservative", approval = "none" };
        lock (state.Gate)
        {
            var snapshot = state.Snapshot(ReadPolicyScope(card));
            return new { status = snapshot.Describe(), device_ranges = PolicyCapabilities(card),
                effective_ranges = EffectivePolicyRanges(card, snapshot) };
        }
    }

    [McpServerTool(Name = "set_tuning_policy", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Select conservative, device, or overrides for explicit tuning. Clocktail asks the person itself before approving " +
        "device mode or configured legacy relaxations, with mandatory confirmation even when CLOCKTAIL_CONFIRM=off. " +
        "overrides approves only the configured relaxations listed in the prompt; device must be explicitly selected. " +
        "An active session is required. conservative revokes approval immediately without prompting. " +
        "Approval expires on session/GPU/recovery changes, crash or worker restart. Performs no GPU writes or workloads. " +
        "Automatic tuners, F8 and daily power limits keep their existing policies.")]
    public async Task<string> SetTuningPolicy(McpServer server,
        [Description("conservative: revoke; overrides: approve configured relaxations only; device: approve device limits.")] string policy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await tuningPolicy.Approve(policy.Trim().ToLowerInvariant(), () => ReadPolicyScope(card),
                () => PolicyConsentSummary(card),
                (question, ct) => Consent.AskMandatory(server, question, ct), cancellationToken);
            return Out(new { success = true, gpu_writes = 0, tuning_policy = PolicyReport(card, tuningPolicy) });
        }
        catch (OperationCanceledException) { return Out(new { success = false, code = "consent_required", error = "Confirmation cancelled; no approval granted" }); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        { return Out(new { success = false, code = (e as TuningPolicyException)?.Code ?? "policy_limit", error = e.Message }); }
    }
}
