using System.Collections.ObjectModel;
using System.Globalization;

namespace Clocktail.Mcp;

/// <summary>A startup snapshot. Explicit bounds remain restrictions even after device consent.</summary>
public sealed class TuningPolicySettings
{
    public const string Variable = "CLOCKTAIL_MCP_TUNING_POLICY";
    public static readonly IReadOnlyDictionary<string, int?> Defaults = new ReadOnlyDictionary<string, int?>(new Dictionary<string, int?>
    {
        ["STEP_MHZ"] = 15, ["MEMORY_STEP_MHZ"] = 250, ["XBAR_STEP_MHZ"] = 50,
        ["SHIFT_STEP_MV"] = 25, ["MAX_ABOVE_DAILY_MHZ"] = 200, ["MIN_MV"] = 850,
        ["MIN_POWER_W"] = 400, ["MAX_POWER_W"] = 600, ["MAX_XBAR_MHZ"] = null,
    });
    public string Requested { get; }
    public IReadOnlyDictionary<string, int> Configured { get; }
    public IReadOnlyDictionary<string, int> Relaxations { get; }

    public TuningPolicySettings(Func<string, string?> read)
    {
        Requested = read(Variable)?.Trim().ToLowerInvariant() ?? "conservative";
        if (Requested is not ("conservative" or "device"))
            throw new ArgumentException($"{Variable} must be conservative or device");
        var configured = new Dictionary<string, int>();
        foreach (var key in Defaults.Keys)
        {
            var raw = read("CLOCKTAIL_MCP_" + key);
            if (raw == null) continue;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0
                || key.Contains("STEP") && v == 0)
                throw new ArgumentException($"CLOCKTAIL_MCP_{key} must be a nonnegative integer (steps must be positive)");
            configured[key] = v;
        }
        if (configured.GetValueOrDefault("MIN_POWER_W", 0) > configured.GetValueOrDefault("MAX_POWER_W", int.MaxValue))
            throw new ArgumentException("CLOCKTAIL_MCP_MIN_POWER_W exceeds CLOCKTAIL_MCP_MAX_POWER_W");
        Configured = new ReadOnlyDictionary<string, int>(configured);
        Relaxations = new ReadOnlyDictionary<string, int>(configured.Where(p =>
            Defaults[p.Key] is { } d && (IsFloor(p.Key) ? p.Value < d : p.Value > d)).ToDictionary());
    }

    public static bool IsFloor(string key) => key is "MIN_MV" or "MIN_POWER_W";
    public int? Bound(string key, string approved)
    {
        int? baseline = Defaults[key];
        if (approved == "device") return Configured.TryGetValue(key, out int explicitValue) ? explicitValue : null;
        if (!Configured.TryGetValue(key, out int value)) return baseline;
        if (approved == "overrides" || baseline == null) return value;
        return IsFloor(key) ? Math.Max(value, baseline.Value) : Math.Min(value, baseline.Value);
    }
}

public sealed class TuningPolicyException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Not an approval token: the broker compares this lifecycle fingerprint under its operation lock.</summary>
public sealed record TuningScope(string Gpu, string Session, string Generation, string Game);

public sealed record TuningPolicySnapshot(TuningPolicySettings Settings, string Approved, TuningScope? Scope, string Requested = "conservative")
{
    public int? Bound(string key) => Settings.Bound(key, Approved);
    public bool Device => Approved == "device";
    public void Ceiling(string key, double value) => Check(key, value, false);
    public void Floor(string key, double value) => Check(key, value, true);
    private void Check(string key, double value, bool floor)
    {
        if (!double.IsFinite(value)) throw new ArgumentException($"{key}: a finite value is required");
        if (Bound(key) is not { } bound || (floor ? value >= bound : value <= bound)) return;
        // Consent is actionable only when the requested mode could actually permit this value.
        // Explicit restrictions continue to bind device mode.
        int? proposed = Settings.Bound(key, Requested == "device" ? "device" : "overrides");
        bool pending = Approved == "conservative"
            && (Requested == "device" || Settings.Relaxations.ContainsKey(key))
            && (proposed is null || (floor ? value >= proposed : value <= proposed));
        throw new TuningPolicyException(pending ? "consent_required" : "policy_limit",
            $"{key}: {value.ToString(CultureInfo.InvariantCulture)} exceeds the effective policy bound {bound}. " +
            (pending ? "Call set_tuning_policy with device or overrides for mandatory session approval." : "Read card_settings for effective bounds."));
    }

    public object Describe() => new
    {
        requested_policy = Requested, effective_policy = Device ? "device" : "conservative",
        approval = Approved, scope = Scope == null ? null : new { gpu = Scope.Gpu, session = Scope.Session, game = Scope.Game },
        consent_required = Approved == "conservative" && (Requested == "device" || Settings.Relaxations.Count > 0),
        pending_overrides = Approved == "conservative" ? Settings.Relaxations : null,
        approved_overrides = Approved is "overrides" or "device" ? Settings.Relaxations : null,
        configured_restrictions = Settings.Configured,
        effective_bounds = TuningPolicySettings.Defaults.Keys.ToDictionary(k => k, Bound),
    };
}

/// <summary>Worker-local approval, never persisted. Gate also sequences policy changes with entire explicit writes.</summary>
public sealed class TuningPolicy(TuningPolicySettings settings)
{
    public object Gate { get; } = new();
    public TuningPolicySettings Settings { get; } = settings;
    private string _requested = settings.Requested, _approved = "conservative";
    private TuningScope? _scope;
    private long _version;
    public Action<string> Log { get; set; } = s => Console.Error.WriteLine("clocktail policy: " + s);

    private void Record(string message)
    {
        // A logging failure must never prevent revocation or turn a declined answer into approval.
        try { Log(message); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            try { Console.Error.WriteLine("clocktail policy log unavailable: " + e.Message); }
            catch (Exception fallbackError) when (fallbackError is not OutOfMemoryException) { }
        }
    }

    public TuningPolicySnapshot Snapshot(TuningScope? current)
    {
        lock (Gate)
        {
            if (_scope != null && _scope != current) Revoke("session, GPU, driver or recovery generation changed");
            return new(Settings, _approved, _scope, _requested);
        }
    }

    public object Describe(TuningScope? current)
    {
        lock (Gate) return new { requested = _requested, policy = Snapshot(current).Describe() };
    }

    public void Revoke(string why)
    {
        lock (Gate)
        {
            ++_version;
            bool hadApproval = _scope != null;
            _scope = null;
            _approved = "conservative";
            if (hadApproval) Record("revoked: " + why);
        }
    }

    public async Task Approve(string policy, Func<TuningScope?> readScope, Func<string> capabilities,
        Func<string, CancellationToken, Task<string?>> ask, CancellationToken ct)
    {
        if (policy is not ("conservative" or "device" or "overrides"))
            throw new ArgumentException("policy is conservative, device or overrides");
        TuningScope before;
        string prompt;
        long version;
        lock (Gate)
        {
            Revoke("policy selection changed");
            _requested = policy;
            version = _version;
            if (policy == "conservative") return;
            before = readScope() ?? throw new TuningPolicyException("session_required",
                "Begin a tuning session first. Device limits need a broker supporting policy-scope; update the software if unavailable.");
            var changes = policy == "device"
                ? "Relax default core/memory/XBAR/curve steps, daily-offset allowance, explicit curve voltage floor, XBAR demand heuristic and ordinary trial power bounds."
                : "Approve only these configured relaxations: " + string.Join(", ", Settings.Relaxations.Select(p => $"CLOCKTAIL_MCP_{p.Key}={p.Value}"));
            if (policy == "overrides" && Settings.Relaxations.Count == 0)
                throw new ArgumentException("No configured legacy relaxations are pending");
            prompt = $"Allow {policy} tuning on {before.Gpu}, session '{before.Game}' ({before.Session})?\n{changes}\n" +
                $"Explicit restrictions: {string.Join(", ", Settings.Configured.Select(p => $"{p.Key}={p.Value}"))}.\n" +
                capabilities() + "\nDevice bounds, transactions, readback verification, crash allowance and recovery remain enforced. " +
                "Permitted settings may still be unstable. This approval changes no GPU settings and runs no workload. " +
                "It expires on session replacement/end, crash, driver reset/recovery or worker restart.";
        }
        string? refusal;
        try { refusal = await ask(prompt, ct); }
        catch
        {
            lock (Gate) Record("declined: confirmation failed or cancelled");
            throw;
        }
        lock (Gate)
        {
            if (refusal != null || ct.IsCancellationRequested)
            {
                Record("declined: " + (refusal ?? "cancelled"));
                throw new TuningPolicyException("consent_required", refusal ?? "Confirmation was cancelled");
            }
            if (version != _version || readScope() != before)
            {
                Record("declined: stale confirmation");
                throw new TuningPolicyException("consent_required", "The session or driver changed while awaiting approval. Call set_tuning_policy explicitly again.");
            }
            _scope = before;
            _approved = policy;
            Record($"granted {policy}: GPU {before.Gpu}, session {before.Session}");
        }
    }
}

/// <summary>Finite, checked conversions shared by explicit inputs; no int.MaxValue sentinel arithmetic.</summary>
public static class TuningValues
{
    public static int Khz(double mhz)
    {
        double khz = Math.Round(mhz * 1000);
        if (!double.IsFinite(khz) || khz < int.MinValue || khz > int.MaxValue)
            throw new ArgumentException("MHz must be finite and representable in kHz");
        return (int)khz;
    }

    public static uint Uv(double mv)
    {
        double uv = Math.Round(mv * 1000);
        if (!double.IsFinite(uv) || uv < 0 || uv > int.MaxValue)
            throw new ArgumentException("mV must be finite, nonnegative and representable in microvolts");
        return (uint)uv;
    }

    public static void DeviceRange(double value, double? min, double? max, string what)
    {
        if (min == null || max == null || !double.IsFinite(min.Value) || !double.IsFinite(max.Value) || min > max)
            throw new TuningPolicyException("device_limit", $"{what}: device bounds are missing or invalid; read diagnostics before retrying");
        if (!double.IsFinite(value) || value < min || value > max)
            throw new TuningPolicyException("device_limit", $"{what}: value must be within the device range {min}..{max}");
    }
}
