// C01 fp32_fma_throughput (spec §7): many independent FP32 FMA accumulators per thread at high occupancy.
// Exact oracle: every operation in the hot loop is fmaf() (IEEE fused multiply-add, one rounding, RN-even, no FTZ),
// every operand is built from integer bits, so the CPU reproduces each class bit for bit with
// MathF.FusedMultiplyAdd / Fma.MultiplyAdd in the same order (Bwt.Workloads/C01Fp32Fma.cs).
//
// Bounded, non-degenerate recurrence (docs/KERNEL_CONTRACT.md §5): x <- fma(x, m, a) with m in [1,2), a in [1,2),
// x >= 1. The map is expanding (no attracting fixed point: a perturbation is not pulled back to a fixed value).
// Every SEG steps the exponent is reset by a bit operation (exact power-of-two scaling back into [1,2)): the old bits
// (sign/exponent/mantissa) are folded into an integer hash h first (a bijection, so a difference that reaches a fold
// is never lost), and the mantissa is XORed with a segment key, which makes the map non-autonomous (no short cycles).
// Within a segment x <= 2^SEG * 4, far below FLT_MAX for SEG = 32; x never approaches 0 or a denormal.
// Limit (measured in tests/C01OracleTests): an error in mantissa bits >= 4 of any FMA result always reaches the next
// fold; a 1..8-ulp error (bits 0..3) can be rounded away at a later binade crossing before the fold (bit 0: ~45%).
// Optional checkpoint8 entries fold after steps 8/16/24/32 without modifying x, its FMA order, SEG or the renorm key.
// They are built into a separate module with C01_CHECKPOINT8_ONLY, keeping the existing default images unchanged.
//
// Freshness (§2): m, a and the start state derive from (nonce, class); each block writes a completion tag derived
// from (epoch, nonce, block, salt) after __syncthreads(). The host poisons outputs and tags before every launch.
//
// Chaining (used by M02): with in_f/in_h non-null a launch continues from the previous launch's outputs, and seg_base
// continues the segment-key sequence, so a chain of launches equals one long run on the CPU.

typedef unsigned int u32;
typedef unsigned long long u64;

#define C01_SEG 32

__device__ __forceinline__ u32 mix64to32(u64 z)
{
    z += 0x9E3779B97F4A7C15ull;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return (u32)(z ^ (z >> 31));
}

__device__ __forceinline__ u32 block_tag(u32 epoch, u64 nonce, u32 block, u32 salt)
{
    return mix64to32(nonce ^ ((u64)epoch << 32) ^ (u64)block * 0x632BE59BD9B4E019ull ^ (u64)salt * 0xD1B54A32D192ED03ull) | 1u;
}

// [1, 2) from 23 random bits: built from bits, never from float arithmetic.
__device__ __forceinline__ float unit_float(u32 r) { return __uint_as_float(0x3F800000u | (r >> 9)); }

template <int NACC, int HASH_INTERVAL = 32>
__device__ __forceinline__ void c01_body(float* __restrict__ out_f, u32* __restrict__ out_h, u32* __restrict__ tags,
                                         const float* __restrict__ in_f, const u32* __restrict__ in_h,
                                         u64 nonce, u32 epoch, u32 seg_base, u32 segs, u32 classes, u32 produce_blocks, u32 tag_salt)
{
    if (blockIdx.x >= produce_blocks) return;
    const u32 n = gridDim.x * blockDim.x;
    const u32 tid = blockIdx.x * blockDim.x + threadIdx.x;
    const u32 k = tid % classes;
    const u64 base = nonce ^ ((u64)(k + 1) * 0x9E3779B97F4A7C15ull);
    float x[NACC], m[NACC], a[NACC];
#pragma unroll
    for (int j = 0; j < NACC; ++j)
    {
        const u64 s = base ^ ((u64)(j + 1) * 0xD6E8FEB86659FD93ull);
        m[j] = unit_float(mix64to32(s ^ 0x1111111111111111ull));
        a[j] = unit_float(mix64to32(s ^ 0x2222222222222222ull));
    }
    u32 h;
    if (in_f != nullptr)
    {
#pragma unroll
        for (int j = 0; j < NACC; ++j) x[j] = in_f[(u64)j * n + tid];
        h = in_h[tid];
    }
    else
    {
#pragma unroll
        for (int j = 0; j < NACC; ++j) x[j] = unit_float(mix64to32(base ^ ((u64)(j + 1) * 0xD6E8FEB86659FD93ull) ^ 0x3333333333333333ull));
        h = mix64to32(base ^ 0xC01C01C01C01C01Cull);
    }
    for (u32 s = 0; s < segs; ++s)
    {
#pragma unroll
        for (int i = 0; i < C01_SEG; ++i)
        {
#pragma unroll
            for (int j = 0; j < NACC; ++j) x[j] = fmaf(x[j], m[j], a[j]);
            if constexpr (HASH_INTERVAL == 8)
            {
                if ((i & 7) == 7)
                {
#pragma unroll
                    for (int j = 0; j < NACC; ++j) h = h * 0x01000193u + __float_as_uint(x[j]);
                }
            }
        }
        const u32 key = (((seg_base + s) * 0x9E3779B9u) & 0x007FFFFFu) | 0x3F800000u;
#pragma unroll
        for (int j = 0; j < NACC; ++j)
        {
            const u32 b = __float_as_uint(x[j]);
            if constexpr (HASH_INTERVAL == 32) h = h * 0x01000193u + b;
            x[j] = __uint_as_float((b & 0x007FFFFFu) ^ key); // exponent reset: exact scale into [1,2)
        }
    }
#pragma unroll
    for (int j = 0; j < NACC; ++j) out_f[(u64)j * n + tid] = x[j];   // structure of arrays: coalesced
    out_h[tid] = h;
    __syncthreads();
    if (threadIdx.x == 0) tags[blockIdx.x] = block_tag(epoch, nonce, blockIdx.x, tag_salt);
}

#define C01_ENTRY(NACC)                                                                                                   \
    extern "C" __global__ void c01_fma_a##NACC(float* __restrict__ out_f, u32* __restrict__ out_h, u32* __restrict__ tags, \
                                               const float* __restrict__ in_f, const u32* __restrict__ in_h, u64 nonce,     \
                                               u32 epoch, u32 seg_base, u32 segs, u32 classes, u32 produce_blocks,          \
                                               u32 tag_salt)                                                                \
    {                                                                                                                       \
        c01_body<NACC>(out_f, out_h, tags, in_f, in_h, nonce, epoch, seg_base, segs, classes, produce_blocks, tag_salt);    \
    }

#ifndef C01_CHECKPOINT8_ONLY
C01_ENTRY(8)
C01_ENTRY(16)
C01_ENTRY(32)
#else
#define C01_H8_ENTRY(NACC)                                                                                                  \
    extern "C" __global__ void c01_fma_a##NACC##_h8(float* __restrict__ out_f, u32* __restrict__ out_h, u32* __restrict__ tags, \
                                                  const float* __restrict__ in_f, const u32* __restrict__ in_h, u64 nonce, \
                                                  u32 epoch, u32 seg_base, u32 segs, u32 classes, u32 produce_blocks,       \
                                                  u32 tag_salt)                                                             \
    {                                                                                                                       \
        c01_body<NACC, 8>(out_f, out_h, tags, in_f, in_h, nonce, epoch, seg_base, segs, classes, produce_blocks, tag_salt); \
    }
C01_H8_ENTRY(8)
C01_H8_ENTRY(16)
C01_H8_ENTRY(32)
#endif
