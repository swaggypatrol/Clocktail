using System.Runtime.InteropServices;

namespace Clocktail.Lab;

/// <summary>One chunk of GPU work. Error != null: the context is dead or the launch failed, and the run stops.</summary>
public readonly record struct ChunkOutcome(double ElapsedMs, long Mismatches, double WorkUnits, string? Error = null, ulong? FirstBadWord = null, int? ErrorCode = null)
{
    public static ChunkOutcome Failed(string error, int? code = null) => new(0, 0, 0, error, ErrorCode: code);
}

/// <summary>Runs GPU work in chunks: the CUDA engine on the card, a fake in tests.</summary>
public interface ILoadEngine : IDisposable
{
    int ComputeThreads { get; }
    int SmCount { get; }
    ulong MemoryBufferBytes { get; }

    /// <summary>One fma_rot launch on <paramref name="threads"/> threads (0 = all): read back and checked against the CPU twin
    /// for a subset every chunk, and byte for byte against the phase's reference chunk once the count is frozen.</summary>
    ChunkOutcome RunCompute(uint iterations, bool isReference, int threads = 0);

    /// <summary>One fill and verify pass over the whole buffer in <paramref name="pattern"/>'s order with a fresh seed; WorkUnits = bytes moved.</summary>
    ChunkOutcome RunMemory(uint seed, AccessPattern pattern);

    /// <summary>One pointer-chase launch: every chain takes <paramref name="hops"/> dependent loads; errors = chains that ended on the wrong slot.</summary>
    ChunkOutcome RunLatency(uint hops);

    /// <summary>One fill and verify of a region that fits in L2, walked many times over in one launch: what it measures is the
    /// crossbar between the SMs and L2, the path XBAR clocks, not the DRAM. WorkUnits = bytes moved.</summary>
    ChunkOutcome RunL2(uint seed);

    /// <summary>Why the soloist cannot play on this card (its module did not load), or null when it can.</summary>
    string? SoloUnavailable { get; }

    /// <summary>The soloist's resident blocks per SM: an SM meant to play ran its full share when it ran this many.</summary>
    int SoloBlocksPerSm { get; }

    /// <summary>One soloist launch of <paramref name="units"/> units with <paramref name="mask"/>'s SMs playing (eight words, a bit
    /// per %smid): read back and judged lane by lane (<see cref="SoloRules.Judge"/>). <paramref name="playing"/>: the SMs in the mask.</summary>
    SoloOutcome RunSolo(uint units, uint seed, uint[] mask, IReadOnlyCollection<int> playing);
}

/// <summary>
/// The load engine over the CUDA driver API (StabilityLab's CudaLoadEngine, its FMA, memory and chase paths). Every launch
/// is timed with CUDA events and read back; a sticky CUDA error ends the run as a GPU fault.
/// </summary>
public sealed unsafe class CudaEngine : ILoadEngine
{
    private readonly CudaApi _cu;
    private readonly int _dev;
    private readonly nint _mod, _fFma, _fFill, _fVerify, _fChaseFill, _fChaseRun, _modSolo, _fSolo;
    private readonly ulong _dSoloSum, _dSoloSm, _dSoloCycles, _dSoloNs, _dSoloMask;
    private readonly ulong[] _soloSum = [], _soloCycles = [], _soloNs = [];
    private readonly uint[] _soloSm = [];
    private readonly uint _soloGrid;
    private int _soloLaunches;
    private readonly ulong _dOut, _dErr, _dMem;
    private readonly byte[] _hostOut, _reference;
    private readonly uint[] _window = new uint[16384];
    private readonly nint _evStart, _evEnd;
    private const uint Block = 256;
    private readonly uint _memGrid;
    private readonly Dictionary<AccessPattern, PatternPlan> _plans = [];
    private uint _referenceIterations;
    private int _referenceThreads;
    private ulong _dChaseOut;
    private uint[]? _chaseHost, _chaseExpected;
    private uint _chaseMask, _chaseExpectedHops;
    private bool _chaseReady, _dead;

    public int ComputeThreads { get; }
    public int SmCount { get; }
    public ulong MemoryBufferBytes { get; }
    public string Name { get; }
    public string PciBusId { get; }
    private int ChaseChains => SmCount * 32;

    public string? SoloUnavailable { get; }
    public int SoloBlocksPerSm { get; }

    /// <summary>A flip of one bit of the output after this compute launch (1-based), as a wrong ALU result would arrive: the
    /// run must then fail; the soloist's launches count on their own, and the bit goes into its mma8 lane. 0 = never.</summary>
    public int InjectAtComputeLaunch { get; set; }
    private int _computeLaunches;

    public CudaEngine(CudaApi cu, int ordinal, double memoryFraction, ulong memoryCapBytes = 0, int computeThreads = 1 << 20, bool allocateMemory = true)
    {
        _cu = cu;
        _dev = cu.Device(ordinal);
        Name = cu.DeviceName(_dev);
        PciBusId = cu.DevicePciBusId(_dev);
        cu.CtxSetCurrent(cu.PrimaryCtxRetain(_dev));
        SmCount = cu.Attribute(_dev, 16);

        _mod = cu.ModuleLoadPtx(Kernels.Module());
        _fFma = cu.Function(_mod, Kernels.Fma);
        // Whole waves of resident blocks, about computeThreads in all: 4096 blocks on 170 SMs x 8 left a fourth wave of 16
        // blocks running alone for a quarter of every launch (62 TFLOP/s and 420 W in burn on the reference card).
        int wave = SmCount * Math.Max(1, cu.MaxActiveBlocksPerSm(_fFma, (int)Block)) * (int)Block;
        ComputeThreads = Math.Max(1, (int)Math.Round((double)computeThreads / wave)) * wave;
        _fFill = cu.Function(_mod, Kernels.MemFill);
        _fVerify = cu.Function(_mod, Kernels.MemVerify);
        _fChaseFill = cu.Function(_mod, Kernels.ChaseFill);
        _fChaseRun = cu.Function(_mod, Kernels.ChaseRun);

        ulong outBytes = (ulong)ComputeThreads * Reference.BytesPerThread;
        _dOut = cu.MemAlloc(outBytes);
        _hostOut = new byte[outBytes];
        _reference = new byte[outBytes];
        _dErr = cu.MemAlloc(16);

        if (allocateMemory)
        {
            var (free, _) = cu.MemInfo();
            ulong want = (ulong)(free * Math.Clamp(memoryFraction, 0.05, 0.9));
            if (memoryCapBytes > 0) want = Math.Min(want, memoryCapBytes);
            const ulong align = 64UL << 20;
            want = Math.Max(want / align * align, 256UL << 20);
            while (want >= 256UL << 20)
            {
                try { _dMem = cu.MemAlloc(want); MemoryBufferBytes = want; break; }
                catch (CudaException ex) when (ex.Code == 2) { want /= 2; }
            }
            if (MemoryBufferBytes == 0) throw new InvalidOperationException("could not allocate even 256 MiB of video memory");
            _memGrid = (uint)(SmCount * 8);
            ulong vectors = MemoryBufferBytes / PatternPlan.VectorBytes;
            foreach (var p in Enum.GetValues<AccessPattern>()) _plans[p] = PatternPlan.Of(p, vectors, (ulong)_memGrid * Block);
        }

        _evStart = cu.EventCreate();
        _evEnd = cu.EventCreate();

        // The soloist is a module of its own (compute_80 PTX: the INT8 mma.sync is sm_80's); a driver that cannot compile it
        // leaves the lab's other phases as they were.
        try
        {
            _modSolo = cu.ModuleLoadPtx(SoloKernel.Ptx());
            _fSolo = cu.Function(_modSolo, SoloKernel.Entry);
            SoloBlocksPerSm = Math.Max(1, cu.MaxActiveBlocksPerSm(_fSolo, (int)Block));
            _soloGrid = (uint)(SmCount * SoloBlocksPerSm);
            _dSoloSum = cu.MemAlloc(_soloGrid * (ulong)SoloKernel.Lanes * 8);
            _dSoloSm = cu.MemAlloc(_soloGrid * 4UL);
            _dSoloCycles = cu.MemAlloc(_soloGrid * 8UL);
            _dSoloNs = cu.MemAlloc(_soloGrid * 8UL);
            _dSoloMask = cu.MemAlloc(32);
            _soloSum = new ulong[_soloGrid * SoloKernel.Lanes];
            _soloSm = new uint[_soloGrid];
            _soloCycles = new ulong[_soloGrid];
            _soloNs = new ulong[_soloGrid];
        }
        catch (CudaException ex) { SoloUnavailable = "the soloist's kernel did not load: " + ex.Message; }
        catch (InvalidOperationException ex) { SoloUnavailable = ex.Message; }
    }

    public SoloOutcome RunSolo(uint units, uint seed, uint[] mask, IReadOnlyCollection<int> playing)
    {
        if (_dead) return SoloOutcome.Failed("the CUDA context is already dead");
        if (SoloUnavailable != null) return SoloOutcome.Failed(SoloUnavailable);
        int rc;
        fixed (uint* m = mask)
            if ((rc = _cu.MemcpyHtoDRaw(_dSoloMask, m, 32)) != 0) return SoloFailed("cuMemcpyHtoD(mask)", rc);
        uint u = units, sd = seed, wf = SoloKernel.WFp32, wi = SoloKernel.WInt, wd = SoloKernel.WDp4a, ws = SoloKernel.WS8;
        uint never = uint.MaxValue, lane = 0;
        ulong dMask = _dSoloMask, dSum = _dSoloSum, dSm = _dSoloSm, dCyc = _dSoloCycles, dNs = _dSoloNs;
        void** args = stackalloc void*[14] { &u, &sd, &wf, &wi, &wd, &ws, &dMask, &never, &never, &lane, &dSum, &dSm, &dCyc, &dNs };
        if ((rc = _cu.EventRecordRaw(_evStart)) != 0) return SoloFailed("cuEventRecord", rc);
        if ((rc = _cu.LaunchRaw(_fSolo, _soloGrid, Block, args)) != 0) return SoloFailed("cuLaunchKernel(solo)", rc);
        if ((rc = _cu.EventRecordRaw(_evEnd)) != 0) return SoloFailed("cuEventRecord", rc);
        if ((rc = _cu.EventSynchronizeRaw(_evEnd)) != 0) return SoloFailed("solo", rc);
        float ms = _cu.EventElapsedMs(_evStart, _evEnd);
        fixed (ulong* p = _soloSum) if ((rc = _cu.MemcpyDtoHRaw(p, _dSoloSum, (ulong)_soloSum.Length * 8)) != 0) return SoloFailed("cuMemcpyDtoH", rc);
        fixed (uint* p = _soloSm) if ((rc = _cu.MemcpyDtoHRaw(p, _dSoloSm, (ulong)_soloSm.Length * 4)) != 0) return SoloFailed("cuMemcpyDtoH", rc);
        fixed (ulong* p = _soloCycles) if ((rc = _cu.MemcpyDtoHRaw(p, _dSoloCycles, (ulong)_soloCycles.Length * 8)) != 0) return SoloFailed("cuMemcpyDtoH", rc);
        fixed (ulong* p = _soloNs) if ((rc = _cu.MemcpyDtoHRaw(p, _dSoloNs, (ulong)_soloNs.Length * 8)) != 0) return SoloFailed("cuMemcpyDtoH", rc);
        if (++_soloLaunches == InjectAtComputeLaunch)
            for (int b = 0; b < _soloNs.Length; b++)
                if (_soloNs[b] != 0) { _soloSum[b * SoloKernel.Lanes + 3] ^= 1UL << 3; break; }   // one block's mma8 lane, as a wrong MAC would arrive
        return new SoloOutcome(ms, SoloRules.Judge(_soloSum, _soloSm, _soloCycles, _soloNs, SoloBlocksPerSm, playing));
    }

    private SoloOutcome SoloFailed(string fn, int rc)
    {
        _dead = true;
        return SoloOutcome.Failed($"{fn}: {_cu.ErrorName(rc)}", rc);
    }

    public PatternPlan Plan(AccessPattern p) => _plans[p];

    private ChunkOutcome Dead(string fn, int rc)
    {
        _dead = true;
        return ChunkOutcome.Failed($"{fn}: {_cu.ErrorName(rc)}", rc);
    }

    public ChunkOutcome RunCompute(uint iterations, bool isReference, int threads = 0)
    {
        if (_dead) return ChunkOutcome.Failed("the CUDA context is already dead");
        int n = threads <= 0 ? ComputeThreads : Math.Clamp(threads / (int)Block * (int)Block, (int)Block, ComputeThreads);
        bool light = n < ComputeThreads;
        int bytes = n * Reference.BytesPerThread;
        ulong dOut = _dOut; uint it = iterations; float c = Reference.RotC, s = Reference.RotS;
        void** args = stackalloc void*[4] { &dOut, &it, &c, &s };
        int rc;
        if ((rc = _cu.EventRecordRaw(_evStart)) != 0) return Dead("cuEventRecord", rc);
        if ((rc = _cu.LaunchRaw(_fFma, (uint)(n / Block), Block, args)) != 0) return Dead("cuLaunchKernel(fma_rot)", rc);
        if ((rc = _cu.EventRecordRaw(_evEnd)) != 0) return Dead("cuEventRecord", rc);
        if ((rc = _cu.EventSynchronizeRaw(_evEnd)) != 0) return Dead("fma_rot", rc);
        float ms = _cu.EventElapsedMs(_evStart, _evEnd);
        fixed (byte* p = _hostOut)
            if ((rc = _cu.MemcpyDtoHRaw(p, _dOut, (ulong)bytes)) != 0) return Dead("cuMemcpyDtoH", rc);
        if (++_computeLaunches == InjectAtComputeLaunch)
            MemoryMarshal.Cast<byte, uint>(_hostOut.AsSpan())[(n - 1) * Reference.FloatsPerThread] ^= 1u << 3;

        // Light chunks run few threads for many iterations, so a CPU replay costs more per thread: the full compare against
        // the reference chunk still covers every thread, and the CPU anchors the reference with fewer.
        // Once frozen, a chunk equal to the CPU-checked reference byte for byte is right in every thread, so the CPU replays
        // threads only for the reference and before the freeze: replaying 64 every chunk kept the GPU idle ~50 ms of each
        // 200 (412 W in burn on the reference card).
        bool comparable = !isReference && _referenceIterations == iterations && _referenceThreads == n;
        int subset = isReference ? (light ? 8 : 1024) : comparable || light ? 0 : 64;
        long mismatches = VerifySubset(iterations, subset, n);
        if (isReference)
        {
            Buffer.BlockCopy(_hostOut, 0, _reference, 0, bytes);
            _referenceIterations = iterations;
            _referenceThreads = n;
        }
        else if (_referenceIterations == iterations && _referenceThreads == n && !_reference.AsSpan(0, bytes).SequenceEqual(_hostOut.AsSpan(0, bytes)))
        {
            var a = MemoryMarshal.Cast<byte, uint>(_reference.AsSpan(0, bytes));
            var b = MemoryMarshal.Cast<byte, uint>(_hostOut.AsSpan(0, bytes));
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) mismatches++;
        }
        return new ChunkOutcome(ms, mismatches, (double)n * iterations * Reference.FlopsPerIteration);
    }

    /// <summary>The CPU recomputes <paramref name="subset"/> threads spread over the first <paramref name="threads"/> and compares bit for bit.</summary>
    private long VerifySubset(uint iterations, int subset, int threads)
    {
        if (subset <= 0) return 0;
        int stride = Math.Max(1, threads / subset);
        long bad = 0;
        var host = MemoryMarshal.Cast<byte, float>(_hostOut).ToArray();
        Parallel.For(0, subset, k =>
        {
            uint gid = (uint)(k * stride);
            if (gid >= (uint)threads) return;
            Span<float> expect = stackalloc float[Reference.FloatsPerThread];
            Reference.RotationThread(gid, iterations, expect);
            int off = (int)gid * Reference.FloatsPerThread;
            long local = 0;
            for (int i = 0; i < Reference.FloatsPerThread; i++)
                if (BitConverter.SingleToUInt32Bits(expect[i]) != BitConverter.SingleToUInt32Bits(host[off + i])) local++;
            if (local > 0) Interlocked.Add(ref bad, local);
        });
        return bad;
    }

    /// <summary>The L2 test's region: 32 MiB, a third of the 5090's 96 MB L2, walked <see cref="L2Passes"/> times a launch.</summary>
    public const ulong L2RegionBytes = 32UL << 20;
    public const ulong L2Passes = 1024;

    public ChunkOutcome RunMemory(uint seed, AccessPattern pattern)
    {
        RequireMemory();
        return Pass(seed, _plans[pattern], MemoryBufferBytes / PatternPlan.VectorBytes, 1);
    }

    private void RequireMemory()
    {
        if (MemoryBufferBytes == 0) throw new InvalidOperationException("this workload did not allocate the memory test buffer");
    }

    public ChunkOutcome RunL2(uint seed)
    {
        RequireMemory();
        ulong region = Math.Min(L2RegionBytes, MemoryBufferBytes) / PatternPlan.VectorBytes;
        return Pass(seed, PatternPlan.Of(AccessPattern.Linear, region, (ulong)_memGrid * Block), region, L2Passes);
    }

    /// <summary>Fill and verify the first <paramref name="n"/> vectors in <paramref name="plan"/>'s order, <paramref name="passes"/> times
    /// over in one launch each.</summary>
    private ChunkOutcome Pass(uint seed, PatternPlan plan, ulong n, ulong passes)
    {
        if (_dead) return ChunkOutcome.Failed("the CUDA context is already dead");
        _chaseReady = false; // the pass overwrites the chase table
        ulong* init = stackalloc ulong[2] { 0, ulong.MaxValue };
        _cu.MemcpyHtoD(_dErr, init, 16);
        ulong dMem = _dMem, mul = plan.Multiplier, delta = plan.Delta, xor = plan.XorFull, limit = n * passes, dErr = _dErr;
        uint sd = seed;
        void** fillArgs = stackalloc void*[7] { &dMem, &n, &sd, &mul, &delta, &xor, &limit };
        void** verifyArgs = stackalloc void*[8] { &dMem, &n, &sd, &mul, &delta, &xor, &limit, &dErr };
        int rc;
        if ((rc = _cu.EventRecordRaw(_evStart)) != 0) return Dead("cuEventRecord", rc);
        if ((rc = _cu.LaunchRaw(_fFill, _memGrid, Block, fillArgs)) != 0) return Dead("cuLaunchKernel(mem_fill)", rc);
        if ((rc = _cu.LaunchRaw(_fVerify, _memGrid, Block, verifyArgs)) != 0) return Dead("cuLaunchKernel(mem_verify)", rc);
        if ((rc = _cu.EventRecordRaw(_evEnd)) != 0) return Dead("cuEventRecord", rc);
        if ((rc = _cu.EventSynchronizeRaw(_evEnd)) != 0) return Dead("mem_fill/mem_verify", rc);
        float ms = _cu.EventElapsedMs(_evStart, _evEnd);

        ulong* errs = stackalloc ulong[2];
        if ((rc = _cu.MemcpyDtoHRaw(errs, _dErr, 16)) != 0) return Dead("cuMemcpyDtoH", rc);
        long bad = (uint)errs[0];
        ulong? first = errs[1] == ulong.MaxValue ? null : errs[1];

        // A window checked on the host: catches a verify kernel that lies (a broken compare or atomics) and late flips.
        ulong words = n * 4, windowWords = (ulong)_window.Length;
        ulong maxStart = words > windowWords ? words - windowWords : 0;
        ulong start = maxStart == 0 ? 0 : Reference.MemoryPattern(seed, 0x51A6u) % (maxStart / 4) * 4;
        fixed (uint* w = _window)
            if ((rc = _cu.MemcpyDtoHRaw(w, _dMem + start * 4, windowWords * 4)) != 0) return Dead("cuMemcpyDtoH(window)", rc);
        long hostBad = Reference.VerifyWindow(_window, start, seed);
        if (hostBad > 0 && bad == 0) { bad = hostBad; first ??= start; }
        return new ChunkOutcome(ms, bad, 2.0 * n * passes * PatternPlan.VectorBytes, null, first);
    }

    /// <summary>The chase table over the memory buffer: the largest power of two of 32-byte slots that fits.</summary>
    private void PrepareChase()
    {
        ulong slots = MemoryBufferBytes / Reference.ChaseSlotBytes;
        uint n = 1u << (63 - System.Numerics.BitOperations.LeadingZeroCount(Math.Min(slots, 1UL << 31)));
        _chaseMask = n - 1;
        if (_dChaseOut == 0) _dChaseOut = _cu.MemAlloc((ulong)ChaseChains * 4);
        _chaseHost ??= new uint[ChaseChains];
        _chaseExpected ??= new uint[ChaseChains];
        _chaseExpectedHops = 0;
        ulong dMem = _dMem; uint mask = _chaseMask, a = Reference.ChaseA, c = Reference.ChaseC;
        void** args = stackalloc void*[4] { &dMem, &mask, &a, &c };
        int rc;
        if ((rc = _cu.LaunchRaw(_fChaseFill, _memGrid, Block, args)) != 0 || (rc = _cu.CtxSynchronizeRaw()) != 0) { Dead("chase_fill", rc); return; }
        _chaseReady = true;
    }

    public ChunkOutcome RunLatency(uint hops)
    {
        RequireMemory();
        if (_dead) return ChunkOutcome.Failed("the CUDA context is already dead");
        if (!_chaseReady) PrepareChase();
        if (_dead) return ChunkOutcome.Failed("chase_fill failed");
        ulong dMem = _dMem, dOut = _dChaseOut; uint mask = _chaseMask, h = hops;
        void** args = stackalloc void*[4] { &dMem, &mask, &h, &dOut };
        int rc;
        if ((rc = _cu.EventRecordRaw(_evStart)) != 0) return Dead("cuEventRecord", rc);
        if ((rc = _cu.LaunchRaw(_fChaseRun, (uint)SmCount, 32, args)) != 0) return Dead("cuLaunchKernel(chase_run)", rc);
        if ((rc = _cu.EventRecordRaw(_evEnd)) != 0) return Dead("cuEventRecord", rc);
        if ((rc = _cu.EventSynchronizeRaw(_evEnd)) != 0) return Dead("chase_run", rc);
        float ms = _cu.EventElapsedMs(_evStart, _evEnd);
        fixed (uint* p = _chaseHost)
            if ((rc = _cu.MemcpyDtoHRaw(p, _dChaseOut, (ulong)ChaseChains * 4)) != 0) return Dead("cuMemcpyDtoH", rc);
        if (_chaseExpectedHops != hops)
        {
            for (int t = 0; t < ChaseChains; t++) _chaseExpected![t] = Reference.ChaseEnd(Reference.ChaseStart((uint)t, mask), hops, mask);
            _chaseExpectedHops = hops;
        }
        long bad = 0;
        for (int t = 0; t < ChaseChains; t++) if (_chaseHost![t] != _chaseExpected![t]) bad++;
        return new ChunkOutcome(ms, bad, (double)hops * ChaseChains);
    }

    public void Dispose()
    {
        _cu.EventDestroy(_evStart); _cu.EventDestroy(_evEnd);
        _cu.MemFree(_dChaseOut); _cu.MemFree(_dOut); _cu.MemFree(_dErr); _cu.MemFree(_dMem);
        _cu.MemFree(_dSoloSum); _cu.MemFree(_dSoloSm); _cu.MemFree(_dSoloCycles); _cu.MemFree(_dSoloNs); _cu.MemFree(_dSoloMask);
        _cu.ModuleUnload(_modSolo);
        _cu.ModuleUnload(_mod);
        _cu.PrimaryCtxRelease(_dev);
    }
}
