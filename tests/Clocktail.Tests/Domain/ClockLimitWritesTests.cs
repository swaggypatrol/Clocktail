using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Profiles;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>
/// The clock limits through slot 27 as the original writes them: one entry per request (+8 = 1), the readback the
/// record before with that entry changed and nothing else, a one-entry restore.
/// </summary>
[Collection("fake-driver")]
public class ClockLimitWritesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private static readonly GpuSession Gpu = new(1, "fake");
    private static readonly uint Word = new Versioned(ClientLimitsLayout.Size, ClientLimitsLayout.Rev).Word;

    public ClockLimitWritesTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static int At(int entry) => ClientLimitsLayout.First + entry * ClientLimitsLayout.Stride;

    /// <summary>The record as the 5090 reads it with no lock in force, and a driver that lands one-entry requests.</summary>
    private static NvClient Install(Action<byte[], int, uint, uint>? alsoChange = null)
    {
        FakeDriver.Reset();
        var b = new byte[ClientLimitsLayout.Size];
        W(b, 0, Word); W(b, 8, 7);
        for (int k = 0; k < 7; k++) W(b, At(k), (uint)k);
        FakeDriver.State[Word] = b;
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (U(req, 0) != Word) return req;
            var next = (byte[])state.Clone();
            int k = (int)U(req, 0xc);
            W(next, At(k) + 8, U(req, 0x14)); W(next, At(k) + 0x10, U(req, 0x1c));
            alsoChange?.Invoke(next, k, U(req, 0x14), U(req, 0x1c));
            return next;
        };
        return new NvClient(FakeDriver.ApiFor([Slot.PerfClientLimitsGetStatus], [Slot.PerfClientLimitsSetStatus]),
            WritePolicySet.Default.WithEnabled(Slot.PerfClientLimitsSetStatus));
    }

    private OwnedWriteResult Write(NvClient hw, string key, string value)
    {
        var (cell, plan, cannot) = ClockLimitWrites.Prepare(key, value);
        Assert.Null(cannot);
        return new OwnedWriter(hw, _journal, _sentinel, _host).Apply(Gpu, cell, plan!);
    }

    [Fact]
    public void Boost_lock_goes_on_as_one_entry_and_off_again_releases_it()
    {
        var hw = Install();
        var on = Write(hw, ClockLimitWrites.VoltageKey, "1500000");
        Assert.Equal(TxOutcome.Applied, on.Tx.Outcome);
        var state = FakeDriver.State[Word];
        Assert.Equal((3u, 1_500_000u), (U(state, At(6) + 8), U(state, At(6) + 0x10)));
        Assert.Contains("Boost Lock", Assert.Single(ClientLimitsLayout.Parse(state)).Describe());
        Assert.Equal("0", Assert.Single(_journal.Load()!.Items).Original);

        var off = Write(hw, ClockLimitWrites.VoltageKey, "off");
        Assert.Equal(TxOutcome.Applied, off.Tx.Outcome);
        Assert.Empty(ClientLimitsLayout.Parse(FakeDriver.State[Word]));
        Assert.StartsWith("released", off.Ownership);
    }

    [Fact]
    public void A_clock_range_is_two_entries_and_a_minimum_above_the_maximum_is_refused()
    {
        var hw = Install();
        Assert.Equal(TxOutcome.Applied, Write(hw, ClockLimitWrites.MaxKey, "2400000").Tx.Outcome);
        Assert.Equal(TxOutcome.Applied, Write(hw, ClockLimitWrites.MinKey, "1200000").Tx.Outcome);
        Assert.Equal(["maximum clock 2400 MHz", "minimum clock 1200 MHz"], ClientLimitsLayout.Parse(FakeDriver.State[Word]).Select(l => l.Describe()));
        var bad = Write(hw, ClockLimitWrites.MinKey, "2500000");
        Assert.Equal(TxOutcome.Aborted, bad.Tx.Outcome);
        Assert.Contains("above the maximum in force", bad.Tx.Detail);
        Assert.Equal(TxOutcome.Aborted, Write(hw, ClockLimitWrites.VoltageKey, "400000").Tx.Outcome);   // under 450 mV
    }

    [Fact]
    public void A_driver_that_moves_another_entry_is_restored_and_nothing_stays_held()
    {
        var hw = Install((next, k, mode, value) => { if (k == 0 && value != 0) W(next, At(1) + 8, 2); });
        var r = Write(hw, ClockLimitWrites.MaxKey, "2400000");
        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Equal(0u, U(FakeDriver.State[Word], At(0) + 0x10));
    }

    private sealed class Target(OwnershipJournal j, ApplySentinel s) : Clocktail.Domain.Broker.IGpuTarget, Clocktail.Domain.Broker.IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => Gpu;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public Clocktail.Domain.Broker.IGpuTarget? Resolve(string? id) => this;
    }

    /// <summary>set_clock_lock's trial: the setter is off by default, and the trial's own write enables it for itself;
    /// back_to_daily releases the lock.</summary>
    [Fact]
    public void A_trial_lock_enables_its_setter_for_itself_and_goes_back_with_the_daily_profile()
    {
        Install();
        var hw = new NvClient(FakeDriver.ApiFor([Slot.PerfClientLimitsGetStatus], [Slot.PerfClientLimitsSetStatus]));   // the default policy: off
        var d = new Clocktail.Domain.Broker.BrokerDispatcher(hw, new Target(_journal, _sentinel), _host, canWrite: true);
        Assert.True(d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"drill"}""")).Ok);
        var t = d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpExperiment, Kind: "try",
            Value: System.Text.Json.JsonSerializer.Serialize(new { curve = "0*127", change = "Boost Lock", keep_curve = true, writes = new[] { new { kind = "clocklimit", key = "voltage", value = "1500000" } } })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(1_500_000u, U(FakeDriver.State[Word], At(6) + 0x10));
        var back = d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(back.Ok, back.Error);
        Assert.Empty(ClientLimitsLayout.Parse(FakeDriver.State[Word]));
        Assert.Empty(_journal.Inspect().Items);
    }

    private Clocktail.Domain.Broker.BrokerDispatcher Dispatcher()
    {
        var hw = new NvClient(FakeDriver.ApiFor([Slot.PerfClientLimitsGetStatus], [Slot.PerfClientLimitsSetStatus]));
        return new Clocktail.Domain.Broker.BrokerDispatcher(hw, new Target(_journal, _sentinel), _host, canWrite: true);
    }

    /// <summary>keep_as_daily keeps a cap and releases a floor: a floor, a point lock and Boost Lock would hold the card high
    /// at idle after every boot, so they are trial-only (mVolt+ 0.50.6 never keeps one either).</summary>
    [Fact]
    public void Keep_takes_the_cap_and_releases_the_floor()
    {
        Install();
        var d = Dispatcher();
        Assert.True(d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"drill"}""")).Ok);
        var t = d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpExperiment, Kind: "try",
            Value: System.Text.Json.JsonSerializer.Serialize(new { curve = "0*127", change = "-lgc 1200,2400", keep_curve = true, writes = new[] {
                new { kind = "clocklimit", key = "max", value = "2400000" }, new { kind = "clocklimit", key = "min", value = "1200000" } } })));
        Assert.True(t.Ok, t.Error);
        Assert.Equal(2, ClientLimitsLayout.Parse(FakeDriver.State[Word]).Count);
        var kept = d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpProfile, Kind: "keep"));
        Assert.True(kept.Ok, kept.Error);
        var profile = DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!;
        Assert.Equal("clocklimit/max=2400000", Assert.Single(profile.Writes, w => w.Kind == ClockLimitWrites.Kind) is var w ? $"{w.Kind}/{w.Key}={w.Value}" : null);
        var held = Assert.Single(ClientLimitsLayout.Parse(FakeDriver.State[Word]));
        Assert.Equal((0, 2_400_000u), (held.Domain, held.Value));   // the cap stays on, the floor went back
    }

    /// <summary>A profile kept by 0.2.5 with Boost Lock in it: the lock no longer goes on with the profile, and one the
    /// journal holds is released; its cap still goes on.</summary>
    [Fact]
    public void A_lock_an_older_profile_kept_stays_off()
    {
        Install();
        new DailyProfile([new(ClockLimitWrites.Kind, ClockLimitWrites.MaxKey, "2400000"), new(ClockLimitWrites.Kind, ClockLimitWrites.VoltageKey, "1500000")],
            _host.Now).Save(DailyProfile.PathBeside(_journal.Path));
        var d = Dispatcher();
        var r = d.Dispatch(new Clocktail.Domain.Broker.BrokerRequest(Clocktail.Domain.Broker.BrokerRequest.OpProfile, Kind: "apply"));
        Assert.True(r.Ok, r.Error);
        var held = Assert.Single(ClientLimitsLayout.Parse(FakeDriver.State[Word]));
        Assert.Equal((0, 2_400_000u), (held.Domain, held.Value));
        Assert.True(DailyProfile.TrialOnly(ClockLimitWrites.Kind, ClockLimitWrites.VoltageKey));
        Assert.True(DailyProfile.TrialOnly(ClockLimitWrites.Kind, ClockLimitWrites.MinKey));
        Assert.False(DailyProfile.TrialOnly(ClockLimitWrites.Kind, ClockLimitWrites.MaxKey));
        Assert.True(DailyProfile.KeptFromCard(ClockLimitWrites.Kind, ClockLimitWrites.MaxKey));
        Assert.False(DailyProfile.KeptFromCard(ClockLimitWrites.Kind, ClockLimitWrites.VoltageKey));
    }

    [Fact]
    public void The_rule_takes_one_entry_and_refuses_the_whole_table()
    {
        var p = WritePolicySet.Default.Get(Slot.PerfClientLimitsSetStatus)!;
        Assert.True(p.Complete);
        Assert.Equal(PolicyState.Disabled, p.State);   // enabled by a clock-limit write for itself
        var enabled = WritePolicySet.Default.WithEnabled(Slot.PerfClientLimitsSetStatus);
        Assert.Null(enabled.Check(Slot.PerfClientLimitsSetStatus, ClockLimitWrites.Request(6, 3, 1_500_000)));
        Assert.Null(enabled.Check(Slot.PerfClientLimitsSetStatus, ClockLimitWrites.Request(0, 2, 0)));   // release
        var table = ClockLimitWrites.Request(0, 2, 2_400_000);
        W(table, 8, 7);
        Assert.NotNull(enabled.Check(Slot.PerfClientLimitsSetStatus, table));
        Assert.NotNull(enabled.Check(Slot.PerfClientLimitsSetStatus, ClockLimitWrites.Request(3, 2, 2_400_000)));   // not a limit we write
        Assert.NotNull(enabled.Check(Slot.PerfClientLimitsSetStatus, ClockLimitWrites.Request(6, 2, 950_000)));      // wrong mode
    }
}
