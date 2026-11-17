using System.Runtime.InteropServices;

namespace Bwt.Gpu;

public sealed class CudaException(string function, int code, string name, string? log = null)
    : Exception($"{function} failed: {name} ({code})" + (string.IsNullOrEmpty(log) ? "" : "\n" + log))
{
    public string Function { get; } = function;
    public int Code { get; } = code;
    public string CodeName { get; } = name;
    /// <summary>The context is unusable after this error (sticky error / device lost): the process must exit and the
    /// host treats the run as DEVICE_LOST, never as a mismatch.</summary>
    public bool ContextFatal => CudaApi.IsContextFatal(Code);
}

/// <summary>
/// The CUDA driver API (nvcuda.dll from the display driver), bound by name at run time. Extended from Clocktail Lab's
/// binding (the user's own code). No CUDA toolkit is needed at run time: kernels are embedded cubin (sm_120) with PTX
/// fallback, built by tools/build_kernels.py with the pinned NVRTC.
/// <para>Method naming: <c>Xxx()</c> throws <see cref="CudaException"/>; <c>XxxRaw()</c> returns the CUresult.</para>
/// </summary>
public sealed unsafe class CudaApi : IDisposable
{
    // CUresult values that leave the context unusable (sticky) or mean the device went away.
    public const int ErrIllegalAddress = 700, ErrLaunchTimeout = 702, ErrLaunchFailed = 719,
        ErrEccUncorrectable = 214, ErrHardwareStackError = 714, ErrIllegalInstruction = 715, ErrMisalignedAddress = 716,
        ErrInvalidAddressSpace = 717, ErrInvalidPc = 718, ErrDeviceUnavailable = 46, ErrContextIsDestroyed = 709,
        ErrUnknown = 999;

    public static bool IsContextFatal(int rc) => rc is ErrIllegalAddress or ErrLaunchTimeout or ErrLaunchFailed or ErrEccUncorrectable
        or ErrHardwareStackError or ErrIllegalInstruction or ErrMisalignedAddress or ErrInvalidAddressSpace or ErrInvalidPc
        or ErrDeviceUnavailable or ErrContextIsDestroyed or ErrUnknown;

    // CUdevice_attribute
    public const int AttrMaxThreadsPerBlock = 1, AttrMaxSharedPerBlock = 8, AttrWarpSize = 10,
        AttrSmCount = 16, AttrKernelExecTimeout = 17,
        AttrGlobalMemoryBusWidth = 37, AttrL2CacheSize = 38, AttrMaxThreadsPerSm = 39,
        AttrAsyncEngineCount = 40, AttrCcMajor = 75, AttrCcMinor = 76, AttrMaxSharedPerSm = 81, AttrMaxRegsPerSm = 82,
        AttrCooperativeLaunch = 95, AttrMaxSharedPerBlockOptin = 97;

    // CUfunction_attribute
    public const int FuncMaxThreadsPerBlock = 0, FuncSharedSizeBytes = 1, FuncLocalSizeBytes = 3,
        FuncNumRegs = 4, FuncPtxVersion = 5, FuncBinaryVersion = 6;

    // CUjit_option
    public const int JitErrorLogBuffer = 5, JitErrorLogBufferSize = 6;

    public const uint StreamNonBlocking = 1;
    public const uint EventDefault = 0;

    private nint _lib;

    private delegate* unmanaged[Cdecl]<uint, int> _init;
    private delegate* unmanaged[Cdecl]<int*, int> _driverGetVersion;
    private delegate* unmanaged[Cdecl]<int*, int> _deviceCount;
    private delegate* unmanaged[Cdecl]<int*, int, int> _deviceGet;
    private delegate* unmanaged[Cdecl]<int*, byte*, int> _deviceByPci;
    private delegate* unmanaged[Cdecl]<byte*, int, int, int> _deviceGetName, _deviceGetPciBusId;
    private delegate* unmanaged[Cdecl]<int*, int, int, int> _deviceGetAttribute;
    private delegate* unmanaged[Cdecl]<nuint*, int, int> _deviceTotalMem;
    private delegate* unmanaged[Cdecl]<byte*, int, int> _deviceGetUuid;
    private delegate* unmanaged[Cdecl]<byte*, uint*, int, int> _deviceGetLuid;
    private delegate* unmanaged[Cdecl]<nint*, int, int> _primaryCtxRetain;
    private delegate* unmanaged[Cdecl]<int, int> _primaryCtxRelease;
    private delegate* unmanaged[Cdecl]<nint, int> _ctxSetCurrent;
    private delegate* unmanaged[Cdecl]<nint*, uint, int> _streamCreate;
    private delegate* unmanaged[Cdecl]<nint, int> _streamDestroy, _streamSynchronize;
    private delegate* unmanaged[Cdecl]<nint*, byte*, uint, int*, nint*, int> _moduleLoadDataEx;
    private delegate* unmanaged[Cdecl]<nint, int> _moduleUnload;
    private delegate* unmanaged[Cdecl]<nint*, nint, byte*, int> _moduleGetFunction;
    private delegate* unmanaged[Cdecl]<int*, int, nint, int> _funcGetAttribute;
    private delegate* unmanaged[Cdecl]<int*, nint, int, nuint, int> _occupancy;
    private delegate* unmanaged[Cdecl]<ulong*, nuint, int> _memAlloc;
    private delegate* unmanaged[Cdecl]<ulong, int> _memFree;
    private delegate* unmanaged[Cdecl]<ulong, void*, nuint, int> _memcpyHtoD;
    private delegate* unmanaged[Cdecl]<void*, ulong, nuint, int> _memcpyDtoH;
    private delegate* unmanaged[Cdecl]<ulong, uint, nuint, nint, int> _memsetD32Async;
    private delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, uint, uint, uint, nint, void**, void**, int> _launchKernel;
    private delegate* unmanaged[Cdecl]<nint*, uint, int> _eventCreate;
    private delegate* unmanaged[Cdecl]<nint, nint, int> _eventRecord;
    private delegate* unmanaged[Cdecl]<nint, int> _eventSynchronize, _eventDestroy;
    private delegate* unmanaged[Cdecl]<float*, nint, nint, int> _eventElapsedTime;
    private delegate* unmanaged[Cdecl]<int, byte**, int> _getErrorName;

    public static CudaApi Load()
    {
        var api = new CudaApi();
        if (!NativeLibrary.TryLoad("nvcuda.dll", out api._lib))
            throw new DllNotFoundException("nvcuda.dll was not found: is the NVIDIA display driver installed?");
        api.Bind();
        api.Check("cuInit", api._init(0));
        return api;
    }

    public nint Sym(string name) => NativeLibrary.TryGetExport(_lib, name, out var p) ? p
        : throw new EntryPointNotFoundException($"the CUDA driver does not export {name}");

    private void Bind()
    {
        _init = (delegate* unmanaged[Cdecl]<uint, int>)Sym("cuInit");
        _driverGetVersion = (delegate* unmanaged[Cdecl]<int*, int>)Sym("cuDriverGetVersion");
        _deviceCount = (delegate* unmanaged[Cdecl]<int*, int>)Sym("cuDeviceGetCount");
        _deviceGet = (delegate* unmanaged[Cdecl]<int*, int, int>)Sym("cuDeviceGet");
        _deviceByPci = (delegate* unmanaged[Cdecl]<int*, byte*, int>)Sym("cuDeviceGetByPCIBusId");
        _deviceGetName = (delegate* unmanaged[Cdecl]<byte*, int, int, int>)Sym("cuDeviceGetName");
        _deviceGetPciBusId = (delegate* unmanaged[Cdecl]<byte*, int, int, int>)Sym("cuDeviceGetPCIBusId");
        _deviceGetAttribute = (delegate* unmanaged[Cdecl]<int*, int, int, int>)Sym("cuDeviceGetAttribute");
        _deviceTotalMem = (delegate* unmanaged[Cdecl]<nuint*, int, int>)Sym("cuDeviceTotalMem_v2");
        _deviceGetUuid = (delegate* unmanaged[Cdecl]<byte*, int, int>)Sym("cuDeviceGetUuid_v2");
        _deviceGetLuid = (delegate* unmanaged[Cdecl]<byte*, uint*, int, int>)Sym("cuDeviceGetLuid");
        _primaryCtxRetain = (delegate* unmanaged[Cdecl]<nint*, int, int>)Sym("cuDevicePrimaryCtxRetain");
        _primaryCtxRelease = (delegate* unmanaged[Cdecl]<int, int>)Sym("cuDevicePrimaryCtxRelease_v2");
        _ctxSetCurrent = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuCtxSetCurrent");
        _streamCreate = (delegate* unmanaged[Cdecl]<nint*, uint, int>)Sym("cuStreamCreate");
        _streamDestroy = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuStreamDestroy_v2");
        _streamSynchronize = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuStreamSynchronize");
        _moduleLoadDataEx = (delegate* unmanaged[Cdecl]<nint*, byte*, uint, int*, nint*, int>)Sym("cuModuleLoadDataEx");
        _moduleUnload = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuModuleUnload");
        _moduleGetFunction = (delegate* unmanaged[Cdecl]<nint*, nint, byte*, int>)Sym("cuModuleGetFunction");
        _funcGetAttribute = (delegate* unmanaged[Cdecl]<int*, int, nint, int>)Sym("cuFuncGetAttribute");
        _occupancy = (delegate* unmanaged[Cdecl]<int*, nint, int, nuint, int>)Sym("cuOccupancyMaxActiveBlocksPerMultiprocessor");
        _memAlloc = (delegate* unmanaged[Cdecl]<ulong*, nuint, int>)Sym("cuMemAlloc_v2");
        _memFree = (delegate* unmanaged[Cdecl]<ulong, int>)Sym("cuMemFree_v2");
        _memcpyHtoD = (delegate* unmanaged[Cdecl]<ulong, void*, nuint, int>)Sym("cuMemcpyHtoD_v2");
        _memcpyDtoH = (delegate* unmanaged[Cdecl]<void*, ulong, nuint, int>)Sym("cuMemcpyDtoH_v2");
        _memsetD32Async = (delegate* unmanaged[Cdecl]<ulong, uint, nuint, nint, int>)Sym("cuMemsetD32Async");
        _launchKernel = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, uint, uint, uint, nint, void**, void**, int>)Sym("cuLaunchKernel");
        _eventCreate = (delegate* unmanaged[Cdecl]<nint*, uint, int>)Sym("cuEventCreate");
        _eventRecord = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym("cuEventRecord");
        _eventSynchronize = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuEventSynchronize");
        _eventDestroy = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuEventDestroy_v2");
        _eventElapsedTime = (delegate* unmanaged[Cdecl]<float*, nint, nint, int>)Sym("cuEventElapsedTime");
        _getErrorName = (delegate* unmanaged[Cdecl]<int, byte**, int>)Sym("cuGetErrorName");
    }

    public string ErrorName(int rc)
    {
        byte* p;
        return _getErrorName(rc, &p) == 0 && p != null ? Marshal.PtrToStringAnsi((nint)p)! : $"CUDA error {rc}";
    }

    internal Action<int>? ErrorObserved { get; set; }

    public void Check(string fn, int rc)
    {
        if (rc != 0) { ErrorObserved?.Invoke(rc); throw new CudaException(fn, rc, ErrorName(rc)); }
    }

    public int DriverVersion() { int v; Check("cuDriverGetVersion", _driverGetVersion(&v)); return v; }
    public int DeviceCount() { int n; Check("cuDeviceGetCount", _deviceCount(&n)); return n; }
    public int Device(int ordinal) { int d; Check("cuDeviceGet", _deviceGet(&d, ordinal)); return d; }

    public int DeviceByPci(string pci)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(pci + "\0");
        int device;
        fixed (byte* p = text) Check("cuDeviceGetByPCIBusId", _deviceByPci(&device, p));
        return device;
    }

    public string DeviceName(int dev)
    {
        byte* b = stackalloc byte[256];
        Check("cuDeviceGetName", _deviceGetName(b, 256, dev));
        return Marshal.PtrToStringAnsi((nint)b) ?? "";
    }

    public string DevicePciBusId(int dev)
    {
        byte* b = stackalloc byte[64];
        Check("cuDeviceGetPCIBusId", _deviceGetPciBusId(b, 64, dev));
        return Marshal.PtrToStringAnsi((nint)b) ?? "";
    }

    /// <summary>"GPU-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", the same text NVML reports.</summary>
    public string DeviceUuid(int dev)
    {
        byte* u = stackalloc byte[16];
        Check("cuDeviceGetUuid_v2", _deviceGetUuid(u, dev));
        var h = Convert.ToHexString(new ReadOnlySpan<byte>(u, 16)).ToLowerInvariant();
        return $"GPU-{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    /// <summary>The adapter LUID as DXGI prints it ("HighPart:LowPart" hex), or null when the driver has none (TCC).</summary>
    public string? DeviceLuid(int dev)
    {
        byte* l = stackalloc byte[8];
        uint mask;
        if (_deviceGetLuid(l, &mask, dev) != 0) return null;
        uint low = BitConverter.ToUInt32(new ReadOnlySpan<byte>(l, 4));
        int high = BitConverter.ToInt32(new ReadOnlySpan<byte>(l + 4, 4));
        return $"{high:x8}:{low:x8}";
    }

    public long DeviceTotalMem(int dev) { nuint b; Check("cuDeviceTotalMem", _deviceTotalMem(&b, dev)); return (long)b; }
    public int Attribute(int dev, int attr) { int v; Check("cuDeviceGetAttribute", _deviceGetAttribute(&v, attr, dev)); return v; }

    public nint PrimaryCtxRetain(int dev) { nint c; Check("cuDevicePrimaryCtxRetain", _primaryCtxRetain(&c, dev)); return c; }
    public void PrimaryCtxRelease(int dev) => _primaryCtxRelease(dev);
    public void CtxSetCurrent(nint ctx) => Check("cuCtxSetCurrent", _ctxSetCurrent(ctx));

    public nint StreamCreate(uint flags = StreamNonBlocking) { nint s; Check("cuStreamCreate", _streamCreate(&s, flags)); return s; }
    public void StreamDestroy(nint s) { if (s != 0) _streamDestroy(s); }
    public int StreamSynchronizeRaw(nint s) => _streamSynchronize(s);

    /// <summary>Loads a cubin / fatbin (bytes) or PTX (ASCII bytes, NUL-terminated). A failure carries the JIT log.</summary>
    public nint ModuleLoad(ReadOnlySpan<byte> image)
    {
        byte[] data = image.ToArray();
        if (data.Length > 0 && data[^1] != 0 && data[0] != 0x7f) // PTX text: make sure it is NUL-terminated (cubin starts with ELF 0x7f)
            data = [.. data, 0];
        var log = new byte[16384];
        int* opts = stackalloc int[2] { JitErrorLogBuffer, JitErrorLogBufferSize };
        fixed (byte* src = data) fixed (byte* lp = log)
        {
            nint* vals = stackalloc nint[2] { (nint)lp, log.Length };
            nint mod;
            int rc = _moduleLoadDataEx(&mod, src, 2, opts, vals);
            if (rc != 0) throw new CudaException("cuModuleLoadDataEx", rc, ErrorName(rc), Marshal.PtrToStringAnsi((nint)lp));
            return mod;
        }
    }

    public void ModuleUnload(nint mod) { if (mod != 0) _moduleUnload(mod); }

    public nint Function(nint mod, string name)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = b) { nint f; Check($"cuModuleGetFunction({name})", _moduleGetFunction(&f, mod, p)); return f; }
    }

    public int FuncAttribute(nint fn, int attr) { int v; Check("cuFuncGetAttribute", _funcGetAttribute(&v, attr, fn)); return v; }

    public int MaxActiveBlocksPerSm(nint fn, int block, int dynamicSmem = 0)
    { int n; Check("cuOccupancyMaxActiveBlocksPerMultiprocessor", _occupancy(&n, fn, block, (nuint)dynamicSmem)); return n; }

    public ulong MemAlloc(ulong bytes) { ulong p; Check("cuMemAlloc", _memAlloc(&p, (nuint)bytes)); return p; }
    public void MemFree(ulong p) { if (p != 0) _memFree(p); }

    public void MemcpyHtoD(ulong dst, void* src, ulong bytes) => Check("cuMemcpyHtoD", _memcpyHtoD(dst, src, (nuint)bytes));
    public void MemcpyDtoH(void* dst, ulong src, ulong bytes) => Check("cuMemcpyDtoH", _memcpyDtoH(dst, src, (nuint)bytes));
    public int MemsetD32AsyncRaw(ulong dst, uint value, ulong count, nint stream) => _memsetD32Async(dst, value, (nuint)count, stream);

    public void MemcpyDtoH<T>(Span<T> dst, ulong src) where T : unmanaged
    { fixed (T* p = dst) MemcpyDtoH(p, src, (ulong)(dst.Length * sizeof(T))); }

    /// <summary>Launches a one-dimensional grid using pointers to each kernel argument.</summary>
    public int LaunchRaw(nint fn, uint grid, uint block, uint sharedBytes, nint stream, void** args)
        => _launchKernel(fn, grid, 1, 1, block, 1, 1, sharedBytes, stream, args, null);

    public nint EventCreate(uint flags = EventDefault) { nint e; Check("cuEventCreate", _eventCreate(&e, flags)); return e; }
    public int EventRecordRaw(nint e, nint stream) => _eventRecord(e, stream);
    public int EventSynchronizeRaw(nint e) => _eventSynchronize(e);
    public float EventElapsedMs(nint a, nint b) { float ms; Check("cuEventElapsedTime", _eventElapsedTime(&ms, a, b)); return ms; }
    public void EventDestroy(nint e) { if (e != 0) _eventDestroy(e); }

    public void Dispose()
    {
        if (_lib != 0) { NativeLibrary.Free(_lib); _lib = 0; }
    }
}
