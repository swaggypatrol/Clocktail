using Clocktail.Domain.Broker;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Writes;

namespace Clocktail.Tests.Domain;

public sealed class PresetsTests
{
    [Fact]
    public void The_founders_preset_ships_and_every_write_has_a_registered_path()
    {
        var p = Assert.Single(Presets.All, p => p.Name == "founders");
        Assert.Same(p, Presets.Find(" Founders "));
        Assert.NotEmpty(p.Writes);
        Assert.All(p.Writes, w => Assert.NotNull(WriteRegistry.Find(w.Kind)));
        Assert.Equal(p.Writes, DailyProfile.InWriteOrder(p.Writes));
        Assert.Null(Presets.Find("nonexistent"));
    }

    [Fact]
    public void A_preset_loads_only_on_the_model_it_was_tuned_on()
    {
        var p = Presets.Find("founders")!;
        Assert.True(p.Fits("NVIDIA GeForce RTX 5090"));
        Assert.True(p.Fits(" nvidia geforce rtx 5090 "));
        Assert.False(p.Fits("NVIDIA GeForce RTX 5080"));
        Assert.False(p.Fits("NVIDIA GeForce RTX 4090"));
        // Other chips under the same number, with their own power ranges.
        Assert.False(p.Fits("NVIDIA GeForce RTX 5090 Laptop GPU"));
        Assert.False(p.Fits("NVIDIA GeForce RTX 5090 D"));
        Assert.False(p.Fits("NVIDIA GeForce RTX 5090 D v2"));
    }
}
