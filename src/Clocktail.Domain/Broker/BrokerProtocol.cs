using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The wire contract between the unprivileged client (the CLI, an agent) and the elevated broker. One request
/// per call, one response. The set of operations is the whitelist: there is no "raw slot" or "register" verb,
/// so a client can only ask for the high-level tuning the domain already knows how to do safely. Adding a verb
/// here is the deliberate act of widening what the broker will do; nothing a client sends can reach a driver
/// call this record does not name.
/// <para>
/// <see cref="OpWrite"/> is the general form: set <see cref="Kind"/>/<see cref="Key"/> to <see cref="Value"/>
/// through the write-path registry, which holds every limit. <see cref="OpPower"/> and <see cref="OpClock"/>
/// are the two first paths' own verbs, kept because their results carry the driver's extra readings (the
/// reported frequency, the board range); they reach the same cells and the same plans.
/// </para>
/// </summary>
public sealed record BrokerRequest(
    string Op,
    string? GpuId = null,
    string? Kind = null,
    uint Mw = 0,
    string? Domain = null,
    int Khz = 0,
    bool AllowCreate = false,
    bool TakeOver = false,
    /// <summary>For <see cref="OpWrite"/> and <see cref="OpProve"/>: the cell's key within its kind.</summary>
    string? Key = null,
    /// <summary>For <see cref="OpWrite"/>: the value in the cell's canonical encoding.</summary>
    string? Value = null,
    /// <summary>
    /// Setters whose write policy is off until their live drill, named by slot, enabled for this one request. The
    /// drill gate, not the firmware ceiling: a setter can be named here only when its whole rule set is decoded
    /// (the policy refuses otherwise), and the limits in the plan apply regardless.
    /// </summary>
    string[]? EnableSlots = null,
    /// <summary>For <see cref="OpProfile"/> "crashed": the core voltage the GPU ran at when it hung, µV (the flight
    /// recorder's crash point), for the record; 0 when it is not known.</summary>
    uint Uv = 0)
{
    public const string OpPing = "ping";
    public const string OpOwnership = "ownership";
    public const string OpRecover = "recover";
    /// <summary>
    /// <see cref="Kind"/> on <see cref="OpRecover"/> when the broker itself asks after the display driver reset (a TDR):
    /// the driver came back with every offset still applied, so what is held goes on record in the crash log
    /// (<see cref="Value"/> says what was seen) and is given back. Without it a curve that hung a game stays on the
    /// card through the reset and hangs it again.
    /// </summary>
    public const string RecoverAfterDriverReset = "driver-reset";
    public const string OpPower = "power";
    public const string OpClock = "clock";
    public const string OpWrite = "write";
    /// <summary>A setter's equal-value drill: write exactly what was read, show that nothing moved.</summary>
    public const string OpProve = "prove";
    /// <summary>
    /// The daily profile (<see cref="Clocktail.Domain.Profiles.DailyProfile"/>), by <see cref="Kind"/>: "show"; "keep"
    /// saves what this broker holds now as the profile (<see cref="Value"/>, optional: why, for the history and the
    /// log), but once a profile is kept only the Core curve is taken from the card: its power limit and memory offset
    /// stay as kept, and a trial's lowered wall goes back to the kept one at once; "off" suspends it; "apply" writes it unless suspended (<see cref="Value"/>: why, for the log); "load" ends a
    /// suspension and writes it; "preset" makes a shipped preset or a saved profile (<see cref="Value"/>: its name) the profile and writes it; "save"
    /// saves the profile under a name (<see cref="Value"/>), "saved" lists the saved ones, "forget" deletes one;
    /// "wall" sets the profile's power limit (<see cref="Value"/>: mW) and writes it unless suspended or a trial holds its own; "apply-power" writes only its power limit (right after a driver reset, which puts the
    /// limit back to 575 W, without waiting for an idle GPU); "crashed" records a crash at once (the held cells already
    /// given back): a trial in force made a data point, nothing written; "after-reset" then writes the profile back
    /// unchanged once the GPU is idle, unless a trial written since is in force. Every one of
    /// them that changes the card or the profile is an entry in the tuning log. Not itself a write: "apply" and "after-reset"
    /// send each write through <see cref="OpWrite"/>, which takes the window and every guard.
    /// </summary>
    public const string OpProfile = "profile";
    /// <summary>
    /// Live tuning in a game (<see cref="Clocktail.Domain.Profiles.Experiment"/>), by <see cref="Kind"/>: "show" (the
    /// session, the trial and the last data points); "begin" (<see cref="Value"/>: JSON {game, note}); "try" writes a
    /// whole Core curve (<see cref="Value"/>: JSON {curve, change, at_uv, at_mhz, power_mw?, keep_curve?, context: [lines
    /// for the log]}) through <see cref="OpWrite"/> and makes it the trial in force, recording the one it replaces as
    /// survived; power_mw first writes a board power limit for the trial (a lower wall samples the curve lower down; a
    /// curve trial after it runs under the same wall), keep_curve records the curve on the card without writing it; "note" puts the
    /// agent's own words in the tuning log (<see cref="Value"/>: the text); "stable" records the trial as stable
    /// (<see cref="Value"/>: a note); "crash" records it as a crash the broker could not see (the game fell to the
    /// desktop with no driver reset; <see cref="Value"/>: what the player saw) and puts the daily profile back; "release"
    /// hands back what only a watching agent may hold (a fixed fan duty, lowered current limits) when the agent has gone,
    /// the rest of the trial staying in force; "end" closes the trial as survived, ends the session and puts the daily profile back. A crash while a trial is in force is recorded by the "crashed" step of <see cref="OpProfile"/>, at once,
    /// and "after-reset" puts the daily profile back unchanged once the GPU is idle. Not itself a write: "try" and "end" write through the ordinary ops.
    /// </summary>
    public const string OpExperiment = "experiment";

    /// <summary>A game's frame rate: <see cref="Kind"/> "start" (<see cref="Value"/> is JSON {seconds, process?}) starts a
    /// PresentMon capture into the GPU's fps folder and answers its path; "stop" ends it. Not a write: the card is untouched.</summary>
    public const string OpFps = "fps";

    /// <summary>Operations that change the GPU: they take the write window, respect the stability-run lock, and need elevation.</summary>
    public bool IsWrite => Op is OpRecover or OpPower or OpClock or OpWrite or OpProve;
}

/// <summary>
/// The broker's answer. <see cref="Result"/> is the same JSON the CLI prints when it runs the op itself, so the
/// client forwards it to stdout unchanged whether the op ran here or over the pipe. <see cref="Code"/> names a
/// refusal that happened before the op ran (one of <see cref="BrokerCode"/>), so a client can show each as its own
/// state instead of matching the sentence in <see cref="Error"/>; an op that ran and failed has a <see cref="Result"/>
/// and no code.
/// </summary>
public sealed record BrokerResponse(
    bool Ok,
    string? Error = null,
    JsonElement? Result = null,
    string? Code = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>The fixed names of the refusals a request can meet before its op runs.</summary>
public static class BrokerCode
{
    /// <summary>The process is not elevated, so the driver would answer -137; install the broker.</summary>
    public const string NeedsAdmin = "needs_admin";
    /// <summary>A stability run holds judge.lock; the write window is not opened.</summary>
    public const string JudgeBusy = "judge_busy";
    /// <summary>Another write held the window for longer than a client should wait; nothing was written, try again.</summary>
    public const string WindowBusy = "window_busy";
    /// <summary>
    /// No broker answered and this process is not elevated: the write is refused here instead of running in-process,
    /// because an unelevated client (often launched from a packaged app whose %LocalAppData% is redirected) would
    /// keep its own journal the broker never sees. Start or reinstall the broker.
    /// </summary>
    public const string BrokerUnreachable = "broker_unreachable";
    /// <summary>No GPU, or none with the requested id.</summary>
    public const string NoGpu = "no_gpu";
    /// <summary>Not an operation this broker performs.</summary>
    public const string UnknownOp = "unknown_op";
    /// <summary>Not a kind in the write-path registry.</summary>
    public const string NotAPath = "not_a_path";
    /// <summary>A required field is missing, or a slot or key name is not one the registry knows.</summary>
    public const string BadRequest = "bad_request";
    /// <summary>The path cannot write this value (its reason is in the error), before anything reached the driver.</summary>
    public const string CannotWrite = "cannot_write";
    /// <summary>The path defines no equal-value proof.</summary>
    public const string NoProof = "no_proof";
    /// <summary>The op threw before it could produce a result: the reason is in the error.</summary>
    public const string Failed = "failed";
}
