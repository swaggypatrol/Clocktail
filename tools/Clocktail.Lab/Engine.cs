using System.Runtime.InteropServices;

namespace Clocktail.Lab;

/// <summary>One chunk of GPU work. Error != null: the context is dead or the launch failed, and the run stops.</summary>
public readonly record struct ChunkOutcome(double ElapsedMs, long Mismatches, double WorkUnits, string? Error = null, ulong? FirstBadWord = null)
{
    public static ChunkOutcome Failed(string error) => new(0, 0, 0, error);
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
}

/// <summary>
/// The load engine over the CUDA driver API (StabilityLab's CudaLoadEngine, its FMA, memory and chase paths). Every launch
/// is timed with CUDA events and read back; a sticky CUDA error ends the run as a GPU fault.
/// </summary>
public sealed unsafe class CudaEngine : ILoadEngine
{
    private readonly CudaApi _cu;
    private readonly int _dev;
    private readonly nint _mod, _fFma, _fFill, _fVerify, _fChaseFill, _fChaseRun;
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

    /// <summary>A flip of one bit of the output after this compute launch (1-based), as a wrong ALU result would arrive: the
    /// run must then fail. 0 = never.</summary>
    public int InjectAtComputeLaunch { get; set; }
    private int _computeLaunches;

    public CudaEngine(CudaApi cu, int ordinal, double memoryFraction, ulong memoryCapBytes = 0, int computeThreads = 1 << 20)
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

        var (free, _) = cu.MemInfo();
        ulong want = (ulong)(free * Math.Clamp(memoryFraction, 0.05, 0.9));
        if (memoryCapBytes > 0) want = Math.Min(want, memoryCapBytes);
        const ulong align = 64UL << 20;
        want = Math.Max(want / align * align, 256UL << 20);
        while (want >= 256UL << 20)
        {
            try { _dMem = cu.MemAlloc(want); MemoryBufferBytes = want; break; }
            catch (CudaException ex) when (ex.Code == 2) { want /= 2; } // out of memory: the display's share or fragmentation; halve and retry
        }
        if (MemoryBufferBytes == 0) throw new InvalidOperationException("could not allocate even 256 MiB of video memory");
        _memGrid = (uint)(SmCount * 8);
        ulong vectors = MemoryBufferBytes / PatternPlan.VectorBytes;
        foreach (var p in Enum.GetValues<AccessPattern>()) _plans[p] = PatternPlan.Of(p, vectors, (ulong)_memGrid * Block);

        _evStart = cu.EventCreate();
        _evEnd = cu.EventCreate();
    }

    public PatternPlan Plan(AccessPattern p) => _plans[p];

    private ChunkOutcome Dead(string fn, int rc)
    {
        _dead = true;
        return ChunkOutcome.Failed($"{fn}: {_cu.ErrorName(rc)}");
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

    public ChunkOutcome RunMemory(uint seed, AccessPattern pattern)
    {
        if (_dead) return ChunkOutcome.Failed("the CUDA context is already dead");
        _chaseReady = false; // the pass overwrites the chase table
        ulong* init = stackalloc ulong[2] { 0, ulong.MaxValue };
        _cu.MemcpyHtoD(_dErr, init, 16);
        var plan = _plans[pattern];
        ulong dMem = _dMem, n = MemoryBufferBytes / PatternPlan.VectorBytes, mul = plan.Multiplier, delta = plan.Delta, xor = plan.XorFull, dErr = _dErr;
        uint sd = seed;
        void** fillArgs = stackalloc void*[6] { &dMem, &n, &sd, &mul, &delta, &xor };
        void** verifyArgs = stackalloc void*[7] { &dMem, &n, &sd, &mul, &delta, &xor, &dErr };
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
        ulong words = MemoryBufferBytes / 4, windowWords = (ulong)_window.Length;
        ulong maxStart = words > windowWords ? words - windowWords : 0;
        ulong start = maxStart == 0 ? 0 : Reference.MemoryPattern(seed, 0x51A6u) % (maxStart / 4) * 4;
        fixed (uint* w = _window)
            if ((rc = _cu.MemcpyDtoHRaw(w, _dMem + start * 4, windowWords * 4)) != 0) return Dead("cuMemcpyDtoH(window)", rc);
        long hostBad = Reference.VerifyWindow(_window, start, seed);
        if (hostBad > 0 && bad == 0) { bad = hostBad; first ??= start; }
        return new ChunkOutcome(ms, bad, 2.0 * MemoryBufferBytes, null, first);
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
        _cu.ModuleUnload(_mod);
        _cu.PrimaryCtxRelease(_dev);
    }
}
