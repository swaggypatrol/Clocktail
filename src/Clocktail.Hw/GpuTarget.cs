using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Clocktail.Hw;

public readonly record struct PciAddress(uint Domain, uint Bus, uint Device, uint Function)
{
    public override string ToString() => $"{Domain:x4}:{Bus:x2}:{Device:x2}.{Function:x}";
    /// <summary>Parses the full PCI address used to cross-check Windows, NVML and CUDA devices.</summary>
    public static PciAddress? Parse(string? text)
    {
        var p = (text ?? "").Trim().Split(':');
        if (p.Length != 3) return null;
        var slot = p[2].Split('.');
        if (slot.Length != 2) return null;
        static bool Hex(string value, out uint number) =>
            uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number)
            && value.All(Uri.IsHexDigit);
        return Hex(p[0], out uint domain) && domain == 0 && Hex(p[1], out uint bus) && bus <= 255
            && Hex(slot[0], out uint device) && device <= 31 && Hex(slot[1], out uint function) && function == 0
            ? new(domain, bus, device, function) : null;
    }
}

public sealed record DisplayAdapter(string InstanceId, PciAddress? Pci)
{
    public bool Nvidia => InstanceId.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase);
    public uint? Id(string field)
    {
        var m = Regex.Match(InstanceId, field + "_([0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

/// <summary>The identity proved before any tuning read/write. No ordinal or model-name selection.</summary>
public sealed record GpuTarget(string Pci, string Name, uint DeviceId, uint SubSystemId, int Major, int Minor,
    string InstanceId, int NvidiaAdapters, int DisplayAdapters)
{
    public bool Validated => DeviceId == 0x2b8510de;
    public void Validate()
    {
        if (PciAddress.Parse(Pci) == null || Major != 12 || Minor != 0 || (DeviceId & 0xffff) != 0x10de)
            throw new NotSupportedException("the target must be an NVIDIA compute capability 12.0 GPU at a verified PCI address");
    }

    public static DisplayAdapter Match(IReadOnlyList<DisplayAdapter> present, string pci, uint deviceId, uint subsystemId)
    {
        var address = PciAddress.Parse(pci) ?? throw new NotSupportedException("invalid target PCI address");
        var matches = present.Where(p => p.Pci == address).ToList();
        if (matches.Count != 1 || !matches[0].Nvidia || matches[0].Id("DEV") != deviceId >> 16 || matches[0].Id("SUBSYS") != subsystemId)
            throw new NotSupportedException($"PnP and the driver disagree about the GPU at {pci}; no fallback is allowed");
        return matches[0];
    }
}

/// <summary>Windows device metadata only. Adapted from Etude's guarded target path; never loads a GPU library.</summary>
public static class DisplayInventory
{
    public static IReadOnlyList<DisplayAdapter> Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GPU access requires Windows");
        const string filter = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        const uint flags = 0x300;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint n, filter, flags) != 0 || n is 0 or > 1_000_000)
                throw new NotSupportedException("cannot list present display adapters");
            var buffer = new char[n];
            uint rc = CM_Get_Device_ID_ListW(filter, buffer, n, flags);
            if (rc == 0x1a) continue;
            if (rc != 0) throw new NotSupportedException("cannot read present display adapters");
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(id => new DisplayAdapter(id, Address(id))).ToList();
        }
        throw new NotSupportedException("display adapters changed repeatedly during identification");
    }

    public static void Verify(GpuTarget target) => GpuTarget.Match(Read(), target.Pci, target.DeviceId, target.SubSystemId);

    private static PciAddress? Address(string id)
    {
        if (CM_Locate_DevNodeW(out uint node, id, 0) != 0) return null;
        uint? Property(uint property)
        {
            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = property };
            uint size = 4;
            var data = new byte[4];
            return CM_Get_DevNode_PropertyW(node, ref key, out uint type, data, ref size, 0) == 0 && type == 7 && size == 4 ? BitConverter.ToUInt32(data) : null;
        }
        return Property(23) is uint bus && Property(30) is uint slot ? PciAddress.Parse($"0000:{bus:x2}:{slot >> 16:x2}.{slot & 0xffff:x}") : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, [Out] byte[] data, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint size, uint flags);
}
