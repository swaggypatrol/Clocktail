using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain;
using Clocktail.Domain.Game;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Broker;

internal sealed class WindowsGameAccess(GpuManager manager, GpuDevice device, IGpuTarget target) : IGameAccess
{
    private readonly NvClient _hw = new(manager.NvApi);
    public GpuTarget Identity => device.Target ?? throw new NotSupportedException("the card host has no verified target identity");
    public void Verify()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the game card host requires Windows");
        var identity = Identity;
        identity.Validate();
        var present = DisplayInventory.Read();
        GpuTarget.Match(present, identity.Pci, identity.DeviceId, identity.SubSystemId);
        // nvlddmkm does not reliably name an adapter in 153 events. Refuse ambiguity, never recover every adapter.
        if (present.Count(p => p.Nvidia) != 1) throw new NotSupportedException("game testing requires exactly one NVIDIA adapter for reset attribution");
        var pci = manager.Nvml!.Pci(device.NvmlHandle);
        var address = PciAddress.Parse(identity.Pci)!.Value;
        var apiId = manager.NvApi.GpuPciIdentifiers(device.NvapiHandle);
        if (PciAddress.Parse(pci.BusId) != address || pci.DeviceId != identity.DeviceId || pci.SubSystemId != identity.SubSystemId
            || manager.Nvml.ComputeCapability(device.NvmlHandle) != (12, 0)
            || manager.NvApi.GpuBusId(device.NvapiHandle) != address.Bus || manager.NvApi.GpuBusSlotId(device.NvapiHandle) != address.Device
            || apiId.DeviceId != identity.DeviceId || apiId.SubSystemId != identity.SubSystemId)
            throw new NotSupportedException("the selected PnP/NVML/NVAPI identity no longer agrees");
        string exe = Environment.GetEnvironmentVariable("CLOCKTAIL_LAB_EXE") ?? Path.Combine(AppContext.BaseDirectory, "clocktail-lab.exe");
        if (!File.Exists(exe)) throw new NotSupportedException("the broker requires the guarded clocktail-lab.exe beside it");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "game-info", "--gpu-pci", identity.Pci, "--device-id", identity.DeviceId.ToString("x8"), "--subsystem-id", identity.SubSystemId.ToString("x8") }) psi.ArgumentList.Add(arg);
        using var job = new KillOnCloseJob();
        using var child = Process.Start(psi) ?? throw new InvalidOperationException("the device proof did not start");
        try
        {
            if (!job.Add(child)) throw new InvalidOperationException("the device proof could not be contained");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(20_000)) throw new TimeoutException("the context-free CUDA/Vulkan device proof timed out");
            if (child.ExitCode != 0) throw new NotSupportedException("CUDA/Vulkan target proof failed: " + stderr.GetAwaiter().GetResult().Trim());
            using var result = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            if (!result.RootElement.GetProperty("verified").GetBoolean() || result.RootElement.GetProperty("pci").GetString() != identity.Pci)
                throw new NotSupportedException("invalid CUDA/Vulkan identity proof");
        }
        finally { ChildProcess.Stop(child); }
    }

    public void BeforeStart()
    {
        if (!Identity.Validated && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clocktail", "game-model-" + Identity.DeviceId.ToString("x8") + ".confirmed")))
            throw new NotSupportedException("confirm this unvalidated GPU model in the Clocktail window first");
        if (manager.Nvml!.Utilization(device.NvmlHandle).Gpu > 50 || manager.Nvml.Memory(device.NvmlHandle).Used > 4UL << 30)
            throw new InvalidOperationException("other work occupies the selected GPU; close it before the game test");
    }

    public GameSnapshot Read()
    {
        // This is the only process that reads the game test's curve. NvClient supplies declared shapes, canaries and the loss latch.
        var curve = VfReader.Read(_hw, target.Session); // unsupported layouts are refused by the parser
        if (curve.EditableCount > VfRules.WritablePoints) throw new NotSupportedException("the curve exceeds the revision-1 setter's point range");
        var power = PowerRequestsReader.Read(_hw, target.Session);
        if (power.Ordinary.Mw is not uint wall) throw new NotSupportedException("the board wall cannot be read/restored without creating a request");
        var cells = FactoryStock.Survey(_hw, target.Session, (_, _) => null, (_, _) => null)
            .Select(c => new ProfileWrite(c.Kind, c.Key, c.Now ?? throw new InvalidDataException("unreadable game baseline cell"))).ToDictionary(GameSnapshot.Id);
        var extra = target.Journal.Inspect().Items.Select(c => new ProfileWrite(c.Kind, c.Key, c.Original))
            .Concat(DailyProfile.Load(DailyProfile.PathBeside(target.Journal.Path))?.Writes ?? []);
        foreach (var c in extra)
        {
            var path = WriteRegistry.Find(c.Kind) ?? throw new NotSupportedException("unknown owned setting in game baseline");
            var cell = path.Prepare(_hw, target.Session, c.Key, c.Value, WriteOptions.Recovery).Cell;
            cells[GameSnapshot.Id(c)] = c with { Value = OwnedWriter.Look(_hw, target.Session, cell) ?? throw new InvalidDataException("owned game setting could not be read") };
        }
        foreach (string required in new[] { "vf/core", "power/fe", "clocklimit/min", "clocklimit/max", "clocklimit/voltage", "pstates20/core", "pstates20/memory" })
        {
            if (!cells.ContainsKey(required)) throw new NotSupportedException("missing required game baseline setting: " + required);
        }
        cells["power/f8"] = new("power", "f8", power.Additional.Mw?.ToString(CultureInfo.InvariantCulture) ?? "absent");
        string encoded = VfOffsets.Encode(curve.Offsets());
        if (cells["vf/core"].Value != encoded) throw new InvalidDataException("the curve changed during its baseline read");
        return new(Identity, encoded, power.CapMw ?? wall, power.Board.MinMw, (int)(curve.Points.Max(p => p.EffectiveKhz) / 1000),
            manager.Nvml!.Memory(device.NvmlHandle).Total, cells.Values.OrderBy(GameSnapshot.Id).ToList(),
            curve.Mode == VfMode.Regional ? curve.Points.Select(VfEffectiveCurve.Displacement).ToArray() : null);
    }
}
