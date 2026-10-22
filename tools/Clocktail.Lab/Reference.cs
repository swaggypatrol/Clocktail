namespace Clocktail.Lab;

/// <summary>
/// CPU twins of the PTX kernels (StabilityLab's). Both sides use only correctly rounded IEEE-754 operations (mul.rn /
/// fma.rn on the GPU; float '*' and MathF.FusedMultiplyAdd here), so the results must match bit for bit: any difference
/// is a wrong answer from the GPU, not floating-point noise.
/// </summary>
public static class Reference
{
    public const int PairsPerThread = 4;
    public const int FloatsPerThread = PairsPerThread * 2;
    public const int BytesPerThread = FloatsPerThread * sizeof(float);

    /// <summary>cos/sin of the rotation: 0.96² + 0.28² = 1 in the reals, so the magnitude stays ~1 and the bits keep changing.</summary>
    public const float RotC = 0.96f, RotS = 0.28f;

    private static readonly float[] XBase = [1.0f, 0.75f, 0.5f, 0.25f];
    private static readonly float[] YBase = [0.0f, 0.5f, -0.5f, 0.25f];
    private const float GidScale = 1.52587890625e-5f; // 2^-16 (0x37800000): 2^20 threads far enough apart that rounding never merges their orbits

    /// <summary>Flops per thread per iteration: 2 mul + 2 fma (2 flops each) per pair.</summary>
    public const double FlopsPerIteration = PairsPerThread * 6.0;

    /// <summary>The fma_rot chain for one thread: four 2-D rotations, <paramref name="iterations"/> steps, 8 floats out.</summary>
    public static void RotationThread(uint gid, uint iterations, Span<float> outState)
    {
        Span<float> st = stackalloc float[FloatsPerThread];
        float g = (int)gid; // cvt.rn.f32.s32 on the GPU: gid < 2^31, so the signed conversion is the same
        for (int k = 0; k < PairsPerThread; k++)
        {
            st[2 * k] = MathF.FusedMultiplyAdd(g, GidScale, XBase[k]);
            st[2 * k + 1] = YBase[k];
        }
        for (uint i = 0; i < iterations; i++)
            for (int k = 0; k < PairsPerThread; k++)
            {
                float x = st[2 * k], y = st[2 * k + 1];
                float t = y * RotS, u = x * RotS;
                st[2 * k] = MathF.FusedMultiplyAdd(x, RotC, -t);
                st[2 * k + 1] = MathF.FusedMultiplyAdd(y, RotC, u);
            }
        st.CopyTo(outState);
    }

    /// <summary>lowbias32 (Chris Wellons) over (lo ^ hi·0x85EBCA6B ^ seed): the word the memory kernels store at a word index.</summary>
    public static uint MemoryPattern(ulong wordIndex, uint seed)
    {
        uint x = (uint)wordIndex ^ ((uint)(wordIndex >> 32) * 0x85EBCA6Bu) ^ seed;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>Words of <paramref name="window"/> (from word <paramref name="firstWord"/>) that differ from the pattern.</summary>
    public static long VerifyWindow(ReadOnlySpan<uint> window, ulong firstWord, uint seed)
    {
        long bad = 0;
        for (int i = 0; i < window.Length; i++)
            if (window[i] != MemoryPattern(firstWord + (ulong)i, seed)) bad++;
        return bad;
    }

    /// <summary>Pointer-chase LCG (full period mod any power of two: A = 1 mod 4, C odd) and its slot, one GDDR7 burst.</summary>
    public const uint ChaseA = 0x9E3779B5u, ChaseC = 0x7F4A7C15u;
    public const int ChaseSlotBytes = 32;

    /// <summary>Start slot of chase thread t (the kernel's t·0x9E3779B1, masked).</summary>
    public static uint ChaseStart(uint t, uint mask) => unchecked(t * 0x9E3779B1u) & mask;

    /// <summary>The slot after <paramref name="hops"/> steps of s → (A·s + C) mod n, in O(log hops): the affine map composed
    /// with itself by squaring, mod 2^32 (n divides 2^32, so the final mask is exact).</summary>
    public static uint ChaseEnd(uint start, uint hops, uint mask)
    {
        uint a = ChaseA, c = ChaseC, ra = 1, rc = 0;
        while (hops != 0)
        {
            if ((hops & 1) != 0) { rc = unchecked(a * rc + c); ra = unchecked(a * ra); }
            c = unchecked(a * c + c); a = unchecked(a * a);
            hops >>= 1;
        }
        return unchecked(ra * start + rc) & mask;
    }
}

/// <summary>
/// The order a memory pass walks the buffer in, from the GDDR7 tester: linear, 64 KiB and 128 KiB strides (across banks and
/// pages: row-buffer conflicts), block-xor (half of each 4 KiB block swapped: timing inside a bank) and a multiplicative
/// permutation (near-random rows and columns). Each is a bijection on the buffer's 16-byte vectors, so no address is skipped
/// or visited twice and a word's expected value depends only on its address and the seed.
/// </summary>
public enum AccessPattern { Linear, Stride64K, Stride128K, BlockXor, Permute }

/// <summary>
/// A pattern as the kernels run it: vector v goes to m = (v·Multiplier) mod n, then to m ^ 128 when m is under XorFull
/// (whole 4 KiB blocks only; the tail maps to itself). A thread's next vector is v + G, whose m is the last one plus Delta
/// = (G·Multiplier) mod n, so the kernel adds and subtracts once instead of dividing per access.
/// </summary>
public readonly record struct PatternPlan(AccessPattern Pattern, ulong Multiplier, ulong Delta, ulong XorFull)
{
    public const int VectorBytes = 16;
    private const ulong BlockVectors = 4096 / VectorBytes;

    public static PatternPlan Of(AccessPattern p, ulong vectors, ulong gridThreads)
    {
        ulong s = p switch
        {
            AccessPattern.Stride64K => Coprime(64 * 1024 / VectorBytes, vectors),
            AccessPattern.Stride128K => Coprime(128 * 1024 / VectorBytes, vectors),
            AccessPattern.Permute => Coprime((ulong)(vectors * 0.6180339887498949), vectors),
            _ => 1,
        };
        ulong d = (ulong)((UInt128)gridThreads * s % vectors);
        return new(p, s, d, p == AccessPattern.BlockXor ? vectors / BlockVectors * BlockVectors : 0);
    }

    /// <summary>Where vector <paramref name="v"/> of the walk lands.</summary>
    public ulong Map(ulong v, ulong vectors)
    {
        ulong m = (ulong)((UInt128)v * Multiplier % vectors);
        return m < XorFull ? m ^ (BlockVectors / 2) : m;
    }

    /// <summary>An odd step near <paramref name="target"/> that shares no factor with <paramref name="n"/>, searched ±2 at a
    /// time as the tester's pick_coprime_step does; 1 when none is found.</summary>
    public static ulong Coprime(ulong target, ulong n)
    {
        if (n <= 2) return 1;
        target = Math.Clamp(target % n, 1, n - 1) | 1;
        for (ulong k = 0; k < 100_000; k++)
            foreach (long sign in new long[] { 1, -1 })
            {
                long c = (long)target + sign * 2 * (long)k;
                if (c > 1 && (ulong)c < n && Gcd((ulong)c, n) == 1) return (ulong)c;
            }
        return 1;
    }

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
