using System.Reflection;
using System.Text.Json.Nodes;
using Clocktail.Mcp;
using ModelContextProtocol.Server;

namespace Clocktail.Tests.Mcp;

/// <summary>The relay between the agent and the worker that holds the GPU: the agent keeps its tools when the worker
/// dies (2026-10-19: a device restart killed the server and the agent had to reconnect by hand).</summary>
public class RelayTests
{
    private sealed class FakeChild : IRelayChild
    {
        public readonly List<string> Got = [];
        public bool Killed;
        public required Action<string> Line;
        public required Action<int> Exit;
        public void Send(string line) => Got.Add(line);
        public void Kill() => Killed = true;
    }

    private readonly List<FakeChild> _children = [];
    private readonly List<string> _out = [], _log = [];
    private readonly List<(TimeSpan Wait, Action Then)> _scheduled = [];
    private DateTimeOffset _now = new(2026, 10, 20, 22, 0, 0, TimeSpan.Zero);

    private RelayCore Relay()
    {
        var r = new RelayCore((line, _, exit) => { var c = new FakeChild { Line = line, Exit = exit }; _children.Add(c); return c; },
            _out.Add, _log.Add, () => _now, (wait, then) => _scheduled.Add((wait, then)));
        r.Start();
        return r;
    }

    private static string Req(int id, string method, string? tool = null) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method,
        ["params"] = tool == null ? new JsonObject() : new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() },
    }.ToJsonString();

    private static string Res(JsonNode id, string text = "ok") => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["text"] = text } }.ToJsonString();

    private static JsonNode Last(List<string> lines) => JsonNode.Parse(lines[^1])!;

    private static JsonObject ToolText(string line) => JsonNode.Parse((string)JsonNode.Parse(line)!["result"]!["content"]![0]!["text"]!)!.AsObject();

    private RelayCore Connected()
    {
        var r = Relay();
        r.FromClient(Req(1, "initialize"));
        _children[0].Line(Res(1));
        r.FromClient("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        return r;
    }

    /// <summary>A worker's own question (Consent's elicitation) keeps to that worker: a new worker numbers its requests
    /// from 1 again, and a yes the person gave the old question must never answer the new one (0.4.0's review).</summary>
    [Fact]
    public void An_answer_to_an_ended_workers_question_never_reaches_the_next_worker()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/call", "keep_as_daily"));
        _children[0].Line(Req(1, "elicitation/create"));
        Assert.Equal("w1:1", (string)Last(_out)["id"]!);

        _children[0].Exit(75);
        var cancelled = _out.Select(l => JsonNode.Parse(l)!).Single(n => (string?)n["method"] == "notifications/cancelled");
        Assert.Equal("w1:1", (string)cancelled["params"]!["requestId"]!);

        _scheduled[^1].Then();                                          // the next worker, initialized again
        _children[1].Line(Res(JsonNode.Parse("\"clocktail-relay-initialize\"")!));
        _children[1].Line(Req(1, "elicitation/create"));
        Assert.Equal("w2:1", (string)Last(_out)["id"]!);
        int got = _children[1].Got.Count;

        r.FromClient(Res(JsonValue.Create("w1:1"), "yes"));            // the old question's answer: dropped
        Assert.Equal(got, _children[1].Got.Count);
        Assert.Contains(_log, l => l.Contains("dropped"));
        r.FromClient(Res(JsonValue.Create("w2:1"), "yes"));            // the new one's: to the new worker, with its own id
        Assert.Equal(1, (int)JsonNode.Parse(_children[1].Got[^1])!["id"]!);
    }

    [Fact]
    public void Lines_pass_both_ways_unchanged()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/call", "gpu_status"));
        Assert.Equal(Req(2, "tools/call", "gpu_status"), _children[0].Got[^1]);
        _children[0].Line(Res(2, "NVVDD 930 mV"));
        Assert.Contains("NVVDD 930 mV", _out[^1]);
        Assert.Single(_children);
    }

    [Fact]
    public void A_worker_that_dies_mid_call_answers_the_call_and_comes_back_initialized()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/call", "adjust_curve"));
        r.FromClient(Req(3, "tools/call", "gpu_status"));
        _children[0].Exit(75);

        var write = ToolText(_out[^2]);
        Assert.Equal("unknown", (string)write["written"]!);   // it may have reached the broker
        Assert.Contains("display driver went away", (string)write["error"]!);
        Assert.Null(ToolText(_out[^1])["written"]);           // a read wrote nothing
        Assert.True((bool)Last(_out)["result"]!["isError"]!);

        // A call while it is down waits for the new worker; the new worker first gets the agent's initialize again.
        r.FromClient(Req(4, "tools/call", "card_report"));
        Assert.Equal(TimeSpan.FromSeconds(2), _scheduled.Single().Wait);
        _scheduled[0].Then();
        var fresh = _children[1];
        var replay = JsonNode.Parse(fresh.Got.Single())!;
        Assert.Equal("initialize", (string)replay["method"]!);
        fresh.Line(Res(replay["id"]!));
        Assert.Equal("notifications/initialized", (string)JsonNode.Parse(fresh.Got[1])!["method"]!);
        Assert.Equal(Req(4, "tools/call", "card_report"), fresh.Got[2]);
        Assert.DoesNotContain(_out, l => l.Contains("clocktail-relay-initialize"));   // the replay's answer is the relay's own

        _children[0].Line(Res(4, "late, from the dead worker"));   // an old worker's line is not the agent's business
        Assert.DoesNotContain(_out, l => l.Contains("late, from the dead worker"));
        fresh.Line(Res(4, "report"));
        Assert.Contains("report", _out[^1]);
    }

    [Fact]
    public void A_worker_that_stays_down_gets_its_waiting_calls_answered_with_the_reason()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/list"));
        _children[0].Line("""{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"gpu_status"}]}}""");
        _children[0].Exit(unchecked((int)0xC0000005));
        r.FromClient(Req(3, "tools/call", "set_power_limit"));
        r.FromClient(Req(4, "tools/list"));
        _now += RelayCore.AnswerAfter + TimeSpan.FromSeconds(1);
        r.Tick();

        var call = ToolText(_out[^2]);
        Assert.Equal("none", (string)call["written"]!);   // it never reached a worker
        Assert.Contains("0xC0000005", (string)call["error"]!);
        Assert.Equal("gpu_status", (string)Last(_out)["result"]!["tools"]![0]!["name"]!);   // the last list seen

        r.FromClient(Req(5, "ping"));   // still down: answered at once
        Assert.NotNull(Last(_out)["result"]);
    }

    [Fact]
    public void Quick_deaths_back_off_and_stopping_kills_the_worker()
    {
        var r = Connected();
        _children[0].Exit(1);
        _scheduled[^1].Then();
        _children[1].Exit(1);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _scheduled.Select(s => s.Wait));
        _scheduled[^1].Then();
        r.Stop();
        Assert.True(_children[2].Killed);
        _children[2].Exit(1);
        Assert.Equal(2, _scheduled.Count);   // a worker ended on purpose is not started again
    }

    private static readonly List<McpServerToolAttribute> Tools = typeof(Card).Assembly.GetTypes().SelectMany(t => t.GetMethods())
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToList();

    /// <summary>The relay answers "wrote nothing" only for the tools that say they only read and the few that keep records.
    /// That list is exactly this, so a tool that joins it is looked at; every other tool counts as a writer, a tool added
    /// later too. In 0.3.3 the relay listed the writers, and the eleven trial tools added after the list (the curve shift,
    /// set_curve, every new trial), begin_session, the lab's jobs and install_service were answered as if they had written
    /// nothing.</summary>
    [Fact]
    public void Only_readers_and_record_keepers_count_as_writing_nothing()
    {
        var names = Tools.Select(t => t.Name!).ToHashSet();
        Assert.Subset(names, RelayCore.RecordOnly.ToHashSet());
        Assert.DoesNotContain(Tools, t => t.ReadOnly && RelayCore.RecordOnly.Contains(t.Name!));
        Assert.Equal(Tools.Where(t => t.ReadOnly).Select(t => t.Name!).Order(StringComparer.Ordinal), RelayCore.Readers.Order(StringComparer.Ordinal));
        Assert.Equal([
                "card_report", "card_settings", "curve_around", "data_points", "delete_profile", "diagnostic_report", "gpu_status", "lab_status",
                "limiter", "list_presets", "live_read", "live_start", "live_stop", "log_note", "manual", "mark_stable", "preview_daily", "read_log",
                "save_profile", "set_tuning_policy", "watch",
            ], names.Where(n => !RelayCore.MayWriteCard(n)).Order(StringComparer.Ordinal));
        foreach (var w in new[] { "shift_curve", "set_curve", "flatten_curve", "adjust_nvvdd", "adjust_clock_domain", "set_core_offset", "set_clock_lock",
                     "set_clock_ratio", "set_voltage_boost", "set_fan_start", "set_power_cap", "begin_session", "lab_test", "tune_memory",
                     "tune_uncore", "lab_stop", "install_service", "adjust_curve", "stress_test", "mark_crash", "end_session" })
            Assert.True(RelayCore.MayWriteCard(w), w);
        Assert.True(RelayCore.MayWriteCard("a_tool_added_later"));   // the safe side
    }

    /// <summary>shift_curve moves the curve on the card from where it is: cut off mid-call it may have landed, and the same
    /// call again would move it twice. In 0.3.3 the relay answered it as it answers a read: try again in a few seconds.</summary>
    [Fact]
    public void A_curve_shift_cut_off_mid_call_says_to_read_the_card_first()
    {
        var r = Connected();
        r.FromClient(Req(2, "tools/call", "shift_curve"));
        _children[0].Exit(75);

        var answer = ToolText(_out[^1]);
        Assert.Equal(WriteOutcome.Unknown, (string)answer["written"]!);
        Assert.Equal(WriteOutcome.ReadFirst, (string)answer["next"]!);
        Assert.Contains("read the card before trying it again", (string)answer["error"]!);
        Assert.DoesNotContain("Try again in a few seconds", (string)answer["error"]!);
    }
}
