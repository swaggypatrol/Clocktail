using System.Runtime.InteropServices;

namespace Clocktail.Hw;

public sealed class NvApiException(string function, int status)
    : Exception($"{function} failed with NVAPI status {status}")
{
    public int Status { get; } = status;
}

/// <summary>Well-known NVAPI interface IDs.</summary>
public static class NvApiIds
{
    public const uint Initialize = 0x0150E828;
    public const uint EnumPhysicalGpus = 0xE5AC921F;
    public const uint GpuGetFullName = 0xCEEE8E9F;
    public const uint GpuGetVbiosVersionString = 0xA561FD7D;
    public const uint GpuGetPciIdentifiers = 0x2DDFB66E;
    public const uint GpuGetBusId = 0x1BE0B8E5;
    public const uint GpuGetBusSlotId = 0x2A0A350F;
    public const uint SysGetDriverAndBranchVersion = 0x2926AAAD;
    public const uint GpuGetPhysicalFrameBufferSize = 0x46FBEB03;
}

/// <summary>
/// Loads nvapi64.dll and resolves interfaces through nvapi_QueryInterface
/// (LoadLibraryExW with LOAD_LIBRARY_SEARCH_SYSTEM32).
/// </summary>
public sealed unsafe class NvApi : INvApi, IDisposable
{
    private const int LoadLibrarySearchSystem32 = 0x800;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string name, nint file, int flags);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);

    private nint _lib;
    private delegate* unmanaged[Cdecl]<uint, nint> _query;
    /// <summary>Interface pointers by ID, filled on first use under its own lock: the broker's telemetry sampler and its
    /// request threads resolve interfaces at the same time (two GPUs, or a call that holds no GPU's lock), and a Dictionary
    /// written from two threads at once can lose entries or corrupt itself (0.3.7).</summary>
    private readonly Dictionary<uint, nint> _cache = new();

    /// <summary>Open and initialize NVAPI, releasing the library if initialization fails.</summary>
    public static NvApi Load()
    {
        var api = new NvApi();
        api._lib = LoadLibraryExW("nvapi64.dll", 0, LoadLibrarySearchSystem32);
        if (api._lib == 0) throw new DllNotFoundException("Could not load the system nvapi64.dll.");
        try
        {
            nint q = GetProcAddress(api._lib, "nvapi_QueryInterface");
            if (q == 0) throw new EntryPointNotFoundException("nvapi_QueryInterface is missing from nvapi64.dll.");
            api._query = (delegate* unmanaged[Cdecl]<uint, nint>)q;

            nint init = api.Query(NvApiIds.Initialize);
            if (init == 0) throw new EntryPointNotFoundException("NvAPI_Initialize interface is unavailable.");
            int rc = ((delegate* unmanaged[Cdecl]<int>)init)();
            if (rc != 0) throw new NvApiException("NvAPI_Initialize", rc);
            return api;
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    /// <summary>NVAPI bound to a stand-in nvapi_QueryInterface, so the interface cache is tested without a driver.</summary>
    internal static NvApi FromQuery(delegate* unmanaged[Cdecl]<uint, nint> query) => new() { _query = query };

    /// <summary>Returns the function pointer for an interface ID, or 0 when the driver does not expose it.</summary>
    public nint Query(uint id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out var p)) return p;
            p = _query(id);
            _cache[id] = p;
            return p;
        }
    }

    /// <summary>Slot resolution for <see cref="NvClient"/>: 0 when the driver does not expose the interface.</summary>
    public nint Resolve(Slot slot) => Query(Slots.Info(slot).Id);

    public nint Require(uint id, string name)
    {
        nint p = Query(id);
        if (p == 0) throw new NotSupportedException($"The driver does not expose {name} (0x{id:x8}).");
        return p;
    }

    public static int Check(string fn, int rc) => rc == 0 ? 0 : throw new NvApiException(fn, rc);

    public nint[] EnumPhysicalGpus()
    {
        var fn = (delegate* unmanaged[Cdecl]<nint*, uint*, int>)Require(NvApiIds.EnumPhysicalGpus, "NvAPI_EnumPhysicalGPUs");
        nint* handles = stackalloc nint[64];
        uint count;
        Check("NvAPI_EnumPhysicalGPUs", fn(handles, &count));
        if (count > 64) throw new InvalidDataException("NVAPI returned more than 64 GPUs.");
        var result = new nint[count];
        for (int i = 0; i < count; i++) result[i] = handles[i];
        return result;
    }

    private string ShortString(uint id, string name, nint gpu)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Require(id, name);
        byte* b = stackalloc byte[64];
        new Span<byte>(b, 64).Clear();
        Check(name, fn(gpu, b));
        return Marshal.PtrToStringAnsi((nint)b) ?? "";
    }

    public string GpuFullName(nint gpu) => ShortString(NvApiIds.GpuGetFullName, "NvAPI_GPU_GetFullName", gpu);
    public string GpuVbiosVersion(nint gpu) => ShortString(NvApiIds.GpuGetVbiosVersionString, "NvAPI_GPU_GetVbiosVersionString", gpu);

    public (uint DeviceId, uint SubSystemId, uint RevisionId, uint ExtDeviceId) GpuPciIdentifiers(nint gpu)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint*, uint*, uint*, uint*, int>)Require(NvApiIds.GpuGetPciIdentifiers, "NvAPI_GPU_GetPCIIdentifiers");
        uint a, b, c, d;
        Check("NvAPI_GPU_GetPCIIdentifiers", fn(gpu, &a, &b, &c, &d));
        return (a, b, c, d);
    }

    public uint GpuBusId(nint gpu)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Require(NvApiIds.GpuGetBusId, "NvAPI_GPU_GetBusId");
        uint v;
        Check("NvAPI_GPU_GetBusId", fn(gpu, &v));
        return v;
    }

    public uint GpuBusSlotId(nint gpu)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Require(NvApiIds.GpuGetBusSlotId, "NvAPI_GPU_GetBusSlotId");
        uint v;
        Check("NvAPI_GPU_GetBusSlotId", fn(gpu, &v));
        return v;
    }

    public (uint Version, string Branch) DriverAndBranch()
    {
        var fn = (delegate* unmanaged[Cdecl]<uint*, byte*, int>)Require(NvApiIds.SysGetDriverAndBranchVersion, "NvAPI_SYS_GetDriverAndBranchVersion");
        uint v;
        byte* b = stackalloc byte[64];
        new Span<byte>(b, 64).Clear();
        Check("NvAPI_SYS_GetDriverAndBranchVersion", fn(&v, b));
        return (v, Marshal.PtrToStringAnsi((nint)b) ?? "");
    }

    public void Dispose()
    {
        if (_lib != 0) { FreeLibrary(_lib); _lib = 0; }
    }
}
