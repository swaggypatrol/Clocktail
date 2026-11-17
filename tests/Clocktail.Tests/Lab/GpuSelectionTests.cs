using Clocktail.Lab;

namespace Clocktail.Tests.Lab;

public class GpuSelectionTests
{
    [Fact]
    public void UnnamedTargetRequiresExactlyOneDevice()
    {
        Assert.Equal("0000:41:00.0", Select(null, "0000:41:00.0"));
        Assert.Contains("no GPU", Assert.Throws<InvalidOperationException>(() => Select(null)).Message);
        Assert.Contains("specify --gpu-pci", Assert.Throws<InvalidOperationException>(
            () => Select(null, "0000:41:00.0", "0000:01:00.0")).Message);
    }

    [Fact]
    public void ExplicitTargetMatchesAcrossDriverFormatsAndRejectsDuplicates()
    {
        Assert.Equal("0000:0a:00.0", Select("00000000:0A:00.0", "0000:01:00.0", "0000:0a:00.0"));
        Assert.Contains("several GPUs", Assert.Throws<InvalidOperationException>(
            () => Select("0000:0a:00.0", "0000:0a:00.0", "00000000:0A:00.0")).Message);
    }

    [Theory]
    [InlineData("0000:1a:00.0")]
    [InlineData("0001:0a:00.0")]
    [InlineData("0000:0a:00.1")]
    [InlineData("0000.0a.00.0")]
    [InlineData("not-reported")]
    public void InvalidOrDifferentInventoryAddressCannotMatch(string reported) =>
        Assert.Throws<InvalidOperationException>(() => Select("0000:0a:00.0", reported));

    [Theory]
    [InlineData("")]
    [InlineData("0a:00.0")]
    [InlineData("a:00.0")]
    [InlineData("0000:0a:00")]
    [InlineData("0000:0a:00:0")]
    [InlineData("0000: 0a:00.0")]
    [InlineData("0000:0a:00.0\0")]
    [InlineData("0001:0a:00.0")]
    [InlineData("0000:0a:00.1")]
    public void MalformedOrUnsupportedRequestCannotSelectAnyDevice(string requested) =>
        Assert.Throws<NotSupportedException>(() => Select(requested, requested, "0000:0a:00.0"));

    [Fact]
    public void AriaRequiresBothSuppliedIdentifiersToMatchTheSameDevice()
    {
        var devices = new[] { (Pci: "0000:01:00.0", Uuid: "GPU-ab"), (Pci: "0000:0a:00.0", Uuid: "GPU-cd") };
        Assert.Equal(devices[1], GpuSelection.Select(devices, "CUDA", null, d => d.Pci, "gpu-CD", d => d.Uuid));
        Assert.Throws<InvalidOperationException>(() =>
            GpuSelection.Select(devices, "CUDA", devices[0].Pci, d => d.Pci, devices[1].Uuid, d => d.Uuid));
    }

    private static string Select(string? pci, params string[] devices) => GpuSelection.Select(devices, "CUDA", pci, d => d);
}
