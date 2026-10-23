using Clocktail.Domain;

namespace Clocktail.Tests.Domain;

public class VerifiedOnTests
{
    /// <summary>617.42, then 621.01 (Clocktail 0.3.3 to 0.3.5 were measured on it).</summary>
    [Theory]
    [InlineData(61742u, "617.42")]
    [InlineData(62101u, "621.01")]
    public void The_reference_card_on_its_drivers_is_what_was_measured(uint driver, string name)
    {
        Assert.Equal(name, VerifiedOn.Driver(driver));
        Assert.Equal((true, true, true), VerifiedOn.Match(driver, "NVIDIA GeForce RTX 5090", "98.02.2e.00.03"));
        Assert.StartsWith("the driver, card and VBIOS", VerifiedOn.Describe(driver, "NVIDIA GeForce RTX 5090", "98.02.2e.00.03"));
    }

    [Fact]
    public void Another_driver_or_card_is_named_and_fails_safe_in_words()
    {
        string d = VerifiedOn.Describe(61803, "NVIDIA GeForce RTX 5090", "98.02.2e.00.03");
        Assert.Contains("measured on driver 617.42 / 621.01 on an RTX 5090", d);
        Assert.Contains("this driver 618.03 is not one of them yet", d);
        Assert.Contains("expect a refusal, not a wrong write", d);
        string g = VerifiedOn.Describe(61742, "NVIDIA GeForce RTX 5080", "98.03.11.00.01");
        Assert.Contains("this NVIDIA GeForce RTX 5080 is not", g);
        Assert.DoesNotContain("VBIOS 98.03", g);   // another model's VBIOS is not news
    }
}
