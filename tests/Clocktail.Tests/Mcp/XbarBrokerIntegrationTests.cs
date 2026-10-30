using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Hw;
using Clocktail.Mcp;
using Clocktail.Tests.Domain;

namespace Clocktail.Tests.Mcp;

/// <summary>Explicit MCP calls through the real broker, registry and transactions to fake driver setters.</summary>
[Collection("fake-driver")]
public class XbarBrokerIntegrationTests
{
    private sealed class Target(GpuDevice device, OwnershipJournal journal, ApplySentinel sentinel) : IGpuTarget, IGpuTargets
    {
        public string StableId => device.Identity.StableId;
        public string Name => device.Identity.Name;
        public GpuSession Session { get; } = new(device.NvapiHandle, device.Identity.Name);
        public OwnershipJournal Journal => journal;
        public ApplySentinel Sentinel => sentinel;
        public IReadOnlyList<string> Ids => [StableId];
        public IGpuTarget? Resolve(string? id) => id == null || id == StableId ? this : null;
    }

    private sealed class Rig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
        private readonly Card _card;
        private readonly TuningTools _tools;
        public readonly List<byte[]> SetRequests = [];
        public readonly List<BrokerRequest> Requests = [];
        public readonly OwnershipJournal Journal;
        public BrokerResponse? Response;
        public string Log => File.ReadAllText(TuningLog.PathBeside(Journal.Path));
        public Experiment? Session => Experiment.Load(Experiment.PathBeside(Journal.Path));
        public static int OffsetAt => ClockDomainsLayout.OffsetAt(1, 0xf);
        public static int Offset => Read(FakeDriver.Current(Slot.ClockClkDomainsGetControl), OffsetAt);
        public static int Demand => Read(FakeDriver.Current(Slot.ClockClkDomainsGetControl), OffsetAt + 8);
        public static int Read(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));

        public Rig(int currentOffset = 0, int currentDemand = 0)
        {
            ClockDomainsFixture.Seed();
            var domainQuery = FakeDriver.QueryResponder!;
            var domainApply = FakeDriver.RequestApplier!;
            var domains = FakeDriver.State.ToArray();
            new FakeVf().Install();
            var vfQuery = FakeDriver.QueryResponder!;
            var vfApply = FakeDriver.RequestApplier!;
            foreach (var (word, bytes) in domains) FakeDriver.State[word] = bytes;
            foreach (var bytes in new[] { RailsFixture.Info(), RailsFixture.Control(), RailsFixture.Status(), RailsFixture.Devices() })
                FakeDriver.State[BitConverter.ToUInt32(bytes)] = bytes;
            BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.Current(Slot.ClockClkDomainsGetControl).AsSpan(OffsetAt), currentOffset * 1000);
            BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.Current(Slot.ClockClkDomainsGetControl).AsSpan(OffsetAt + 8), currentDemand * 1000);
            // A non-stock curve must survive every XBAR trial without being rewritten.
            BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.ControlWord].AsSpan(VfLayout.OffsetField(70)), 15_000);
            uint domainWord = Slots.Info(Slot.ClockClkDomainsGetControl).Shape!.Value.Word;
            FakeDriver.QueryResponder = request => domainQuery(request) ?? vfQuery(request);
            FakeDriver.RequestApplier = (state, request) =>
            {
                SetRequests.Add((byte[])request.Clone());
                return BitConverter.ToUInt32(request) == domainWord ? domainApply(state, request) : vfApply(state, request);
            };
            var hw = new NvClient(FakeDriver.ApiFor(
                [Slot.ClockClkDomainsGetInfo, Slot.ClockClkDomainsGetControl,
                 Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetControl, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltDevicesGetInfo,
                 Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetControl, Slot.ClockClientClkVfPointsGetStatus, Slot.GetPstates20],
                [Slot.ClockClkDomainsSetControl, Slot.ClockClientClkVfPointsSetControl]));
            var device = new GpuDevice
            {
                Identity = new GpuIdentity(0, "fake RTX 5080", "fake", 0, 0, 0, 0, 1, 0), NvapiHandle = 1,
            };
            Directory.CreateDirectory(_directory);
            var host = new FakeHost();
            Journal = new OwnershipJournal(Path.Combine(_directory, "ownership.json"), host, device.Identity.StableId);
            var target = new Target(device, Journal, new ApplySentinel(Path.Combine(_directory, "apply.lock")));
            var broker = new BrokerDispatcher(hw, target, host, canWrite: true);
            var begun = broker.FromClient(new BrokerRequest(BrokerRequest.OpExperiment, target.StableId, Kind: "begin", Value: """{"game":"fake XBAR integration"}"""));
            Assert.True(begun.Ok, begun.Error);
            Assert.Equal(0, FakeDriver.Sets);
            Func<BrokerRequest, BrokerResponse?> exchange = request =>
            {
                Requests.Add(request);
                // Exercise the wire envelopes too, but never connect to a pipe or installed service.
                using var wire = new MemoryStream();
                BrokerTransport.WriteRequest(wire, request);
                wire.Position = 0;
                Response = broker.FromClient(BrokerTransport.ReadRequest(wire));
                wire.SetLength(0);
                BrokerTransport.WriteResponse(wire, Response);
                wire.Position = 0;
                return BrokerTransport.ReadResponse(wire);
            };
            _card = (Card)typeof(Card).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(NvClient), typeof(GpuDevice), typeof(Func<BrokerRequest, BrokerResponse?>)], null)!
                .Invoke([hw, device, exchange]);
            _tools = new TuningTools(_card, null!, null!);
        }

        public JsonElement Adjust(double offset, double demand = 0) => JsonDocument.Parse(_tools.AdjustXbar(offset, demand)).RootElement;

        public void AssertOutcome(JsonElement answer, bool success)
        {
            Assert.NotNull(Response);
            Assert.Equal(success, Response.Ok);
            Assert.Equal(Response.Ok, answer.GetProperty("success").GetBoolean());
            Assert.Equal(WriteOutcome.Of(Response.Ok, Response.Code, Response.Result), answer.GetProperty("written").GetString());
            var request = Assert.Single(Requests);
            Assert.Equal(BrokerRequest.OpExperiment, request.Op);
            Assert.Equal("try", request.Kind);
            Assert.False(File.Exists(DailyProfile.PathBeside(Journal.Path)));
            Assert.Equal(FakeDriver.Sets, SetRequests.Count);
        }

        public void AssertLoggedAdvice(JsonElement answer, int? reference)
        {
            if (reference is null)
            {
                Assert.Equal(JsonValueKind.Null, answer.GetProperty("advisories").ValueKind);
                Assert.DoesNotContain("xbar_reference_voltage_demand", Log);
                return;
            }
            var advice = Assert.Single(answer.GetProperty("advisories").EnumerateArray());
            Assert.Equal(reference.Value, advice.GetProperty("reference_demand_mv").GetInt32());
            var line = Assert.Single(Log.Split('\n'), line => line.Contains("xbar_reference_voltage_demand"));
            var recorded = JsonDocument.Parse(line[line.IndexOf('{')..]).RootElement;
            Assert.True(JsonElement.DeepEquals(advice, recorded));
        }

        public void Dispose()
        {
            _card.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(300, 20)]
    [InlineData(350, 50)]
    public void Offset_only_reaches_one_setter_without_touching_demand(double offset, int reference)
    {
        using var rig = new Rig();
        var before = (byte[])FakeDriver.Current(Slot.ClockClkDomainsGetControl).Clone();
        var curve = FakeVf.Offsets();
        var answer = rig.Adjust(offset);
        rig.AssertOutcome(answer, success: true);
        Assert.Equal("applied", answer.GetProperty("written").GetString());
        Assert.True(answer.GetProperty("curve_intact").GetBoolean());
        var write = Assert.Single(rig.SetRequests);
        Assert.Equal(Slots.Info(Slot.ClockClkDomainsSetControl).Shape!.Value.Word, BitConverter.ToUInt32(write));
        Assert.Equal(1u << 1, BitConverter.ToUInt32(write, ClockDomainsLayout.ControlMask));
        Assert.Equal(offset * 1000, Rig.Read(write, Rig.OffsetAt));
        Assert.Equal(0, Rig.Read(write, Rig.OffsetAt + 4));
        Assert.Equal(0, Rig.Read(write, Rig.OffsetAt + 8));
        BinaryPrimitives.WriteInt32LittleEndian(before.AsSpan(Rig.OffsetAt), (int)(offset * 1000));
        Assert.Equal(before, FakeDriver.Current(Slot.ClockClkDomainsGetControl));
        Assert.Equal(curve, FakeVf.Offsets());
        Assert.Equal((int)(offset * 1000), rig.Session!.Trial!.XbarKhz);
        Assert.Null(rig.Session.Trial.XbarVdemandUv);
        Assert.Equal("clockdomain", Assert.Single(rig.Journal.Inspect().Items).Kind);
        rig.AssertLoggedAdvice(answer, reference);
    }

    [Theory]
    [InlineData(300, 20, null)]
    [InlineData(350, 20, 50)]
    public void Combined_changes_write_demand_before_offset(double offset, double demand, int? reference)
    {
        using var rig = new Rig();
        var answer = rig.Adjust(offset, demand);
        rig.AssertOutcome(answer, success: true);
        Assert.Equal(2, rig.SetRequests.Count);
        Assert.Equal(new[] { (0, (int)(demand * 1000)), ((int)(offset * 1000), (int)(demand * 1000)) },
            rig.SetRequests.Select(write => (Rig.Read(write, Rig.OffsetAt), Rig.Read(write, Rig.OffsetAt + 8))));
        Assert.Equal((int)(offset * 1000), Rig.Offset);
        Assert.Equal((int)(demand * 1000), Rig.Demand);
        Assert.Equal(2, rig.Journal.Inspect().Items.Count);
        rig.AssertLoggedAdvice(answer, reference);
    }

    [Fact]
    public void Lowering_demand_at_an_existing_high_offset_writes_only_demand_and_records_advice()
    {
        using var rig = new Rig(currentOffset: 350, currentDemand: 50);
        var answer = rig.Adjust(0, -30);
        rig.AssertOutcome(answer, success: true);
        var write = Assert.Single(rig.SetRequests);
        Assert.Equal(350_000, Rig.Read(write, Rig.OffsetAt));
        Assert.Equal(20_000, Rig.Read(write, Rig.OffsetAt + 8));
        Assert.Equal((350_000, 20_000), (Rig.Offset, Rig.Demand));
        Assert.Equal("vdemand", Assert.Single(rig.Journal.Inspect().Items).Kind);
        rig.AssertLoggedAdvice(answer, 50);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refused_or_dropped_offset_is_a_real_failed_transaction_despite_advice(bool drop)
    {
        using var rig = new Rig();
        FakeDriver.Mode = drop ? FakeDriver.SetMode.ClampField : FakeDriver.SetMode.RefuseUntouched;
        FakeDriver.Field = Rig.OffsetAt;
        FakeDriver.MisbehaveOnce = true;
        var answer = rig.Adjust(300);
        rig.AssertOutcome(answer, success: false);
        Assert.Equal("none", answer.GetProperty("written").GetString());
        Assert.Equal(rig.Response!.Code, answer.GetProperty("code").GetString());
        Assert.Equal(rig.Response.Error ?? rig.Response.Code ?? "failed", answer.GetProperty("error").GetString());
        Assert.True(JsonElement.DeepEquals(rig.Response.Result!.Value, answer.GetProperty("result")));
        Assert.Equal("Failed", answer.GetProperty("result").GetProperty("applied")[0].GetProperty("outcome").GetString());
        Assert.Single(rig.SetRequests);
        Assert.Equal((0, 0), (Rig.Offset, Rig.Demand));
        Assert.Empty(rig.Journal.Inspect().Items);
        Assert.Null(rig.Session!.Trial);
        rig.AssertLoggedAdvice(answer, 20);
    }

    [Fact]
    public void A_combined_trial_puts_back_landed_demand_when_the_offset_is_refused()
    {
        using var rig = new Rig();
        FakeDriver.OnSet = () =>
        {
            if (FakeDriver.Sets != 2) return;
            FakeDriver.Mode = FakeDriver.SetMode.RefuseUntouched;
            FakeDriver.MisbehaveOnce = true;
        };
        var answer = rig.Adjust(350, 20);
        rig.AssertOutcome(answer, success: false);
        Assert.Equal("none", answer.GetProperty("written").GetString());
        Assert.Equal(new[] { (0, 20_000), (350_000, 20_000), (0, 0) },
            rig.SetRequests.Select(write => (Rig.Read(write, Rig.OffsetAt), Rig.Read(write, Rig.OffsetAt + 8))));
        Assert.Equal((0, 0), (Rig.Offset, Rig.Demand));
        Assert.Empty(rig.Journal.Inspect().Items);
        Assert.Null(rig.Session!.Trial);
        Assert.Contains("put_back", answer.GetProperty("result").ToString());
        rig.AssertLoggedAdvice(answer, 50);
    }
}
