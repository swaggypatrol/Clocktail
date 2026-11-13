using System.Buffers.Binary;
using System.Text.Json;
using Clocktail.Broker;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Power;
using Clocktail.Domain.Profiles;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>Regression tests for Audit Items 2, 3, and 4 using the fake driver only.</summary>
[Collection("fake-driver")]
public class AuditProfileFixesTests : IDisposable
{
    private const int Board = 0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");
    private readonly Dictionary<uint, uint> _requests = new() { [0xfe] = 575_000, [0xf8] = 0xFFFFFFFF };

    public AuditProfileFixesTests()
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

    private static void W(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    private static byte[] PowerInfo()
    {
        var b = NvClient.NewBuffer(Slot.ClientPwrPoliciesGetInfo);
        W(b, 4, 1u << Board);
        int e = PowerRequestsLayout.InfoEntry(Board);
        W(b, e, 1); W(b, e + 8, 400_000); W(b, e + 0xc, 575_000); W(b, e + 0x10, 600_000);
        return b;
    }

    private BrokerDispatcher PowerAndOcp()
    {
        FakeDriver.Reset();
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = PowerInfo();
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        uint controlWord = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) != controlWord) return null;
            var a = (byte[])q.Clone();
            uint cmd = U(q, PowerRequestsLayout.ControlEntry(Board) + 4);
            W(a, PowerRequestsLayout.ControlEntry(Board), _requests.GetValueOrDefault(cmd, 0xFFFFFFFF));
            return a;
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            int e = PowerRequestsLayout.ControlEntry(Board);
            _requests[U(req, e + 4)] = U(req, e);
            return state;
        };
        CurrentLimitsFixture.Install();
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo],
            [Slot.ClientPwrPoliciesSetControl, Slot.PowerPolicySetControl]));
        return new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true);
    }

    private (BrokerDispatcher D, NvClient Hw) VfAndPower()
    {
        new FakeVf().Install();
        var vf = FakeDriver.RequestApplier!;
        FakeDriver.State[Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape!.Value.Word] = PowerInfo();
        FakeDriver.Seed(Slot.ClientPwrPoliciesGetControl);
        uint controlWord = Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape!.Value.Word;
        var prevQuery = FakeDriver.QueryResponder;
        FakeDriver.QueryResponder = q =>
        {
            if (BitConverter.ToUInt32(q) == controlWord)
            {
                var a = (byte[])q.Clone();
                uint cmd = U(q, PowerRequestsLayout.ControlEntry(Board) + 4);
                W(a, PowerRequestsLayout.ControlEntry(Board), _requests.GetValueOrDefault(cmd, 0xFFFFFFFF));
                return a;
            }
            return prevQuery?.Invoke(q);
        };
        FakeDriver.RequestApplier = (state, req) =>
        {
            if (BitConverter.ToUInt32(req) == controlWord)
            {
                int e = PowerRequestsLayout.ControlEntry(Board);
                _requests[U(req, e + 4)] = U(req, e);
                return state;
            }
            return vf(state, req);
        };
        var hw = new NvClient(FakeDriver.ApiFor(
            [Slot.ClockClientClkVfPointsGetInfo, Slot.ClockClientClkVfPointsGetControl, Slot.ClockClientClkVfPointsGetStatus,
             Slot.GetPstates20,
             Slot.ClientPwrPoliciesGetInfo, Slot.ClientPwrPoliciesGetControl, Slot.PowerPolicyGetInfo, Slot.PowerPolicyGetControl, Slot.PowerMonitorGetInfo],
            [Slot.ClockClientClkVfPointsSetControl, Slot.SetPstates20, Slot.ClientPwrPoliciesSetControl, Slot.PowerPolicySetControl]),
            WritePolicySet.Default.WithEnabled(Slot.ClockClientClkVfPointsSetControl));
        return (new BrokerDispatcher(hw, new Target(_journal, _sentinel, _gpu), _host, canWrite: true), hw);
    }

    private static BrokerRequest Exp(string kind, string? value = null) => new(BrokerRequest.OpExperiment, Kind: kind, Value: value);
    private ExperimentTrial? TrialNow() => Experiment.Load(Experiment.PathBeside(_journal.Path))?.Trial;

    private static string PreviewFingerprint(BrokerDispatcher d, string kind, string? value = null)
    {
        var p = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: kind, Value: value));
        Assert.True(p.Ok, p.Error);
        return p.Result!.Value.GetProperty("values_fingerprint").GetString()!;
    }

    // -------------------------------------------------------------------------
    // Item 4: Canonicalize kind and key before rules, trial records, and writes
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("power", "FE", "fe")]
    [InlineData("POWER", "F8", "f8")]
    [InlineData("pstates20", "CORE", "core")]
    [InlineData("pstates20", "Memory", "memory")]
    [InlineData("rails", "NVVDD", "nvvdd")]
    [InlineData("rails", "MSVDD", "msvdd")]
    [InlineData("ocp", "core", "nvvdd")]
    [InlineData("ocp", "MEMORY", "msvdd")]
    [InlineData("clocklimit", "MAX", "max")]
    [InlineData("clocklimit", "Min", "min")]
    [InlineData("clocklimit", "VOLTAGE", "voltage")]
    [InlineData("clockratio", "MSVDD", "msvdd")]
    [InlineData("fan", "00", "0")]
    [InlineData("fancurve", "00", "0")]
    [InlineData("fanstop", "05", "5")]
    [InlineData("vf", "core", "core")]
    [InlineData("clockdomain", "xbar", "xbar")]
    [InlineData("vdemand", "graphics", "graphics")]
    [InlineData("boost", "independent", "independent")]
    public void WriteKeys_canonicalizes_valid_keys_per_path_semantics(string kind, string key, string expected) =>
        Assert.Equal(expected, WriteKeys.Canonicalize(kind, key));

    [Theory]
    [InlineData("pstates20", "0")]
    [InlineData("pstates20", "4")]
    [InlineData("rails", "0")]
    [InlineData("rails", "1")]
    [InlineData("power", "ordinary")]
    [InlineData("power", "0xfe")]
    [InlineData("ocp", "gpu")]
    [InlineData("clockdomain", "XBAR")]
    [InlineData("vdemand", "CORE")]
    [InlineData("boost", "auto")]
    [InlineData("fancurve", "32")]
    [InlineData("fanstop", "-1")]
    [InlineData("vf", "memory")]
    [InlineData("unknown", "core")]
    public void WriteKeys_rejects_invalid_and_numeric_enum_keys(string kind, string key) =>
        Assert.Throws<ArgumentException>(() => WriteKeys.Canonicalize(kind, key));

    /// <summary>Inverted Item 4 repro: "POWER" and key aliases ("FE", "core", "MAX") cannot bypass the board-wall
    /// rule, the curve rule, or trial-only/create matching in "try".</summary>
    [Theory]
    [InlineData("POWER", "fe")]
    [InlineData("power", "FE")]
    [InlineData("Power", "Fe")]
    public void Wall_through_writes_with_noncanonical_kind_or_key_is_rejected_and_writes_nothing(string kind, string key)
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"audit"}""")).Ok);

        var r = d.Dispatch(Exp("try", JsonSerializer.Serialize(new
        {
            curve = "0*127",
            change = "wall through writes",
            keep_curve = true,
            writes = new[] { new { kind, key, value = "450000" } },
        })));

        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.BadRequest, r.Code);
        Assert.Contains("the board wall goes in 'power_mw'", r.Error);
        Assert.Equal(575_000u, _requests[0xfe]);
        Assert.Null(TrialNow());
    }

    [Fact]
    public void Vf_in_writes_with_uppercase_kind_is_rejected()
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"audit"}""")).Ok);

        var r = d.Dispatch(Exp("try", JsonSerializer.Serialize(new
        {
            curve = "0*127",
            change = "vf via writes",
            keep_curve = true,
            writes = new[] { new { kind = "VF", key = "core", value = "0*127" } },
        })));

        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.BadRequest, r.Code);
        Assert.Contains("the curve goes in 'curve'", r.Error);
        Assert.Null(TrialNow());
    }

    [Fact]
    public void Noncanonical_ocp_and_power_aliases_canonicalize_before_trial_record_creates_and_keep()
    {
        var (d, _) = VfAndPower();
        CurrentLimitsFixture.Install();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"audit"}""")).Ok);

        // ocp/core -> ocp/nvvdd (trial-only), pstates20/MEMORY -> pstates20/memory (kept), POWER/F8 + create -> power/f8 with AllowCreate
        var r = d.Dispatch(Exp("try", JsonSerializer.Serialize(new
        {
            curve = "0*127",
            change = "canonicalize writes",
            keep_curve = false,
            writes = new object[]
            {
                new { kind = "POWER", key = "F8", value = "500000", create = true },
                new { kind = "OCP", key = "CORE", value = "350000" },
            },
        })));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(500_000u, _requests[0xf8]);

        var trial = TrialNow()!;
        Assert.NotNull(trial.Writes);
        Assert.Contains(trial.Writes!, w => w.Kind == "power" && w.Key == "f8" && w.Value == "500000");
        Assert.Contains(trial.Writes!, w => w.Kind == "ocp" && w.Key == "nvvdd" && w.Value == "350000");

        // Keep must exclude trial-only power/f8 and ocp/nvvdd even though they were requested via aliases, keeping none of them.
        var kept = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep"));
        Assert.False(kept.Ok);
        Assert.Contains("nothing this broker holds differs from stock", kept.Error);
    }

    [Fact]
    public void Invalid_key_in_later_try_write_is_rejected_before_earlier_writes_run()
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(Exp("begin", """{"game":"audit"}""")).Ok);

        var r = d.Dispatch(Exp("try", JsonSerializer.Serialize(new
        {
            curve = "0*127",
            change = "bad key second",
            keep_curve = true,
            writes = new object[]
            {
                new { kind = "power", key = "f8", value = "500000", create = true },
                new { kind = "pstates20", key = "0", value = "15000" },
            },
        })));

        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.BadRequest, r.Code);
        Assert.Equal(0xFFFFFFFFu, _requests[0xf8]);
        Assert.Null(TrialNow());
    }

    [Fact]
    public void OpWrite_canonicalizes_key_for_AdoptedOriginal_and_rejects_numeric_enum_keys()
    {
        var d = PowerAndOcp();
        var numeric = d.Dispatch(new BrokerRequest(BrokerRequest.OpWrite, "gpu-A", Kind: "pstates20", Key: "0", Value: "15000"));
        Assert.False(numeric.Ok);
        Assert.Equal(BrokerCode.BadRequest, numeric.Code);

        var upper = d.Dispatch(new BrokerRequest(BrokerRequest.OpWrite, "gpu-A", Kind: "OCP", Key: "CORE", Value: "350000",
            EnableSlots: [nameof(Slot.PowerPolicySetControl)]));
        Assert.True(upper.Ok, upper.Error);
        Assert.Equal("nvvdd", Assert.Single(_journal.Inspect().Items).Key);
    }

    // -------------------------------------------------------------------------
    // Item 3: Preserve Adopted across wall, save, and preset (LoadSaved)
    // -------------------------------------------------------------------------

    /// <summary>Inverted Item 3 repro: setting the daily power limit ("wall") must preserve Adopted.</summary>
    [Fact]
    public void Setting_the_daily_wall_preserves_the_profiles_adopted_list()
    {
        var d = PowerAndOcp();
        string path = DailyProfile.PathBeside(_journal.Path);
        var adopted = new[] { new ProfileWrite("vf", "core", "0*127") };
        new DailyProfile([new ProfileWrite("power", "fe", "560000")], _host.Now, Adopted: adopted).Save(path);

        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "550000"));
        Assert.True(r.Ok, r.Error);

        var after = DailyProfile.Load(path)!;
        Assert.NotNull(after.Adopted);
        Assert.Equal(adopted, after.Adopted);
    }

    [Fact]
    public void Save_and_preset_preserve_relevant_adopted_provenance()
    {
        var (d, hw) = VfAndPower();
        d.AuthorizeProfileChange = _ => true;
        const string afterburnerCurve = "0*60,90000*20,-150000*47";
        const string tunedCurve = "0*60,105000*20,-150000*47";
        string dailyPath = DailyProfile.PathBeside(_journal.Path);

        // Simulate another tool's curve on the card before Clocktail's journal saw it.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpWrite, Kind: VfWrites.Kind, Key: VfWrites.CoreKey, Value: afterburnerCurve,
            EnableSlots: [nameof(Slot.ClockClientClkVfPointsSetControl)])).Ok);
        _journal.Clear();

        // Adopt it via FromClient with valid fingerprint.
        var fpAdopt = PreviewFingerprint(d, "preview-adopt");
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "adopt", ExpectedProfileFingerprint: fpAdopt)).Ok);
        var adoptedDaily = DailyProfile.Load(dailyPath)!;
        Assert.Equal([new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, afterburnerCurve)], adoptedDaily.Adopted);

        // Save profile -> SavedProfile keeps relevant Adopted (vf/core = afterburnerCurve), omits unrelated entries.
        var wallAndCurve = new DailyProfile(
            [new ProfileWrite("power", "fe", "550000"), new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, tunedCurve)],
            _host.Now,
            Adopted: [new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, afterburnerCurve), new ProfileWrite("clockdomain", "xbar", "15000")]);
        wallAndCurve.Save(dailyPath);

        var save = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "tuned"));
        Assert.True(save.Ok, save.Error);
        var saved = SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "tuned")!.Value.Profile;
        Assert.Equal([new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, afterburnerCurve)], saved.Adopted);

        // Switch to another profile cleanly, then load preset "tuned": Adopted returns for vf/core and FactoryStock sees no OtherTool.
        new DailyProfile([new ProfileWrite("power", "fe", "575000")], _host.Now).Save(dailyPath);
        var fpPreset = PreviewFingerprint(d, "preview", "tuned");
        var preset = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "tuned", ExpectedProfileFingerprint: fpPreset));
        Assert.True(preset.Ok, preset.Error);

        var loaded = DailyProfile.Load(dailyPath)!;
        Assert.Equal([new ProfileWrite(VfWrites.Kind, VfWrites.CoreKey, afterburnerCurve)], loaded.Adopted);
        var survey = FactoryStock.Survey(hw, _gpu,
            (kind, key) => _journal.Inspect().Items.FirstOrDefault(i => i.Kind == kind && i.Key == key)?.Original,
            (kind, key) => loaded.Writes.FirstOrDefault(w => w.Kind == kind && w.Key == key)?.Value,
            (kind, key) => loaded.Adopted?.FirstOrDefault(w => w.Kind == kind && w.Key == key)?.Value);
        Assert.False(Assert.Single(survey, c => c.Id == "vf/core").OtherTool);
    }

    // -------------------------------------------------------------------------
    // Item 2: Elevated human consent — FromClient fingerprint + broker hook
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("keep", null)]
    [InlineData("keep", "")]
    [InlineData("keep", "   ")]
    [InlineData("adopt", null)]
    [InlineData("adopt", "")]
    [InlineData("preset", null)]
    [InlineData("preset", "")]
    public void FromClient_keep_adopt_preset_require_expected_profile_fingerprint(string kind, string? fp)
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 550_000)).Ok);
        bool prompted = false;
        d.AuthorizeProfileChange = _ => { prompted = true; return true; };

        var r = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: kind, Value: kind == "preset" ? "any" : null, ExpectedProfileFingerprint: fp));
        Assert.False(r.Ok);
        Assert.Equal(BrokerCode.BadRequest, r.Code);
        Assert.False(prompted);
        Assert.False(File.Exists(DailyProfile.PathBeside(_journal.Path)));
    }

    [Fact]
    public void FromClient_keep_and_preset_enforce_matching_fingerprint_and_broker_consent_hook()
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        var fp = PreviewFingerprint(d, "preview");

        // Mismatched fingerprint fails with CannotWrite before prompting.
        bool prompted = false;
        d.AuthorizeProfileChange = _ => { prompted = true; return true; };
        var wrongFp = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", ExpectedProfileFingerprint: new string('0', 64)));
        Assert.False(wrongFp.Ok);
        Assert.Equal(BrokerCode.CannotWrite, wrongFp.Code);
        Assert.False(prompted);

        // Declining at broker leaves no daily profile saved.
        ProfileConsentRequest? seen = null;
        d.AuthorizeProfileChange = req => { seen = req; return false; };
        var declined = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", ExpectedProfileFingerprint: fp));
        Assert.False(declined.Ok);
        Assert.Equal(BrokerCode.CannotWrite, declined.Code);
        Assert.NotNull(seen);
        Assert.Equal("keep", seen!.Kind);
        Assert.False(File.Exists(DailyProfile.PathBeside(_journal.Path)));

        // Approving saves daily profile.
        d.AuthorizeProfileChange = req => { seen = req; return true; };
        var approved = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", ExpectedProfileFingerprint: fp));
        Assert.True(approved.Ok, approved.Error);
        Assert.True(File.Exists(DailyProfile.PathBeside(_journal.Path)));

        // Save it as "daily560", then test stock / wall / factory / preset with allow & deny.
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "daily560")).Ok);

        d.AuthorizeProfileChange = _ => false;
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "540000")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        Assert.False(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!.Suspended);
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "factory")).Ok);
        Assert.False(DailyProfile.Load(DailyProfile.PathBeside(_journal.Path))!.Suspended);

        var presetFp = PreviewFingerprint(d, "preview", "daily560");
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "daily560", ExpectedProfileFingerprint: presetFp)).Ok);

        d.AuthorizeProfileChange = _ => true;
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "540000")).Ok);
        Assert.Equal(540_000u, _requests[0xfe]);
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "daily560", ExpectedProfileFingerprint: presetFp)).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        Assert.Equal(575_000u, _requests[0xfe]);
    }

    [Fact]
    public void Internal_Dispatch_recovery_and_startup_never_prompt_broker_consent_hook()
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);

        bool prompted = false;
        d.AuthorizeProfileChange = _ => { prompted = true; return false; };

        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply", Value: "startup")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "resume", Value: "wake")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover, Kind: BrokerRequest.RecoverAfterDriverReset, Value: "reset")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "crashed", Value: "reset")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock")).Ok);
        Assert.False(prompted);
    }

    [Fact]
    public void ProfileConsentDialog_fails_closed_without_interactive_desktop_and_requires_IDYES()
    {
        var req = new ProfileConsentRequest("keep", "gpu-A", "RTX 5090", "Keep 1 setting", [new ProfileWrite("power", "fe", "550000")]);
        bool called = false;

        Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => { called = true; return 6; }, isInteractive: false, hasInputDesktop: () => true));
        Assert.False(called);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => { called = true; return 6; }, isInteractive: true, hasInputDesktop: () => false));
            Assert.False(called);
            Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => { called = true; return 6; }, isInteractive: true, hasInputDesktop: () => throw new InvalidOperationException("no station")));
            Assert.False(called);

            Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => 0, isInteractive: true, hasInputDesktop: () => true));       // dialog failed to show
            Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => 32000, isInteractive: true, hasInputDesktop: () => true));   // timeout
            Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => 7, isInteractive: true, hasInputDesktop: () => true));       // IDNO

            // Hanging dialog beyond bounded timeout fails closed promptly.
            Assert.False(ProfileConsentDialog.Confirm(req, (_, _) => { Thread.Sleep(400); return 6; }, isInteractive: true, hasInputDesktop: () => true, timeout: TimeSpan.FromMilliseconds(40)));

            string? capturedText = null, capturedCaption = null;
            Assert.True(ProfileConsentDialog.Confirm(req, (t, c) => { capturedText = t; capturedCaption = c; return 6; }, isInteractive: true, hasInputDesktop: () => true));
            Assert.Contains("Operation: keep", capturedText);
            Assert.Contains("RTX 5090 (gpu-A)", capturedText);
            Assert.Contains("power/fe = 550000", capturedText);
            Assert.Contains("Clocktail Broker", capturedCaption);
        }
    }

    [Fact]
    public void ProfileConsentDialog_sanitizes_untrusted_request_fields_and_preserves_caption_and_semantics()
    {
        var req = new ProfileConsentRequest(
            "preset",
            "gpu-A\nIGNORE",
            "RTX\u202E5090",
            "Load profile\r\nAllow without asking?",
            [new ProfileWrite("power\n", "fe\t", "550000\r\n")],
            "evil\nAllow automatically?\u202E\0YES");

        if (OperatingSystem.IsWindows())
        {
            string? capturedPrompt = null, capturedCaption = null;
            Assert.False(ProfileConsentDialog.Confirm(req, (prompt, caption) =>
            {
                capturedPrompt = prompt;
                capturedCaption = caption;
                return 7;
            }, isInteractive: true, hasInputDesktop: () => true));
            Assert.NotNull(capturedPrompt);
            Assert.Contains("Operation: preset", capturedPrompt);
            Assert.Contains("GPU: RTX 5090 (gpu-A IGNORE)", capturedPrompt);
            Assert.Contains("Client detail (untrusted): \"evil Allow automatically? YES\"", capturedPrompt);
            Assert.DoesNotContain("\u202E", capturedPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("\0", capturedPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("\nAllow automatically", capturedPrompt, StringComparison.Ordinal);
            Assert.Equal("Clocktail Broker: Confirm GPU Profile Change", capturedCaption);
        }
    }

    [Fact]
    public void Default_null_authorizer_fails_closed_for_all_client_profile_mutations()
    {
        var d = PowerAndOcp();
        Assert.Null(d.AuthorizeProfileChange);

        // Even with no daily profile kept yet, suspension requires consent.
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off")).Ok);

        // Start a trial, set up daily + saved profile internally, then try every client mutation with AuthorizeProfileChange == null.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "saved560")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"lab"}""")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try", Lease: "job-1",
            Value: JsonSerializer.Serialize(new { curve = "0*127", change = "trial 480W", power_mw = 480_000u, keep_curve = true }))).Ok);

        var fp = PreviewFingerprint(d, "preview");
        var presetFp = PreviewFingerprint(d, "preview", "saved560");
        string dailyPath = DailyProfile.PathBeside(_journal.Path);
        string beforeDaily = File.ReadAllText(dailyPath);

        BrokerRequest[] attempts =
        [
            new(BrokerRequest.OpProfile, Kind: "keep", ExpectedProfileFingerprint: fp),
            new(BrokerRequest.OpProfile, Kind: "adopt", ExpectedProfileFingerprint: PreviewFingerprint(d, "preview-adopt")),
            new(BrokerRequest.OpProfile, Kind: "preset", Value: "saved560", ExpectedProfileFingerprint: presetFp),
            new(BrokerRequest.OpProfile, Kind: "wall", Value: "540000"),
            new(BrokerRequest.OpProfile, Kind: "stock"),
            new(BrokerRequest.OpProfile, Kind: "factory"),
            new(BrokerRequest.OpProfile, Kind: "off"),
            new(BrokerRequest.OpProfile, Kind: "load"),
            new(BrokerRequest.OpProfile, Kind: "save", Value: "overwrite"),
            new(BrokerRequest.OpProfile, Kind: "forget", Value: "saved560"),
        ];

        foreach (var req in attempts)
        {
            var res = d.FromClient(req);
            Assert.False(res.Ok, $"Expected {req.Kind} to fail closed when AuthorizeProfileChange is null, got Ok.");
        }

        // Authorizer throwing an exception must also fail closed without mutating anything.
        d.AuthorizeProfileChange = _ => throw new InvalidOperationException("dialog crash");
        foreach (var req in attempts)
        {
            var res = d.FromClient(req);
            Assert.False(res.Ok, $"Expected {req.Kind} to fail closed when AuthorizeProfileChange throws.");
        }

        Assert.Equal(480_000u, _requests[0xfe]);
        Assert.Equal(beforeDaily, File.ReadAllText(dailyPath));
        Assert.NotNull(SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "saved560"));
        Assert.Null(SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "overwrite"));
    }

    [Fact]
    public void Service_lifecycle_and_recovery_aliases_rejected_from_client_and_allowed_internally()
    {
        var d = PowerAndOcp();
        d.AuthorizeProfileChange = _ => true;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);

        // Even with an approving authorizer, clients may not invoke service-only recovery/lifecycle aliases.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 575_000)).Ok);
        foreach (string alias in new[] { "start", "resume", "apply-power", "after-reset", "crashed", "crashed-held" })
        {
            var r = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: alias, Value: "client"));
            Assert.False(r.Ok, $"Expected {alias} to be rejected from client.");
            Assert.Equal(BrokerCode.BadRequest, r.Code);
            Assert.Equal(575_000u, _requests[0xfe]);
        }

        // Trusted internal Dispatch still performs them without prompting.
        d.AuthorizeProfileChange = _ => throw new InvalidOperationException("internal Dispatch must not prompt");
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "apply-power")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 575_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "resume", Value: "wake")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);
    }

    [Fact]
    public void Client_profile_mutation_denial_and_nested_dispatch_preserve_state()
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "s560")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"lab"}""")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try", Lease: "job-1",
            Value: JsonSerializer.Serialize(new { curve = "0*127", change = "trial 490W", power_mw = 490_000u, keep_curve = true }))).Ok);

        string dailyPath = DailyProfile.PathBeside(_journal.Path);
        string beforeDaily = File.ReadAllText(dailyPath);

        // Nested Dispatch inside an active FromClient context must remain constrained by ClientAsks=true.
        bool nestedFailedClosed = false;
        bool nestedStartBlocked = false;
        int promptDepth = 0;
        d.AuthorizeProfileChange = _ =>
        {
            if (promptDepth++ == 0)
            {
                var nestedOff = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off"));
                nestedFailedClosed = !nestedOff.Ok;
                var nestedStart = d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "start"));
                nestedStartBlocked = !nestedStart.Ok && nestedStart.Code == BrokerCode.BadRequest;
            }
            return false;
        };

        var fpKeep = PreviewFingerprint(d, "preview");
        var fpAdopt = PreviewFingerprint(d, "preview-adopt");
        var fpPreset = PreviewFingerprint(d, "preview", "s560");

        foreach (var req in new[]
        {
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "off"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "load"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "s560-new"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "forget", Value: "s560"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "530000"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "stock"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "factory"),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", ExpectedProfileFingerprint: fpKeep),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "adopt", ExpectedProfileFingerprint: fpAdopt),
            new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "s560", ExpectedProfileFingerprint: fpPreset),
        })
        {
            promptDepth = 0;
            nestedFailedClosed = false;
            nestedStartBlocked = false;
            var res = d.FromClient(req);
            Assert.False(res.Ok, $"Expected {req.Kind} denial.");
            Assert.True(nestedFailedClosed, $"Nested Dispatch inside {req.Kind} must observe ClientAsks=true and fail closed.");
            Assert.True(nestedStartBlocked, $"Nested service-only Dispatch inside {req.Kind} must observe ClientAsks=true.");
        }

        Assert.Equal(490_000u, _requests[0xfe]);
        Assert.Equal(beforeDaily, File.ReadAllText(dailyPath));
        Assert.NotNull(SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "s560"));
        Assert.Null(SavedProfiles.Find(SavedProfiles.PathBeside(_journal.Path), "s560-new"));
    }

    [Fact]
    public void Post_consent_mutation_race_detected_and_aborted_across_profile_mutations()
    {
        var d = PowerAndOcp();
        string dailyPath = DailyProfile.PathBeside(_journal.Path);
        string savedPath = SavedProfiles.PathBeside(_journal.Path);

        // 1. keep / adopt race: card power changed while waiting on consent.
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        var fpKeep = PreviewFingerprint(d, "preview");
        d.AuthorizeProfileChange = _ => { _requests[0xfe] = 520_000; return true; };
        var racedKeep = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep", ExpectedProfileFingerprint: fpKeep));
        Assert.False(racedKeep.Ok);
        Assert.Equal(BrokerCode.CannotWrite, racedKeep.Code);
        Assert.False(File.Exists(dailyPath));

        _requests[0xfe] = 550_000;
        var fpAdopt = PreviewFingerprint(d, "preview-adopt");
        d.AuthorizeProfileChange = _ => { _requests[0xfe] = 510_000; return true; };
        var racedAdopt = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "adopt", ExpectedProfileFingerprint: fpAdopt));
        Assert.False(racedAdopt.Ok);
        Assert.Equal(BrokerCode.CannotWrite, racedAdopt.Code);
        Assert.False(File.Exists(dailyPath));

        // Establish initial daily profile + saved profile internally.
        _requests[0xfe] = 560_000;
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "base")).Ok);

        // 2. preset race: saved profile modified while waiting on consent.
        var presetFp = PreviewFingerprint(d, "preview", "base");
        d.AuthorizeProfileChange = _ =>
        {
            SavedProfiles.Save(savedPath, "base", new DailyProfile([new ProfileWrite("power", "fe", "505000")], _host.Now));
            return true;
        };
        var racedPreset = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "preset", Value: "base", ExpectedProfileFingerprint: presetFp));
        Assert.False(racedPreset.Ok);
        Assert.Equal(BrokerCode.CannotWrite, racedPreset.Code);
        Assert.Equal(560_000u, _requests[0xfe]);

        // Restore saved "base" cleanly for remaining races.
        SavedProfiles.Save(savedPath, "base", new DailyProfile([new ProfileWrite("power", "fe", "555000")], _host.Now));

        // 3. load / wall / save / off race: daily profile changed while waiting on consent.
        void SaveDaily(uint mw, bool suspended = false) =>
            new DailyProfile([new ProfileWrite("power", "fe", mw.ToString())], _host.Now, Suspended: suspended).Save(dailyPath);

        SaveDaily(560_000);
        d.AuthorizeProfileChange = _ => { SaveDaily(545_000); return true; };
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "load")).Ok);
        Assert.Equal(560_000u, _requests[0xfe]);

        SaveDaily(560_000);
        d.AuthorizeProfileChange = _ => { SaveDaily(545_000); return true; };
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "wall", Value: "530000")).Ok);
        Assert.Equal("545000", Assert.Single(DailyProfile.Load(dailyPath)!.Writes).Value);

        SaveDaily(560_000);
        d.AuthorizeProfileChange = _ => { SaveDaily(545_000); return true; };
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "save", Value: "raced")).Ok);
        Assert.Null(SavedProfiles.Find(savedPath, "raced"));

        SaveDaily(560_000);
        d.AuthorizeProfileChange = _ => { SaveDaily(545_000); return true; };
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "off")).Ok);
        Assert.False(DailyProfile.Load(dailyPath)!.Suspended);

        // 4. forget race: saved profile changed while waiting on consent -> refuse to delete.
        d.AuthorizeProfileChange = _ =>
        {
            SavedProfiles.Save(savedPath, "base", new DailyProfile([new ProfileWrite("power", "fe", "515000")], _host.Now));
            return true;
        };
        Assert.False(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: "forget", Value: "base")).Ok);
        Assert.NotNull(SavedProfiles.Find(savedPath, "base"));
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("finish-job")]
    public void Cleanup_restores_existing_daily_without_new_consent_or_profile_mutation(string operation)
    {
        var d = PowerAndOcp();
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpPower, Kind: "fe", Mw: 560_000)).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpProfile, Kind: "keep")).Ok);
        string path = DailyProfile.PathBeside(_journal.Path);
        string before = File.ReadAllText(path);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "begin", Value: """{"game":"lab"}""")).Ok);
        Assert.True(d.Dispatch(new BrokerRequest(BrokerRequest.OpExperiment, Kind: "try", Lease: "job-1",
            Value: JsonSerializer.Serialize(new { curve = "0*127", change = "trial", power_mw = 480_000u, keep_curve = true }))).Ok);
        int prompts = 0;
        d.AuthorizeProfileChange = _ => { prompts++; return false; };
        if (operation == "finish-job")
        {
            Assert.True(d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: operation, Lease: "other-job")).Ok);
            Assert.Equal(480_000u, _requests[0xfe]); // another job cannot end the current trial
        }
        var answer = d.FromClient(new BrokerRequest(BrokerRequest.OpProfile, Kind: operation, Lease: "job-1"));
        Assert.True(answer.Ok, answer.Error);
        Assert.Equal(0, prompts);
        Assert.Equal(560_000u, _requests[0xfe]);
        Assert.Equal(before, File.ReadAllText(path));
    }
}
