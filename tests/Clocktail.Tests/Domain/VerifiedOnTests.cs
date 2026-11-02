using Clocktail.Domain;

namespace Clocktail.Tests.Domain;

public class VerifiedOnTests
{
    [Fact]
    public void The_reference_card_on_its_driver_is_what_was_measured()
    {
        Assert.Equal("617.42", VerifiedOn.Driver(61742));
        Assert.Equal((true, true, true), VerifiedOn.Match(61742, "NVIDIA GeForce RTX 5090", "98.02.2e.00.03"));
        Assert.Contains("has recorded hardware validation", VerifiedOn.Describe(61742, "NVIDIA GeForce RTX 5090", "98.02.2e.00.03"));
    }

    [Fact]
    public void Another_driver_or_card_is_named_and_fails_safe_in_words()
    {
        string d = VerifiedOn.Describe(61803, "NVIDIA GeForce RTX 5090", "98.02.2e.00.03");
        Assert.Contains("driver 618.03", d);
        Assert.Contains("no recorded hardware-validation result", d);
        Assert.Contains("not a model whitelist", d);
        string g = VerifiedOn.Describe(61742, "NVIDIA GeForce RTX 5080", "98.03.11.00.01");
        Assert.Contains("detected NVIDIA GeForce RTX 5080", g);
        Assert.Contains("VBIOS 98.03", g);
        Assert.Contains("RTX 50 series", g);
        Assert.Contains("No tuning profile or default daily settings", g);
    }
}
