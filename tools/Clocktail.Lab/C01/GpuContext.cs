using System.Reflection;
using System.Security.Cryptography;
using Bwt.Contracts;

namespace Bwt.Gpu;

/// <summary>What this card reports about itself. Everything sizing a workload comes from here, never from a spec sheet.</summary>
public sealed record DeviceInfo(
    string Name,
    string Uuid,
    string PciBusId,
    string? Luid,
    int Ordinal,
    int CcMajor,
    int CcMinor,
    int SmCount,
    long L2Bytes,
    int MaxThreadsPerBlock,
    int MaxThreadsPerSm,
    int WarpSize,
    int MaxSharedPerBlock,
    int MaxSharedPerBlockOptin,
    int MaxSharedPerSm,
    int MaxRegsPerSm,
    long TotalMemBytes,
    int MemoryBusWidthBits,
    int AsyncEngineCount,
    bool CooperativeLaunch,
    bool KernelExecTimeout,
    int CudaDriverApiVersion);

/// <summary>
/// One CUDA device context held by a worker: the driver binding, the primary context, one non-blocking stream and
/// the device's queried properties. A context-fatal error (see <see cref="CudaException.ContextFatal"/>) poisons it:
/// the worker must report DEVICE_LOST and exit, never retry in the same process.
/// </summary>
public sealed class GpuContext : IDisposable
{
    public CudaApi Cuda { get; }
    public int Device { get; }
    public nint Context { get; }
    public nint Stream { get; }
    public DeviceInfo Info { get; }
    public bool Poisoned { get; private set; }

    private readonly List<KernelModule> _modules = [];

    private GpuContext(CudaApi cuda, int device, nint ctx, nint stream, DeviceInfo info)
    {
        Cuda = cuda; Device = device; Context = ctx; Stream = stream; Info = info;
        cuda.ErrorObserved = rc => { if (CudaApi.IsContextFatal(rc)) Poisoned = true; };
    }

    /// <summary>
    /// Opens the target GPU. With several CUDA devices one of <paramref name="uuid"/> / <paramref name="pciBusId"/> must
    /// name it (a CUDA ordinal is never taken to mean the same card as another API's index).
    /// </summary>
    public static GpuContext Open(string? uuid = null, string? pciBusId = null)
    {
        var cuda = CudaApi.Load();
        int count = cuda.DeviceCount();
        if (count == 0) throw new InvalidOperationException("no CUDA device");
        int? chosen = null;
        for (int i = 0; i < count; i++)
        {
            int d = cuda.Device(i);
            bool match = (uuid == null || string.Equals(cuda.DeviceUuid(d), uuid, StringComparison.OrdinalIgnoreCase))
                && (pciBusId == null || PciEquals(cuda.DevicePciBusId(d), pciBusId));
            if (!match) continue;
            if (chosen != null) throw new InvalidOperationException("more than one CUDA device matches: name the target by uuid or PCI bus id");
            chosen = i;
        }
        if (chosen == null) throw new InvalidOperationException($"no CUDA device matches uuid={uuid} pci={pciBusId}");
        if (uuid == null && pciBusId == null && count > 1) throw new InvalidOperationException("several CUDA devices: name the target by uuid or PCI bus id");

        int dev = cuda.Device(chosen.Value);
        nint ctx = cuda.PrimaryCtxRetain(dev);
        cuda.CtxSetCurrent(ctx);
        nint stream = cuda.StreamCreate();
        var info = new DeviceInfo(
            Name: cuda.DeviceName(dev),
            Uuid: cuda.DeviceUuid(dev),
            PciBusId: cuda.DevicePciBusId(dev),
            Luid: cuda.DeviceLuid(dev),
            Ordinal: chosen.Value,
            CcMajor: cuda.Attribute(dev, CudaApi.AttrCcMajor),
            CcMinor: cuda.Attribute(dev, CudaApi.AttrCcMinor),
            SmCount: cuda.Attribute(dev, CudaApi.AttrSmCount),
            L2Bytes: cuda.Attribute(dev, CudaApi.AttrL2CacheSize),
            MaxThreadsPerBlock: cuda.Attribute(dev, CudaApi.AttrMaxThreadsPerBlock),
            MaxThreadsPerSm: cuda.Attribute(dev, CudaApi.AttrMaxThreadsPerSm),
            WarpSize: cuda.Attribute(dev, CudaApi.AttrWarpSize),
            MaxSharedPerBlock: cuda.Attribute(dev, CudaApi.AttrMaxSharedPerBlock),
            MaxSharedPerBlockOptin: cuda.Attribute(dev, CudaApi.AttrMaxSharedPerBlockOptin),
            MaxSharedPerSm: cuda.Attribute(dev, CudaApi.AttrMaxSharedPerSm),
            MaxRegsPerSm: cuda.Attribute(dev, CudaApi.AttrMaxRegsPerSm),
            TotalMemBytes: cuda.DeviceTotalMem(dev),
            MemoryBusWidthBits: cuda.Attribute(dev, CudaApi.AttrGlobalMemoryBusWidth),
            AsyncEngineCount: cuda.Attribute(dev, CudaApi.AttrAsyncEngineCount),
            CooperativeLaunch: cuda.Attribute(dev, CudaApi.AttrCooperativeLaunch) != 0,
            KernelExecTimeout: cuda.Attribute(dev, CudaApi.AttrKernelExecTimeout) != 0,
            CudaDriverApiVersion: cuda.DriverVersion());
        return new GpuContext(cuda, dev, ctx, stream, info);
    }

    private static bool PciEquals(string a, string b)
    {
        static string N(string s) { s = s.Trim().ToLowerInvariant(); int c = s.IndexOf(':'); return c >= 0 && s[..c].TrimStart('0').Length == 0 ? s[(c + 1)..] : s; }
        return N(a).EndsWith(N(b), StringComparison.Ordinal) || N(b).EndsWith(N(a), StringComparison.Ordinal);
    }

    public GpuIdentity Identity(string? vbios = null, string? driver = null, int? nvmlIndex = null)
        => new(Info.Name, Info.Uuid, Info.PciBusId, Info.Luid, vbios, driver, Info.Ordinal, nvmlIndex, Info.CcMajor, Info.CcMinor);

    /// <summary>
    /// Loads kernel image <paramref name="baseName"/> embedded in <paramref name="assembly"/>: "{baseName}.cubin"
    /// (native sm_XY SASS) when it matches this device, else "{baseName}.ptx" (driver JIT). Which one ran is recorded.
    /// </summary>
    public KernelModule LoadModule(Assembly assembly, string baseName)
    {
        string arch = $"sm_{Info.CcMajor}{Info.CcMinor}";
        byte[]? cubin = ReadResource(assembly, $"{baseName}.{arch}.cubin") ?? ReadResource(assembly, $"{baseName}.cubin");
        string format;
        byte[] image;
        nint mod;
        if (cubin != null)
        {
            try { mod = Cuda.ModuleLoad(cubin); image = cubin; format = $"cubin:{arch}"; }
            catch (CudaException e) when (e.Code is 200 or 209 or 218 or 222) { mod = 0; image = []; format = ""; }
        }
        else { mod = 0; image = []; format = ""; }
        if (mod == 0)
        {
            byte[] ptx = ReadResource(assembly, $"{baseName}.ptx")
                ?? throw new FileNotFoundException($"kernel image {baseName}(.cubin|.ptx) is not embedded in {assembly.GetName().Name}");
            mod = Cuda.ModuleLoad(ptx);
            image = ptx;
            format = "ptx:jit";
        }
        var km = new KernelModule(this, baseName, format, Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), mod);
        _modules.Add(km);
        return km;
    }

    private static byte[]? ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Throws on a CUresult; a context-fatal one poisons this context first.</summary>
    public void Check(string fn, int rc)
    {
        if (rc == 0) return;
        if (CudaApi.IsContextFatal(rc)) Poisoned = true;
        throw new CudaException(fn, rc, Cuda.ErrorName(rc));
    }

    public void MarkPoisoned() => Poisoned = true;

    public void Synchronize() => Check("cuStreamSynchronize", Cuda.StreamSynchronizeRaw(Stream));

    public void Dispose()
    {
        if (!Poisoned)
        {
            foreach (var m in _modules) Cuda.ModuleUnload(m.Handle);
            Cuda.StreamDestroy(Stream);
            Cuda.PrimaryCtxRelease(Device);
            Cuda.Dispose();
        }
        // A poisoned CUDA context belongs to this short-lived worker. Do not call into it or unload its library.
    }
}

/// <summary>A loaded kernel image and which form of it the driver took (native cubin or PTX JIT).</summary>
public sealed class KernelModule(GpuContext gpu, string name, string format, string sha256, nint handle)
{
    public string Name { get; } = name;
    public string Format { get; } = format;
    public string Sha256 { get; } = sha256;
    public nint Handle { get; } = handle;

    public nint Function(string entry) => gpu.Cuda.Function(Handle, entry);

    /// <summary>Registers / local (spill) bytes / static shared bytes of one entry point, for the build evidence.</summary>
    public KernelResources Resources(string entry)
    {
        nint f = Function(entry);
        var c = gpu.Cuda;
        return new KernelResources(entry, c.FuncAttribute(f, CudaApi.FuncNumRegs), c.FuncAttribute(f, CudaApi.FuncLocalSizeBytes),
            c.FuncAttribute(f, CudaApi.FuncSharedSizeBytes), c.FuncAttribute(f, CudaApi.FuncMaxThreadsPerBlock),
            c.FuncAttribute(f, CudaApi.FuncBinaryVersion), c.FuncAttribute(f, CudaApi.FuncPtxVersion));
    }
}

public sealed record KernelResources(string Entry, int Registers, int LocalBytes, int StaticSharedBytes, int MaxThreadsPerBlock, int BinaryVersion, int PtxVersion);
