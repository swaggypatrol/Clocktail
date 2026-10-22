using System.Runtime.InteropServices;

namespace Clocktail.Lab;

public sealed class CudaException(string function, int code, string name) : Exception($"{function} failed: {name} ({code})")
{
    public int Code { get; } = code;
}

/// <summary>
/// The CUDA driver API (nvcuda.dll), bound by name at run time (StabilityLab's binding, trimmed to what the lab launches).
/// It ships with the display driver and JIT-compiles PTX itself: no CUDA toolkit on the machine, the driver is the only
/// dependency.
/// </summary>
public sealed unsafe class CudaApi : IDisposable
{
    private nint _lib;

    private delegate* unmanaged[Cdecl]<uint, int> _init;
    private delegate* unmanaged[Cdecl]<int*, int> _deviceCount;
    private delegate* unmanaged[Cdecl]<int*, int, int> _deviceGet;
    private delegate* unmanaged[Cdecl]<byte*, int, int, int> _deviceGetName, _deviceGetPciBusId;
    private delegate* unmanaged[Cdecl]<int*, int, int, int> _deviceGetAttribute;
    private delegate* unmanaged[Cdecl]<nint*, int, int> _primaryCtxRetain;
    private delegate* unmanaged[Cdecl]<int, int> _primaryCtxRelease;
    private delegate* unmanaged[Cdecl]<nint, int> _ctxSetCurrent;
    private delegate* unmanaged[Cdecl]<int> _ctxSynchronize;
    private delegate* unmanaged[Cdecl]<nint*, byte*, uint, int*, nint*, int> _moduleLoadDataEx;
    private delegate* unmanaged[Cdecl]<nint, int> _moduleUnload;
    private delegate* unmanaged[Cdecl]<nint*, nint, byte*, int> _moduleGetFunction;
    private delegate* unmanaged[Cdecl]<ulong*, nuint, int> _memAlloc;
    private delegate* unmanaged[Cdecl]<ulong, int> _memFree;
    private delegate* unmanaged[Cdecl]<ulong, void*, nuint, int> _memcpyHtoD;
    private delegate* unmanaged[Cdecl]<void*, ulong, nuint, int> _memcpyDtoH;
    private delegate* unmanaged[Cdecl]<nuint*, nuint*, int> _memGetInfo;
    private delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, uint, uint, uint, nint, void**, void**, int> _launchKernel;
    private delegate* unmanaged[Cdecl]<nint*, uint, int> _eventCreate;
    private delegate* unmanaged[Cdecl]<nint, nint, int> _eventRecord;
    private delegate* unmanaged[Cdecl]<nint, int> _eventSynchronize, _eventDestroy;
    private delegate* unmanaged[Cdecl]<float*, nint, nint, int> _eventElapsedTime;
    private delegate* unmanaged[Cdecl]<int, byte**, int> _getErrorName;
    private delegate* unmanaged[Cdecl]<int*, nint, int, nuint, int> _occupancy;

    public static CudaApi Load()
    {
        var api = new CudaApi();
        if (!NativeLibrary.TryLoad("nvcuda.dll", out api._lib))
            throw new DllNotFoundException("nvcuda.dll was not found: is the NVIDIA driver installed?");
        api.Bind();
        api.Check("cuInit", api._init(0));
        return api;
    }

    private nint Sym(string name) => NativeLibrary.TryGetExport(_lib, name, out var p) ? p
        : throw new EntryPointNotFoundException($"the CUDA driver does not export {name}");

    private void Bind()
    {
        _init = (delegate* unmanaged[Cdecl]<uint, int>)Sym("cuInit");
        _deviceCount = (delegate* unmanaged[Cdecl]<int*, int>)Sym("cuDeviceGetCount");
        _deviceGet = (delegate* unmanaged[Cdecl]<int*, int, int>)Sym("cuDeviceGet");
        _deviceGetName = (delegate* unmanaged[Cdecl]<byte*, int, int, int>)Sym("cuDeviceGetName");
        _deviceGetPciBusId = (delegate* unmanaged[Cdecl]<byte*, int, int, int>)Sym("cuDeviceGetPCIBusId");
        _deviceGetAttribute = (delegate* unmanaged[Cdecl]<int*, int, int, int>)Sym("cuDeviceGetAttribute");
        _primaryCtxRetain = (delegate* unmanaged[Cdecl]<nint*, int, int>)Sym("cuDevicePrimaryCtxRetain");
        _primaryCtxRelease = (delegate* unmanaged[Cdecl]<int, int>)Sym("cuDevicePrimaryCtxRelease_v2");
        _ctxSetCurrent = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuCtxSetCurrent");
        _ctxSynchronize = (delegate* unmanaged[Cdecl]<int>)Sym("cuCtxSynchronize");
        _moduleLoadDataEx = (delegate* unmanaged[Cdecl]<nint*, byte*, uint, int*, nint*, int>)Sym("cuModuleLoadDataEx");
        _moduleUnload = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuModuleUnload");
        _moduleGetFunction = (delegate* unmanaged[Cdecl]<nint*, nint, byte*, int>)Sym("cuModuleGetFunction");
        _memAlloc = (delegate* unmanaged[Cdecl]<ulong*, nuint, int>)Sym("cuMemAlloc_v2");
        _memFree = (delegate* unmanaged[Cdecl]<ulong, int>)Sym("cuMemFree_v2");
        _memcpyHtoD = (delegate* unmanaged[Cdecl]<ulong, void*, nuint, int>)Sym("cuMemcpyHtoD_v2");
        _memcpyDtoH = (delegate* unmanaged[Cdecl]<void*, ulong, nuint, int>)Sym("cuMemcpyDtoH_v2");
        _memGetInfo = (delegate* unmanaged[Cdecl]<nuint*, nuint*, int>)Sym("cuMemGetInfo_v2");
        _launchKernel = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, uint, uint, uint, nint, void**, void**, int>)Sym("cuLaunchKernel");
        _eventCreate = (delegate* unmanaged[Cdecl]<nint*, uint, int>)Sym("cuEventCreate");
        _eventRecord = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym("cuEventRecord");
        _eventSynchronize = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuEventSynchronize");
        _eventDestroy = (delegate* unmanaged[Cdecl]<nint, int>)Sym("cuEventDestroy_v2");
        _eventElapsedTime = (delegate* unmanaged[Cdecl]<float*, nint, nint, int>)Sym("cuEventElapsedTime");
        _getErrorName = (delegate* unmanaged[Cdecl]<int, byte**, int>)Sym("cuGetErrorName");
        _occupancy = (delegate* unmanaged[Cdecl]<int*, nint, int, nuint, int>)Sym("cuOccupancyMaxActiveBlocksPerMultiprocessor");
    }

    public string ErrorName(int rc)
    {
        byte* p;
        return _getErrorName(rc, &p) == 0 && p != null ? Marshal.PtrToStringAnsi((nint)p)! : $"CUDA error {rc}";
    }

    public void Check(string fn, int rc)
    {
        if (rc != 0) throw new CudaException(fn, rc, ErrorName(rc));
    }

    public int DeviceCount() { int n; Check("cuDeviceGetCount", _deviceCount(&n)); return n; }
    public int Device(int ordinal) { int d; Check("cuDeviceGet", _deviceGet(&d, ordinal)); return d; }

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

    /// <summary>16 = the SM count, 75/76 = the compute capability.</summary>
    public int Attribute(int dev, int attr) { int v; Check("cuDeviceGetAttribute", _deviceGetAttribute(&v, attr, dev)); return v; }

    public nint PrimaryCtxRetain(int dev) { nint c; Check("cuDevicePrimaryCtxRetain", _primaryCtxRetain(&c, dev)); return c; }
    public void PrimaryCtxRelease(int dev) => _primaryCtxRelease(dev);
    public void CtxSetCurrent(nint ctx) => Check("cuCtxSetCurrent", _ctxSetCurrent(ctx));
    public int CtxSynchronizeRaw() => _ctxSynchronize();

    /// <summary>JIT-compiles PTX text; a failure carries the compiler's log.</summary>
    public nint ModuleLoadPtx(string ptx)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(ptx + "\0");
        var log = new byte[8192];
        int* opts = stackalloc int[2] { 5, 6 }; // CU_JIT_ERROR_LOG_BUFFER, CU_JIT_ERROR_LOG_BUFFER_SIZE_BYTES
        fixed (byte* src = bytes) fixed (byte* lp = log)
        {
            nint* vals = stackalloc nint[2] { (nint)lp, log.Length };
            nint mod;
            int rc = _moduleLoadDataEx(&mod, src, 2, opts, vals);
            if (rc != 0) throw new CudaException("cuModuleLoadDataEx", rc, ErrorName(rc) + "\n" + (Marshal.PtrToStringAnsi((nint)lp) ?? ""));
            return mod;
        }
    }

    public void ModuleUnload(nint mod) { if (mod != 0) _moduleUnload(mod); }

    public nint Function(nint mod, string name)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = b) { nint f; Check("cuModuleGetFunction", _moduleGetFunction(&f, mod, p)); return f; }
    }

    /// <summary>How many blocks of <paramref name="block"/> threads of <paramref name="fn"/> fit on one SM at once.</summary>
    public int MaxActiveBlocksPerSm(nint fn, int block) { int n; Check("cuOccupancyMaxActiveBlocksPerMultiprocessor", _occupancy(&n, fn, block, 0)); return n; }

    public ulong MemAlloc(ulong bytes) { ulong p; Check("cuMemAlloc", _memAlloc(&p, (nuint)bytes)); return p; }
    public void MemFree(ulong p) { if (p != 0) _memFree(p); }
    public (ulong Free, ulong Total) MemInfo() { nuint f, t; Check("cuMemGetInfo", _memGetInfo(&f, &t)); return (f, t); }
    public int MemcpyDtoHRaw(void* dst, ulong src, ulong bytes) => _memcpyDtoH(dst, src, (nuint)bytes);
    public void MemcpyHtoD(ulong dst, void* src, ulong bytes) => Check("cuMemcpyHtoD", _memcpyHtoD(dst, src, (nuint)bytes));

    public int LaunchRaw(nint fn, uint grid, uint block, void** args) => _launchKernel(fn, grid, 1, 1, block, 1, 1, 0, 0, args, null);

    public nint EventCreate() { nint e; Check("cuEventCreate", _eventCreate(&e, 0)); return e; }
    public int EventRecordRaw(nint e) => _eventRecord(e, 0);
    public int EventSynchronizeRaw(nint e) => _eventSynchronize(e);
    public float EventElapsedMs(nint a, nint b) { float ms; Check("cuEventElapsedTime", _eventElapsedTime(&ms, a, b)); return ms; }
    public void EventDestroy(nint e) { if (e != 0) _eventDestroy(e); }

    public void Dispose()
    {
        if (_lib != 0) { NativeLibrary.Free(_lib); _lib = 0; }
    }
}
