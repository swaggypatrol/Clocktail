using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace Clocktail.Mcp;

/// <summary>Watching a whole session of play as it happens: the sampler is <see cref="LiveWatch"/>; these are its verbs.</summary>
[McpServerToolType]
public sealed class LiveTools(LiveWatch live)
{
    private static readonly JsonSerializerOptions Json = SettingsTools.Json;

    [McpServerTool(Name = "live_start", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Watch the card live while the person plays or runs a benchmark, for as long as it takes: sampled 4 times a second in the " +
        "background (read-only), and every 10 s under load one line: the clock range and median, the voltage and the curve point it " +
        "ran on, MSVDD, XBAR, power against the wall and how much of the time it sat on it, temperature, fans, and what held the clock. " +
        "Plus a line the moment load starts or stops, the card stops answering (a hang or a driver reset), or it runs hot. Returns at " +
        "once with the log's path. In Claude Code, follow the log with the Monitor tool (the command is in 'follow') and comment as " +
        "lines arrive; any agent can call live_read whenever the person talks. Every sample also goes to a CSV beside the log. Each " +
        "line under load ends with the game's frame rate over those seconds (average, 1 % and 0.1 % lows), from the service's " +
        "PresentMon capture (nothing injected into the game); the summary has the whole run's under load. A clock lock set, changed or " +
        "released mid-watch is checked per period. Ends " +
        "on live_stop, after 6 h, or when the server stops. Use watch instead for one fixed stretch you wait for.")]
    public string Start(
        [Description("A line every this many seconds under load (2..120; default 10).")] int everySeconds = 10,
        [Description("Sample interval, ms (100..2000; default 250).")] int intervalMs = 250,
        [Description("What is being played (game, scene, settings), for the log.")] string? note = null,
        [Description("The game's process name for the frame rate (e.g. \"Cyberpunk2077\"); omit for the application that draws the most frames.")] string? process = null) =>
        Guard(() => live.Start(intervalMs, everySeconds, note, process));

    [McpServerTool(Name = "live_read", ReadOnly = true, OpenWorld = false), Description(
        "The live watch's lines since the last live_read (the newest 'max' of them), and the run so far: time under load, " +
        "p10/p50/p90 clock, the curve points it spent most time on, how much of it sat on the wall, max temperature and power, " +
        "what held the clock how often, each clock lock period against what ran, and how often the card stopped answering or looked hung. After a crash or a stutter, tailSeconds returns every sample of the last seconds " +
        "(up to 30). Works after live_stop too, for the last run.")]
    public string Read(
        [Description("At most this many new lines (default 30; the rest stay in the log).")] int max = 30,
        [Description("Also return every sample of the last N seconds (0..30; default 0).")] int tailSeconds = 0) =>
        Guard(() => live.Read(max, tailSeconds));

    [McpServerTool(Name = "live_stop", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "End the live watch: the summary of the whole run (also written to the tuning log), the lines not read yet, and the totals.")]
    public string Stop() => Guard(live.Stop);

    private static string Guard(Func<object> tool)
    {
        try { return JsonText.Serialize(tool(), Json); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return JsonText.Serialize(new { success = false, error = e.Message }, Json);
        }
    }
}
