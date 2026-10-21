using System.Runtime.InteropServices;
using System.Text;

namespace Clocktail.Hw;

public sealed class NvmlException(string function, int code)
    : Exception($"{function} failed with NVML status {code} ({NvmlApi.Describe(code)})")
{
    public int Code { get; } = code;
}

public enum NvmlClock : uint { Graphics = 0, Sm = 1, Memory = 2, Video = 3 }

public readonly record struct NvmlMemory(ulong Total, ulong Free, ulong Used);
public readonly record struct NvmlUtilization(uint Gpu, uint Memory);
public readonly record struct NvmlPci(uint Domain, uint Bus, uint Device, uint DeviceId, uint SubSystemId, string BusId);

/// <summary>One field of nvmlDeviceGetFieldValues: its own status (0 = read), and the value as its type gives it.</summary>
public readonly record struct NvmlFieldValue(uint Id, int Status, int Type, ulong Raw)
{
    /// <summary>The value by nvmlValueType_t: 0 double, 1 uint, 2 ulong (32-bit on Windows), 3 ulonglong, 4 longlong,
    /// 5 int, 6 ushort. Null when the field was not read.</summary>
    public double? Value => Status != 0 ? null : Type switch
    {
        0 => BitConverter.Int64BitsToDouble((long)Raw),
        1 or 2 => (uint)Raw,
        3 => Raw,
        4 => (long)Raw,
        5 => (int)(uint)Raw,
        6 => (ushort)Raw,
        _ => null,
    };
}

/// <summary>The NVML field ids Clocktail reads (public nvml.h), read on the reference card (driver 617.42, 2026-10-20):
/// the board's instantaneous power, where nvmlDeviceGetPowerUsage on this card is close to a 1 s average. The
/// time-in-limit counters (field 74 and the like, nvidia-smi's "Clocks Event Reasons Counters") are not used: 30 s at the
/// 600 W wall with sw_power_cap set added nothing to the power one, nvidia-smi's included.</summary>
public static class NvmlField
{
    public const uint PowerAverageMw = 185, PowerInstantMw = 186;
}

/// <summary>nvmlTemperatureThresholds_t, the ones the 5090 answers (absolute °C: 96, 93, 90 on the reference card).</summary>
public enum NvmlTemperatureThreshold : uint { Shutdown = 0, Slowdown = 1, GpuMax = 3 }

/// <summary>
/// Thin, dynamically bound wrapper over nvml.dll.
/// <para>
/// What this process holds of NVML (its initialisation and every device handle) belongs to the driver instance it was
/// opened against. A device restart or a driver update replaces that instance under the process, and NVML does not
/// survive being called through the old one: on 2026-10-19 21:48:29Z a <c>pnputil /restart-device</c> of a GPU left hung
/// by a TDR tore the driver down while the broker, the tray and the MCP server each had a call blocked in NVML, and all
/// three died of an access violation inside it within 15 ms. A call already in flight cannot be saved from that; what this
/// class does is make sure that once NVML has answered that the driver is gone (<see cref="MeansDriverGone"/>), or the
/// owner has learnt it some other way (<see cref="MarkLost"/>), nothing calls into that state again, the next read or
/// nvmlShutdown alike: every call then fails as it would have, without touching the library, and the owner opens a fresh
/// process (the broker's supervisor, the tray's) to see the driver that came back.
/// </para>
/// </summary>
public sealed unsafe class NvmlApi : IDisposable
{
    public const int Uninitialized = 1, DriverNotLoaded = 9, GpuIsLost = 15, ResetRequired = 16, LibRmVersionMismatch = 18;

    private nint _lib;
    private bool _inited;
    private volatile bool _lost;
    private int _lostCode;

    private delegate* unmanaged[Cdecl]<int> _init, _shutdown;
    private delegate* unmanaged[Cdecl]<uint*, int> _count;
    private delegate* unmanaged[Cdecl]<uint, nint*, int> _byIndex;
    private delegate* unmanaged[Cdecl]<nint, byte*, uint, int> _name;
    private delegate* unmanaged[Cdecl]<nint, byte*, int> _pci;
    private delegate* unmanaged[Cdecl]<nint, uint, uint*, int> _temp;
    private delegate* unmanaged[Cdecl]<nint, uint*, int> _powerUsage, _powerLimit, _powerDefault, _powerEnforced, _fanCount;
    private delegate* unmanaged[Cdecl]<nint, uint*, uint*, int> _powerConstraints;
    private delegate* unmanaged[Cdecl]<nint, uint, uint*, int> _clock, _maxClock;
    private delegate* unmanaged[Cdecl]<nint, uint*, int> _util, _mem, _memV2;
    private delegate* unmanaged[Cdecl]<nint, uint, uint*, int> _fanSpeed;
    private delegate* unmanaged[Cdecl]<nint, ulong*, int> _throttle;
    private delegate* unmanaged[Cdecl]<nint, byte*, uint, int> _vbios;
    private delegate* unmanaged[Cdecl]<nint, int, FieldValue*, int> _fields;
    private delegate* unmanaged[Cdecl]<nint, MarginTemperature*, int> _margin;
    private delegate* unmanaged[Cdecl]<nint, uint*, int> _pstate;
    private delegate* unmanaged[Cdecl]<nint, uint, uint*, int> _threshold;

    /// <summary>nvmlFieldValue_t: 40 bytes, the value union 8-aligned at +32.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FieldValue { public uint FieldId, ScopeId; public long Timestamp, LatencyUsec; public int ValueType, Return; public ulong Value; }

    /// <summary>nvmlMarginTemperature_v1_t: the version word (size | 1 &lt;&lt; 24), then the margin in °C.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MarginTemperature { public uint Version; public int MarginC; }

    public static string Describe(int code) => code switch
    {
        0 => "success", 1 => "uninitialized", 2 => "invalid argument", 3 => "not supported",
        4 => "no permission", 5 => "already initialized", 6 => "not found", 7 => "insufficient size",
        8 => "insufficient power", 9 => "driver not loaded", 10 => "timeout", 12 => "library not found",
        13 => "function not found", 15 => "gpu is lost", 16 => "reset required", 17 => "operating system error",
        18 => "library and driver versions differ", 999 => "unknown", _ => "unrecognized"
    };

    /// <summary>
    /// The answers that say the driver this process opened NVML against is gone, so nothing it holds is good any more:
    /// uninitialized (after a successful init, the library's own state went), driver not loaded, gpu is lost, reset
    /// required, and a library/driver version mismatch (the driver was updated
    /// under the process). "Unknown" is left out: NVML also says it for a failure of one call, and a latch on it would turn
    /// one flaky sensor into a process restarting forever.
    /// </summary>
    public static bool MeansDriverGone(int code) => code is Uninitialized or DriverNotLoaded or GpuIsLost or ResetRequired or LibRmVersionMismatch;

    /// <summary>True once NVML said the driver is gone or <see cref="MarkLost"/> was called; never cleared.</summary>
    public bool Lost => _lost;

    /// <summary>The status that latched <see cref="Lost"/> (0 while not lost).</summary>
    public int LostCode => _lostCode;

    /// <summary>Stop calling into this NVML for good, for a loss learnt elsewhere (an NVAPI handle invalidated by the same
    /// driver restart). Calls from now on fail with <paramref name="code"/> without touching the library.</summary>
    public void MarkLost(int code = GpuIsLost)
    {
        if (_lost) return;
        _lostCode = code;
        _lost = true;
    }

    public static NvmlApi Load()
    {
        string path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!NativeLibrary.TryLoad(File.Exists(path) ? path : "nvml.dll", out var lib))
            throw new DllNotFoundException("nvml.dll could not be loaded (is the NVIDIA driver installed?).");
        var api = FromExports(name => NativeLibrary.TryGetExport(lib, name, out var p) ? p : 0);
        api._lib = lib;
        return api;
    }

    /// <summary>Bind the entry points <paramref name="export"/> resolves (0 for one that is missing) and initialise. The
    /// loader passes nvml.dll's exports; tests pass stand-ins.</summary>
    internal static NvmlApi FromExports(Func<string, nint> export)
    {
        var api = new NvmlApi();
        api.Bind(export);
        int rc = api._init();
        if (rc != 0) throw new NvmlException("nvmlInit_v2", rc);
        api._inited = true;
        return api;
    }

    private static nint Sym(Func<string, nint> export, string name, bool optional = false)
    {
        nint p = export(name);
        if (p != 0 || optional) return p;
        throw new EntryPointNotFoundException($"nvml.dll does not export {name}");
    }

    private void Bind(Func<string, nint> e)
    {
        _init = (delegate* unmanaged[Cdecl]<int>)Sym(e, "nvmlInit_v2");
        _shutdown = (delegate* unmanaged[Cdecl]<int>)Sym(e, "nvmlShutdown");
        _count = (delegate* unmanaged[Cdecl]<uint*, int>)Sym(e, "nvmlDeviceGetCount_v2");
        _byIndex = (delegate* unmanaged[Cdecl]<uint, nint*, int>)Sym(e, "nvmlDeviceGetHandleByIndex_v2");
        _name = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)Sym(e, "nvmlDeviceGetName");
        _pci = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Sym(e, "nvmlDeviceGetPciInfo_v3");
        _temp = (delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)Sym(e, "nvmlDeviceGetTemperature");
        _powerUsage = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetPowerUsage");
        _powerLimit = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetPowerManagementLimit");
        _powerDefault = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetPowerManagementDefaultLimit");
        _powerEnforced = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetEnforcedPowerLimit");
        _powerConstraints = (delegate* unmanaged[Cdecl]<nint, uint*, uint*, int>)Sym(e, "nvmlDeviceGetPowerManagementLimitConstraints");
        _clock = (delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)Sym(e, "nvmlDeviceGetClockInfo");
        _maxClock = (delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)Sym(e, "nvmlDeviceGetMaxClockInfo");
        _util = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetUtilizationRates");
        _mem = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetMemoryInfo");
        _memV2 = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetMemoryInfo_v2", true);
        _fanCount = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetNumFans", true);
        _fanSpeed = (delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)Sym(e, "nvmlDeviceGetFanSpeed_v2", true);
        _throttle = (delegate* unmanaged[Cdecl]<nint, ulong*, int>)Sym(e, "nvmlDeviceGetCurrentClocksThrottleReasons", true);
        _vbios = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)Sym(e, "nvmlDeviceGetVbiosVersion", true);
        _fields = (delegate* unmanaged[Cdecl]<nint, int, FieldValue*, int>)Sym(e, "nvmlDeviceGetFieldValues", true);
        _margin = (delegate* unmanaged[Cdecl]<nint, MarginTemperature*, int>)Sym(e, "nvmlDeviceGetMarginTemperature", true);
        _pstate = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Sym(e, "nvmlDeviceGetPerformanceState", true);
        _threshold = (delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)Sym(e, "nvmlDeviceGetTemperatureThreshold", true);
    }

    /// <summary>Before every call: once lost, fail as the call would have, without entering the library.</summary>
    private void Live(string fn)
    {
        if (_lost) throw new NvmlException(fn, _lostCode);
    }

    private int Check(string fn, int rc)
    {
        if (rc == 0) return 0;
        Seen(rc);
        throw new NvmlException(fn, rc);
    }

    /// <summary>A status from a call that does not throw: it still latches a loss.</summary>
    private int Seen(int rc)
    {
        if (MeansDriverGone(rc)) MarkLost(rc);
        return rc;
    }

    public uint DeviceCount() { Live("nvmlDeviceGetCount_v2"); uint n; Check("nvmlDeviceGetCount_v2", _count(&n)); return n; }

    public nint HandleByIndex(uint i) { Live("nvmlDeviceGetHandleByIndex_v2"); nint h; Check("nvmlDeviceGetHandleByIndex_v2", _byIndex(i, &h)); return h; }

    public string Name(nint h)
    {
        Live("nvmlDeviceGetName");
        byte* b = stackalloc byte[96];
        Check("nvmlDeviceGetName", _name(h, b, 96));
        return Marshal.PtrToStringAnsi((nint)b) ?? "";
    }

    public string? VbiosVersion(nint h)
    {
        if (_vbios == null || _lost) return null;
        byte* b = stackalloc byte[96];
        return Seen(_vbios(h, b, 96)) == 0 ? Marshal.PtrToStringAnsi((nint)b) : null;
    }

    public NvmlPci Pci(nint h)
    {
        Live("nvmlDeviceGetPciInfo_v3");
        // nvmlPciInfo_t: char busIdLegacy[16]; uint domain, bus, device, pciDeviceId, pciSubSystemId; char busId[32]
        byte* b = stackalloc byte[128];
        new Span<byte>(b, 128).Clear();
        Check("nvmlDeviceGetPciInfo_v3", _pci(h, b));
        uint* u = (uint*)(b + 16);
        return new NvmlPci(u[0], u[1], u[2], u[3], u[4], Marshal.PtrToStringAnsi((nint)(b + 36)) ?? "");
    }

    public uint TemperatureC(nint h) { Live("nvmlDeviceGetTemperature"); uint v; Check("nvmlDeviceGetTemperature", _temp(h, 0, &v)); return v; }
    public uint PowerUsageMw(nint h) { Live("nvmlDeviceGetPowerUsage"); uint v; Check("nvmlDeviceGetPowerUsage", _powerUsage(h, &v)); return v; }
    public uint PowerLimitMw(nint h) { Live("nvmlDeviceGetPowerManagementLimit"); uint v; Check("nvmlDeviceGetPowerManagementLimit", _powerLimit(h, &v)); return v; }
    public uint PowerDefaultMw(nint h) { Live("nvmlDeviceGetPowerManagementDefaultLimit"); uint v; Check("nvmlDeviceGetPowerManagementDefaultLimit", _powerDefault(h, &v)); return v; }
    public uint PowerEnforcedMw(nint h) { Live("nvmlDeviceGetEnforcedPowerLimit"); uint v; Check("nvmlDeviceGetEnforcedPowerLimit", _powerEnforced(h, &v)); return v; }

    public (uint MinMw, uint MaxMw) PowerConstraintsMw(nint h)
    {
        Live("nvmlDeviceGetPowerManagementLimitConstraints");
        uint a, b;
        Check("nvmlDeviceGetPowerManagementLimitConstraints", _powerConstraints(h, &a, &b));
        return (a, b);
    }

    public uint ClockMhz(nint h, NvmlClock c) { Live("nvmlDeviceGetClockInfo"); uint v; Check("nvmlDeviceGetClockInfo", _clock(h, (uint)c, &v)); return v; }
    public uint MaxClockMhz(nint h, NvmlClock c) { Live("nvmlDeviceGetMaxClockInfo"); uint v; Check("nvmlDeviceGetMaxClockInfo", _maxClock(h, (uint)c, &v)); return v; }

    public NvmlUtilization Utilization(nint h)
    {
        Live("nvmlDeviceGetUtilizationRates");
        uint* v = stackalloc uint[2];
        Check("nvmlDeviceGetUtilizationRates", _util(h, v));
        return new NvmlUtilization(v[0], v[1]);
    }

    /// <summary>Total, free and used memory. The _v2 call (nvmlMemory_v2_t: version, total, reserved, free, used) leaves
    /// the driver's reservation out of "used", as nvidia-smi does (2175 MiB where the first call said 2.5 GiB on the
    /// reference card); the first call is the fallback.</summary>
    public NvmlMemory Memory(nint h)
    {
        Live("nvmlDeviceGetMemoryInfo");
        if (_memV2 != null)
        {
            ulong* m = stackalloc ulong[5];
            new Span<ulong>(m, 5).Clear();
            *(uint*)m = 5 * sizeof(ulong) | 2u << 24;
            if (Seen(_memV2(h, (uint*)m)) == 0) return new NvmlMemory(m[1], m[3], m[4]);
            Live("nvmlDeviceGetMemoryInfo");
        }
        ulong* v = stackalloc ulong[3];
        Check("nvmlDeviceGetMemoryInfo", _mem(h, (uint*)v));
        return new NvmlMemory(v[0], v[1], v[2]);
    }

    public uint FanCount(nint h)
    {
        if (_fanCount == null || _lost) return 0;
        uint v;
        return Seen(_fanCount(h, &v)) == 0 ? v : 0;
    }

    public uint? FanPercent(nint h, uint fan)
    {
        if (_fanSpeed == null || _lost) return null;
        uint v;
        return Seen(_fanSpeed(h, fan, &v)) == 0 ? v : null;
    }

    /// <summary>Several fields in one call (<see cref="NvmlField"/>); null when this NVML has no field call or the call
    /// itself failed. Each field carries its own status, and a status that says the driver is gone latches the loss.</summary>
    public NvmlFieldValue[]? FieldValues(nint h, params uint[] ids)
    {
        if (_fields == null || _lost || ids.Length == 0) return null;
        var v = stackalloc FieldValue[ids.Length];
        for (int i = 0; i < ids.Length; i++) v[i] = new FieldValue { FieldId = ids[i] };
        if (Seen(_fields(h, ids.Length, v)) != 0) return null;
        var result = new NvmlFieldValue[ids.Length];
        for (int i = 0; i < ids.Length; i++) result[i] = new NvmlFieldValue(v[i].FieldId, Seen(v[i].Return), v[i].ValueType, v[i].Value);
        return result;
    }

    /// <summary>How many °C the GPU is under its maximum operating temperature (nvidia-smi's "T.Limit"); null when not answered.</summary>
    public int? MarginTemperatureC(nint h)
    {
        if (_margin == null || _lost) return null;
        var m = new MarginTemperature { Version = (uint)sizeof(MarginTemperature) | 1u << 24 };
        return Seen(_margin(h, &m)) == 0 ? m.MarginC : null;
    }

    /// <summary>The performance state, 0 (P0, the fastest) to 15; null when NVML does not say (it answers 32 for unknown).
    /// The memory offset and the curve's P0 offsets act in P0 only.</summary>
    public int? PerformanceState(nint h)
    {
        if (_pstate == null || _lost) return null;
        uint p;
        return Seen(_pstate(h, &p)) == 0 && p <= 15 ? (int)p : null;
    }

    public uint? TemperatureThresholdC(nint h, NvmlTemperatureThreshold t)
    {
        if (_threshold == null || _lost) return null;
        uint v;
        return Seen(_threshold(h, (uint)t, &v)) == 0 ? v : null;
    }

    public ulong ThrottleReasons(nint h)
    {
        if (_throttle == null || _lost) return 0;
        ulong v;
        return Seen(_throttle(h, &v)) == 0 ? v : 0;
    }

    /// <summary>Shut NVML down and unload it, unless it was lost: then both are left alone (they would run through the
    /// state of a driver that is gone).</summary>
    public void Dispose()
    {
        if (_lost) return;
        if (_inited) { _shutdown(); _inited = false; }
        if (_lib != 0) { NativeLibrary.Free(_lib); _lib = 0; }
    }
}
