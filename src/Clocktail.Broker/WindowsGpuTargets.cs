using Clocktail.Core.Gpu;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Hw;

namespace Clocktail.Broker;

/// <summary>The GPUs the running broker manages, each with its own journal and sentinel under the per-user root.</summary>
internal sealed class WindowsGpuTargets : IGpuTargets
{
    private readonly List<Target> _targets;

    /// <param name="standDown">What each sentinel asks once it is on disk (the judge lock); null asks nothing.</param>
    public WindowsGpuTargets(GpuManager mgr, IHost host, Func<string?>? standDown = null)
        => _targets = mgr.Gpus.Select(g => new Target(g, host, standDown, mgr.Driver.Version == 0 ? null : $"{mgr.Driver.Version / 100}.{mgr.Driver.Version % 100:00}")).ToList();

    public IReadOnlyList<string> Ids => _targets.Select(t => t.StableId).ToList();

    public IGpuTarget? Resolve(string? id)
        => id == null ? _targets.FirstOrDefault()
                      : _targets.FirstOrDefault(t => string.Equals(t.StableId, id, StringComparison.OrdinalIgnoreCase));

    private sealed class Target : IGpuTarget
    {
        public Target(GpuDevice g, IHost host, Func<string?>? standDown, string? driver)
        {
            StableId = g.Identity.StableId;
            Name = g.Identity.Name;
            Vbios = string.IsNullOrWhiteSpace(g.Identity.Vbios) ? null : g.Identity.Vbios;
            Driver = driver;
            Session = new GpuSession(g.NvapiHandle, g.Identity.Name);
            var dir = BrokerPaths.GpuStateDir(StableId);
            Journal = new OwnershipJournal(Path.Combine(dir, "ownership.json"), host, StableId);
            Sentinel = new ApplySentinel(Path.Combine(dir, "apply.lock"), standDown);
        }

        public string StableId { get; }
        public string Name { get; }
        public GpuSession Session { get; }
        public OwnershipJournal Journal { get; }
        public ApplySentinel Sentinel { get; }
        public string? Vbios { get; }
        public string? Driver { get; }
    }
}
