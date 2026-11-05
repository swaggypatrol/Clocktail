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
            var effectiveTarget = req.EffectiveCoreTargetKhz;
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
            // Check adoption before opening a window, just as OwnedWriter.Apply does.
            writer.AdoptedOriginal(vfCell.Kind, vfCell.Key);
            writer.AdoptedOriginal(ClockOffsets.Kind, ClockOffsets.Key(domain));
            var scope = target.Sentinel.Begin("P0 and V/F", req.TakeOver, $"{ClockOffsets.Kind}/{req.Key} <- {khz}; preserve effective Core curve")
                ?? throw new InvalidOperationException("a previous application did not complete; run --recover before tuning");
            var steps = new List<object>();
            bool touched = false;

            OwnedWriteResult Set(string kind, string key, string value)
            {
                var path = WriteRegistry.Find(kind)!;
                var prepared = path.Prepare(hw, gpu, key, value, new WriteOptions(CurveFollows: kind == ClockOffsets.Kind));
                if (prepared.Plan == null) throw new InvalidOperationException(prepared.CannotWrite);
                // An intermediate return to an original is not a committed give-back. The final coupled proof can
                // still fail and roll back to a tuned state, including a NoChange VF setter after native mirroring.
                var r = writer.Write(gpu, prepared.Cell, prepared.Plan, retainOriginal: true);
                touched |= r.Tx.Outcome is TxOutcome.Applied or TxOutcome.Failed;
                steps.Add(new { kind, key, requested = value, before = r.Before, after = r.After,
                    transaction_outcome = r.Tx.Outcome.ToString(), transaction_restore = r.Tx.Restore.ToString(), detail = r.Tx.Detail });
                if (!r.Tx.Succeeded) throw new InvalidOperationException($"{kind}/{key}: {r.Tx}");
                return r;
            }

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

            try
            {
                bool p0Changes = entry.DeltaKhz != khz;
                bool curveNeedsRepair = effectiveTarget is { } targetKhz && before.Points.Any(p => Math.Abs(VfEffectiveCurve.Displacement(p) - targetKhz) >= 1_000);
                if (p0Changes || curveNeedsRepair)
                {
                    writer.ClaimOriginal(vfCell, before.Control);
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
                var after = VfReader.Read(hw, gpu);
                Verify(after);
                writer.ReleaseIfRestored(gpu, ClockOffsets.Cell(domain));
                writer.ReleaseIfRestored(gpu, VfWrites.Cell(after));
                scope.Complete();
                return Ok(new
                {
                    success = true, curve = VfOffsets.Encode(after.Offsets()), effective_curve_verified = true, steps,
                    // One logical before/after per cell, including the V/F dependency when its bytes did not change.
                    // A later write in this trial can fail: its rollback must restore P0 first and this curve last.
                    applied = new object[]
                    {
                        new { kind = ClockOffsets.Kind, key = ClockOffsets.Key(domain), before = entry.DeltaKhz.ToString(CultureInfo.InvariantCulture),
                            after = khz.ToString(CultureInfo.InvariantCulture), outcome = entry.DeltaKhz == khz ? "NoChange" : "Applied", coupled = true },
                        new { kind = VfWrites.Kind, key = VfWrites.CoreKey, before = VfOffsets.Encode(before.Offsets()),
                            after = VfOffsets.Encode(after.Offsets()), outcome = p0Changes || !before.Offsets().SequenceEqual(after.Offsets()) ? "Applied" : "NoChange", coupled = true },
                    },
                });
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or OverflowException or IOException or UnauthorizedAccessException)
            {
                string? rollbackError = null;
                try
                {
                    if (touched)
                    {
                        // A P0 restore can itself wipe/mirror V/F controls. Always restore P0 before the captured curve,
                        // including when the P0 transaction says its own bytes were already restored.
                        Set(ClockOffsets.Kind, ClockOffsets.Key(domain), entry.DeltaKhz.ToString(CultureInfo.InvariantCulture));
                        Set(VfWrites.Kind, VfWrites.CoreKey, VfOffsets.Encode(before.Offsets()));
                    }
                    if (Transaction.FirstDifference(p0.Raw, Pstates20Reader.Read(hw, gpu).Raw, Pstates20Layout.StatusRanges) != null)
                        throw new InvalidOperationException("the original P0 controls did not return");
                    var restored = VfReader.Read(hw, gpu);
                    if (!restored.Offsets().SequenceEqual(before.Offsets()) || VfEffectiveCurve.Mismatch(before, restored) != null)
                        throw new InvalidOperationException("the original effective curve did not return");
                    // Settle only after the whole rollback proof. Originals remain on disk throughout every
                    // intermediate return and native side effect; no post-effect snapshot replaces them.
                    writer.ReleaseIfRestored(gpu, ClockOffsets.Cell(domain));
                    writer.ReleaseIfRestored(gpu, VfWrites.Cell(restored));
                    scope.Complete();
                }
                catch (Exception restore) when (restore is InvalidOperationException or InvalidDataException or OverflowException or IOException or UnauthorizedAccessException)
                {
                    rollbackError = restore.Message;
                    // Keep both debts even if an individual transaction released a record before the coupled proof failed.
                    writer.ClaimOriginal(vfCell, before.Control);
                    writer.ClaimOriginal(ClockOffsets.Cell(domain), p0.Raw);
                }
                return new BrokerResponse(false, $"effective P0/V/F write failed: {e.Message}; " + (rollbackError == null
                    ? "the previous P0 and effective curve were verified restored"
                    : $"rollback could not be verified ({rollbackError}); run recovery and read the card before tuning"),
                    Element(new
                    {
                        success = false, effective_curve_verified = false, restored = rollbackError == null, steps,
                        applied = new[] { new { kind = ClockOffsets.Kind, key = ClockOffsets.Key(domain),
                            outcome = nameof(TxOutcome.Failed), restore = (rollbackError == null ? RestoreState.Restored : RestoreState.RestoreFailed).ToString(),
                            state_known = rollbackError == null, detail = e.Message } },
                    }), BrokerCode.Failed);
            }
        }
    }

}
