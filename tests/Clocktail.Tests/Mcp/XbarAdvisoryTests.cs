using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Hw;
using Clocktail.Mcp;
using Clocktail.Tests.Domain;

namespace Clocktail.Tests.Mcp;

[Collection("fake-driver")]
public class XbarAdvisoryTests
{
    private sealed class Rig : IDisposable
    {
        public readonly List<BrokerRequest> Requests = [];
        public readonly Card Card;
        public readonly TuningTools Tools;
        public BrokerResponse? Answer = new(true, Result: JsonSerializer.SerializeToElement(new
        {
            applied = new[] { new { outcome = "Applied", restore = "NotNeeded" } },
        }));
        public Exception? TransportFailure;
        public Action? OnRequest;

        public Rig()
        {
            ClockDomainsFixture.Seed();
            var domainQuery = FakeDriver.QueryResponder;
            var domains = FakeDriver.State.ToArray();
            var hw = new FakeVf().Install(Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl,
                Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo);
            var vfQuery = FakeDriver.QueryResponder;
            foreach (var (word, bytes) in domains) FakeDriver.State[word] = bytes;
            foreach (var bytes in new[] { RailsFixture.Info(), RailsFixture.Control(), RailsFixture.Status(), RailsFixture.Devices() })
                FakeDriver.State[BitConverter.ToUInt32(bytes)] = bytes;
            FakeDriver.QueryResponder = q => domainQuery!(q) ?? vfQuery!(q);
            var gpu = new GpuDevice
            {
                Identity = new GpuIdentity(0, "fake RTX 5080", "fake", 0, 0, 0, 0, 1, 0), NvapiHandle = 1,
            };
            Func<BrokerRequest, BrokerResponse?> exchange = req =>
            {
                Requests.Add(req);
                // Exercise the wire envelopes without ever connecting to the installed broker.
                using var wire = new MemoryStream();
                BrokerTransport.WriteRequest(wire, req);
                wire.Position = 0;
                Assert.Equal(req, BrokerTransport.ReadRequest(wire));
                OnRequest?.Invoke();
                if (TransportFailure != null) throw TransportFailure;
                if (Answer == null) return null;
                wire.SetLength(0);
                BrokerTransport.WriteResponse(wire, Answer);
                wire.Position = 0;
                return BrokerTransport.ReadResponse(wire);
            };
            Card = (Card)typeof(Card).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(NvClient), typeof(GpuDevice), typeof(Func<BrokerRequest, BrokerResponse?>)], null)!
                .Invoke([hw, gpu, exchange]);
            Tools = new TuningTools(Card, null!, null!); // AdjustXbar does not sample residency or watch a workload.
        }

        public void Dispose() => Card.Dispose();

        public JsonElement Adjust(double offset = 0, double demand = 0) =>
            JsonDocument.Parse(Tools.AdjustXbar(offset, demand)).RootElement;

        public void Current(int offsetMhz, int demandMv)
        {
            var control = FakeDriver.Current(Slot.ClockClkDomainsGetControl);
            int at = ClockDomainsLayout.OffsetAt(1, 0xf);
            BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(at), offsetMhz * 1000);
            BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(at + 8), demandMv * 1000);
        }
    }

    [Fact]
    public void In_range_300_at_zero_demand_reaches_the_broker_with_advice_and_no_demand_write()
    {
        using var rig = new Rig();
        var result = rig.Adjust(300);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal("applied", result.GetProperty("written").GetString());
        var advisory = Assert.Single(result.GetProperty("advisories").EnumerateArray());
        Assert.Equal("xbar_reference_voltage_demand", advisory.GetProperty("code").GetString());
        Assert.Equal(300, advisory.GetProperty("target_offset_mhz").GetDouble());
        Assert.Equal(0, advisory.GetProperty("target_demand_mv").GetDouble());
        Assert.Equal(20, advisory.GetProperty("reference_demand_mv").GetInt32());
        Assert.Contains("RTX 5090", advisory.GetProperty("source").GetString());
        var request = Assert.Single(rig.Requests);
        Assert.Equal(BrokerRequest.OpExperiment, request.Op);
        Assert.Equal("try", request.Kind);
        var args = JsonDocument.Parse(request.Value!).RootElement;
        Assert.Equal(300_000, args.GetProperty("xbar_khz").GetInt32());
        Assert.False(args.TryGetProperty("xbar_vdemand_uv", out _));
        Assert.False(args.TryGetProperty("writes", out _));
        Assert.True(args.GetProperty("keep_curve").GetBoolean());
        var recorded = Assert.Single(args.GetProperty("context").EnumerateArray(),
            line => line.GetString()!.Contains("xbar_reference_voltage_demand"));
        Assert.True(JsonElement.DeepEquals(advisory, JsonDocument.Parse(recorded.GetString()!).RootElement));
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Theory]
    [InlineData(250, 0, null)]
    [InlineData(250.001, 0, 20)]
    [InlineData(300, 0, 20)]
    [InlineData(300.001, 0, 50)]
    [InlineData(350, 0, 50)]
    [InlineData(300, 20, null)]
    [InlineData(350, 50, null)]
    [InlineData(300, 19, 20)]
    [InlineData(350, 49, 50)]
    [InlineData(250, -25, null)]
    public void Advice_uses_the_final_pair_at_the_reference_boundaries(double offset, double demand, int? reference)
    {
        using var rig = new Rig();
        var result = rig.Adjust(offset, demand);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        if (reference is { } expected)
        {
            var advice = Assert.Single(result.GetProperty("advisories").EnumerateArray());
            Assert.Equal(expected, advice.GetProperty("reference_demand_mv").GetInt32());
            Assert.Equal(offset, advice.GetProperty("target_offset_mhz").GetDouble());
            Assert.Equal(demand, advice.GetProperty("target_demand_mv").GetDouble());
            Assert.Contains("MSVDD", advice.GetProperty("message").GetString());
            Assert.Contains("performance", advice.GetProperty("message").GetString());
            Assert.Contains("does not guarantee stability", advice.GetProperty("message").GetString());
        }
        else Assert.Equal(JsonValueKind.Null, result.GetProperty("advisories").ValueKind);
        var args = JsonDocument.Parse(Assert.Single(rig.Requests).Value!).RootElement;
        Assert.Equal((int)Math.Round(offset * 1000), args.GetProperty("xbar_khz").GetInt32());
        Assert.Equal(demand != 0, args.TryGetProperty("xbar_vdemand_uv", out var sentDemand));
        if (demand != 0) Assert.Equal(demand * 1000, sentDemand.GetInt32());
        Assert.Equal(reference.HasValue, args.GetProperty("context").ToString().Contains("xbar_reference_voltage_demand"));
    }

    [Theory]
    [InlineData(200, 0, 100, 0, 300, 0, 20)]
    [InlineData(300, 20, 50, 0, 350, 20, 50)]
    [InlineData(350, 50, -50, -50, 300, 0, 20)]
    [InlineData(300, 20, 0, -20, 300, 0, 20)]
    [InlineData(350, 50, 0, -30, 350, 20, 50)]
    public void Cumulative_and_demand_only_changes_use_final_values(int currentOffset, int currentDemand, int deltaOffset,
        int deltaDemand, int finalOffset, int finalDemand, int reference)
    {
        using var rig = new Rig();
        rig.Current(currentOffset, currentDemand);
        var result = rig.Adjust(deltaOffset, deltaDemand);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        var advice = Assert.Single(result.GetProperty("advisories").EnumerateArray());
        Assert.Equal(finalOffset, advice.GetProperty("target_offset_mhz").GetDouble());
        Assert.Equal(finalDemand, advice.GetProperty("target_demand_mv").GetDouble());
        Assert.Equal(reference, advice.GetProperty("reference_demand_mv").GetInt32());
        var args = JsonDocument.Parse(Assert.Single(rig.Requests).Value!).RootElement;
        Assert.Equal(deltaOffset != 0, args.TryGetProperty("xbar_khz", out var off));
        if (deltaOffset != 0) Assert.Equal(finalOffset * 1000, off.GetInt32());
        Assert.Equal(deltaDemand != 0, args.TryGetProperty("xbar_vdemand_uv", out var demand));
        if (deltaDemand != 0) Assert.Equal(finalDemand * 1000, demand.GetInt32());
    }

    [Theory]
    [InlineData(250, 20, 50, 0)]
    [InlineData(350, 0, -100, 0)]
    [InlineData(300, 0, 0, 20)]
    [InlineData(350, 20, 0, 30)]
    public void A_final_pair_that_meets_the_advice_does_not_warn(int currentOffset, int currentDemand, int deltaOffset, int deltaDemand)
    {
        using var rig = new Rig();
        rig.Current(currentOffset, currentDemand);
        var result = rig.Adjust(deltaOffset, deltaDemand);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("advisories").ValueKind);
        Assert.Single(rig.Requests);
    }

    [Theory]
    [InlineData(301, 0)]
    [InlineData(-101, 0)]
    [InlineData(300, 51)]
    [InlineData(300, -26)]
    [InlineData(double.NaN, 0)]
    [InlineData(300, double.NaN)]
    [InlineData(300, double.PositiveInfinity)]
    public void Queried_bounds_and_the_standard_demand_range_still_refuse_without_a_request(double offset, double demand)
    {
        using var rig = new Rig();
        var topo = FakeDriver.Current(Slot.ClockClkDomainsGetInfo);
        int at = ClockDomainsLayout.TopologyAt(1) + 0x40;
        BinaryPrimitives.WriteInt16LittleEndian(topo.AsSpan(at), -100);
        BinaryPrimitives.WriteInt16LittleEndian(topo.AsSpan(at + 2), 300);
        var result = rig.Adjust(offset, demand);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Empty(rig.Requests);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Theory]
    [InlineData("demand capability")]
    [InlineData("rail identity")]
    [InlineData("topology")]
    [InlineData("control")]
    [InlineData("rails")]
    [InlineData("offset editability")]
    public void Unsupported_or_unreadable_capabilities_still_refuse_without_a_request(string broken)
    {
        using var rig = new Rig();
        var topo = FakeDriver.Current(Slot.ClockClkDomainsGetInfo);
        int at = ClockDomainsLayout.TopologyAt(1);
        switch (broken)
        {
            case "demand capability": topo[at + 0xc4] = 0; break;
            case "rail identity": BinaryPrimitives.WriteUInt32LittleEndian(topo.AsSpan(at + 0x78), 3); break;
            case "topology": FakeDriver.State.Remove(Slots.Info(Slot.ClockClkDomainsGetInfo).Shape!.Value.Word); break;
            case "control": FakeDriver.State.Remove(Slots.Info(Slot.ClockClkDomainsGetControl).Shape!.Value.Word); break;
            case "rails": FakeDriver.State.Remove(Slots.Info(Slot.VoltVoltRailsGetInfo).Shape!.Value.Word); break;
            case "offset editability": topo.AsSpan(at + 0x40, 4).Clear(); break;
        }
        var result = rig.Adjust(300);
        Assert.False(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Empty(rig.Requests);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void A_configured_cap_still_refuses_even_an_advisory_pair()
    {
        // A separate assembly gets its own static env cap; the fake-driver collection prevents parallel tests while
        // the variable is set. The installed MCP executable is never launched.
        using var rig = new Rig();
        const string variable = "CLOCKTAIL_MCP_MAX_XBAR_MHZ";
        string? was = Environment.GetEnvironmentVariable(variable);
        var load = new AssemblyLoadContext("xbar-cap", isCollectible: true);
        try
        {
            Environment.SetEnvironmentVariable(variable, "275");
            var assembly = load.LoadFromAssemblyPath(typeof(TuningTools).Assembly.Location);
            var cardType = assembly.GetType(typeof(Card).FullName!)!;
            var toolsType = assembly.GetType(typeof(TuningTools).FullName!)!;
            var hw = rig.Card.WithDriver((hw, _) => hw);
            var gpu = new GpuDevice { Identity = new GpuIdentity(0, "fake", "fake", 0, 0, 0, 0, 1, 0), NvapiHandle = 1 };
            Func<BrokerRequest, BrokerResponse?> exchange = req => { rig.Requests.Add(req); return rig.Answer; };
            using var card = (IDisposable)cardType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                [typeof(NvClient), typeof(GpuDevice), typeof(Func<BrokerRequest, BrokerResponse?>)], null)!
                .Invoke([hw, gpu, exchange]);
            var tools = Activator.CreateInstance(toolsType, card, null, null);
            Assert.Equal(275, toolsType.GetField(nameof(TuningTools.MaxXbarMhz))!.GetValue(null));
            var result = JsonDocument.Parse((string)toolsType.GetMethod(nameof(TuningTools.AdjustXbar))!
                .Invoke(tools, [300d, 0d, null])!).RootElement;
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Contains("+275 MHz", result.GetProperty("error").GetString());
            Assert.Empty(rig.Requests);
            Assert.Equal(0, FakeDriver.Sets);
        }
        finally { Environment.SetEnvironmentVariable(variable, was); load.Unload(); }
    }

    [Theory]
    [InlineData(BrokerCode.WindowBusy, "none")]
    [InlineData(BrokerCode.Failed, "unknown")]
    public void Broker_failures_keep_their_error_code_result_and_write_outcome(string code, string written)
    {
        using var rig = new Rig();
        var details = JsonSerializer.SerializeToElement(new { detail = "fake broker refusal" });
        rig.Answer = new BrokerResponse(false, Error: "refused", Code: code, Result: details);
        var result = rig.Adjust(300);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(code, result.GetProperty("code").GetString());
        Assert.Equal("refused", result.GetProperty("error").GetString());
        Assert.Equal(written, result.GetProperty("written").GetString());
        Assert.Equal(details.ToString(), result.GetProperty("result").ToString());
        Assert.Single(result.GetProperty("advisories").EnumerateArray());
        Assert.Single(rig.Requests);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transport_failures_are_not_hidden_by_advice(bool sent)
    {
        using var rig = new Rig();
        rig.Answer = null;
        if (sent) rig.TransportFailure = new IOException("fake disconnected transport");
        var result = rig.Adjust(350);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(sent ? "no_answer" : BrokerCode.BrokerUnreachable, result.GetProperty("code").GetString());
        Assert.Equal(sent ? "unknown" : "none", result.GetProperty("written").GetString());
        Assert.Single(result.GetProperty("advisories").EnumerateArray());
        Assert.Single(rig.Requests);
    }

    [Fact]
    public void Advice_does_not_hide_unreadable_readback_or_claim_a_curve_check()
    {
        using var rig = new Rig();
        rig.OnRequest = () => FakeDriver.State.Remove(FakeVf.InfoWord);
        var result = rig.Adjust(300);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("applied", result.GetProperty("written").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("curve_intact").ValueKind);
        Assert.NotEmpty(result.GetProperty("warning").GetString()!);
        Assert.Single(result.GetProperty("advisories").EnumerateArray());
        Assert.Single(rig.Requests);
    }

    [Theory]
    [InlineData(300, 0, 20)]
    [InlineData(400, 20, 50)]
    public void The_report_retains_a_caution_attributed_to_the_reference_card(int offset, int demand, int reference)
    {
        var settings = JsonSerializer.SerializeToElement(new { clock_domains = new { xbar = new { offset_mhz = offset, vdemand_mv = demand } } });
        var row = Assert.Single(SettingsTools.ReportRows(settings, JsonSerializer.SerializeToElement(new { })), r => r.Name == "XBAR (crossbar)");
        Assert.Equal(SettingsTools.Look, row.Mark);
        Assert.Contains("reference RTX 5090 advice", row.Note);
        Assert.Contains($"+{reference} mV", row.Note);
        Assert.Contains("MSVDD-floor/performance", row.Note);
        Assert.Contains("does not guarantee stability", row.Note);
    }

    [Fact]
    public void The_explicit_tool_signature_and_full_and_lite_counts_stay_the_same()
    {
        var method = typeof(TuningTools).GetMethod(nameof(TuningTools.AdjustXbar))!;
        Assert.Equal(typeof(string), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(["offsetDeltaMhz", "vdemandDeltaMv", "reason"], parameters.Select(p => p.Name));
        Assert.Equal([typeof(double), typeof(double), typeof(string)], parameters.Select(p => p.ParameterType));
        Assert.All(parameters, p => Assert.True(p.IsOptional));
        Assert.Equal(0d, parameters[0].DefaultValue);
        Assert.Equal(0d, parameters[1].DefaultValue);
        Assert.Null(parameters[2].DefaultValue);
        var tools = typeof(TuningTools).Assembly.GetTypes().SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()).OfType<ModelContextProtocol.Server.McpServerToolAttribute>();
        Assert.Equal(53, tools.Count()); // upstream added game_test
        Assert.Equal(22, LiteMode.Tools.Count);
        Assert.DoesNotContain("adjust_xbar", LiteMode.Tools);
    }
}
