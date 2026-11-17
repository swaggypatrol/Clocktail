using System.Globalization;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Vf;
using Clocktail.Domain.Writes;
using Clocktail.Hw;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    /// <summary>
    /// P0 and V/F are coupled controls. Their relationship is observed on this write, never selected by card name:
    /// retain the old effective displacement plus the requested core delta (zero for memory), then verify it.
    /// One sentinel and GPU lock cover original capture, native side effects, repair and rollback.
    /// </summary>
    private BrokerResponse WriteP0WithCurve(IGpuTarget target, BrokerRequest req)
    {
        if (!Enum.TryParse<ClockDomain>(req.Key, true, out var domain) || !Enum.IsDefined(domain)
            || !int.TryParse(req.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int khz))
            return Fail("a P0 write needs core or memory and an integer kHz target", BrokerCode.BadRequest);
        var hw = Client([.. req.EnableSlots ?? [], nameof(Slot.ClockClientClkVfPointsSetControl)]);
        var gpu = target.Session;
        if (!gpu.TryEnter(TimeSpan.FromSeconds(5), out var gate))
            return Fail("another operation holds this GPU; nothing written", BrokerCode.WindowBusy);
        using (gate)
        {
            var before = VfReader.Read(hw, gpu);
            var p0 = Pstates20Reader.Read(hw, gpu);
            var entry = p0.P0(domain) ?? throw new InvalidDataException($"P0 has no {domain} clock entry");
            var initialPlan = Pstates20Rules.Plan(p0, domain, khz);
            if (initialPlan is Mutation.Abort abort) return Fail(abort.Reason, BrokerCode.CannotWrite);
            int? effectiveTarget = req.P0Curve == P0CurvePolicy.RestoreDailyTarget && domain == ClockDomain.Core ? khz : null;
            if (effectiveTarget != null && before.Mode != VfMode.Regional)
                return Fail("this daily profile has a P0 core target without an explicit V/F curve; a legacy derived baseline cannot verify its uniform effective target: save an explicit verified curve", BrokerCode.CannotWrite);
            long delta = domain == ClockDomain.Core ? (long)khz - entry.DeltaKhz : 0;
            // An implicit daily target replaces the old shape. Its actual replacement is bounded below,
            // after native P0 readback; only relative preservation spends the old point offsets here.
            if (effectiveTarget == null && domain == ClockDomain.Core && VfBudget.Of(before)
                .Check(VfLimits.From(p0), khz) is { } over)
                return Fail(over, BrokerCode.CannotWrite);

            var writer = new OwnedWriter(hw, target.Journal, target.Sentinel, _host);
            var vfCell = VfWrites.Cell(before);
            var p0Cell = ClockOffsets.Cell(domain);
            // Check adoption before opening a window, just as OwnedWriter.Apply does.
            writer.AdoptedOriginal(vfCell.Kind, vfCell.Key);
            writer.AdoptedOriginal(p0Cell.Kind, p0Cell.Key);
            var scope = target.Sentinel.Begin("P0 and V/F", req.TakeOver, $"{ClockOffsets.Kind}/{req.Key} <- {khz}; preserve effective Core curve")
                ?? throw new InvalidOperationException("a previous application did not complete; run --recover before tuning");
            bool touched = false;

            // Write each cell through its existing firmware rules while the outer transaction owns both originals.
            void Set(string kind, string key, string value)
            {
                Prepared prepared;
                if (kind == VfWrites.Kind)
                {
                    var fresh = VfReader.Read(hw, gpu);
                    VfEffectiveCurve.RequireSameCurve(before, fresh);
                    var limits = VfLimits.From(Pstates20Reader.Read(hw, gpu));
                    prepared = new(VfWrites.Cell(fresh), VfWrites.Plan(fresh, limits, VfOffsets.Decode(value)));
                }
                else
                {
                    prepared = WriteRegistry.Find(kind)!.Prepare(hw, gpu, key, value, new WriteOptions(CurveFollows: true));
                }
                if (prepared.Plan == null) throw new InvalidOperationException(prepared.CannotWrite);
                // An intermediate return to an original is not a committed give-back. The final coupled proof can
                // still fail and roll back to a tuned state, including a NoChange VF setter after native mirroring.
                var r = writer.Write(gpu, prepared.Cell, prepared.Plan, retainOriginal: true);
                touched |= r.Tx.Outcome is TxOutcome.Applied or TxOutcome.Failed;
                if (!r.Tx.Succeeded) throw new InvalidOperationException($"{kind}/{key}: {r.Tx}");
            }

            // Prove both the requested P0 entry and its effect at each point.
            void Verify(VfCurveSnapshot actual)
            {
                VfEffectiveCurve.RequireSameCurve(before, actual);
                if (effectiveTarget is { } targetKhz)
                {
                    foreach (var point in actual.Points)
                        if (Math.Abs(VfEffectiveCurve.Displacement(point) - targetKhz) >= 1_000)
                            throw new InvalidOperationException($"point {point.Index}: the daily effective Core target {targetKhz} kHz did not verify");
                }
                else if (VfEffectiveCurve.Mismatch(before, actual, delta) is { } mismatch) throw new InvalidOperationException(mismatch);
                if (Pstates20Reader.Read(hw, gpu).P0(domain)?.DeltaKhz != khz)
                    throw new InvalidOperationException("P0 target no longer matches its readback");
            }

            // A bookkeeping error after either hardware proof must leave the sentinel for recovery.
            // Rewriting hardware here could recreate an original that the journal already released.
            string? SettleOwnership(VfCurveSnapshot verified)
            {
                try
                {
                    writer.ReleaseIfRestored(gpu, p0Cell);
                    writer.ReleaseIfRestored(gpu, VfWrites.Cell(verified));
                    scope.Complete();
                    return null;
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return $"the hardware state was verified, but ownership could not be settled ({e.Message}); run recovery before tuning";
                }
            }

            BrokerResponse Failure(string error, bool restored, bool stateKnown) => new(false, error, Element(new
            {
                success = false, effective_curve_verified = false, restored,
                applied = new[] { new { kind = p0Cell.Kind, key = p0Cell.Key, outcome = nameof(TxOutcome.Failed),
                    restore = (restored ? RestoreState.Restored : RestoreState.RestoreFailed).ToString(),
                    state_known = stateKnown, detail = error } },
            }), BrokerCode.Failed);

            bool p0Changes = entry.DeltaKhz != khz;
            VfCurveSnapshot after;
            try
            {
                bool curveNeedsRepair = effectiveTarget is { } targetKhz && before.Points.Any(p => Math.Abs(VfEffectiveCurve.Displacement(p) - targetKhz) >= 1_000);
                if (p0Changes || curveNeedsRepair)
                {
                    writer.ClaimOriginal(vfCell, before.Control);
                    if (p0Changes) writer.ClaimOriginal(p0Cell, p0.Raw);
                    // The ordinary P0 path refuses to wipe a nonzero curve. Set it aside through its own rules.
                    if (p0Changes && before.Offsets().Any(o => o != 0))
                        Set(VfWrites.Kind, VfWrites.CoreKey, VfOffsets.Encode(new int[before.EditableCount]));
                    if (p0Changes) Set(ClockOffsets.Kind, ClockOffsets.Key(domain), khz.ToString(CultureInfo.InvariantCulture));

                    var native = VfReader.Read(hw, gpu);
                    VfEffectiveCurve.RequireSameCurve(before, native);
                    int[] repair;
                    if (before.Mode == VfMode.Regional)
                    {
                        // Regional BaseKhz is independently reported. Subtract it to avoid saving thermal changes as
                        // tuning. Never add the requested offset to both representations: use only the observed error.
                        repair = native.Points.Select((point, i) =>
                        {
                            long desired = effectiveTarget ?? VfEffectiveCurve.Displacement(before.Points[i]) + delta;
                            long error = desired - VfEffectiveCurve.Displacement(point);
                            if (Math.Abs(error) < 1_000) return point.OffsetKhz;
                            long correction = checked((long)Math.Round(error / (double)VfShapes.StepKhz) * VfShapes.StepKhz);
                            if (Math.Abs(error - correction) >= 1_000)
                                throw new InvalidOperationException($"point {i}: effective change is not a verifiable clock step; no adaptive curve repair");
                            return checked((int)(point.OffsetKhz + correction));
                        }).ToArray();
                    }
                    else
                    {
                        // A legacy baseline is derived from effective minus control, so it cannot identify the P0
                        // contribution. Restore controls only and require direct effective-frequency verification.
                        repair = before.Offsets();
                    }
                    // The normal V/F path retains individual and global-plus-point firmware bounds.
                    // Check even unchanged controls here: a native P0 side effect can already have set the candidate.
                    var limits = VfLimits.From(Pstates20Reader.Read(hw, gpu));
                    for (int i = 0; i < repair.Length; i++)
                        if (repair[i] < limits.LowestKhz || repair[i] > limits.HighestKhz)
                            throw new InvalidOperationException($"point {i}: preserving the effective curve would exceed the firmware offset or global-plus-point range ({limits})");
                    Set(VfWrites.Kind, VfWrites.CoreKey, VfOffsets.Encode(repair));
                }
                after = VfReader.Read(hw, gpu);
                Verify(after);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or OverflowException or IOException or UnauthorizedAccessException)
            {
                VfCurveSnapshot restored;
                try
                {
                    if (touched)
                    {
                        // Restoring P0 can wipe or mirror the controls again. Restore the captured V/F last,
                        // with the fresh topology verified before the setter sees a mask.
                        Set(ClockOffsets.Kind, p0Cell.Key, entry.DeltaKhz.ToString(CultureInfo.InvariantCulture));
                        var fresh = VfReader.Read(hw, gpu);
                        VfEffectiveCurve.RequireSameCurve(before, fresh);
                        var controls = new VfControlSnapshot(fresh.Layout, fresh.Info, fresh.Control, fresh.Mask,
                            before.Points.Select(p => (p.Index, p.OffsetKhz)).ToArray());
                        var restore = new Transaction(hw).Run(gpu, VfWrites.Spec(fresh.Mask),
                            raw => VfWrites.RestoreControls(controls, raw));
                        if (!restore.Succeeded) throw new InvalidOperationException(restore.Detail);
                    }
                    if (Transaction.FirstDifference(p0.Raw, Pstates20Reader.Read(hw, gpu).Raw, Pstates20Layout.StatusRanges) != null)
                        throw new InvalidOperationException("the original P0 controls did not return");
                    restored = VfReader.Read(hw, gpu);
                    if (!restored.Offsets().SequenceEqual(before.Offsets()) || VfEffectiveCurve.Mismatch(before, restored) != null)
                        throw new InvalidOperationException("the original effective curve did not return");
                }
                catch (Exception restore) when (restore is InvalidOperationException or InvalidDataException or OverflowException or IOException or UnauthorizedAccessException)
                {
                    return Failure($"effective P0/V/F write failed: {e.Message}; rollback could not be verified ({restore.Message}); run recovery and read the card before tuning",
                        restored: false, stateKnown: false);
                }
                string? settlementError = SettleOwnership(restored);
                return Failure($"effective P0/V/F write failed: {e.Message}; " + (settlementError ??
                    "the previous P0 and effective curve were verified restored"), restored: true, stateKnown: settlementError == null);
            }

            if (SettleOwnership(after) is { } settlement)
                return Failure(settlement, restored: false, stateKnown: false);
            return Ok(new
            {
                success = true, curve = VfOffsets.Encode(after.Offsets()), effective_curve_verified = true,
                applied = new object[]
                {
                    new { kind = p0Cell.Kind, key = p0Cell.Key, before = entry.DeltaKhz.ToString(CultureInfo.InvariantCulture),
                        after = khz.ToString(CultureInfo.InvariantCulture), outcome = p0Changes ? "Applied" : "NoChange", effective_curve_verified = true },
                    new { kind = VfWrites.Kind, key = VfWrites.CoreKey, before = VfOffsets.Encode(before.Offsets()),
                        after = VfOffsets.Encode(after.Offsets()), outcome = !before.Offsets().SequenceEqual(after.Offsets()) ? "Applied" : "NoChange" },
                },
            });
        }
    }
}
