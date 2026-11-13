using System.Text.Json;
using System.Text.Json.Serialization;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Hw;

namespace Clocktail.Cli;

/// <summary>
/// What every command shares once an adapter is selected: the client, the session, and the one way to perform a
/// privileged operation. A running broker performs it (it holds the single write window for the whole machine);
/// otherwise the identical dispatcher runs here and decides for itself what an unelevated process may do (reads
/// and recovery's look-and-release: yes; a write: no, with the reason). Command files take this instead of opening
/// the driver and the state files themselves, so there is exactly one write route and one place that knows where
/// the journal lives.
/// </summary>
internal sealed class CliContext
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Per-GPU state directory: the journal and the sentinel live next to each other, keyed by the stable id.
    // It is the same %LocalAppData%\Clocktail the broker uses, because the broker runs as this user.
    private readonly string _stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail");
    private BrokerDispatcher? _local;

    public CliContext(GpuManager mgr, GpuDevice gpu)
    {
        Mgr = mgr;
        Gpu = gpu;
        Hw = new NvClient(mgr.NvApi);
        Session = new GpuSession(gpu.NvapiHandle, gpu.Identity.Name);
    }

    public GpuManager Mgr { get; }
    public GpuDevice Gpu { get; }
    public NvClient Hw { get; }
    public GpuSession Session { get; }
    public string StableId => Gpu.Identity.StableId;

    /// <summary>
    /// The op, performed by the broker when one answers. Otherwise a read runs in this process; a write does only
    /// when this process is elevated, else it fails loudly: an unelevated fallback could not write the card anyway
    /// and would only split the journal from the broker's (see <see cref="BrokerCode.BrokerUnreachable"/>).
    /// </summary>
    public BrokerResponse Perform(BrokerRequest req) =>
        BrokerClient.TryCall(req, connectMs: 700)
        ?? (req.IsWrite && Elevation.NeedsElevationHere()
            ? new BrokerResponse(false, "the Clocktail broker did not answer and this process is not elevated; nothing was written (start or reinstall the broker)", Code: BrokerCode.BrokerUnreachable)
            : Local().FromClient(req));

    public BrokerRequest WriteRequest(string kind, string key, string value, bool takeOver = false, bool allowCreate = false, string[]? enableSlots = null) =>
        new(BrokerRequest.OpWrite, StableId, Kind: kind, Key: key, Value: value, AllowCreate: allowCreate, TakeOver: takeOver, EnableSlots: enableSlots);

    /// <summary>Print the response as the op's own JSON, or the error envelope; exit code from <see cref="BrokerResponse.Ok"/>.</summary>
    public static int Print(BrokerResponse r)
    {
        Console.WriteLine(r.Result is { } res ? JsonSerializer.Serialize(res, Json) : JsonSerializer.Serialize(new { success = false, code = r.Code, error = r.Error }, Json));
        return r.Ok ? 0 : 1;
    }

    /// <summary>
    /// Several requests on one command line print as one {success, applied[]}; one failure is enough, so nothing
    /// is piled onto an uncertain state.
    /// </summary>
    public int PrintAll(IEnumerable<BrokerRequest> reqs)
    {
        var applied = new List<JsonElement>();
        foreach (var req in reqs)
        {
            var r = Perform(req);
            if (!(r.Result is { } res && res.TryGetProperty("applied", out var a) && a.GetArrayLength() > 0)) return Print(r);   // an error envelope: as-is
            applied.Add(a[0]);
            if (!r.Ok) { Console.WriteLine(JsonSerializer.Serialize(new { success = false, applied }, Json)); return 1; }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, applied }, Json));
        return 0;
    }

    internal BrokerDispatcher Local()
    {
        if (_local != null) return _local;
        var host = new SystemHost();
        var dir = Path.Combine(_stateRoot, StableId);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Looked up per ask: a packaged copy may appear later. A run that waits for the service's operations ("quiet") cannot
        // ask this process, so here it is judged as one that does not: every write after it took the lock stands down.
        var judge = BrokerDispatcher.Judge(() => JudgeLock.Live(JudgeLock.Candidates(Path.Combine(_stateRoot, "judge.lock"), localAppData), host, out _) is { } run
            ? run with { Waits = false } : null);
        var journal = new OwnershipJournal(Path.Combine(dir, "ownership.json"), host, StableId);
        var sentinel = new ApplySentinel(Path.Combine(dir, "apply.lock"), judge);   // asked again once the sentinel is on disk
        // AuthorizeProfileChange is intentionally left null here: when the broker pipe does not answer, local CLI
        // fallback (even if elevated) must fail closed on client profile mutations instead of bypassing confirmation.
        return _local = new BrokerDispatcher(Hw, new OneGpu(this, journal, sentinel), host, canWrite: !Elevation.NeedsElevationHere(), judge);
    }

    /// <summary>The selected adapter as the one GPU a local dispatcher manages, with the journal and sentinel built for it.</summary>
    private sealed class OneGpu(CliContext ctx, OwnershipJournal journal, ApplySentinel sentinel) : IGpuTarget, IGpuTargets
    {
        public string StableId => ctx.StableId;
        public string Name => ctx.Gpu.Identity.Name;
        public GpuSession Session => ctx.Session;
        public OwnershipJournal Journal => journal;
        public ApplySentinel Sentinel => sentinel;
        public IReadOnlyList<string> Ids => [StableId];
        public IGpuTarget? Resolve(string? id) => id == null || string.Equals(id, StableId, StringComparison.OrdinalIgnoreCase) ? this : null;
    }
}
