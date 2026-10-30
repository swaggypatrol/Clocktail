using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The card against NVIDIA's own settings (0.4.0). A tester's MSI Afterburner undervolt, on before Clocktail's first write,
/// was that curve's "original": reported as stock, given back as stock, and a weak model took it for the default curve
/// and could not clear it (2026-10-24). These pin that another tool's settings are told apart from stock, that the
/// agent's stock ("factory") clears them while the uninstall's ("stock") still gives back only what Clocktail changed,
/// and that "adopt" keeps them as the daily profile.
/// </summary>
[Collection("fake-driver")]
public class FactoryStockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public FactoryStockTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Target(OwnershipJournal j, ApplySentinel s, GpuSession g) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => g;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    private static int MemoryAt => Pstates20Layout.Clock(0, 1);

    /// <summary>The V/F fake with a memory entry in P0 (±1000 / +3000 MHz) whose setter writes the offset and, as the
    /// driver does, resets every V/F point offset.</summary>
    private (BrokerDispatcher D, NvClient Hw) Card()
    {
        new FakeVf().Install();
        var table = FakeDriver.State[FakeVf.PstatesWord];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0xc), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(MemoryAt), (uint)ClockDomain.Memory);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(MemoryAt + 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(MemoryAt + 0x10), -1_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(MemoryAt + 0x14), 3_000_000);
        var vf = FakeDriver.RequestApplier!;
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) != FakeVf.PstatesWord) return vf(state, req);
            var next = (byte[])state.Clone();
            int entry = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(0x1c)) == (uint)ClockDomain.Memory ? MemoryAt : Pstates20Layout.Clock(0, 0);
            req.AsSpan(0x28, 4).CopyTo(next.AsSpan(entry + 0xc));
            var control = FakeDriver.State[FakeVf.ControlWord];
            for (int i = 0; i < 127; i++) control.AsSpan(VfLayout.OffsetField(i), 4).Clear();
            return next;
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetStatus, Slot.ClockClientClkVfPointsGetControl, Slot.GetPstates20],
            [Slot.ClockClientClkVfPointsSetControl, Slot.SetPstates20]), WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl));
        return (new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true), hw);
    }

    private static int MemoryKhz => BinaryPrimitives.ReadInt32LittleEndian(FakeDriver.State[FakeVf.PstatesWord].AsSpan(MemoryAt + 0xc));

    private static BrokerRequest Write(string kind, string key, string value) =>
        new(BrokerRequest.OpWrite, Kind: kind, Key: key, Value: value, EnableSlots: kind == VfWrites.Kind ? [nameof(Slot.ClockClientClkVfPointsSetControl)] : null);

    private static BrokerRequest Profile(string kind, string? value = null) => new(BrokerRequest.OpProfile, Kind: kind, Value: value);

    /// <summary>Another tool's settings: written through the broker, then forgotten by the journal, as if they had been
    /// on the card before Clocktail's first write. The memory offset goes first: a P0 write resets the curve.</summary>
    private void OtherTool(BrokerDispatcher d, string curve, int memoryKhz)
    {
        Assert.True(d.Dispatch(Write(ClockOffsets.Kind, "memory", memoryKhz.ToString(System.Globalization.CultureInfo.InvariantCulture))).Ok);
        Assert.True(d.Dispatch(Write(VfWrites.Kind, VfWrites.CoreKey, curve)).Ok);
        _journal.Clear();
    }

    private const string Undervolt = "0*60,90000*20,-150000*47";

    [Fact]
    public void Another_tools_curve_and_memory_offset_are_not_stock_and_are_named()
    {
        var (d, hw) = Card();
        OtherTool(d, Undervolt, 1_000_000);

        var cells = FactoryStock.Survey(hw, _gpu, (_, _) => null, (_, _) => null);
        var curve = Assert.Single(cells, c => c.Id == "vf/core");
        Assert.Equal("0*127", curve.Stock);
        Assert.Equal(Undervolt, curve.Now);
        Assert.True(curve.OtherTool);
        Assert.Equal("67 of 127 points offset, -150 to +90 MHz", curve.Describe(curve.Now));
        var memory = Assert.Single(cells, c => c.Id == "pstates20/memory");
        Assert.True(memory.OtherTool);
        Assert.Equal("+1000 MHz", memory.Describe(memory.Now));
        Assert.False(Assert.Single(cells, c => c.Id == "pstates20/core").OtherTool);   // still at stock
    }

    [Fact]
    public void A_setting_Clocktail_wrote_over_stock_or_keeps_daily_is_its_own()
    {
        var (d, hw) = Card();
        Assert.True(d.Dispatch(Write(VfWrites.Kind, VfWrites.CoreKey, "0*60,30000*67")).Ok);   // held, original stock
        var held = _journal.Inspect().Items;
        string? Original(string kind, string key) => held.FirstOrDefault(i => i.Kind == kind && i.Key == key)?.Original;
        Assert.False(FactoryStock.Survey(hw, _gpu, Original, (_, _) => null).Single(c => c.Id == "vf/core").OtherTool);

        // The daily profile's value on the card, not held (applied before a restart of the service): its own, too.
        Assert.False(FactoryStock.Survey(hw, _gpu, (_, _) => null, (k, _) => k == VfWrites.Kind ? "0*60,30000*67" : null).Single(c => c.Id == "vf/core").OtherTool);

        // A curve Clocktail wrote over another tool's owes that one back: still another tool's baseline.
        _journal.Clear();
        OtherTool(d, Undervolt, 0);
        Assert.True(d.Dispatch(Write(VfWrites.Kind, VfWrites.CoreKey, "0*60,30000*67")).Ok);
        var over = _journal.Inspect().Items;
        var cell = FactoryStock.Survey(hw, _gpu, (k, key) => over.FirstOrDefault(i => i.Kind == k && i.Key == key)?.Original, (_, _) => null).Single(c => c.Id == "vf/core");
        Assert.Equal(Undervolt, cell.Found);
        Assert.True(cell.OtherTool);
    }

    [Fact]
    public void Factory_clears_another_tools_settings_and_lets_them_go_while_stock_gives_back_only_its_own()
    {
        var (d, _) = Card();
        OtherTool(d, Undervolt, 1_000_000);

        // The uninstall's stock: Clocktail changed nothing, so nothing goes back and the other tool's settings stay.
        Assert.True(d.Dispatch(Profile("stock")).Ok);
        Assert.Equal(VfOffsets.Decode(Undervolt), FakeVf.Offsets());
        Assert.Equal(1_000_000, MemoryKhz);

        // A trial over them, then the agent's stock: the trial's give-back puts the other tool's curve back, and factory
        // then writes stock over both and keeps no record of them.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"drill"}""")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try",
            Value: JsonSerializer.Serialize(new { curve = "0*60,30000*67", change = "+30 MHz", at_uv = 950_000u, at_mhz = 2880u }))).Ok);
        var r = d.Dispatch(Profile("factory"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(new int[127], FakeVf.Offsets());
        Assert.Equal(0, MemoryKhz);
        Assert.Empty(_journal.Inspect().Items);
        var cleared = r.Result!.Value.GetProperty("cleared").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains(cleared, c => c!.StartsWith("V/F curve: 67 of 127 points offset", StringComparison.Ordinal));
        Assert.Contains(cleared, c => c!.StartsWith("memory offset: +1000 MHz", StringComparison.Ordinal));
        Assert.Null(Experiment.Load(Experiment.PathBeside(_journal.Path))!.Trial);   // the trial left the record

        // Nothing gives the other tool's values back later: a give-back now has nothing to give.
        Assert.True(d.Dispatch(Profile("apply")).Ok);
        Assert.Equal(new int[127], FakeVf.Offsets());
        Assert.Equal(0, MemoryKhz);
    }

    [Fact]
    public void Factory_suspends_the_daily_profile()
    {
        var (d, _) = Card();
        Assert.True(d.Dispatch(Write(VfWrites.Kind, VfWrites.CoreKey, "0*60,30000*67")).Ok);
        Assert.True(d.Dispatch(Profile("keep")).Ok);
        Assert.True(d.Dispatch(Profile("factory")).Ok);
        Assert.True(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!.Suspended);
        Assert.Equal(new int[127], FakeVf.Offsets());
        Assert.True(d.Dispatch(Profile("load")).Ok);
        Assert.Equal(VfOffsets.Decode("0*60,30000*67"), FakeVf.Offsets());
    }

    [Fact]
    public void Adopt_keeps_another_tools_curve_and_keep_alone_does_not()
    {
        var (d, _) = Card();
        OtherTool(d, Undervolt, 0);

        var keep = d.Dispatch(Profile("keep"));
        Assert.False(keep.Ok);
        Assert.Equal(BrokerCode.CannotWrite, keep.Code);

        var preview = d.Dispatch(Profile("preview-adopt"));
        Assert.True(preview.Ok, preview.Error);
        var change = Assert.Single(preview.Result!.Value.GetProperty("changes").EnumerateArray());
        Assert.Equal(Undervolt, change.GetProperty("becomes").GetString());

        Assert.True(d.Dispatch(Profile("adopt", "the person's Afterburner curve")).Ok);
        var kept = DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;
        Assert.Contains(kept.Writes, w => w.Kind == VfWrites.Kind && w.Value == Undervolt);

        // Kept, it goes on after a give-back like any daily profile: the curve no longer depends on the other tool.
        Assert.True(d.Dispatch(Write(VfWrites.Kind, VfWrites.CoreKey, "0*127")).Ok);
        Assert.True(d.Dispatch(Profile("apply")).Ok);
        Assert.Equal(VfOffsets.Decode(Undervolt), FakeVf.Offsets());
    }

    private DailyProfile Daily() => DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;

    private FactoryCell Curve(NvClient hw)
    {
        var held = _journal.Inspect().Items;
        var p = Daily();
        return FactoryStock.Survey(hw, _gpu, (k, key) => held.FirstOrDefault(i => i.Kind == k && i.Key == key)?.Original,
            (k, key) => p.Writes.FirstOrDefault(w => w.Kind == k && w.Key == key)?.Value,
            (k, key) => p.Adopted?.FirstOrDefault(w => w.Kind == k && w.Key == key)?.Value).Single(c => c.Id == "vf/core");
    }

    /// <summary>
    /// Adopted, another tool's curve is Clocktail's daily: a trial over it records it as its original, a memory trial sets
    /// it aside and writes it back (until the review it was refused: a curve the broker does not hold), and a keep after
    /// the trial neither drops it from the profile nor calls it another tool's (0.4.0's review).
    /// </summary>
    [Fact]
    public void An_adopted_curve_is_clocktails_through_trials_and_keeps()
    {
        var (d, hw) = Card();
        OtherTool(d, Undervolt, 0);
        Assert.True(d.Dispatch(Profile("adopt", "the person's Afterburner curve")).Ok);
        Assert.Equal(Undervolt, Assert.Single(Daily().Adopted!).Value);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"drill"}""")).Ok);

        var mem = d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try",
            Value: JsonSerializer.Serialize(new { curve = Undervolt, change = "memory +358", memory_khz = 358_000, at_uv = 950_000u, at_mhz = 2880u })));
        Assert.True(mem.Ok, mem.Error);
        Assert.Equal(358_000, MemoryKhz);
        Assert.Equal(VfOffsets.Decode(Undervolt), FakeVf.Offsets());
        Assert.False(Curve(hw).OtherTool);

        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try",
            Value: JsonSerializer.Serialize(new { curve = "0*60,105000*20,-150000*47", change = "+15 MHz", at_uv = 950_000u, at_mhz = 2880u }))).Ok);
        Assert.Equal(Undervolt, Curve(hw).Original);                      // the trial's original: the adopted curve
        Assert.False(Curve(hw).OtherTool);

        Assert.True(d.Dispatch(Profile("keep", "it held")).Ok);
        Assert.Contains(Daily().Writes, w => w.Kind == VfWrites.Kind && w.Value == "0*60,105000*20,-150000*47");
        Assert.Contains(Daily().Writes, w => w.Kind == ClockOffsets.Kind && w.Value == "358000");
        Assert.False(Curve(hw).OtherTool);                                // still held over the adopted one: Clocktail's
    }

    [Fact]
    public void A_keep_after_a_trial_elsewhere_keeps_the_adopted_curve()
    {
        var (d, _) = Card();
        OtherTool(d, Undervolt, 0);
        Assert.True(d.Dispatch(Profile("adopt")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"drill"}""")).Ok);
        // The curve comes back to its original bytes but remains a dependency while memory is held:
        // restoring memory can wipe the curve again.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try",
            Value: JsonSerializer.Serialize(new { curve = Undervolt, change = "memory +200", memory_khz = 200_000, at_uv = 950_000u, at_mhz = 2880u }))).Ok);
        Assert.Contains(_journal.Inspect().Items, i => i.Kind == VfWrites.Kind && i.Original == Undervolt);
        Assert.True(d.Dispatch(Profile("keep")).Ok);
        Assert.Contains(Daily().Writes, w => w.Kind == VfWrites.Kind && w.Value == Undervolt);   // on the card still, so still kept
    }

    /// <summary>A record from before a clean reboot is no debt: the start's apply used to put another tool's old value back
    /// over a card at stock, and then hold it as Clocktail's (0.4.0's review).</summary>
    [Fact]
    public void A_record_from_before_a_clean_reboot_is_not_put_back()
    {
        var (d, _) = Card();
        OtherTool(d, "0*127", 1_000_000);                                 // another tool's memory offset
        Assert.True(d.Dispatch(Write(ClockOffsets.Kind, "memory", "500000")).Ok);   // held, original +1000
        _host.BootId = "boot-B";                                         // a clean reboot: the card is at stock again
        BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.PstatesWord].AsSpan(MemoryAt + 0xc), 0);
        Assert.True(d.Dispatch(Profile("apply", "the broker started")).Ok);
        Assert.Equal(0, MemoryKhz);
        Assert.Empty(_journal.Inspect().Items);
    }

    /// <summary>The reference card's daily boost 100 % through the legacy byte read 100 % on the independent API too, and
    /// the first survey on the card called that another tool's (2026-10-24).</summary>
    [Fact]
    public void The_two_boost_apis_are_one_setting()
    {
        FactoryCell Cell(string key, string now, string? original = null, string? daily = null) =>
            new("boost", key, $"voltage boost ({key})", "0", now, original, daily);

        var ours = FactoryStock.Boost([Cell("independent", "100"), Cell("legacy", "100", original: "0")]);
        Assert.DoesNotContain(ours, c => c.OtherTool);
        var kept = FactoryStock.Boost([Cell("independent", "100"), Cell("legacy", "100", daily: "100")]);
        Assert.DoesNotContain(kept, c => c.OtherTool);

        var theirs = FactoryStock.Boost([Cell("independent", "100"), Cell("legacy", "100")]);
        Assert.Equal(["boost/independent"], theirs.Where(c => c.OtherTool).Select(c => c.Id));   // named once
        Assert.Equal(2, theirs.Count(c => c.Now != c.Stock));                                       // both still go to stock

        // Clocktail wrote over another tool's boost: its own record says so, whatever the other API shows.
        var over = FactoryStock.Boost([Cell("independent", "50"), Cell("legacy", "50", original: "100")]);
        Assert.Equal(["boost/legacy"], over.Where(c => c.OtherTool).Select(c => c.Id));
    }

    [Theory]
    [InlineData("pstates20", "memory", "-250000", "-250 MHz")]
    [InlineData("vdemand", "xbar", "20000", "+20 mV")]
    [InlineData("boost", "independent", "100", "100 %")]
    [InlineData("power", "fe", "450000", "450 W")]
    [InlineData("fan", "1", "manual:60", "held at a fixed duty (manual:60)")]
    [InlineData("clocklimit", "voltage", "1500000", "Boost Lock")]
    [InlineData("clocklimit", "max", "2600000", "at most 2600 MHz")]
    [InlineData("vf", "core", "0*127", "stock")]
    [InlineData("vf", "core", "15000*127", "127 of 127 points offset, +15 MHz")]
    public void Settings_are_described_in_words(string kind, string key, string value, string words) =>
        Assert.Equal(words, FactoryStock.Describe(kind, key, value));
}
