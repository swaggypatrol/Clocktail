using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Writes;
using Clocktail.Hw;
using Clocktail.Tests.Domain;
using CliProgram = Clocktail.Cli.Program;

namespace Clocktail.Tests.Cli;

/// <summary>Regression tests for CLI `--profile` preview/fingerprint forwarding, stale preview rejection, and elevated local fallback fail-closed behavior.</summary>
[Collection("fake-driver")]
public class CliProfileTests : IDisposable
{
    private const int Board = 0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-cli-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = 0xFFFFFFFF };

    public CliProfileTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    private sealed class Target(OwnershipJournal j, ApplySentinel s, GpuSession g) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => g;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    private BrokerDispatcher PowerDispatcher(bool canWrite = true)
    {
        FakeDriver.Reset();
        var info = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(info, 4, 1u << Board);
        int eInfo = PowerRequestsLayout.InfoEntry(Board);
        W(info, eInfo, 1); W(info, eInfo + 8, 400_000); W(info, eInfo + 0xc, 575_000); W(info, eInfo + 0x10, 600_000);
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = info;
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        uint controlWord = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != controlWord) return null;
            var a = (byte[])q.Clone();
            uint cmd = U(q, PowerRequestsLayout.ControlEntry(Board) + 4);
            W(a, PowerRequestsLayout.ControlEntry(Board), _requests.GetValueOrDefault(cmd, 0xFFFFFFFF));
            return a;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            int e = PowerRequestsLayout.ControlEntry(Board);
            _requests[U(req, e + 4)] = U(req, e);
            return state;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl],
            [Slot.ClientPwrPoliciesSetControl]));
        return new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: canWrite);
    }

    [Theory]
    [InlineData("keep", "preview", "keep", null)]
    [InlineData("adopt", "preview-adopt", "adopt", null)]
    [InlineData("preset:daily560", "preview", "preset", "daily560")]
    public void RunProfileCommand_fetches_preview_and_forwards_exact_fingerprint(
        string verbArg, string expectedPreviewKind, string expectedMutateKind, string? expectedValue)
    {
        var sent = new List<BrokerRequest>();
        BrokerResponse FakePerform(BrokerRequest req)
        {
            sent.Add(req);
            if (req.Kind == expectedPreviewKind)
                return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true, values_fingerprint = "fp-123" }));
            return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true }));
        }

        var res = CliProgram.RunProfileCommand(FakePerform, "gpu-A", verbArg);
        Assert.True(res.Ok);
        Assert.Equal(2, sent.Count);
        Assert.Equal((BrokerRequest.OpProfile, "gpu-A", expectedPreviewKind, expectedValue, (string?)null),
            (sent[0].Op, sent[0].GpuId, sent[0].Kind, sent[0].Value, sent[0].ExpectedProfileFingerprint));
        Assert.Equal((BrokerRequest.OpProfile, "gpu-A", expectedMutateKind, expectedValue, "fp-123"),
            (sent[1].Op, sent[1].GpuId, sent[1].Kind, sent[1].Value, sent[1].ExpectedProfileFingerprint));
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("adopt")]
    [InlineData("preset:daily560")]
    public void RunProfileCommand_aborts_without_mutation_when_preview_fails_or_lacks_fingerprint(string verbArg)
    {
        var calls = new List<BrokerRequest>();
        var failRes = CliProgram.RunProfileCommand(req =>
        {
            calls.Add(req);
            return new BrokerResponse(false, "preview unavailable", Code: BrokerCode.BrokerUnreachable);
        }, "gpu-A", verbArg);
        Assert.False(failRes.Ok);
        Assert.Single(calls);

        calls.Clear();
        var missingFpRes = CliProgram.RunProfileCommand(req =>
        {
            calls.Add(req);
            return new BrokerResponse(true, Result: JsonSerializer.SerializeToElement(new { success = true }));
        }, "gpu-A", verbArg);
        Assert.False(missingFpRes.Ok);
        Assert.Equal(BrokerCode.CannotWrite, missingFpRes.Code);
        Assert.Single(calls);
    }

    [Fact]
    public void RunProfileCommand_refuses_stale_preview_when_hardware_changes_before_commit()
    {
        var d = PowerDispatcher(canWrite: true);
        d.AuthorizeProfileChange = _ => true;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);

        int phase = 0;
        var res = CliProgram.RunProfileCommand(req =>
        {
            var ans = d.FromClient(req);
            if (++phase == 1) _requests[0xfe] = 520_000;   // card changed after CLI received preview
            return ans;
        }, "gpu-A", "keep");

        Assert.False(res.Ok);
        Assert.Equal(BrokerCode.CannotWrite, res.Code);
        Assert.False(File.Exists(DailyProfile.PathBeside(_journal.Path)));
    }

    [Fact]
    public void Elevated_local_fallback_default_authorizer_fails_closed_for_cli_profile_mutations()
    {
        // Simulates CliContext.Local() when running elevated without the broker pipe: canWrite=true, AuthorizeProfileChange=null.
        var d = PowerDispatcher(canWrite: true);
        Assert.Null(d.AuthorizeProfileChange);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 550_000)).Ok);

        // Read-only preview still works, but keep/stock/off fail closed locally rather than bypassing consent.
        Assert.True(CliProgram.RunProfileCommand(d.FromClient, "gpu-A", "preview").Ok);
        var keep = CliProgram.RunProfileCommand(d.FromClient, "gpu-A", "keep");
        Assert.False(keep.Ok);
        Assert.Equal(BrokerCode.CannotWrite, keep.Code);
        Assert.False(File.Exists(DailyProfile.PathBeside(_journal.Path)));
        Assert.False(CliProgram.RunProfileCommand(d.FromClient, "gpu-A", "stock").Ok);
        Assert.False(CliProgram.RunProfileCommand(d.FromClient, "gpu-A", "off").Ok);
    }
}
