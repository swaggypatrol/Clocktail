using System.Buffers.Binary;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Tests.Domain;

/// <summary>The Core V/F write path against <see cref="FakeVf"/>: ownership, the firmware envelope, the kind-7 shape, rollback.</summary>
[Collection("fake-driver")]
public class VfWritesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeHost _host = new();
    private readonly OwnershipJournal _journal;
    private readonly ApplySentinel _sentinel;
    private readonly GpuSession _gpu = new(1, "fake");

    public VfWritesTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new OwnershipJournal(Path.Combine(_dir, "ownership.json"), _host, "gpu-A");
        _sentinel = new ApplySentinel(Path.Combine(_dir, "apply.lock"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private VfWrites Writes(NvClient hw) => new(hw, _journal, _sentinel, _host);

    private static int[] With(int point, int khz) { var o = new int[127]; o[point] = khz; return o; }

    [Fact]
    public void The_current_offsets_are_NoChange_and_the_setter_is_never_called()
    {
        var hw = new FakeVf().Install();
        var r = Writes(hw).Apply(_gpu, new int[127]);
        Assert.Equal(TxOutcome.NoChange, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.StartsWith("nothing held", r.Ownership);
    }

    [Fact]
    public void One_point_lands_with_only_that_point_selected_and_the_original_on_record_first()
    {
        var f = new FakeVf();
        var hw = f.Install();
        string? recordedBeforeTheWrite = null;
        FakeDriver.OnSet = () => recordedBeforeTheWrite = _journal.Load()?.Items.Single().Original;

        var r = Writes(hw).Apply(_gpu, With(60, 15_000));

        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.True(r.Succeeded);
        Assert.Equal("0*127", recordedBeforeTheWrite);
        var mask = f.LastRequest!.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes);
        Assert.Equal(1, VfLayout.PopCount(mask));
        Assert.True(VfLayout.Has(mask, 60));
        Assert.Equal(15_000, FakeVf.Offsets()[60]);
        Assert.Equal(15_000, r.After![60]);
        Assert.Empty(r.Surprises);
        Assert.Equal(f.Base(60) + 15_000u, r.CurveAfter!.Points[60].EffectiveKhz);
        Assert.Equal("held: original 0*127 kHz, now 0*60,15000,0*66 kHz", r.Ownership);
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void Writing_the_original_back_releases_the_record()
    {
        var hw = new FakeVf().Install();
        var vf = Writes(hw);
        Assert.Equal(TxOutcome.Applied, vf.Apply(_gpu, With(60, 15_000)).Tx.Outcome);
        var back = vf.Apply(_gpu, new int[127]);
        Assert.Equal(TxOutcome.Applied, back.Tx.Outcome);
        Assert.StartsWith("released", back.Ownership);
        Assert.Null(_journal.Load());
        Assert.All(FakeVf.Offsets(), o => Assert.Equal(0, o));
    }

    [Fact]
    public void The_firmware_range_bounds_the_point_offset_and_global_plus_point()
    {
        var hw = new FakeVf { GlobalKhz = 300_000 }.Install();
        var vf = Writes(hw);

        var over = vf.Apply(_gpu, With(10, 800_000));
        Assert.Equal(TxOutcome.Aborted, over.Tx.Outcome);
        Assert.Contains("global 300 MHz + point 800 MHz = 1100 MHz", over.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);

        var edge = vf.Apply(_gpu, With(10, 700_000));   // 300 + 700 = the range's own maximum
        Assert.Equal(TxOutcome.Applied, edge.Tx.Outcome);

        var hw2 = new FakeVf().Install();
        var alone = Writes(hw2).Apply(_gpu, With(10, -1_000_001));
        Assert.Equal(TxOutcome.Aborted, alone.Tx.Outcome);
        Assert.Contains("outside the firmware's core offset range", alone.Tx.Detail);
    }

    [Fact]
    public void A_request_of_the_wrong_length_is_refused()
    {
        var hw = new FakeVf().Install();
        var r = Writes(hw).Apply(_gpu, new int[80]);
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("127 editable points", r.Tx.Detail);
    }

    [Fact]
    public void A_curve_another_tool_changed_between_the_reads_is_not_written()
    {
        var f = new FakeVf();
        var hw = f.Install();
        f.AfterControlRead = n =>
        {
            if (n != 1) return;   // right after the planning read: someone else moves point 5
            BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.ControlWord].AsSpan(VfLayout.OffsetField(5)), 7_000);
        };
        var r = Writes(hw).Apply(_gpu, With(60, 15_000));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Contains("changed between the curve read and the write snapshot", r.Tx.Detail);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public void A_write_the_driver_alters_is_restored_and_verified()
    {
        var hw = new FakeVf().Install();
        FakeDriver.Mode = FakeDriver.SetMode.TouchNeighbour;
        FakeDriver.Field = VfLayout.OffsetField(60);
        FakeDriver.Neighbour = 4;          // the entry's next field
        FakeDriver.MisbehaveOnce = true;

        var r = Writes(hw).Apply(_gpu, With(60, 15_000));

        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.Equal(0, FakeVf.Offsets()[60]);
        Assert.StartsWith("released", r.Ownership);
        Assert.Null(_journal.Load());
    }

    /// <summary>Once point 60 carries an offset, the driver reports point 61 at point 60's voltage: not a curve.</summary>
    private static FakeVf BreaksWhenPoint60Moves() => new()
    {
        StatusHook = (s, c) =>
        {
            if (FakeVf.Off(c, 60) != 0)
                BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(VfLayout.Status(61) + 8), 700_000 + 60 * 5_000);
        },
    };

    [Fact]
    public void A_curve_that_no_longer_validates_after_the_write_is_put_back_by_the_transaction()
    {
        var hw = BreaksWhenPoint60Moves().Install();

        var r = Writes(hw).Apply(_gpu, With(60, 15_000));

        Assert.Equal(TxOutcome.Failed, r.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, r.Tx.Restore);
        Assert.False(r.Succeeded);
        Assert.Contains("no longer validates", r.Tx.Detail);
        Assert.StartsWith("released", r.Ownership);
        Assert.All(FakeVf.Offsets(), o => Assert.Equal(0, o));
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void The_registry_path_alone_still_runs_the_post_write_proof()
    {
        // Prepare + OwnedWriter, nothing else: what the broker's generic write verb does.
        var hw = BreaksWhenPoint60Moves().Install();
        var (cell, plan) = VfWrites.Prepare(hw, _gpu, VfWrites.CoreKey, "0*60,15000,0*66");
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan);

        Assert.Equal(TxOutcome.Failed, w.Tx.Outcome);
        Assert.Equal(RestoreState.Restored, w.Tx.Restore);
        Assert.Contains(w.Tx.Log, l => l.StartsWith("post-write check: the V/F curve no longer validates", StringComparison.Ordinal));
        Assert.Equal(Hold.Released, w.Hold);
        Assert.All(FakeVf.Offsets(), o => Assert.Equal(0, o));
    }

    [Fact]
    public void Prepare_writes_the_value_it_is_given_through_the_same_plan()
    {
        var f = new FakeVf();
        var hw = f.Install();
        var (cell, plan) = VfWrites.Prepare(hw, _gpu, VfWrites.CoreKey, "0*126,-7500");
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, cell, plan);

        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal("0*126,-7500", w.After);
        Assert.True(VfLayout.Has(f.LastRequest!.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes), 126));
        Assert.Equal(1, VfLayout.PopCount(f.LastRequest!.AsSpan(VfLayout.MaskOffset, VfLayout.MaskBytes)));
        Assert.Equal("held: original 0*127 kHz, now 0*126,-7500 kHz", w.Ownership);

        // Out of range is the plan's refusal, carried by the transaction; nothing is written.
        var (c2, p2) = VfWrites.Prepare(hw, _gpu, VfWrites.CoreKey, "1000001*127");
        Assert.Equal(TxOutcome.Aborted, new OwnedWriter(hw, _journal, _sentinel, _host).Apply(_gpu, c2, p2).Tx.Outcome);
    }

    [Theory]
    [InlineData("memory", "0*127", "unknown V/F curve")]
    [InlineData("core", "0*80", "this curve has 127 editable points")]
    [InlineData("core", "zero", "is not a list of V/F point offsets")]
    [InlineData("core", "99999999999*127", "is not a list of V/F point offsets")]
    public void Prepare_refuses_a_key_or_value_that_cannot_describe_this_curve(string key, string value, string why)
    {
        var hw = new FakeVf().Install();
        Assert.Contains(why, Assert.Throws<InvalidDataException>(() => VfWrites.Prepare(hw, _gpu, key, value)).Message);
        Assert.Equal(0, FakeDriver.Sets);
    }

    [Fact]
    public void With_the_shipped_policy_the_setter_is_refused_and_nothing_is_owed()
    {
        var hw = new FakeVf { DefaultPolicy = true }.Install();
        var r = Writes(hw).Apply(_gpu, With(60, 15_000));
        Assert.Equal(TxOutcome.Aborted, r.Tx.Outcome);
        Assert.Equal(0, FakeDriver.Sets);
        Assert.Null(_journal.Load());
        Assert.False(_sentinel.IsBlocked(out _));
    }

    [Fact]
    public void A_driver_whose_frequency_is_not_base_plus_offset_is_reported()
    {
        var f = new FakeVf();
        f.Effective = (i, o) => (uint)(f.Base(i) + o / 2);
        var hw = f.Install();
        var r = Writes(hw).Apply(_gpu, With(60, 16_000));
        Assert.True(r.Succeeded);
        Assert.Single(r.Surprises);
        Assert.Contains("point 60", r.Surprises[0]);
    }

    [Fact]
    public void A_driver_that_lands_offsets_as_drill_19_measured_reads_back_the_preview_and_no_surprise()
    {
        var f = Live5090.Curve();
        f.Effective = (i, o) => Driver617(Live5090.Bases[i], o);
        var hw = f.Install();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var shape = VfShapes.Undervolt(s, new VfLimits(-1_000_000, 1_000_000, 0), 25_000, Live5090.CeilingUv);

        var r = Writes(hw).Apply(_gpu, shape.Offsets);

        Assert.True(r.Succeeded);
        Assert.Empty(r.Surprises);
        Assert.Equal(shape.NewKhz, r.CurveAfter!.Points.Select(p => (long)p.EffectiveKhz));
    }

    /// <summary>W14: shift_curve moves the curve on the card, not the stock one. With no offsets it is the stock undervolt;
    /// with the daily profile's +60 MHz on every point, every point up to the ceiling keeps at least its own offset (the
    /// stock shift would not: it starts from the bases), and nothing passes the current curve's clock at the ceiling.</summary>
    [Fact]
    public void Shifting_the_current_curve_starts_from_its_offsets_not_from_stock()
    {
        var f = Live5090.Curve();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        Assert.Equal(VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv).Offsets, VfShapes.ShiftCurrent(s, limits, 25_000, Live5090.CeilingUv).Offsets);

        var raised = s with { Points = s.Points.Select(p => p with { OffsetKhz = 60_000 }).ToList() };
        var shifted = VfShapes.ShiftCurrent(raised, limits, 25_000, Live5090.CeilingUv);
        var fromStock = VfShapes.Undervolt(raised, limits, 25_000, Live5090.CeilingUv);
        var under = raised.Points.Where(p => p.ReferenceVoltageUv <= Live5090.CeilingUv).Select(p => p.Index).ToList();
        Assert.All(under, i => Assert.True(shifted.Offsets[i] >= 60_000, $"point {i}: {shifted.Offsets[i]}"));
        Assert.Contains(under, i => fromStock.Offsets[i] < 60_000);
        Assert.All(shifted.NewKhz, k => Assert.True(k <= shifted.CapKhz, $"{k} above the cap {shifted.CapKhz}"));
        Assert.Contains(under, i => shifted.Offsets[i] > 60_000);   // it does move
    }

    [Fact]
    public void A_driver_that_carries_half_steps_is_reported()
    {
        // 2317.5 + 22.5 MHz: driver 617.42 reports 2332, a driver that adds exactly would report 2340.
        var f = Live5090.Curve();
        f.Effective = (i, o) => (uint)((Step(Live5090.Bases[i]) + o) / 1000 * 1000);
        var hw = f.Install();
        Assert.Equal(2_317_000u, Live5090.Bases[71]);

        var r = Writes(hw).Apply(_gpu, With(71, 22_500));

        Assert.True(r.Succeeded);
        Assert.Equal("point 71: reports 2340 MHz, the model 2332 MHz (base 2317 + global 0 + offset 22.5)", Assert.Single(r.Surprises));
    }

    /// <summary>Driver 617.42 as drill 19 measured it, in clock steps: a raise adds its whole pairs of steps and keeps an
    /// odd step if either side has one; a cut subtracts exactly. Reported floored to the MHz.</summary>
    private static uint Driver617(uint reportedBase, int offsetKhz)
    {
        long k = Step(reportedBase) / VfShapes.StepKhz, d = offsetKhz / VfShapes.StepKhz;
        long landed = offsetKhz <= 0 ? k + d : (k / 2 + d / 2) * 2 + ((k | d) & 1);
        return (uint)(landed * VfShapes.StepKhz / 1000 * 1000);
    }

    [Fact]
    public void Landing_reproduces_every_point_drill_19_read_back()
    {
        // (base, offset, what the driver reported after the write), kHz: every point that carried an offset in drill 19
        // (d06: point 126; d14: the 6.25 mV shape on the 33 °C bases, points 67..126; e1-e4: points 105 and 104).
        (uint Base, int Offset, long Read)[] seen =
        [
            (3180000, -7500, 3172000), (2205000, 30000, 2235000), (2235000, 22500, 2257000), (2257000, 30000, 2287000),
            (2287000, 30000, 2317000), (2317000, 22500, 2332000), (2340000, 30000, 2370000), (2370000, 22500, 2392000),
            (2392000, 30000, 2422000), (2422000, 22500, 2437000), (2445000, 22500, 2467000), (2467000, 22500, 2482000),
            (2490000, 30000, 2520000), (2520000, 22500, 2542000), (2542000, 22500, 2557000), (2565000, 22500, 2587000),
            (2587000, 22500, 2602000), (2610000, 22500, 2632000), (2632000, 22500, 2647000), (2655000, 22500, 2677000),
            (2677000, 15000, 2692000), (2692000, 30000, 2722000), (2722000, 22500, 2737000), (2745000, 22500, 2767000),
            (2767000, 15000, 2782000), (2782000, 22500, 2797000), (2805000, 15000, 2820000), (2820000, 7500, 2827000),
            (2827000, 15000, 2842000), (2842000, 15000, 2857000), (2857000, 15000, 2872000), (2872000, 7500, 2872000),
            (2880000, 15000, 2895000), (2895000, 7500, 2902000), (2902000, 15000, 2917000), (2917000, 15000, 2932000),
            (2932000, 7500, 2932000), (2955000, -15000, 2940000), (2962000, -22500, 2940000), (2977000, -37500, 2940000),
            (2985000, -45000, 2940000), (3000000, -60000, 2940000), (3007000, -67500, 2940000), (3022000, -82500, 2940000),
            (3030000, -90000, 2940000), (3045000, -105000, 2940000), (3052000, -112500, 2940000), (3067000, -127500, 2940000),
            (3075000, -135000, 2940000), (3082000, -142500, 2940000), (3097000, -157500, 2940000), (3105000, -165000, 2940000),
            (3112000, -172500, 2940000), (3120000, -180000, 2940000), (3135000, -195000, 2940000), (3142000, -202500, 2940000),
            (3150000, -210000, 2940000), (3157000, -217500, 2940000), (3172000, -232500, 2940000), (3180000, -240000, 2940000),
            (2962000, 7500, 2962000), (2962000, 15000, 2977000), (2962000, -7500, 2955000), (2955000, 7500, 2962000),
        ];
        Assert.Equal(64, seen.Length);
        Assert.All(seen, p => Assert.Equal(p.Read, VfShapes.Landing(p.Base, p.Offset)));
        Assert.All(seen, p => Assert.Equal(p.Read, Driver617(p.Base, p.Offset)));
        // Exact arithmetic gets the ones with a half step on both sides wrong: 10 of the 6.25 mV shape's points and e1.
        Assert.Equal(11, seen.Count(p => (Step(p.Base) + p.Offset) / 1000 * 1000 != p.Read));
        // A raise off the 7.5 MHz grid (Afterburner's whole MHz) is floored to whole steps before it lands.
        Assert.Equal(VfShapes.Landing(2842000, 7500), VfShapes.Landing(2842000, 10000));
        Assert.Equal(VfShapes.Landing(2895000, 30000), VfShapes.Landing(2895000, 31000));
    }

    [Theory]
    [InlineData(6_250)]
    [InlineData(25_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void A_shape_asks_only_for_what_lands_and_loses_nothing_where_it_is_written(int undervoltUv)
    {
        foreach (var bases in new[] { Live5090.Bases, Live5090.Bases75C, Live5090.Bases33C })
        {
            var f = new FakeVf { Voltage = i => Live5090.Voltages[i], Base = i => bases[i] };
            var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
            var shape = VfShapes.Undervolt(s, new VfLimits(-1_000_000, 1_000_000, 0), undervoltUv, Live5090.CeilingUv);

            // Every point lands exactly where it asks to, so the preview is what reads back ...
            for (int i = 0; i < 127; i++)
                Assert.Equal((Step(bases[i]) + shape.Offsets[i]) / 1000 * 1000, shape.NewKhz[i]);
            Assert.All(Enumerable.Range(0, 127), i => Assert.Equal(VfShapes.Landing(bases[i], shape.Offsets[i]), shape.NewKhz[i]));
            // ... and the points that ask for a step less than the shape's target sit on a half step, where asking for
            // that step would have landed on the same frequency: nothing is lost at this temperature.
            Assert.NotEmpty(shape.Short);
            foreach (int i in shape.Short)
            {
                Assert.Equal(1, Step(bases[i]) / VfShapes.StepKhz % 2);
                Assert.Equal(shape.NewKhz[i], VfShapes.Landing(bases[i], shape.Offsets[i] + VfShapes.StepKhz));
            }
        }
    }

    [Theory]
    [InlineData(6_250)]
    [InlineData(25_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void Shaped_where_it_is_judged_no_point_gets_further_ahead_of_its_base_when_the_card_cools(int undervoltUv)
    {
        // Shaped and judged in the 75 °C burn; then the card cools and the bases climb back. The verdict covers that
        // only if no point gains more than its own base did.
        var h = Live5090.Curve75C();
        var hot = VfParser.Parse(h.Info(), h.Status(h.Control()), h.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        foreach (var cool in new[] { Live5090.Bases, Live5090.Bases33C })
        {
            var shape = VfShapes.Undervolt(hot, limits, undervoltUv, Live5090.CeilingUv, references: [new VfReference("cool", Live5090.Voltages, cool)]);
            long Gain(int i, long o) => VfShapes.Landing(cool[i], o) - VfShapes.Landing(Live5090.Bases75C[i], o) - (Step(cool[i]) - Step(Live5090.Bases75C[i]));

            // Floored to the MHz, a gain of 0 can read up to 0.5 MHz either way; a step is 7.5.
            Assert.All(Enumerable.Range(0, 127), i => Assert.True(Gain(i, shape.Offsets[i]) < 1_000, $"point {i}"));
            // Asking a short point for the step it cannot reach here lands it in full where its base is a whole step:
            // one step further ahead than the base moved.
            Assert.Contains(shape.Short, i => Gain(i, shape.Offsets[i] + VfShapes.StepKhz) > 6_000);
        }
    }

    [Fact]
    public void Same_frequency_undervolt_shifts_the_curve_left_and_caps_it_at_the_ceiling()
    {
        var f = new FakeVf();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);

        // 25 mV at 4 kHz/µV is +100 MHz, floored to the 7.5 MHz grid; a straight curve has no ramp, so it all moves.
        var shape = VfShapes.Undervolt(s, limits, 25_000, capUv: 1_100_000);
        Assert.Equal(2_500_000u, shape.CapKhz);
        Assert.Equal(700_000u, shape.FloorUv);
        Assert.Equal(97_500, shape.Offsets[0]);
        Assert.Equal(97_500, shape.Offsets[75]);
        // From 1.080 V up, v + 25 mV passes the 1.100 V ceiling: flat at the ceiling's stock frequency.
        Assert.Equal(75_000, shape.Offsets[76]);
        Assert.Equal(0, shape.Offsets[80]);
        Assert.Equal(-922_500, shape.Offsets[126]);
        Assert.All(shape.Offsets, o => Assert.Equal(0, o % VfShapes.StepKhz));
        Assert.All(shape.NewKhz, k => Assert.True(k <= 2_500_000, $"{k} kHz exceeds the stock maximum"));
        Assert.Empty(shape.Clamped);
        // 5 mV points are not the regulator's 6.25 mV steps: the request is taken as is.
        Assert.Equal(25_000, shape.AppliedUv);

        // The same request is the same curve, whatever is applied now; 0 µV without a cap is stock.
        Assert.Equal(shape.Offsets, VfShapes.Undervolt(s with { Points = s.Points.Select(p => p with { OffsetKhz = 50_000 }).ToList() }, limits, 25_000, 1_100_000).Offsets);
        Assert.All(VfShapes.Undervolt(s, limits, 0, null).Offsets, o => Assert.Equal(0, o));
    }

    [Fact]
    public void On_the_live_5090_curve_the_shift_starts_at_the_knee_and_stops_at_the_stock_maximum()
    {
        Assert.Equal(127, Live5090.Voltages.Length);
        Assert.Equal(127, Live5090.Bases.Length);
        var f = Live5090.Curve();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);

        // The ramp (0.745–0.865 V, 16–21 MHz/mV) ends at point 67, 0.870 V, with or without a ceiling to look below.
        Assert.Equal(67, VfShapes.Knee(s, Live5090.CeilingUv));
        Assert.Equal(67, VfShapes.Knee(s));

        var shape = VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv);
        Assert.Equal(870_000u, shape.FloorUv);
        Assert.Equal(2_932_000u, shape.CapKhz);
        Assert.All(shape.Offsets.Take(67), o => Assert.Equal(0, o));          // below the knee: stock
        Assert.Equal(105_000, shape.Offsets[67]);                             // 0.870 V takes 0.895 V's 2317 MHz
        Assert.Equal(105_000, shape.Offsets.Max());                           // a classic undervolt's size, not +400 MHz
        Assert.Equal(0, shape.Offsets[103]);                                  // the ceiling point keeps its frequency
        Assert.Equal(-232_500, shape.Offsets[126]);                           // above it, flattened to the cap: 3165 → 2932.5 MHz
        Assert.Empty(shape.Clamped);
        for (int i = 0; i < 127; i++)
            Assert.True(shape.NewKhz[i] <= 2_932_000, $"point {i} would run above the stock maximum");
        // The shifted window never asks a point for less than stock.
        for (int i = 67; i < 103; i++) Assert.True(shape.Offsets[i] >= 0, $"point {i}");
    }

    [Theory]
    [InlineData(25_000, 4)]
    [InlineData(37_500, 6)]
    [InlineData(10_000, 1)]      // 1.6 steps: only whole regulator steps exist
    [InlineData(6_250, 1)]
    [InlineData(5_000, 0)]       // under one step: no point can be reached any lower, so nothing moves
    public void On_the_live_curve_an_undervolt_is_whole_regulator_steps_and_lands_on_stock_frequencies(int undervoltUv, int steps)
    {
        var f = Live5090.Curve();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var shape = VfShapes.Undervolt(s, new VfLimits(-1_000_000, 1_000_000, 0), undervoltUv, Live5090.CeilingUv);

        Assert.Equal(steps * VfShapes.VidStepUv, shape.AppliedUv);
        // Every point from the knee up takes the stock frequency of the point that many steps above it, to the 0.5 MHz
        // the driver hides (0.870 V reads as 2212 MHz and takes 2317 (2317.5) from 0.895 V at four steps), or the clock
        // step below it where the driver cannot land that frequency from the point's own base.
        for (int i = 67; i + steps <= 103; i++)
            Assert.Equal(shape.Short.Contains(i) ? (Step(Live5090.Bases[i + steps]) - VfShapes.StepKhz) / 1000 * 1000 : Live5090.Bases[i + steps], shape.NewKhz[i]);
        Assert.All(shape.Offsets, o => Assert.Equal(0, o % VfShapes.StepKhz));
        if (steps == 4)
            Assert.Equal(52_500, shape.Offsets[99]);   // 2880 → 2932.5 MHz: seven whole steps; the reported 2932 floored it to six
        if (steps == 0)
            Assert.All(shape.Offsets, o => Assert.Equal(0, o));
    }

    [Theory]
    [InlineData(-25_000)]
    [InlineData(10_000)]
    public void A_voltage_demand_offset_in_force_leaves_the_shape_alone_and_never_lifts_its_cap(int shiftUv)
    {
        var stock = Live5090.Curve();
        var shifted = Live5090.Curve();
        shifted.ShiftUv = shiftUv;
        var s0 = VfParser.Parse(stock.Info(), stock.Status(stock.Control()), stock.Control());
        var s = VfParser.Parse(shifted.Info(), shifted.Status(shifted.Control()), shifted.Control());
        Assert.Equal(shiftUv, s.DemandShiftUv);
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);

        var want = VfShapes.Undervolt(s0, limits, 25_000, Live5090.CeilingUv);
        var shape = VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv);

        // The two knobs add: the V/F shape is the same whatever offset is in force ...
        Assert.Equal(want.Offsets, shape.Offsets);
        Assert.Equal(want.FloorUv, shape.FloorUv);
        // ... and its top is still the stock frequency of the ceiling. Drawn on the running voltages, −25 mV would have
        // lifted the cap to 1.120 V's 2977 MHz (the same margin spent twice) and +10 mV lowered it to 1.085 V's 2902.
        Assert.Equal(2_932_000u, shape.CapKhz);
        Assert.All(shape.NewKhz, k => Assert.True(k <= 2_932_000, $"{k} kHz is above the stock maximum"));
    }

    /// <summary>A reported base back on the 7.5 MHz grid: k × 7.5 MHz reads as itself or 0.5 MHz less.</summary>
    private static long Step(uint reported) => reported % VfShapes.StepKhz == 0 ? reported : reported + 500;

    /// <summary>On a curve with a point on every regulator step, the stock frequency n steps above point i.</summary>
    private static long StockAt(uint[] bases, int i, int n) => Enumerable.Range(0, Math.Min(i + n, bases.Length - 1) + 1).Max(j => Step(bases[j]));

    [Fact]
    public void A_shape_read_cold_runs_deeper_hot_and_passes_the_hot_stock_maximum()
    {
        // Why references exist: as the card heats the firmware lowers the bases from the knee up, more at higher voltage,
        // and the offsets stay. 100 mV computed on the idle curve tops the 75 °C curve at 2872.5 MHz as the driver lands
        // it (points 88 to 90), above the 2850 that curve itself tops at; 25 mV runs point 88 three steps deeper there.
        var f = Live5090.Curve();
        var cold = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        long Hot(int i, int[] offsets) => VfShapes.Landing(Live5090.Bases75C[i], offsets[i]);

        var deep = VfShapes.Undervolt(cold, limits, 100_000, Live5090.CeilingUv);
        Assert.Equal(2_850_000, Step(Live5090.Bases75C[103]));
        Assert.Equal(2_872_000, Enumerable.Range(0, 104).Max(i => Hot(i, deep.Offsets)));

        var shape = VfShapes.Undervolt(cold, limits, 25_000, Live5090.CeilingUv);
        Assert.Equal(StockAt(Live5090.Bases75C, 88, 4) + 3 * VfShapes.StepKhz, Step((uint)Hot(88, shape.Offsets)));
    }

    [Theory]
    [InlineData(6_250)]
    [InlineData(25_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void With_both_temperatures_given_the_shape_holds_its_bounds_at_each_and_does_not_depend_on_which_was_read_now(int undervoltUv)
    {
        var f = Live5090.Curve();
        var h = Live5090.Curve75C();
        var cold = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var hot = VfParser.Parse(h.Info(), h.Status(h.Control()), h.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        int n = undervoltUv / VfShapes.VidStepUv;

        // Written at idle with the burn read as reference, or in the burn with the idle read: the same offsets but for
        // a clock step at points whose target is out of reach from the base read at the time of writing.
        var atIdle = VfShapes.Undervolt(cold, limits, undervoltUv, Live5090.CeilingUv, references: [VfReference.Of(hot, "75 C")]);
        var inBurn = VfShapes.Undervolt(hot, limits, undervoltUv, Live5090.CeilingUv, references: [VfReference.Of(cold, "idle")]);
        for (int i = 0; i < 127; i++)
            if (atIdle.Offsets[i] != inBurn.Offsets[i])
            {
                Assert.Equal(VfShapes.StepKhz, Math.Abs(atIdle.Offsets[i] - inBurn.Offsets[i]));
                Assert.True(atIdle.Short.Contains(i) || inBurn.Short.Contains(i), $"point {i}");
            }

        foreach (var shape in new[] { atIdle, inBurn })
            foreach (var (bases, name) in new[] { (Live5090.Bases, "idle"), (Live5090.Bases75C, "75 C") })
            {
                long top = Step(bases[103]);
                // At each temperature no point runs more than ΔV left of that curve, nor above its stock maximum ...
                for (int i = 0; i < 127; i++)
                    Assert.True(Step(bases[i]) + shape.Offsets[i] <= Math.Min(StockAt(bases, i, n), top), $"point {i} at {name}");
                // ... and the new curve still reaches that maximum below the ceiling.
                Assert.Equal(top, Enumerable.Range(0, 104).Max(i => Step(bases[i]) + shape.Offsets[i]));
            }
    }

    [Fact]
    public void A_reference_can_only_lower_offsets()
    {
        var f = Live5090.Curve();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        var v = s.Points.Select(p => p.ReferenceVoltageUv).ToArray();
        var alone = VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv);

        // The curve itself changes nothing.
        Assert.Equal(alone.Offsets, VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv, references: [VfReference.Of(s, "self")]).Offsets);

        // A steeper one (7.5 MHz more per step from the knee up) allows every shift this curve does, so nothing changes
        // up to the ceiling; above it, it rises further past the cap and asks for deeper cuts there.
        var steeper = new VfReference("steeper", v, [.. s.Points.Select(p => p.Index < 67 ? p.BaseKhz : p.BaseKhz + (uint)(p.Index - 66) * 7_500)]);
        var steep = VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv, references: [steeper]);
        Assert.Equal(alone.Offsets.Take(104), steep.Offsets.Take(104));
        Assert.All(Enumerable.Range(104, 23), i => Assert.True(steep.Offsets[i] < alone.Offsets[i], $"point {i}"));

        // A flatter one (the 75 °C read) lowers points below the ceiling too. Either way none is raised: a wrong
        // reference costs frequency, never margin.
        var hot = VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv, references: [new VfReference("75 C", v, Live5090.Bases75C)]);
        Assert.Contains(Enumerable.Range(67, 37), i => hot.Offsets[i] < alone.Offsets[i]);
        foreach (var shape in new[] { steep, hot })
            Assert.All(Enumerable.Range(0, 127), i => Assert.True(shape.Offsets[i] <= alone.Offsets[i], $"point {i}"));
    }

    [Fact]
    public void A_reference_that_is_not_this_curve_is_refused()
    {
        var f = Live5090.Curve();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        var v = s.Points.Select(p => p.ReferenceVoltageUv).ToArray();
        var b = s.Points.Select(p => p.BaseKhz).ToArray();

        var shorter = new VfReference("short", v[..126], b[..126]);
        Assert.Contains("126 points; this curve has 127",
            Assert.Throws<InvalidDataException>(() => VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv, references: [shorter])).Message);
        var moved = v.ToArray();
        moved[90] += 6_250;
        Assert.Contains("point 90",
            Assert.Throws<InvalidDataException>(() => VfShapes.Undervolt(s, limits, 25_000, Live5090.CeilingUv, references: [new VfReference("other", moved, b)])).Message);
    }

    [Fact]
    public void Undervolt_offsets_come_only_from_stock_points_and_are_pulled_into_the_firmware_range()
    {
        var f = new FakeVf();
        var s = VfParser.Parse(f.Info(), f.Status(f.Control()), f.Control());

        // 5 mV reaches the next point's 20 MHz, floored to the 7.5 MHz step (this fake's bases are off it). Just under
        // 5 mV reaches no point at all: the stock curve says nothing between its points, so nothing moves.
        Assert.Equal(15_000, VfShapes.Undervolt(s, new VfLimits(-1_000_000, 1_000_000, 0), 5_000, null).Offsets[10]);
        Assert.Equal(0, VfShapes.Undervolt(s, new VfLimits(-1_000_000, 1_000_000, 0), 4_999, null).Offsets[10]);

        // A ±500 MHz range cannot pull the far top of the curve all the way down to the cap: those points are
        // clamped to the last grid step inside the range, still higher than the cap, still out of the firmware's reach.
        var tight = VfShapes.Undervolt(s, new VfLimits(-500_000, 500_000, 0), 25_000, 1_100_000);
        Assert.Equal(Enumerable.Range(105, 22), tight.Clamped);
        Assert.Equal(-495_000, tight.Offsets[126]);
        Assert.Throws<ArgumentOutOfRangeException>(() => VfShapes.Undervolt(s, new VfLimits(-1, 1, 0), -1, null));
    }

    [Fact]
    public void Undervolt_takes_its_cap_from_the_firmware_voltage_ceiling_and_lands()
    {
        var f = new FakeVf();
        var hw = f.Install(Slot.VoltVoltRailsGetInfo, Slot.VoltVoltRailsGetStatus, Slot.VoltVoltRailsGetControl, Slot.VoltVoltDevicesGetInfo);
        FakeDriver.State[BitConverter.ToUInt32(RailsFixture.Info())] = RailsFixture.Info();
        FakeDriver.State[BitConverter.ToUInt32(RailsFixture.Status())] = RailsFixture.Status();
        FakeDriver.State[BitConverter.ToUInt32(RailsFixture.Control())] = RailsFixture.Control();
        FakeDriver.State[BitConverter.ToUInt32(RailsFixture.Devices())] = RailsFixture.Devices();

        var (shape, r) = Writes(hw).Undervolt(_gpu, 25_000);

        Assert.Equal(RailsFixture.NvvddMax, shape.CapUv);
        Assert.Equal(TxOutcome.Applied, r.Tx.Outcome);
        Assert.True(r.Succeeded);
        Assert.Equal(shape.Offsets, FakeVf.Offsets());
        Assert.Equal(f.Base(80), r.CurveAfter!.Points.Max(p => p.EffectiveKhz));
    }

    [Fact]
    public void The_recorded_original_is_given_back_by_a_later_process()
    {
        var hw = new FakeVf().Install();
        _host.Pid = 4242; _host.Alive.Add(4242);
        Assert.Equal(TxOutcome.Applied, Writes(hw).Apply(_gpu, With(60, 15_000)).Tx.Outcome);
        _host.Alive.Remove(4242); _host.Pid = 100;

        var item = _journal.Inspect().Items.Single();
        Assert.Equal((VfWrites.Kind, VfWrites.CoreKey, "0*127"), (item.Kind, item.Key, item.Original));
        // The give-back is the same entry point as a write, with the recorded original as the value.
        var (cell, plan) = VfWrites.Prepare(hw, _gpu, item.Key, item.Original);
        var w = new OwnedWriter(hw, _journal, _sentinel, _host).Write(_gpu, cell, plan);

        Assert.Equal(TxOutcome.Applied, w.Tx.Outcome);
        Assert.Equal(Hold.Released, w.Hold);
        Assert.All(FakeVf.Offsets(), o => Assert.Equal(0, o));
    }

    /// <summary>The kind-7 setter ships off, opened per request; the broker's recover still gives the curve back,
    /// so a session that failed half-way (or a crashed tuner) leaves no curve on the card and no debt in the journal.</summary>
    [Fact]
    public void Recover_gives_the_curve_back_with_the_shipped_policy()
    {
        var hw = new FakeVf().Install();
        _host.Pid = 4242; _host.Alive.Add(4242);
        Assert.Equal(TxOutcome.Applied, Writes(hw).Apply(_gpu, With(60, 15_000)).Tx.Outcome);
        _host.Alive.Remove(4242); _host.Pid = 100;

        var shipped = new NvClient(hw.Api, WritePolicySet.Default);
        var d = new BrokerDispatcher(shipped, new OneGpu(_journal, _sentinel, _gpu), _host, canWrite: true);
        var r = d.Dispatch(new BrokerRequest(BrokerRequest.OpRecover));

        Assert.True(r.Ok, r.Error);
        Assert.All(FakeVf.Offsets(), o => Assert.Equal(0, o));
        Assert.Null(_journal.Load());
        Assert.Equal(PolicyState.Disabled, shipped.Policies.Get(Slot.ClockClientClkVfPointsSetControl)!.State);
    }

    private sealed class OneGpu(OwnershipJournal j, ApplySentinel s, GpuSession g) : IGpuTarget, IGpuTargets
    {
        public string StableId => "gpu-A";
        public string Name => "fake";
        public GpuSession Session => g;
        public OwnershipJournal Journal => j;
        public ApplySentinel Sentinel => s;
        public IReadOnlyList<string> Ids => ["gpu-A"];
        public IGpuTarget? Resolve(string? id) => id == null || id == "gpu-A" ? this : null;
    }

    [Fact]
    public void A_global_offset_is_checked_against_the_offsets_the_curve_already_carries()
    {
        var f = new FakeVf();
        var c = f.Control();
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(VfLayout.OffsetField(30)), 900_000);
        var s = VfParser.Parse(f.Info(), f.Status(c), c);
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        Assert.Null(VfRules.CheckGlobal(s, limits, 100_000));
        Assert.Contains("V/F point 30", VfRules.CheckGlobal(s, limits, 200_000));
    }

    [Fact]
    public void The_global_budget_reads_the_same_offsets_as_the_full_curve_read()
    {
        var hw = new FakeVf().Install();
        Assert.Equal(TxOutcome.Applied, Writes(hw).Apply(_gpu, With(60, 15_000)).Tx.Outcome);

        var budget = VfBudget.Read(hw, _gpu);

        Assert.Null(budget.Unreadable);
        Assert.Equal(VfReader.Read(hw, _gpu).Points.Select(p => (p.Index, p.OffsetKhz)), budget.Offsets!);
    }

    [Theory]
    [InlineData("demand shift")]
    [InlineData("zero frequency")]
    public void A_curve_whose_status_fails_its_proofs_still_answers_the_global_budget(string trouble)
    {
        var f = new FakeVf();
        if (trouble == "demand shift")
            // The top point's running voltage one regulator step off the others' (a vdemand off the 6.25 mV grid).
            f.StatusHook = (b, _) =>
            {
                var v = b.AsSpan(VfLayout.Status(126) + 8);
                BinaryPrimitives.WriteUInt32LittleEndian(v, BinaryPrimitives.ReadUInt32LittleEndian(v) + 6_250);
            };
        else
            // The bottom points reported at zero frequency (a global below their base).
            f.Effective = (i, o) => i < 3 ? 0u : (uint)(f.Base(i) + f.GlobalKhz + o);
        var hw = f.Install();
        BinaryPrimitives.WriteInt32LittleEndian(FakeDriver.State[FakeVf.ControlWord].AsSpan(VfLayout.OffsetField(30)), 900_000);
        Assert.Throws<InvalidDataException>(() => VfReader.Read(hw, _gpu));

        var budget = VfBudget.Read(hw, _gpu);

        Assert.Null(budget.Unreadable);
        var limits = new VfLimits(-1_000_000, 1_000_000, 0);
        Assert.Null(budget.Check(limits, 100_000));
        Assert.Contains("V/F point 30", budget.Check(limits, 200_000));
    }

    [Fact]
    public void Offsets_that_cannot_be_read_make_a_budget_that_says_why_instead_of_throwing()
    {
        var hw = new FakeVf().Install();
        FakeDriver.State.Remove(FakeVf.ControlWord);

        var budget = VfBudget.Read(hw, _gpu);

        Assert.Null(budget.Offsets);
        Assert.Contains("V/F point control read", budget.Unreadable);
        Assert.Contains("cannot be read", budget.Check(new VfLimits(-1_000_000, 1_000_000, 0), 7_500));
    }

    [Theory]
    [InlineData(200_000, 100_000, true)]
    [InlineData(200_000, 0, true)]
    [InlineData(200_000, 200_000, true)]
    [InlineData(200_000, 207_500, false)]
    [InlineData(200_000, -7_500, false)]
    [InlineData(-150_000, -75_000, true)]
    [InlineData(-150_000, 0, true)]
    [InlineData(-150_000, -157_500, false)]
    [InlineData(-150_000, 7_500, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 7_500, false)]
    [InlineData(0, -7_500, false)]
    public void Without_the_offsets_a_global_may_only_move_toward_zero(int nowKhz, int nextKhz, bool allowed)
    {
        var why = new VfBudget(null, "unreadable").Check(new VfLimits(-1_000_000, 1_000_000, nowKhz), nextKhz);
        Assert.Equal(allowed, why == null);
    }

    [Fact]
    public void A_total_left_outside_the_range_may_be_walked_back_but_not_pushed_further()
    {
        var budget = new VfBudget([(30, 300_000)], null);
        var outside = new VfLimits(-1_000_000, 1_000_000, 800_000);   // another tool left 800 + 300 = 1100 MHz
        Assert.Null(budget.Check(outside, 750_000));
        Assert.Null(budget.Check(outside, 800_000));
        Assert.Contains("1150 MHz", budget.Check(outside, 850_000));

        var inside = new VfLimits(-1_000_000, 1_000_000, 0);
        Assert.Null(budget.Check(inside, 700_000));
        Assert.Contains("1007.5 MHz", budget.Check(inside, 707_500));
    }

    /// <summary>
    /// The unread rule is the read rule held against every curve the range admits: never more permissive than any
    /// one curve (safe), and refusing only what some curve refuses (nothing stricter than it has to be). The worst
    /// curves are a single point at either edge.
    /// </summary>
    [Fact]
    public void Not_knowing_the_offsets_allows_exactly_what_every_admissible_curve_allows()
    {
        const int range = 1_000_000;
        int[] grid = [-1_000_000, -607_500, -250_000, -7_500, 0, 7_500, 250_000, 607_500, 1_000_000];
        foreach (int now in grid)
        foreach (int next in grid)
        {
            var limits = new VfLimits(-range, range, now);
            bool unread = new VfBudget(null, "x").Check(limits, next) == null;
            var curves = grid.Append(Math.Max(-range, -range - now)).Append(Math.Min(range, range - now))
                .Where(p => p != 0 && Math.Abs((long)now + p) <= range).Distinct().ToList();
            bool every = curves.All(p => new VfBudget([(0, p)], null).Check(limits, next) == null);
            Assert.True(unread == every, $"global {now} → {next}: unread {(unread ? "allows" : "refuses")}, admissible curves {(every ? "all allow" : "do not all allow")}");
        }
    }
}
