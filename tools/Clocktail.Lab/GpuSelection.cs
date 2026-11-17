using Clocktail.Hw;

namespace Clocktail.Lab;

internal static class GpuSelection
{
    internal static T Select<T>(IEnumerable<T> devices, string api, string? pci, Func<T, string> pciFor,
        string? uuid = null, Func<T, string>? uuidFor = null)
    {
        PciAddress? address = pci == null ? null
            : PciAddress.Parse(pci) ?? throw new NotSupportedException($"invalid target PCI address {pci}");
        var matches = devices.Where(device =>
            (address == null || PciAddress.Parse(pciFor(device)) == address)
            && (uuid == null || string.Equals(uuidFor!(device), uuid, StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToArray();
        string target = (pci, uuid) switch
        {
            (null, null) => "",
            (null, _) => $" with UUID {uuid}",
            (_, null) => $" at PCI {pci}",
            _ => $" at PCI {pci} with UUID {uuid}",
        };
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"{api} has no GPU{target}"),
            _ when target.Length == 0 => throw new InvalidOperationException($"{api} has several GPUs; specify --gpu-pci"),
            _ => throw new InvalidOperationException($"{api} has several GPUs{target}"),
        };
    }
}
