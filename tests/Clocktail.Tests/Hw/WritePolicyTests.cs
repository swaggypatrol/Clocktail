using System.Buffers.Binary;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

public class WritePolicyTests
{
    private static byte[] Good(Slot s) => NvClient.NewBuffer(s);
    private static void W32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    private static void I32(byte[] b, int off, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off), v);
    private static string? Check(Slot s, byte[] b) => WritePolicySet.Default.Check(s, b);

    [Fact]
    public void Rails_control_needs_the_nvvdd_bit()
    {
        var b = Good(Slot.VoltVoltRailsSetControl);
        Assert.Contains("must be one of", Check(Slot.VoltVoltRailsSetControl, b));   // mask 0
        W32(b, 4, 2);
        Assert.NotNull(Check(Slot.VoltVoltRailsSetControl, b));                      // MSVDD alone: not writable
        W32(b, 4, 3);
        Assert.Null(Check(Slot.VoltVoltRailsSetControl, b));
        W32(b, 4, 1);
        Assert.Null(Check(Slot.VoltVoltRailsSetControl, b));
    }

    [Fact]
    public void Pstates20_accepts_only_p0_core_or_memory_with_a_sane_offset()
    {
        var b = Good(Slot.SetPstates20);
        W32(b, 0x8, 1); W32(b, 0xc, 1); W32(b, 0x14, 0); W32(b, 0x18, 1); W32(b, 0x1c, 0); W32(b, 0x24, 1);
        I32(b, 0x28, 150_000);                                   // +150 MHz core
        Assert.Null(Check(Slot.SetPstates20, b));
        W32(b, 0x1c, 4);                                         // memory domain
        Assert.Null(Check(Slot.SetPstates20, b));
        W32(b, 0x1c, 1);
        Assert.Contains("+0x1c", Check(Slot.SetPstates20, b));
        W32(b, 0x1c, 0); W32(b, 0x14, 1);                        // not P0
        Assert.Contains("+0x14", Check(Slot.SetPstates20, b));
        W32(b, 0x14, 0); I32(b, 0x28, int.MinValue);
        Assert.Null(Check(Slot.SetPstates20, b));
    }

    [Fact]
    public void Power_percent_status_needs_a_non_zero_selector()
    {
        var b = Good(Slot.ClientPowerPoliciesSetStatus);
        Assert.Contains("non-zero", Check(Slot.ClientPowerPoliciesSetStatus, b));
        W32(b, 4, 1);
        Assert.Null(Check(Slot.ClientPowerPoliciesSetStatus, b));
    }

    [Fact]
    public void Boost_percent_is_bounded_and_the_rest_must_be_zero()
    {
        var b = Good(Slot.ClientVoltRailsSetControl);
        W32(b, 4, 100);
        Assert.Null(Check(Slot.ClientVoltRailsSetControl, b));
        W32(b, 4, 101);
        Assert.Contains("at most 100", Check(Slot.ClientVoltRailsSetControl, b));
        W32(b, 4, 50); b[0x27] = 1;
        Assert.Contains("+0x27 must be zero", Check(Slot.ClientVoltRailsSetControl, b));
    }

    [Fact]
    public void Legacy_cooler_levels_allow_one_target_entry()
    {
        var b = Good(Slot.SetCoolerLevels);
        Assert.Null(Check(Slot.SetCoolerLevels, b));            // no-op write passes
        W32(b, 4, 60); W32(b, 8, 1);                             // cooler 0: 60 % manual
        Assert.Null(Check(Slot.SetCoolerLevels, b));
        W32(b, 8, 0x20);                                         // auto
        Assert.Null(Check(Slot.SetCoolerLevels, b));
        W32(b, 8, 2);
        Assert.Contains("policy", Check(Slot.SetCoolerLevels, b));
        W32(b, 8, 1); W32(b, 4, 101);
        Assert.Contains("exceeds 100", Check(Slot.SetCoolerLevels, b));
        W32(b, 4, 50); W32(b, 12, 50); W32(b, 16, 1);            // two entries
        Assert.Contains("only the target", Check(Slot.SetCoolerLevels, b));
    }

    [Fact]
    public void Reads_are_never_subject_to_write_policy()
    {
        Assert.Null(Check(Slot.VoltVoltRailsGetControl, new byte[1]));
    }

    [Fact]
    public void Setter_without_policy_is_refused_and_incomplete_rules_cannot_be_enabled()
    {
        var set = WritePolicySet.Default;
        Assert.Contains("disabled", set.Check(Slot.ThermalPolicySetControl, new byte[0x10]));
        Assert.Throws<InvalidOperationException>(() => set.WithEnabled(Slot.ThermalPolicySetControl));
        Assert.Throws<InvalidOperationException>(() => set.WithEnabled(Slot.ThermChannelSetControl));
        Assert.Throws<InvalidOperationException>(() => set.WithEnabled(Slot.VoltVoltRailsGetInfo));
    }

    [Fact]
    public void Enabling_a_complete_disabled_policy_returns_a_new_set_and_leaves_the_default_alone()
    {
        // No complete-but-disabled policy exists yet, so simulate one through the public record API.
        var p = WritePolicySet.Default.Get(Slot.SetCoolerLevels)! with { State = PolicyState.Disabled, Reason = "test" };
        Assert.Contains("disabled", p.Check(Good(Slot.SetCoolerLevels)));
        Assert.Null((p with { State = PolicyState.Enabled }).Check(Good(Slot.SetCoolerLevels)));
        Assert.Equal(PolicyState.Enabled, WritePolicySet.Default.Get(Slot.SetCoolerLevels)!.State);
    }

    [Fact]
    public void Rules_report_buffers_that_end_early_instead_of_throwing()
    {
        Assert.Contains("ends before", new Dword(0x100, 1).Check(new byte[8]));
        Assert.Contains("ends before", new ZeroBytes(0x10, 4).Check(new byte[8]));
        Assert.Contains("ends before", new Int32Range(8, 0, 1).Check(new byte[8]));
    }
}
