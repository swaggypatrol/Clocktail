using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Platform;

namespace Clocktail.Mcp;

/// <summary>One worker as the relay sees it: lines in, a way to end it.</summary>
public interface IRelayChild
{
    void Send(string line);
    void Kill();
}

/// <summary>
/// What the agent starts: a relay that loads no driver library and passes the MCP conversation, line by line, to a worker
/// (this program with <c>--worker</c>) that holds the GPU. A process with NVML loaded can die of an access violation .NET
/// cannot catch: on 2026-10-19 a device restart tore the driver down under the server's own once-a-second read, the
/// server died, and the agent lost every tool until the person reconnected it (/mcp). Now only the worker dies, or
/// leaves on purpose when it finds its driver gone (exit 75); the relay answers the calls it had in flight, starts a new
/// worker (backing off as the broker's supervisor does), replays the agent's initialize to it, and goes on. Calls that
/// arrive meanwhile wait for it, and are answered with the reason if it is not back within <see cref="AnswerAfter"/>.
/// </summary>
public sealed class RelayCore(
    Func<Action<string>, Action<string>, Action<int>, IRelayChild> start,
    Action<string> output,
    Action<string> log,
    Func<DateTimeOffset> now,
    Action<TimeSpan, Action> after,
    Action<string>? workerLog = null)
{
    /// <summary>A call waits this long for a worker that is starting again before it is answered with the reason.</summary>
    public static readonly TimeSpan AnswerAfter = TimeSpan.FromSeconds(20);

    /// <summary>The tools that can change what is on the card (stress_test too: a failure puts the daily profile back).
    /// One of these cut off mid-call may or may not have written; any other wrote nothing to the card.</summary>
    public static readonly IReadOnlySet<string> CardWriters = new HashSet<string>
    {
        "adjust_curve", "adjust_msvdd", "adjust_xbar", "set_power_limit", "set_current_limit", "set_memory_offset", "set_fan_curve",
        "set_fan_speed", "set_daily_power_limit", "back_to_daily", "stock", "load_daily", "keep_as_daily", "load_preset",
        "end_session", "mark_crash", "stress_test",
    };
    private const string ReplayId = "\"clocktail-relay-initialize\"";

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Method, string? Tool)> _inflight = [];
    private readonly List<string> _queue = [];
    private IRelayChild? _child;
    private int _generation, _quick;
    private bool _ready, _clientInitialized, _stopping;
    private string? _initialize, _lastError;
    private JsonNode? _tools;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _downSince;
    private string _down = "it is starting";

    public void Start()
    {
        lock (_gate) StartChild(replay: false);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            _child?.Kill();
        }
    }

    /// <summary>A line from the agent.</summary>
    public void FromClient(string line)
    {
        lock (_gate)
        {
            var node = Parse(line);
            string? method = node?["method"]?.GetValueKind() == JsonValueKind.String ? (string?)node["method"] : null;
            var id = node?["id"];
            if (method == "initialize") _initialize = line;
            if (method == "notifications/initialized") _clientInitialized = true;
            if (!_ready || _child == null)
            {
                if (id != null && method != null && _downSince is { } d && now() - d > AnswerAfter) Answer(id, method, Tool(node!), inFlight: false);
                else _queue.Add(line);
                return;
            }
            Forward(line, node);
        }
    }

    /// <summary>Once a second: calls that have waited too long for a worker get their answer.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_ready || _downSince is not { } d || now() - d <= AnswerAfter) return;
            foreach (var line in _queue.ToList())
                if (Parse(line) is { } n && n["id"] is { } id && n["method"]?.GetValueKind() == JsonValueKind.String)
                {
                    Answer(id, (string)n["method"]!, Tool(n), inFlight: false);
                    _queue.Remove(line);
                }
        }
    }

    private void Forward(string line, JsonNode? node)
    {
        if (node?["id"] is { } id && node["method"]?.GetValueKind() == JsonValueKind.String)
            _inflight[id.ToJsonString()] = ((string)node["method"]!, Tool(node));
        _child!.Send(line);
    }

    private void StartChild(bool replay)
    {
        int g = ++_generation;
        _lastError = null;
        _startedAt = now();
        try { _child = start(line => FromChild(g, line), line => ErrorFromChild(g, line), code => Exited(g, code)); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _child = null;
            log($"could not start the worker: {e.Message}");
            Exited(g, 1);
            return;
        }
        if (replay && _initialize != null && Parse(_initialize) is { } init)
        {
            init["id"] = JsonNode.Parse(ReplayId);
            _ready = false;
            _child.Send(init.ToJsonString());
        }
        else
        {
            _ready = true;
            _downSince = null;
        }
    }

    private void FromChild(int g, string line)
    {
        lock (_gate)
        {
            if (g != _generation) return;
            var node = Parse(line);
            if (node?["id"] is { } id && node["method"] == null)
            {
                string key = id.ToJsonString();
                if (key == ReplayId)
                {
                    _ready = true;
                    log($"worker back after {Span(now() - (_downSince ?? now()))}");
                    _downSince = null;
                    if (_clientInitialized) _child!.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
                    var queued = _queue.ToList();
                    _queue.Clear();
                    foreach (var q in queued) Forward(q, Parse(q));
                    return;
                }
                if (_inflight.Remove(key, out var r) && r.Method == "tools/list" && node["result"] is { } tools) _tools = tools.DeepClone();
            }
            output(line);
        }
    }

    private void ErrorFromChild(int g, string line)
    {
        // The worker's own log goes on to the agent's log; the first line of a fatal exception is kept as the reason.
        if (line.StartsWith("Unhandled exception.", StringComparison.Ordinal)) lock (_gate) if (g == _generation) _lastError ??= line["Unhandled exception.".Length..].Trim();
        workerLog?.Invoke(line);
    }

    private void Exited(int g, int code)
    {
        lock (_gate)
        {
            if (g != _generation || _stopping) return;
            _child = null;
            _ready = false;
            _downSince ??= now();
            _down = Reason(code, _lastError);
            foreach (var (key, r) in _inflight) Answer(JsonNode.Parse(key)!, r.Method, r.Tool, inFlight: true);
            _inflight.Clear();
            var ran = now() - _startedAt;
            _quick = ran >= Supervisor.Healthy ? 1 : _quick + 1;
            var wait = Supervisor.Delay(_quick);
            log($"worker ended after {Span(ran)}: {_down}; a new one starts in {Span(wait)}");
            after(wait, () => { lock (_gate) if (!_stopping && _child == null && g == _generation) StartChild(replay: true); });
        }
    }

    /// <summary>An answer the worker can no longer give: a tool call gets the reason as its result (as every tool reports
    /// a failure), tools/list the last list seen, ping an empty result, anything else a JSON-RPC error.</summary>
    private void Answer(JsonNode id, string method, string? tool, bool inFlight)
    {
        string why = inFlight
            ? $"Clocktail's reader process ended while this call ran ({_down}); it starts again by itself"
            : $"Clocktail's reader process is not running ({_down}); it is being started again";
        JsonObject reply = new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (method == "tools/call")
        {
            bool writes = tool != null && CardWriters.Contains(tool);
            var text = new JsonObject
            {
                ["success"] = false,
                ["error"] = why + ". Try again in a few seconds.",
                ["written"] = !writes ? null : inFlight ? WriteOutcome.Unknown : WriteOutcome.None,
                ["next"] = writes && inFlight ? WriteOutcome.ReadFirst : null,
            };
            reply["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.ToJsonString() }),
                ["isError"] = true,
            };
        }
        else if (method == "tools/list" && _tools != null) reply["result"] = _tools.DeepClone();
        else if (method == "ping") reply["result"] = new JsonObject();
        else reply["error"] = new JsonObject { ["code"] = -32603, ["message"] = why };
        output(reply.ToJsonString());
    }

    private static string Reason(int code, string? error) => code switch
    {
        Supervisor.DriverLost => "the display driver went away: a reset, an update or a device restart",
        unchecked((int)0xC0000005) => "it crashed inside the driver (0xC0000005), as a call does when the driver is torn down under it",
        _ => error ?? $"exit {Supervisor.Describe(code)}",
    };

    private static string? Tool(JsonNode node) =>
        node["params"]?["name"]?.GetValueKind() == JsonValueKind.String ? (string?)node["params"]!["name"] : null;

    private static JsonNode? Parse(string line)
    {
        try { return JsonNode.Parse(line); } catch (JsonException) { return null; }
    }

    private static string Span(TimeSpan t) => t.TotalSeconds < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.#} s")
        : string.Create(CultureInfo.InvariantCulture, $"{t.TotalMinutes:0} min");
}

/// <summary>The relay on the real process: stdio both ways, the worker in a job that dies with this process.</summary>
[SupportedOSPlatform("windows")]
public static class Relay
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static int Run(string exe)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };
        var stderr = new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true, NewLine = "\n" };
        var outGate = new object();
        void Out(string line) { lock (outGate) stdout.WriteLine(line); }
        void Log(string line) { lock (outGate) stderr.WriteLine($"clocktail relay: {line}"); }
        void Pass(string line) { lock (outGate) stderr.WriteLine(line); }
        var job = new WorkerProcess(exe);
        var core = new RelayCore(
            (onLine, onError, onExit) => new ProcessChild(exe, job, onLine, onError, onExit),
            Out, Log, () => DateTimeOffset.Now,
            (wait, then) => _ = Task.Delay(wait).ContinueWith(_ => then(), TaskScheduler.Default),
            Pass);
        core.Start();
        using var tick = new Timer(_ => core.Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8);
        while (stdin.ReadLine() is { } line)
            if (line.Length > 0) core.FromClient(line);
        Release(Log);  // the agent closed the connection: nothing it was watching stays held for it,
        core.Stop();   // and the worker goes with it
        return 0;
    }

    /// <summary>
    /// What only a watching agent may hold (a fixed fan duty, lowered current limits) goes back on every GPU the service
    /// has, through the broker's "release": the broker keeps a trial until someone ends it, and a fan held at 30 % with no
    /// agent watching temp_c stays there until a reboot. The relay loads no driver; it only asks the service.
    /// </summary>
    private static void Release(Action<string> log)
    {
        try
        {
            var ping = BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpPing), connectMs: 800);
            if (ping?.Result is not { } r || !r.TryGetProperty("gpus", out var gpus) || gpus.ValueKind != JsonValueKind.Array) return;   // no service: it holds nothing
            foreach (var id in gpus.EnumerateArray().Select(g => g.GetString()))
            {
                var a = BrokerClient.TryCall(new BrokerRequest(BrokerRequest.OpExperiment, id, Kind: "release", Value: "the agent disconnected"),
                    connectMs: 800, answerWait: TimeSpan.FromSeconds(30));
                if (a is { Ok: false }) log($"gpu {id}: trial-only settings not all handed back: {a.Error}");
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or ObjectDisposedException)
        {
            log($"trial-only settings not handed back: {e.Message}");
        }
    }

    private sealed class ProcessChild : IRelayChild
    {
        private readonly Process _p;

        public ProcessChild(string exe, WorkerProcess job, Action<string> onLine, Action<string> onError, Action<int> onExit)
        {
            var psi = new ProcessStartInfo(exe, "--worker")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            _p = new Process { StartInfo = psi };
            _p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) onLine(d); };
            _p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) onError(d); };
            _p.Start();
            job.Adopt(_p);
            _p.StandardInput.NewLine = "\n";
            _p.StandardInput.AutoFlush = true;
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
            // The exit is reported once its output has drained: an answer it wrote just before ending still reaches the agent.
            _ = Task.Run(() => { _p.WaitForExit(); onExit(_p.ExitCode); });
        }

        public void Send(string line)
        {
            try { _p.StandardInput.WriteLine(line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }   // it is ending: its exit answers
        }

        public void Kill()
        {
            try { _p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
