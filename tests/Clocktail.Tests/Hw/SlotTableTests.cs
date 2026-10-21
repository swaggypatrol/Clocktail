using System.Globalization;
using System.Reflection;
using Clocktail.Hw;

namespace Clocktail.Tests.Hw;

/// <summary>The C# table must agree with the embedded csv.</summary>
public class SlotTableTests
{
    private static readonly Dictionary<int, string> Aliases = new()
    {
        [25] = "(unnamed)",                 // SingleDomainFrequencyRead: no public name
        [40] = "GetMemoryInfoEx(jNizM)",    // the csv notes the public table it came from
        [68] = "GetControl",                // the csv keeps the bare name; ours carries the VFE prefix
    };

    private static List<string[]> Csv()
    {
        using var s = typeof(Slot).Assembly.GetManifestResourceStream("nvapi_slots.csv")!;
        using var r = new StreamReader(s);
        var rows = new List<string[]>();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var fields = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') q = false;
                    else cur.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            fields.Add(cur.ToString());
            rows.Add(fields.ToArray());
        }
        return rows;
    }

    [Fact]
    public void Table_matches_the_csv_row_for_row()
    {
        var rows = Csv();
        Assert.Equal("slot", rows[0][0]);
        Assert.Equal(77, rows.Count);   // header + 76 slots
        foreach (var row in rows.Skip(1))
        {
            int n = int.Parse(row[0], CultureInfo.InvariantCulture);
            var info = Slots.Info((Slot)n);
            Assert.Equal(uint.Parse(row[1].AsSpan(2), NumberStyles.HexNumber), info.Id);
            Assert.Equal(Aliases.GetValueOrDefault(n, info.Name), row[2]);
            var access = row[4] switch { "R" => SlotAccess.Read, "W" => SlotAccess.Write, _ => SlotAccess.Forbidden };
            Assert.Equal(access, info.Access);
            var ev = row[6][0] switch { 'H' => Evidence.High, 'M' => Evidence.Medium, _ => Evidence.Low };
            Assert.Equal(ev, info.Evidence);
        }
    }

    [Fact]
    public void Ids_are_unique_and_every_slot_is_filled()
    {
        var all = Slots.All;
        Assert.Equal(78, all.Count);
        Assert.All(Enum.GetValues<Slot>(), s => Assert.Equal(s, Slots.Info(s).Slot));
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Setter_slots_are_exactly_the_sixteen_known_setters()
    {
        int[] expected = [3, 7, 10, 14, 24, 27, 30, 34, 37, 48, 50, 52, 57, 61, 64, 72];
        Assert.Equal(expected, Slots.Setters.Select(s => (int)s.Slot).Order());
    }

    [Fact]
    public void Every_setter_has_a_write_policy_and_only_fully_decoded_ones_are_enabled()
    {
        foreach (var s in Slots.Setters)
        {
            var p = WritePolicySet.Default.Get(s.Slot);
            Assert.NotNull(p);
            Assert.NotEqual(PolicyState.Forbidden, p.State);
            if (p.State == PolicyState.Enabled) Assert.True(p.Complete, $"{s.Slot} is enabled with an incomplete rule set");
            if (p.State == PolicyState.Disabled) Assert.False(string.IsNullOrWhiteSpace(p.Reason), $"{s.Slot} is disabled without a reason");
        }
        // Enabling a setter is a decision, so it has to show up here. ClientPwrPoliciesSetControl: since its live drill (2026-10-17).
        // FanPolicySetControl: decoded whole and read live (2026-10-17, 2026-10-20); its first write is the drill.
        // ClockClkDomainsSetControl: since its live drill (2026-10-20).
        Slot[] enabled = [Slot.VoltVoltRailsSetControl, Slot.SetPstates20, Slot.ClientPowerPoliciesSetStatus, Slot.ClientVoltRailsSetControl,
                          Slot.SetCoolerLevels, Slot.ClientPwrPoliciesSetControl, Slot.ClientFanCoolersSetControl, Slot.FanPolicySetControl,
                          Slot.ClockClkDomainsSetControl];
        Assert.Equal(enabled.Order(), WritePolicySet.Default.All.Where(p => p.State == PolicyState.Enabled).Select(p => p.Slot).Order());
    }

    [Fact]
    public void Register_access_is_forbidden_and_cannot_be_enabled()
    {
        var p = WritePolicySet.Default.Get(Slot.RegisterOp)!;
        Assert.Equal(PolicyState.Forbidden, p.State);
        Assert.Equal(SlotAccess.Forbidden, Slots.Info(Slot.RegisterOp).Access);
        Assert.Throws<InvalidOperationException>(() => WritePolicySet.Default.WithEnabled(Slot.RegisterOp));
    }

    [Fact]
    public void Known_shapes_match_the_sizes_in_the_notes()
    {
        Assert.Equal(new Versioned(0x184c, 2), Slots.Info(Slot.VoltVoltRailsGetInfo).Shape);
        Assert.Equal(new Versioned(0x1620, 2), Slots.Info(Slot.VoltVoltRailsGetStatus).Shape);
        Assert.Equal(new Versioned(0xac8, 2), Slots.Info(Slot.VoltVoltRailsSetControl).Shape);
        Assert.Equal(new Versioned(0x1cf8, 2), Slots.Info(Slot.SetPstates20).Shape);
        Assert.Equal(new Versioned(0x15b0c, 2), Slots.Info(Slot.ClockClientClkVfPointsGetStatus).Shape);
        Assert.Equal(new Versioned(0xf48, 1), Slots.Info(Slot.VoltVoltDevicesGetInfo).Shape);
        Assert.Equal(new Versioned(0x62c, 1), Slots.Info(Slot.ClientFanCoolersGetInfo).Shape);   // settled by the live size probe on 617.42
        Assert.Equal(new Versioned(0x5ac, 1), Slots.Info(Slot.ClientFanCoolersGetControl).Shape);   // 0x2c + 32 x 0x2c; matches the setter's 0x105ac
        Assert.Equal(new Versioned(0x728, 1), Slots.Info(Slot.ClientPwrPoliciesGetInfo).Shape);
        Assert.Equal(new Versioned(0x528, 1), Slots.Info(Slot.ClientPwrPoliciesGetControl).Shape);   // live echo verified
        Assert.Equal(new Versioned(0xa70, 4), Slots.Info(Slot.PowerPolicyGetInfo).Shape);   // layout 1, read live 2026-10-19
        Assert.Equal(new Versioned(0xa4c, 1), Slots.Info(Slot.PowerPolicyGetControl).Shape);
        Assert.Equal(new Versioned(0xca8, 3), Slots.Info(Slot.PowerMonitorGetInfo).Shape);
        Assert.Equal(new Versioned(0x928, 1), Slots.Info(Slot.FanPolicyGetInfo).Shape);      // the fan policies' sizes, live 2026-10-17
        Assert.Equal(new Versioned(0x8a8, 1), Slots.Info(Slot.FanPolicyGetStatus).Shape);
        Assert.Equal(new Versioned(0x14ac, 2), Slots.Info(Slot.FanPolicyGetControl).Shape);
        // The driver writes past 0x75c: the original's buffer is larger than the structure its word names (live 2026-10-21).
        Assert.Equal((new Versioned(0x75c, 1), 0x2000), (Slots.Info(Slot.ClockClkPropTopRelsGetControl).Shape!.Value, Slots.Info(Slot.ClockClkPropTopRelsGetControl).Buffer!.Value));
        Assert.Equal((new Versioned(0x5798, 1), 0x8000), (Slots.Info(Slot.ClockClkPropTopRelsGetInfo).Shape!.Value, Slots.Info(Slot.ClockClkPropTopRelsGetInfo).Buffer!.Value));
    }
}
