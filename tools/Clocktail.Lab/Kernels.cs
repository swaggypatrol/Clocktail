using System.Text;

namespace Clocktail.Lab;

/// <summary>
/// The kernels as PTX text (StabilityLab's, the memory pair extended with the GDDR7 tester's access orders). The driver
/// JIT-compiles PTX for whatever GPU is present: no CUDA toolkit, no nvcc, no per-architecture binaries. Every arithmetic
/// op carries an explicit rounding mode (.rn), so ptxas cannot contract mul+add into an fma the CPU twin does not compute.
/// </summary>
public static class Kernels
{
    public const string Fma = "fma_rot", MemFill = "mem_fill", MemVerify = "mem_verify", ChaseFill = "chase_fill", ChaseRun = "chase_run";

    public static string Module() => new StringBuilder("""
        .version 7.0
        .target sm_52
        .address_size 64

        """).Append(FmaRot).Append(MemoryKernel(verify: false)).Append(MemoryKernel(verify: true)).Append(Chase).ToString();

    // fma_rot(float* out, u32 iters, f32 c, f32 s): four independent 2-D rotations per thread, 8 floats at out[gid*8].
    private const string FmaRot = """

        .visible .entry fma_rot(
            .param .u64 p_out,
            .param .u32 p_iters,
            .param .f32 p_c,
            .param .f32 p_s
        )
        {
            .reg .pred %p<2>;
            .reg .b32 %r<8>;
            .reg .f32 %f<40>;
            .reg .b64 %rd<6>;

            ld.param.u64 %rd1, [p_out];
            ld.param.u32 %r1, [p_iters];
            ld.param.f32 %f1, [p_c];
            ld.param.f32 %f2, [p_s];
            cvta.to.global.u64 %rd2, %rd1;

            mov.u32 %r2, %ctaid.x;
            mov.u32 %r3, %ntid.x;
            mov.u32 %r4, %tid.x;
            mad.lo.s32 %r5, %r2, %r3, %r4;
            cvt.rn.f32.s32 %f3, %r5;
            mov.f32 %f4, 0f37800000;

            mov.f32 %f5, 0f3F800000;
            fma.rn.f32 %f10, %f3, %f4, %f5;
            mov.f32 %f11, 0f00000000;
            mov.f32 %f5, 0f3F400000;
            fma.rn.f32 %f12, %f3, %f4, %f5;
            mov.f32 %f13, 0f3F000000;
            mov.f32 %f5, 0f3F000000;
            fma.rn.f32 %f14, %f3, %f4, %f5;
            mov.f32 %f15, 0fBF000000;
            mov.f32 %f5, 0f3E800000;
            fma.rn.f32 %f16, %f3, %f4, %f5;
            mov.f32 %f17, 0f3E800000;

            mov.u32 %r6, 0;
            setp.ge.u32 %p1, %r6, %r1;
            @%p1 bra FMA_DONE;
        FMA_LOOP:
            mul.rn.f32 %f20, %f11, %f2;
            neg.f32 %f20, %f20;
            mul.rn.f32 %f21, %f10, %f2;
            fma.rn.f32 %f10, %f10, %f1, %f20;
            fma.rn.f32 %f11, %f11, %f1, %f21;

            mul.rn.f32 %f22, %f13, %f2;
            neg.f32 %f22, %f22;
            mul.rn.f32 %f23, %f12, %f2;
            fma.rn.f32 %f12, %f12, %f1, %f22;
            fma.rn.f32 %f13, %f13, %f1, %f23;

            mul.rn.f32 %f24, %f15, %f2;
            neg.f32 %f24, %f24;
            mul.rn.f32 %f25, %f14, %f2;
            fma.rn.f32 %f14, %f14, %f1, %f24;
            fma.rn.f32 %f15, %f15, %f1, %f25;

            mul.rn.f32 %f26, %f17, %f2;
            neg.f32 %f26, %f26;
            mul.rn.f32 %f27, %f16, %f2;
            fma.rn.f32 %f16, %f16, %f1, %f26;
            fma.rn.f32 %f17, %f17, %f1, %f27;

            add.u32 %r6, %r6, 1;
            setp.lt.u32 %p1, %r6, %r1;
            @%p1 bra FMA_LOOP;
        FMA_DONE:
            mul.wide.u32 %rd3, %r5, 32;
            add.s64 %rd4, %rd2, %rd3;
            st.global.v4.f32 [%rd4], {%f10, %f11, %f12, %f13};
            st.global.v4.f32 [%rd4+16], {%f14, %f15, %f16, %f17};
            ret;
        }

        """;

    /// <summary>lowbias32 over (lo ^ hi·0x85EBCA6B ^ seed) for the word index in <paramref name="idx64"/>, into <paramref name="dst"/>.</summary>
    private static string Hash(string idx64, string dst, string tmp) => $"""
            cvt.u32.u64 {dst}, {idx64};
            shr.u64 %rd9, {idx64}, 32;
            cvt.u32.u64 {tmp}, %rd9;
            mul.lo.u32 {tmp}, {tmp}, 2246822507;
            xor.b32 {dst}, {dst}, {tmp};
            xor.b32 {dst}, {dst}, %r1;
            shr.u32 {tmp}, {dst}, 16;
            xor.b32 {dst}, {dst}, {tmp};
            mul.lo.u32 {dst}, {dst}, 2146121005;
            shr.u32 {tmp}, {dst}, 15;
            xor.b32 {dst}, {dst}, {tmp};
            mul.lo.u32 {dst}, {dst}, 2221713035;
            shr.u32 {tmp}, {dst}, 16;
            xor.b32 {dst}, {dst}, {tmp};

        """;

    /// <summary>The four words of the vector whose first word index is in %rd26, into %r10..%r13.</summary>
    private static string FourHashes() =>
        Hash("%rd26", "%r10", "%r20") +
        "    add.s64 %rd6, %rd26, 1;\n" + Hash("%rd6", "%r11", "%r20") +
        "    add.s64 %rd6, %rd26, 2;\n" + Hash("%rd6", "%r12", "%r20") +
        "    add.s64 %rd6, %rd26, 3;\n" + Hash("%rd6", "%r13", "%r20");

    /// <summary>
    /// mem_fill(u32* buf, u64 nVectors, u32 seed, u64 mul, u64 delta, u64 xorFull, u64 limit) writes every 16-byte vector its
    /// pattern words; mem_verify(..., u64* errs) reads them back, errs = { u32 bad words, u32 pad, u64 lowest bad word index }. Both
    /// walk the buffer in one <see cref="PatternPlan"/> order: thread gid takes vectors v = gid, gid + G, ... up to limit (n for one
    /// pass; a multiple of n walks a small region over and over, the L2 test); its first m =
    /// (gid·mul) mod n is one division, each next one the last plus delta, less n once if it wrapped; then m ^ 128 under
    /// xorFull. Loads and stores are .cg (L2 only, as the tester's -dlcm=cg): an L1 hit would hide the DRAM.
    /// </summary>
    private static string MemoryKernel(bool verify) => $$"""

        .visible .entry {{(verify ? MemVerify : MemFill)}}(
            .param .u64 p_buf,
            .param .u64 p_n,
            .param .u32 p_seed,
            .param .u64 p_mul,
            .param .u64 p_delta,
            .param .u64 p_xor,
            .param .u64 p_limit{{(verify ? ",\n    .param .u64 p_errs" : "")}}
        )
        {
            .reg .pred %p<8>;
            .reg .b32 %r<32>;
            .reg .b64 %rd<32>;

            ld.param.u64 %rd1, [p_buf];
            ld.param.u64 %rd2, [p_n];
            ld.param.u32 %r1, [p_seed];
            ld.param.u64 %rd20, [p_mul];
            ld.param.u64 %rd21, [p_delta];
            ld.param.u64 %rd22, [p_xor];
            ld.param.u64 %rd27, [p_limit];
            cvta.to.global.u64 %rd3, %rd1;
            mov.u32 %r2, %ctaid.x;
            mov.u32 %r3, %ntid.x;
            mov.u32 %r4, %tid.x;
            mad.lo.s32 %r5, %r2, %r3, %r4;
            mov.u32 %r6, %nctaid.x;
            mul.lo.u32 %r7, %r6, %r3;
            cvt.u64.u32 %rd4, %r5;
            cvt.u64.u32 %rd5, %r7;
            mul.lo.u64 %rd23, %rd4, %rd20;
            rem.u64 %rd23, %rd23, %rd2;
        {{(verify ? "    ld.param.u64 %rd10, [p_errs];\n    cvta.to.global.u64 %rd10, %rd10;\n    add.s64 %rd11, %rd10, 8;\n" : "")}}
        WALK:
            setp.ge.u64 %p1, %rd4, %rd27;
            @%p1 bra WALK_DONE;
            setp.lt.u64 %p1, %rd23, %rd22;
            xor.b64 %rd25, %rd23, 128;
            selp.b64 %rd24, %rd25, %rd23, %p1;
            shl.b64 %rd26, %rd24, 2;
            shl.b64 %rd7, %rd24, 4;
            add.s64 %rd7, %rd3, %rd7;
        {{FourHashes()}}
        {{(verify ? VerifyVector : "    st.global.cg.v4.u32 [%rd7], {%r10, %r11, %r12, %r13};\n")}}
        NEXT:
            add.s64 %rd4, %rd4, %rd5;
            add.s64 %rd23, %rd23, %rd21;
            setp.ge.u64 %p1, %rd23, %rd2;
            @%p1 sub.s64 %rd23, %rd23, %rd2;
            bra WALK;
        WALK_DONE:
            ret;
        }

        """;

    private const string VerifyVector = """
            ld.global.cg.v4.u32 {%r14, %r15, %r16, %r17}, [%rd7];
            setp.ne.u32 %p2, %r14, %r10;
            setp.ne.u32 %p3, %r15, %r11;
            setp.ne.u32 %p4, %r16, %r12;
            setp.ne.u32 %p5, %r17, %r13;
            or.pred %p6, %p2, %p3;
            or.pred %p7, %p4, %p5;
            or.pred %p6, %p6, %p7;
            @!%p6 bra NEXT;
            selp.u32 %r21, 1, 0, %p2;
            selp.u32 %r22, 1, 0, %p3;
            add.u32 %r21, %r21, %r22;
            selp.u32 %r22, 1, 0, %p4;
            add.u32 %r21, %r21, %r22;
            selp.u32 %r22, 1, 0, %p5;
            add.u32 %r21, %r21, %r22;
            atom.global.add.u32 %r23, [%rd10], %r21;
            atom.global.min.u64 %rd12, [%rd11], %rd26;

        """;

    // Pointer chase over the whole buffer, one u32 per 32-byte slot (a GDDR7 burst): slot s holds (a·s + c) mod nSlots, a
    // full-period LCG (nSlots a power of two, a = 1 mod 4, c odd). Every hop is a dependent load to a far slot, so the time
    // per hop is the DRAM round trip; a wrong word breaks the chain, whose end the CPU knows in closed form.
    // chase_fill(u32* buf, u32 mask, u32 a, u32 c); chase_run(u32* buf, u32 mask, u32 hops, u32* out).
    private const string Chase = """

        .visible .entry chase_fill(
            .param .u64 p_buf,
            .param .u32 p_mask,
            .param .u32 p_a,
            .param .u32 p_c
        )
        {
            .reg .pred %p<2>;
            .reg .b32 %r<16>;
            .reg .b64 %rd<8>;

            ld.param.u64 %rd1, [p_buf];
            ld.param.u32 %r1, [p_mask];
            ld.param.u32 %r2, [p_a];
            ld.param.u32 %r3, [p_c];
            cvta.to.global.u64 %rd1, %rd1;
            mov.u32 %r4, %ctaid.x;
            mov.u32 %r5, %ntid.x;
            mov.u32 %r6, %tid.x;
            mad.lo.s32 %r7, %r4, %r5, %r6;
            mov.u32 %r8, %nctaid.x;
            mul.lo.u32 %r9, %r8, %r5;
        CF_LOOP:
            setp.gt.u32 %p1, %r7, %r1;
            @%p1 bra CF_DONE;
            mad.lo.u32 %r10, %r2, %r7, %r3;
            and.b32 %r10, %r10, %r1;
            cvt.u64.u32 %rd2, %r7;
            shl.b64 %rd2, %rd2, 5;
            add.s64 %rd3, %rd1, %rd2;
            st.global.u32 [%rd3], %r10;
            add.u32 %r7, %r7, %r9;
            setp.lt.u32 %p1, %r7, %r9;
            @%p1 bra CF_DONE;
            bra CF_LOOP;
        CF_DONE:
            ret;
        }

        .visible .entry chase_run(
            .param .u64 p_buf,
            .param .u32 p_mask,
            .param .u32 p_hops,
            .param .u64 p_out
        )
        {
            .reg .pred %p<2>;
            .reg .b32 %r<16>;
            .reg .b64 %rd<8>;

            ld.param.u64 %rd1, [p_buf];
            ld.param.u32 %r1, [p_mask];
            ld.param.u32 %r2, [p_hops];
            ld.param.u64 %rd4, [p_out];
            cvta.to.global.u64 %rd1, %rd1;
            cvta.to.global.u64 %rd4, %rd4;
            mov.u32 %r4, %ctaid.x;
            mov.u32 %r5, %ntid.x;
            mov.u32 %r6, %tid.x;
            mad.lo.s32 %r7, %r4, %r5, %r6;
            mul.lo.u32 %r8, %r7, 0x9E3779B1;
            and.b32 %r8, %r8, %r1;
            mov.u32 %r9, 0;
        CR_LOOP:
            setp.ge.u32 %p1, %r9, %r2;
            @%p1 bra CR_DONE;
            cvt.u64.u32 %rd2, %r8;
            shl.b64 %rd2, %rd2, 5;
            add.s64 %rd3, %rd1, %rd2;
            ld.global.cg.u32 %r8, [%rd3];
            add.u32 %r9, %r9, 1;
            bra CR_LOOP;
        CR_DONE:
            cvt.u64.u32 %rd5, %r7;
            shl.b64 %rd5, %rd5, 2;
            add.s64 %rd5, %rd4, %rd5;
            st.global.u32 [%rd5], %r8;
            ret;
        }

        """;
}
