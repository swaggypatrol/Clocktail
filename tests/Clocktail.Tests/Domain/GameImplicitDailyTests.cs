using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Game;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

public class GameImplicitDailyTests
{
    private sealed class Rig : IGameAccess, IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ct-game-p0-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_dir, "game.json");
        public GpuTarget Identity { get; } = new("0000:07:03.0", "RTX 5090", 0x2b8510de, 0x205710de, 12, 0,
            @"PCI\VEN_10DE&DEV_2B85&SUBSYS_205710DE\ONE", 1, 1);
        public List<ProfileWrite> Cells = [new("vf", "core", "90000*127"), new("pstates20", "core", "240000"),
            new("pstates20", "memory", "3000000"), new("power", "fe", "450000")];
        public long[]? Effective = Enumerable.Repeat(90000L, 127).ToArray();
        public DailyProfile Daily;
        public Experiment Session;
        public int Writes;
        public string RestoredCurve = "240000*127";
        public Action? AfterRestore;
        public Rig()
        {
            Directory.CreateDirectory(_dir);
            var at = DateTimeOffset.UtcNow;
            Daily = new(Cells.Where(c => c.Kind != "vf").ToList(), at);
            Session = new("offline", null, at, new ExperimentTrial(at, "90000*127", "trial", null, null));
        }
        public void Set(string kind, string key, string value)
        {
            Cells.RemoveAll(c => c.Kind == kind && c.Key == key);
            Cells.Add(new(kind, key, value));
        }
        public void Verify() => Identity.Validate();
        public GameSnapshot Read() => new(Identity, Cells.Single(c => c.Kind == "vf").Value, 450000, 250000, 3000,
            16UL << 30, Cells.ToArray(), Effective?.ToArray());
        public void Restore()
        {
            Set("vf", "core", RestoredCurve);
            Effective = Enumerable.Repeat(240000L, 127).ToArray();
            Session = Session with { Trial = null };
            AfterRestore?.Invoke();
        }
        public GameSession Service() => new("card", Path, this, _ => { Writes++; Restore(); return new(true); },
            () => Session, e => Session = e, () => Daily,
            (kind, key) => kind == "vf" ? "0*127" : Cells.Single(c => c.Kind == kind && c.Key == key).Value,
            _ => true, _ => true);
        public BrokerResponse Begin() => Service().Call("begin", "run", new(Pci: Identity.Pci,
            SessionAt: Session.StartedAt, TrialAt: Session.Trial?.At));
        public BrokerResponse Finish(string route) => route == "recover"
            ? Service().Call("recover", "run", new()) : Service().Call("finish", "run", new(Mode: route));
        public void Dispose() => Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("recover", "0*127")]
    [InlineData("recover", "240000*127")]
    [InlineData("failed", "0*127")]
    [InlineData("failed", "240000*127")]
    [InlineData("reset", "0*127")]
    [InlineData("reset", "240000*127")]
    public void Daily_return_verifies_effective_curve_in_both_driver_models(string route, string curve)
    {
        using var r = new Rig { RestoredCurve = curve };
        Assert.True(r.Begin().Ok);
        if (route == "reset") r.Restore();
        var result = r.Finish(route);
        Assert.True(result.Ok, result.Error);
        Assert.False(File.Exists(r.Path));
        Assert.True(File.Exists(r.Path + ".completed"));
        Assert.Equal(route == "reset" ? 0 : 1, r.Writes);
        Assert.True(r.Finish("failed").Ok); // persisted receipt survives a new service instance
        r.Effective![126] -= 15000;
        Assert.False(r.Finish("failed").Ok); // raw controls alone cannot replay a stale success
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("point")]
    [InlineData("boundary")]
    [InlineData("memory")]
    [InlineData("core")]
    public void Unverified_or_partial_daily_retains_recovery_record(string fault)
    {
        using var r = new Rig();
        Assert.True(r.Begin().Ok);
        r.AfterRestore = () =>
        {
            switch (fault)
            {
                case "missing": r.Effective = null; break;
                case "empty": r.Effective = []; break;
                case "short": r.Effective = r.Effective![..126]; break;
                case "point": r.Effective![126] = 225000; break;
                case "boundary": r.Effective![126] = 239000; break;
                case "memory": r.Set("pstates20", "memory", "0"); break;
                case "core": r.Set("pstates20", "core", "0"); break;
            }
        };
        var result = r.Finish("recover");
        Assert.False(result.Ok);
        Assert.Contains("did not read back", result.Error);
        Assert.True(File.Exists(r.Path));
        Assert.False(File.Exists(r.Path + ".completed"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retry_accepts_generated_daily_curve_but_not_unrelated_shape(bool correct)
    {
        using var r = new Rig();
        Assert.True(r.Begin().Ok);
        // Simulate losing the reply after restoration, before the completion receipt was saved.
        r.Restore();
        if (!correct) r.Effective![126] = 225000;
        var result = r.Finish("recover");
        Assert.Equal(correct, result.Ok);
        Assert.Equal(correct ? 1 : 0, r.Writes);
        Assert.Equal(!correct, File.Exists(r.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_or_suspended_daily_still_requires_original_raw_curve(bool suspended)
    {
        using var r = new Rig();
        r.Daily = suspended ? r.Daily with { Suspended = true }
            : r.Daily with { Writes = [.. r.Daily.Writes, new("vf", "core", "0*127")] };
        Assert.True(r.Begin().Ok);
        Assert.False(r.Finish("recover").Ok); // correct effective offset does not waive raw requirements
        Assert.True(File.Exists(r.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Starting_on_daily_requires_independent_effective_proof(bool correct)
    {
        using var r = new Rig();
        r.Restore();
        if (!correct) r.Effective = null;
        Assert.Equal(correct, r.Begin().Ok);
        Assert.Equal(0, r.Writes);
        Assert.Equal(correct, File.Exists(r.Path));
    }

    [Fact]
    public void Older_recovery_records_without_effective_snapshots_can_return_to_daily()
    {
        using var r = new Rig();
        Assert.True(r.Begin().Ok);
        var state = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(r.Path))!;
        File.WriteAllText(r.Path, JsonSerializer.Serialize(state with
        {
            Baseline = state.Baseline with { EffectiveCoreOffsetsKhz = null },
            Expected = state.Expected with { EffectiveCoreOffsetsKhz = null },
        }));
        Assert.True(r.Finish("recover").Ok);
    }

    [Fact]
    public void Memory_only_daily_does_not_claim_an_implicit_core_curve()
    {
        using var r = new Rig();
        r.Daily = r.Daily with { Writes = r.Daily.Writes.Where(c => c.Key != "core").ToList() };
        Assert.True(r.Begin().Ok);
        Assert.False(r.Finish("recover").Ok);
        Assert.True(File.Exists(r.Path));
    }

    [Fact]
    public void Sub_MHz_rounding_matches_the_coupled_restoration_tolerance()
    {
        using var r = new Rig();
        Assert.True(r.Begin().Ok);
        r.AfterRestore = () => r.Effective![126] = 239500;
        Assert.True(r.Finish("recover").Ok);
    }
}
