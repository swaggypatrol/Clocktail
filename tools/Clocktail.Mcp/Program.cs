using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Clocktail.Mcp;

// The agent starts the relay, which loads no driver library and runs this program again as the worker that does: a
// worker that dies in the driver (or leaves because the driver went away) is started again and the agent keeps its
// tools (Relay). CLOCKTAIL_MCP_DIRECT=1 runs the server in this process instead.
bool worker = args.Contains("--worker");
if (!worker && OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CLOCKTAIL_MCP_DIRECT") != "1" && Environment.ProcessPath is { } self)
    return Relay.Run(self);

// stdio is the protocol: every log line goes to stderr, never to stdout.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<Card>();
builder.Services.AddSingleton<Residency>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Residency>());
builder.Services.AddSingleton<LiveWatch>();
builder.Services.AddHostedService<Heartbeat>();
if (worker) builder.Services.AddHostedService<DriverWatch>();
builder.Services.AddMcpServer(o => o.ServerInstructions = ManualTools.Instructions)
    .WithStdioServerTransport().WithTools<TuningTools>().WithTools<SettingsTools>().WithTools<LiveTools>().WithTools<ManualTools>();
_ = Task.Run(UpdateCheck.Latest);   // ask npm once at start, so the first card_report need not wait
await builder.Build().RunAsync();
return 0;
