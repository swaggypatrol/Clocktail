using Bwt.Workloads;

namespace Clocktail.Tests.Lab;

public sealed class AriaOracleTests
{
    [Fact]
    public void Scalar_and_vector_oracles_agree_bit_for_bit()
    {
        foreach (int accumulators in new[] { 8, 16, 32 })
        foreach (int hash in new[] { 8, 32 })
        foreach (ulong nonce in new[] { 0UL, 1UL, 0x5eed5eed5eed5eedUL, ulong.MaxValue })
        foreach (uint cls in new[] { 0u, 1u, 31u })
        foreach (uint segments in new[] { 0u, 1u, 3u, 100u })
        {
            var scalar = C01Kernel.Reference(nonce, cls, accumulators, segments, scalar: true, hashInterval: hash);
            var vector = C01Kernel.Reference(nonce, cls, accumulators, segments, scalar: false, hashInterval: hash);
            Assert.Equal(scalar.H, vector.H);
            Assert.Equal(scalar.XBits, vector.XBits);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("device_bit_flip")]
    [InlineData("non_finite")]
    [InlineData("comparator_fixture")]
    [InlineData("skip_output")]
    [InlineData("short_output")]
    [InlineData("stale_epoch")]
    [InlineData("replay_previous")]
    public void Oracle_detects_corruption_missing_outputs_and_replays(string fault)
    {
        const int n = 257, acc = 16, classes = 32, blocks = 2;
        const uint epoch = 2;
        const ulong nonce = 123;
        var refs = new uint[classes * acc];
        var refH = new uint[classes];
        for (int k = 0; k < classes; k++)
        {
            var expected = C01Kernel.Reference(nonce, (uint)k, acc, 3, hashInterval: 8);
            expected.XBits.CopyTo(refs, k * acc); refH[k] = expected.H;
        }
        var values = new uint[n * acc]; var hashes = new uint[n]; var tags = new uint[blocks]; var wanted = refs.ToArray();
        ulong actualNonce = fault == "replay_previous" ? nonce - 1 : nonce;
        for (int tid = 0; tid < n; tid++)
        {
            var expected = C01Kernel.Reference(actualNonce, (uint)(tid % classes), acc, 3, hashInterval: 8);
            for (int a = 0; a < acc; a++) values[a * n + tid] = expected.XBits[a];
            hashes[tid] = expected.H;
        }
        for (int b = 0; b < blocks; b++) tags[b] = Hash.Tag(fault == "stale_epoch" ? epoch - 1 : epoch, actualNonce, (uint)b, 0);
        if (fault == "device_bit_flip") values[n * acc / 3] ^= 1u << 17;
        if (fault == "non_finite") values[n * acc / 2] = 0x7fc00000;
        if (fault == "comparator_fixture") wanted[0] ^= 1u << 5;
        if (fault == "skip_output") { Array.Fill(values, C01Fp32Fma.OutSentinel); Array.Fill(hashes, C01Fp32Fma.OutSentinel); Array.Clear(tags); }
        if (fault == "short_output") { for (int a = 0; a < acc; a++) values[a * n + 256] = C01Fp32Fma.OutSentinel; hashes[256] = C01Fp32Fma.OutSentinel; tags[1] = 0; }
        var result = C01Kernel.Compare(values, hashes, tags, wanted, refH, n, acc, classes, epoch, nonce, 0,
            C01Fp32Fma.OutSentinel, 0, "test");
        Assert.Equal(fault == "none", result.Clean);
    }
}
