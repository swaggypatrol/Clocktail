using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

public class DailyProfileTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 13, 37, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Writes_are_ordered_power_then_P0_offsets_and_the_curve_last()
    {
        var o = DailyProfile.InWriteOrder([new("vf", "core", "0*6"), new("power", "fe", "1"), new("pstates20", "memory", "0")]);
        Assert.Equal(["power", "pstates20", "vf"], o.Select(w => w.Kind));
    }

    /// <summary>MSVDD's offsets and XBAR's voltage demand go on before the XBAR offset they carry; all before the curve.</summary>
    [Fact]
    public void The_msvdd_offsets_go_on_before_the_xbar_offset()
    {
        var o = DailyProfile.InWriteOrder([new("vf", "core", "0*6"), new("clockdomain", "xbar", "400000"), new("ocp", "nvvdd", "1"),
                                           new("vdemand", "xbar", "20000"), new("rails", "msvdd", "rel=20000,alt=0,ov=0,vmin=0"),
                                           new("pstates20", "memory", "0")]);
        Assert.Equal(["pstates20", "rails", "ocp", "vdemand", "clockdomain", "vf"], o.Select(w => w.Kind));
    }

    /// <summary>The fan curve touches nothing else: it goes on right after the wall, cooling before clocks; the two
    /// policies keep their order.</summary>
    [Fact]
    public void The_fan_curve_goes_on_after_the_wall_and_before_the_clocks()
    {
        var o = DailyProfile.InWriteOrder([new("vf", "core", "0*6"), new("fancurve", "0", "a"), new("pstates20", "memory", "0"),
                                           new("ocp", "nvvdd", "1"), new("fancurve", "1", "b"), new("power", "fe", "1")]);
        Assert.Equal(["power/fe", "fancurve/0", "fancurve/1", "pstates20/memory", "ocp/nvvdd", "vf/core"], o.Select(w => $"{w.Kind}/{w.Key}"));
        Assert.True(DailyProfile.KeptFromCard("vf", "core"));
        Assert.True(DailyProfile.KeptFromCard("fancurve", "0"));
        Assert.True(DailyProfile.KeptFromCard("rails", "msvdd"));
        Assert.True(DailyProfile.KeptFromCard("rails", "nvvdd"));   // adjust_nvvdd tunes it (2026-10-21)
        Assert.True(DailyProfile.KeptFromCard("clockdomain", "xbar"));
        Assert.True(DailyProfile.KeptFromCard("vdemand", "xbar"));
        Assert.False(DailyProfile.KeptFromCard("power", "fe"));
        Assert.True(DailyProfile.KeptFromCard("pstates20", "memory"));   // the agent tunes it now (2026-10-20)
        Assert.True(DailyProfile.KeptFromCard("boost", "legacy"));     // set_voltage_boost tunes it (2026-10-21)
        Assert.True(DailyProfile.KeptFromCard("pstates20", "core"));    // set_core_offset tunes it (2026-10-21)
    }

    /// <summary>The profile on this machine was kept by a build with the step-down: its Down and LastResetAt must not
    /// stop this build from reading it.</summary>
    [Fact]
    public void A_profile_kept_by_the_step_down_build_still_loads()
    {
        Directory.CreateDirectory(_dir);
        string p = Path.Combine(_dir, DailyProfile.FileName);
        File.WriteAllText(p, """
            {
              "Writes": [ { "Kind": "power", "Key": "fe", "Value": "600000" }, { "Kind": "vf", "Key": "core", "Value": "0*57,30000*70" } ],
              "Down": { "Khz": 15000, "FromUv": 875000, "ToUv": 1050000 },
              "KeptAt": "2026-10-19T03:22:02+00:00",
              "Suspended": false,
              "LastResetAt": null,
              "History": [ "2026-10-19 03:22:02Z kept" ]
            }
            """);
        var d = DailyProfile.Load(p)!;
        Assert.Equal(["power", "vf"], d.Writes.Select(w => w.Kind));
        Assert.Equal("600000", d.Writes[0].Value);
        Assert.False(d.Suspended);
        Assert.Single(d.History!);
    }

    [Fact]
    public void History_keeps_the_last_twenty_lines()
    {
        var d = new DailyProfile([new("power", "fe", "600000")], T0);
        for (int i = 0; i < 25; i++) d = d.Noted(T0.AddMinutes(i), $"line {i}");
        Assert.Equal(20, d.History!.Count);
        Assert.EndsWith("line 24", d.History[^1]);
        Assert.EndsWith("line 5", d.History[0]);
    }

    /// <summary>S14: a profile is bound to the VBIOS it was kept on, and one from before 0.2.3 (none recorded) or a card
    /// whose VBIOS cannot be read is not held back.</summary>
    [Fact]
    public void A_profile_is_bound_to_the_vbios_it_was_kept_on()
    {
        var d = new DailyProfile([new("power", "fe", "560000")], T0).On("98.02.2E.00.0A", "617.42");
        Assert.Equal(("98.02.2E.00.0A", "617.42"), (d.Vbios, d.Driver));
        Assert.Null(d.OtherVbios("98.02.2e.00.0a"));
        Assert.Contains("kept on VBIOS 98.02.2E.00.0A and the card now runs 98.02.30.00.01", d.OtherVbios("98.02.30.00.01"));
        Assert.Null(d.OtherVbios(null));
        Assert.Null(d.OtherVbios(""));
        Assert.Null(new DailyProfile([], T0).OtherVbios("98.02.30.00.01"));
        Assert.Equal("617.42", d.On("", null).Driver);   // an unknown reading keeps the old stamp

        string path = Path.Combine(_dir, "profile.json");
        Directory.CreateDirectory(_dir);
        d.Save(path);
        Assert.Equal("98.02.2E.00.0A", DailyProfile.Load(path)!.Vbios);
        File.WriteAllText(path, """{"Writes":[],"KeptAt":"2026-10-18T13:37:00+00:00","Suspended":false}""");   // 0.2.2's file
        Assert.Null(DailyProfile.Load(path)!.Vbios);
    }
}
