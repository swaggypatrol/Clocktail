using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Clocks;
using Clocktail.Mcp;

namespace Clocktail.Tests.Domain;

/// <summary>T01: the driver's perf-limit arbitration as read on the 5090 (617.42, 2026-10-21) under nvidia-smi -lgc
/// 210,2000: the GPC ceiling 1995 MHz by CLIENT_LOW_STRICT_GPC_MAX and its floor 210 by CLIENT_LOW_STRICT_GPC_MIN, XBAR's
/// ceiling 1792 by the same lock, memory at 14359 MHz.</summary>
public class PerfLimitsTests
{
    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);

    private static byte[] Info(params (int Index, string Name)[] limits)
    {
        var b = new byte[PerfLimits.InfoSize];
        foreach (var (i, name) in limits)
        {
            W(b, PerfLimits.Mask + i / 32 * 4, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(PerfLimits.Mask + i / 32 * 4)) | 1u << (i % 32));
            Encoding.ASCII.GetBytes(name).CopyTo(b, PerfLimits.NamesAt + i * PerfLimits.NameStride);
        }
        return b;
    }

    private static readonly string?[] Names = PerfLimits.Names(Info((4, "PERF_CF_CONTROLLER_DRAM_MIN"), (0x2c, "CLIENT_LOW_STRICT_GPC_MAX"), (0x33, "CLIENT_LOW_STRICT_GPC_MIN"), (0xa5, "RELIABILITY_ALT_NVVDD_0")));

    private static byte[] Status()
    {
        var b = new byte[PerfLimits.StatusSize];
        void Pair(int o, uint khz, uint by) { W(b, o, khz); W(b, o + 4, by); }
        Pair(PerfLimits.CeilingsAt, 1_995_000, 0x2c); Pair(PerfLimits.FloorsAt, 210_000, 0x33);
        Pair(PerfLimits.CeilingsAt + 0x10, 1_792_000, 0x2c); Pair(PerfLimits.FloorsAt + 0x10, 885_000, 4);
        Pair(PerfLimits.CeilingsAt + 0x20, 14_359_000, 4); Pair(PerfLimits.FloorsAt + 0x20, 405_000, 4);
        return b;
    }

    [Fact]
    public void Each_domain_has_its_ceiling_and_floor_and_who_set_them()
    {
        var d = PerfLimits.Parse(Status(), Names);
        Assert.Equal(["gpc", "xbar", "memory"], d.Select(x => x.Domain));
        Assert.Equal((1995.0, "CLIENT_LOW_STRICT_GPC_MAX"), (d[0].Ceiling!.Mhz, d[0].Ceiling!.By));
        Assert.Equal((210.0, "CLIENT_LOW_STRICT_GPC_MIN"), (d[0].Floor!.Mhz, d[0].Floor!.By));
        Assert.Equal(1792.0, d[1].Ceiling!.Mhz);
        Assert.Contains("clock lock", PerfLimits.Means(d[0].Ceiling!.By));
        Assert.Contains("the curve's top", PerfLimits.Means("RELIABILITY_ALT_NVVDD_0"));
        Assert.Contains("a CUDA program has the GPU", PerfLimits.Means("CUDA_MAX"));   // the memory's ceiling beside api_triggered
        string json = JsonSerializer.Serialize(SettingsTools.PerfLimitsView(d));
        Assert.Contains("\"domain\":\"gpc\",\"ceiling_mhz\":1995,\"ceiling_by\":\"CLIENT_LOW_STRICT_GPC_MAX\"", json);
    }

    [Fact]
    public void A_pair_naming_no_limit_is_left_out_and_unmasked_names_are_not_read()
    {
        var b = Status();
        W(b, PerfLimits.CeilingsAt + 4, 0x99);   // an index the info has no limit for
        Assert.Null(PerfLimits.Parse(b, Names)[0].Ceiling);
        var info = Info((4, "PERF_CF_CONTROLLER_DRAM_MIN"));
        Encoding.ASCII.GetBytes("NOT_MASKED").CopyTo(info, PerfLimits.NamesAt + 7 * PerfLimits.NameStride);
        Assert.Null(PerfLimits.Names(info)[7]);
        Assert.Equal("PERF_CF_CONTROLLER_DRAM_MIN", PerfLimits.Names(info)[4]);
    }
}
