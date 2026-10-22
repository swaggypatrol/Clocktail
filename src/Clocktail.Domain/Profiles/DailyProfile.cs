using System.Text.Json;
using Clocktail.Domain.Boost;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Fan;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;

namespace Clocktail.Domain.Profiles;

/// <summary>One write of the daily profile, in the registry's own terms.</summary>
public sealed record ProfileWrite(string Kind, string Key, string Value);

/// <summary>
/// The state the user keeps on the card day to day ("daily profile"), held by the broker: applied when it starts, and put back
/// unchanged after a display driver reset (which leaves the offsets on the card but puts the power limit back to stock).
/// It changes only when someone keeps a new one: the person, or an agent through the MCP server. There is no automatic
/// step-down (the user, 2026-10-19: a crash happens with the person at the machine, and the agent adjusts from there).
/// The journal's originals stay the card's stock; this is the last-known-good above it. <see cref="Suspended"/> means
/// stock until someone loads or keeps a profile again. <see cref="Vbios"/> and <see cref="Driver"/> are what the card ran
/// when it was kept or loaded (null from a build before 0.2.3): the offsets were measured on that firmware's curve, so
/// a profile kept on another VBIOS does not go on by itself (<see cref="OtherVbios"/>).
/// </summary>
public sealed record DailyProfile(
    IReadOnlyList<ProfileWrite> Writes,
    DateTimeOffset KeptAt,
    bool Suspended = false,
    IReadOnlyList<string>? History = null,
    string? Vbios = null,
    string? Driver = null)
{
    public const string FileName = "profile.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathBeside(string journalPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, FileName);

    /// <summary>The profile on disk, or null. Fields an older build wrote and this one no longer has (the step-down's
    /// Down and LastResetAt) are skipped.</summary>
    public static DailyProfile? Load(string path)
    {
        var bytes = AtomicFile.Read(path, 1024 * 1024);
        if (bytes == null) return null;
        try { return JsonSerializer.Deserialize<DailyProfile>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    public void Save(string path) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));

    /// <summary>Power first (a driver reset puts the limit back to 575 W), then the fan curve (it touches nothing else, and
    /// the cooling is on before the clocks are), then P0 offsets, the rails' offsets and the domains' voltage demands
    /// before the XBAR/SYS/Video clock offsets (the voltage before the clock it carries), the V/F curve last: a Pstates20
    /// write resets every V/F point offset.</summary>
    public static IReadOnlyList<ProfileWrite> InWriteOrder(IEnumerable<ProfileWrite> writes) =>
        writes.OrderBy(w => Rank(w.Kind)).ToList();

    /// <summary>Where a kind goes in a run of writes: a P0 write (the memory offset) zeroes the V/F points, so the curve
    /// goes last, and a voltage demand before the clock offset it carries.</summary>
    public static int Rank(string kind) => kind switch
    {
        PowerCap.Kind => 0, FanPolicyWrites.Kind or FanStopWrites.Kind => 1, ClockOffsets.Kind => 2, RailWrites.Kind => 3,
        ClockVdemand.Kind => 5, ClockDomainOffsets.Kind => 6, VfWrites.Kind => 7, _ => 4,
    };

    /// <summary>The cells of a profile that were tuned together (the method's steps): the wall; the fans; the core (its curve,
    /// NVVDD's offsets, the P0 core offset, the voltage boost, the core's voltage demand and clock offset, a clock cap); the
    /// uncore (MSVDD's offsets, the XBAR/core ratio, the XBAR, SYS and Video offsets and voltage demands); the memory offset.
    /// A cell that does not go on takes the rest of its group with it, the other groups go on: XBAR +400 / +50 mV and the
    /// ratio 0.95 were found with MSVDD REL +50, and without it the ratio slows the core (driver 621.01 drops every rail
    /// write). A kind this does not name is a group of its own.</summary>
    public static string Group(string kind, string key) => kind switch
    {
        PowerCap.Kind => "power",
        FanPolicyWrites.Kind or FanStopWrites.Kind => "fans",
        RailWrites.Kind => key == RailWrites.Key(Rail.Msvdd) ? "uncore" : "core",
        ClockOffsets.Kind => key == ClockOffsets.Key(ClockDomain.Memory) ? "memory" : "core",
        ClockVdemand.Kind => key == ClockVdemand.Key(ClockDomainId.Graphics) ? "core" : "uncore",
        ClockDomainOffsets.Kind => key == ClockDomainOffsets.Key(ClockDomainId.Graphics) ? "core" : "uncore",
        ClockRatio.Kind => "uncore",
        VfWrites.Kind or VoltBoost.Kind or ClockLimitWrites.Kind => "core",
        _ => $"{kind}/{key}",
    };

    /// <summary>What keep takes from the card even when a profile is kept already: everything an agent tunes in a trial,
    /// the curves (V/F, fan) and the fans' zero-RPM band, the P0 core and memory offsets, both rails' offsets, the voltage boost, a clock cap, the
    /// XBAR/core clock ratio and the XBAR/SYS/Video clock offsets and voltage demands (the user, 2026-10-20: the agent does everything we did by hand). The wall stays as kept, so a
    /// wall lowered in a trial to sample the curve never becomes the daily one; the daily wall is set on its own
    /// ("wall").</summary>
    public static bool KeptFromCard(string kind, string key) =>
        kind is VfWrites.Kind or FanPolicyWrites.Kind or FanStopWrites.Kind or ClockDomainOffsets.Kind or ClockVdemand.Kind or ClockOffsets.Kind or RailWrites.Kind
            or VoltBoost.Kind or ClockRatio.Kind
        || kind == ClockLimitWrites.Kind && key == ClockLimitWrites.MaxKey;

    /// <summary>What only a trial sets and no daily profile holds: a rail's current limit, a fixed fan duty, the F8 request
    /// (it can only go back to non-limiting, never away), and a clock floor, V/F point lock or Boost Lock. Each of those
    /// would hold the card at a high clock or voltage at idle after every boot; mVolt+ 0.50.6 never puts one in a profile
    /// either (they are leases there). A clock cap is kept: it only ever lowers.</summary>
    public static bool TrialOnly(string kind, string key) => kind is CurrentLimitWrites.Kind or FanControl.Kind
        || kind == PowerCap.Kind && key == PowerCap.Key(PowerRequestKind.Additional)
        || kind == ClockLimitWrites.Kind && key != ClockLimitWrites.MaxKey;

    /// <summary>The profile bound to the card it is kept or loaded on now; an unknown VBIOS or driver leaves the old one.</summary>
    public DailyProfile On(string? vbios, string? driver) =>
        this with { Vbios = Known(vbios) ?? Vbios, Driver = Known(driver) ?? Driver };

    /// <summary>Why the profile does not go on by itself on a card that runs <paramref name="vbiosNow"/>: it was kept on
    /// another VBIOS (a flash changes the stock curve under the offsets). Null when it goes on, also when either is unknown.</summary>
    public string? OtherVbios(string? vbiosNow) =>
        Known(Vbios) is { } kept && Known(vbiosNow) is { } now && !string.Equals(kept, now, StringComparison.OrdinalIgnoreCase)
            ? $"the daily profile was kept on VBIOS {kept} and the card now runs {now}: its offsets were measured on the other firmware's curve, so it does not go on by itself (the card stays at stock); load_daily puts it on and binds it to this VBIOS, then stress_test it at load 100 and 50"
            : null;

    private static string? Known(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The profile with one more line of history (the last 20 are kept).</summary>
    public DailyProfile Noted(DateTimeOffset at, string line) =>
        this with { History = [.. (History ?? []).TakeLast(19), $"{at:u} {line}"] };
}
