using System.Diagnostics;
using System.Text.Json;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

/// <summary>Exercises discovery and a failed GPU call through the executable's actual stdio transport.</summary>
public class StdioDiscoveryTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("--full", "lite", false)]
    [InlineData("--lite", "full", true)]
    [InlineData("--worker", "full", false)]
    public async Task Discovery_stays_online_without_a_gpu(string? flag, string? mode, bool lite)
    {
        if (OperatingSystem.IsWindows()) return; // The test needs a host where GPU access is unavailable.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(Card).Assembly.Location);
        if (flag != null) start.ArgumentList.Add(flag);
        start.Environment.Remove(LiteMode.Variable);
        if (mode != null) start.Environment[LiteMode.Variable] = mode;
        start.Environment["CLOCKTAIL_NO_UPDATE_CHECK"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start MCP");
        Task<string> log = process.StandardError.ReadToEndAsync(deadline.Token);

        async Task Send(object body) =>
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(body).AsMemory(), deadline.Token);

        async Task<JsonElement> Call(int id, string method, object parameters)
        {
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                using var parsed = JsonDocument.Parse(line);
                if (parsed.RootElement.TryGetProperty("id", out var seen) && seen.GetInt32() == id)
                    return parsed.RootElement.GetProperty("result").Clone();
            }
            throw new InvalidOperationException($"MCP exited while answering {method}: {await log}");
        }

        try
        {
            var init = await Call(1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "offline-discovery-test", version = "1" },
            });
            Assert.Equal(lite ? ManualTools.LiteInstructions : ManualTools.Instructions, init.GetProperty("instructions").GetString());
            await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            var list = await Call(2, "tools/list", new { });
            var names = list.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet();
            if (lite)
                Assert.True(names.SetEquals(LiteMode.Tools));
            else
                Assert.True(names.IsProperSupersetOf(LiteMode.Tools) && names.Contains("gpu_status"));

            var gpu = await Call(3, "tools/call", new { name = "gpu_status", arguments = new { } });
            Assert.Contains("GPU access requires Windows", gpu.GetProperty("content")[0].GetProperty("text").GetString());
            var install = await Call(4, "tools/call", new { name = "install_service", arguments = new { } });
            Assert.Contains("the Clocktail service requires Windows", install.GetProperty("content")[0].GetProperty("text").GetString());
            var manual = await Call(5, "tools/call", new { name = "manual", arguments = new { topic = "start" } });
            Assert.Contains("Getting started", manual.GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
