using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Clocktail.Mcp;

// The agent starts the relay, which loads no driver library and runs this program again as the worker that does: a
// worker that dies in the driver (or leaves because the driver went away) is started again and the agent keeps its
// tools (Relay). CLOCKTAIL_MCP_DIRECT=1 runs the server in this process instead.
bool worker = args.Contains("--worker");
// The lite tool set (LiteMode): --lite on the relay reaches the worker through its environment.
bool lite = LiteMode.On(args);
if (lite) Environment.SetEnvironmentVariable(LiteMode.Variable, "lite");
if (!worker && OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CLOCKTAIL_MCP_DIRECT") != "1" && Environment.ProcessPath is { } self)
    return Relay.Run(self);

// stdio is the protocol: every log line goes to stderr, never to stdout.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(new TuningPolicySettings(Environment.GetEnvironmentVariable));
builder.Services.AddSingleton(sp =>
{
    var policy = new TuningPolicy(sp.GetRequiredService<TuningPolicySettings>());
    var card = sp.GetRequiredService<Card>();
    policy.Log = message =>
    {
        Console.Error.WriteLine("clocktail policy: " + message);
        card.Broker(Clocktail.Domain.Broker.BrokerRequest.OpExperiment, "note", "MCP tuning policy: " + message);
    };
    return policy;
});
builder.Services.AddSingleton<Card>();
builder.Services.AddSingleton<Residency>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Residency>());
builder.Services.AddSingleton<LiveWatch>();
builder.Services.AddHostedService<Heartbeat>();
if (worker) builder.Services.AddHostedService<DriverWatch>();
var server = builder.Services.AddMcpServer(o => o.ServerInstructions = lite ? ManualTools.LiteInstructions : ManualTools.Instructions)
    .WithStdioServerTransport().WithTools<TuningTools>().WithTools<SettingsTools>().WithTools<LiveTools>().WithTools<ManualTools>();
if (lite)
    server.WithRequestFilters(f => f.AddListToolsFilter(next => async (request, ct) =>
    {
        var list = await next(request, ct);
        list.Tools = [.. list.Tools.Where(t => LiteMode.Tools.Contains(t.Name))];
        return list;
    }));
_ = Task.Run(UpdateCheck.Latest);   // ask npm once at start, so the first card_report need not wait
await builder.Build().RunAsync();
return 0;
