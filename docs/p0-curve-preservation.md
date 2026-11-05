# P0 offsets and effective V/F preservation

This fix is based on upstream `0.4.1`, commit
`e42d0e77f3a37b781bc562bb57163c2a9da71df5`. Upstream's driver-derived limits
and chat confirmation behavior remain in force, alongside the existing
global-plus-point firmware validator.

The original implementation and native validation used
`fix/p0-curve-preservation` at `078c8ab`, based on the now-abandoned
consent-feature branch at `38ef607`. That branch remains a backup. This
upstream-based version carries only the preservation changes and their test
support; it has no dependency on that feature's policy modes or scope protocol.

## Software evidence

The first six fake-driver regressions failed four cases on the baseline:
an isolated core write lost the effective increase in an overlapping model,
an existing curve did not receive the requested effective change in that
model, and daily/ownership expectations failed under both models. These
tests do not establish which native call caused the reported hardware loss.

`EffectiveP0Tests` uses the real broker with two deliberately hypothetical
driver models. Both read the raw P0 value dynamically:

- Additive: effective frequency = stock base + P0 core + point control.
  A P0 request clears point controls.
- Overlapping: effective frequency = stock base + point control. A native
  core request mirrors its target into point controls; memory clears them.

The tests isolate core and memory, check all points of a nonuniform shape,
raise and lower core targets, and return to originals. They cover refused
core/memory/curve writes, failed native and effective readbacks, failed
rollback, live-owner refusal, dead-owner adoption, thermal base changes,
combined-budget refusal, a later failure in a multi-write trial, and daily
save/recovery/reapply entirely against fakes. MCP tests reject a broker's
success when effective frequencies did not change, and accept matching
effective results even when control bytes changed.

Historical validation of the combined feature/fix branch after the review
fixes on 2026-10-26 (not a test count for the upstream-based branch):

```
dotnet test tests/Clocktail.Tests -c Release --no-restore --nologo
# 1049 passed, 0 failed, 0 skipped
dotnet build Clocktail.slnx -c Release --nologo
# 0 warnings, 0 errors
```

## Write and ownership behavior

A preserving P0 write holds one GPU lock and sentinel across original
capture, native write, any curve repair, verification and rollback. The V/F
original is on disk before the first possible P0 side effect. An existing
journal original always wins.

For core, the target is the pre-operation effective curve shifted by the
change in the raw P0 target. For memory, the effective core curve is kept.
After the native call, regional readback determines the missing change:
Clocktail adjusts point controls by that observed error, rather than adding
the requested offset to both representations. The correction must match
clock-step quantization; only one repair is attempted, through the normal
V/F path and bounds. Both the P0 field and effective frequencies must then
verify. Regional effective-minus-base comparisons avoid converting thermal
base changes into saved offsets; the reporting tolerance is under 1 MHz,
smaller than a full 7.5 MHz clock step.

Legacy bases are derived from effective frequency minus stored controls.
They cannot identify the P0 contribution independently. Legacy writes
restore the prior controls and verify direct effective frequencies; they
fail and roll back when that result cannot be established. Adaptive repair
from a derived legacy baseline is deliberately unsupported.

V/F ownership remains while a P0 cell is held, even if point controls equal
their original: restoring P0 can change those controls again. Daily capture
retains this explicit V/F dependency, including a zero curve. The trial
records the verified final control encoding. Recovery keeps P0-first,
V/F-last ordering; trial rollback uses that order too and verifies the
whole coupled result. Unverified rollback retains originals and the
sentinel, and reports an unknown write outcome. Verified rollback reports
that nothing remains written.

## Startup evidence and daily restoration follow-up

The supplied `tuning-log.md`, `profile.json` and `ownership.json` under
`%LOCALAPPDATA%/Clocktail/pci-2c0210de-89de1043-00002c02-b01-s00/` were read
without modifying them. At 2026-10-25 22:04:05 Europe/Copenhagen (21:04:05Z),
the installed `0.4.0-fork.7a0bfd8-prompt1` build logged core 0 -> +240 MHz,
then aborted memory before its setter because all 127 V/F offsets were
nonzero. Power 450 W and XBAR +300 MHz applied. The saved daily contains
core +240, memory +3000, power 450 W and XBAR +300, without `vf/core`.
The startup journal holds power/core/XBAR originals and no V/F original.
The supplied subsequent readback reports +240 on all V/F points and memory
0, with no temporary trial or new recorded reset.

This narrows this startup failure: a successful native memory write did not
produce the nonzero controls that its guard refused. Core's native side
effects are consistent with that state. It does not retrospectively isolate
the original explicit-setter sequence or prove every native intermediate.
The earlier successful save/readback and Speed Way result remain separate
observations. The later coordinated native validation is recorded below.

The first fix, `9d96bd0`, still missed this path. Of the first ten new
startup regressions, seven failed on it: the overlapping model could apply
memory but lose effective core tuning, reapplication could give back the
generated curve, and partial-reporting coverage was incomplete. Three
further regressions exposed matching raw P0 values masking a lost curve or
retaining a temporary point-curve trial during back-to-daily.

Daily restoration now uses the coupled path for profiles without an
explicit V/F entry. A saved P0 core target with no point curve means a
uniform effective displacement from the independent regional base; it is
verified and repaired even if raw P0 already matches. Memory then preserves
that verified effective curve. A memory-only profile preserves the captured
original core shape, giving back any unkept point-curve trial. An explicit saved V/F entry remains authoritative and is written
last. A derived legacy base cannot prove the implicit uniform target, so
that core group fails actionably rather than guessing; an explicit verified
curve is needed for that legacy profile.

The generated V/F state remains an implicit daily dependency across startup,
resume and after-reset application. This does not rewrite the saved profile
during application. A later explicit keep captures its verified V/F entry.
Dropping a failed explicit core/VF group still restores its true original;
it is not reclassified as memory's implicit curve. Native-generated curves
with a captured stock original are attributed to Clocktail. A pre-existing
external curve retains its actual original and attribution.

Partial responses retain `success=false`, identify omitted groups, and
include requested/current values and readback availability. Logs say
`Daily profile partly applied` for a completed degraded application and
`Daily profile application incomplete` otherwise. They no longer assume an
omitted setting's original was stock. An unverified coupled rollback stops
later groups and reports an unknown outcome with the sentinel retained.

`StartupP0Tests` adds twenty fake-driver cases covering the supplied profile,
both control models, repeat application, a matching raw target with lost
effective tuning, back-to-daily from a point-curve trial, partial and unknown
outcomes, nonstock originals, attribution, explicit curves, legacy refusal,
and software-only keep/recovery/reload.

## Review regressions addressed on 2026-10-26

The review of `d62ac1e` supplied three additional fake-driver reproducers.
All twelve initial in-repository variants failed on that tip. They exposed
premature ownership release, a memory-only daily retaining a newer curve
trial, and a full-status prerequisite blocking an ordinary return to zero.

Coupled writes now retain both cells' originals through every intermediate
write and rollback. Only after the complete effective/P0 proof succeeds are
eligible originals released, P0 first and V/F second. Returning to zero and
then failing the final proof therefore leaves the original records intact
while rolling back to the previous tuning; a NoChange rollback setter cannot
lose the V/F original. Subsequent recovery remains possible.

Only an implicit **core** target protects a generated daily curve during
profile restoration. A memory-only daily restores the captured original
curve before preserving it for memory; it does not adopt a later temporary
point curve. Tests cover both zero and nonzero originals under both models,
including the trial leaving the record and recovery of the true original.

Ordinary `ClockOffsets.Apply`, broker `OpClock`, and raw P0 `OpWrite` now
capture the coupled original from validated Info/Control topology and
offsets, without requiring effective Status. The same control snapshot
supplies the firmware budget. Registry preparation carries readable coupled
originals to the transaction's write-ahead callback. An existing original
still wins; an unreadable control never becomes an invented original. The
existing unknown-budget rule still bounds core to a move toward zero and
retains existing debt. Adaptive P0 preservation still requires full curve
readback, and V/F writes still require full validation and firmware bounds.

`P0ReviewRegressionTests` adds twenty-two cases. Besides the initial twelve,
they cover release only after a successful final proof, preservation and
recovery of previously recorded nonzero originals, allowed zero returns and
refused moves away from zero with unreadable controls, and continued refusal
of V/F writes when full Status is invalid. All 1,035 software tests and the
Release solution build passed. These findings and fixes remain software
evidence; they do not establish native semantics or hardware stability.

## Second-review topology regression addressed on 2026-10-26

The second review of `03de151` confirmed that the three preceding findings
were fixed, but exposed another budget failure: matching Info/Control masks
could expose readable nonzero Core offsets while using an unsupported
ownership topology. The ownership reader rejected that topology after
parsing the offsets, and its caller mistakenly downgraded the whole budget
to unknown. A return-to-zero request could then bypass the nonzero-curve
wipe guard and erase an external curve without a captured V/F original.

Budget data and ownership capability now remain separate results of the
same Info/Control read. Validated offsets stay known when the mask or Core
sequence cannot encode a recoverable original. Nonzero offsets continue to
trigger the existing wipe refusal. Even with zero offsets, a changed P0
request is refused if those readable controls cannot be recorded for
recovery. An equal-value request remains NoChange. Only controls that cannot
be read/validated use the existing unknown-budget fallback; its bounded
core return-to-zero behavior remains covered by the prior review tests.

`P0TopologyRegressionTests` adds fourteen cases. Twelve changing-write cases
failed on the reviewed tip. They exercise actual Apply/OpClock/OpWrite with
unsupported masks and noncontiguous Core entries, each with zero and nonzero
offsets. They now refuse before any fake setter runs, retain the exact
control buffer, and preserve known budget offsets separately from the
unavailable ownership snapshot. Two equal-value cases remain permitted
without writes. All 1,049 software tests and the Release solution build
passed. The new case is fake-driver evidence, not an observed native driver
transition. No hardware operations, commits or pushes were performed for
this second-review follow-up.

## Upstream-based review fixes on 2026-10-27

The independent review of `3f8d920` found two missing combinations. Of the
first 22 new regression cases, 14 failed before these corrections. Three
additional cases then extended failure settlement coverage to memory.

Ordinary `ClockOffsets.Apply`, broker `OpClock`, and default P0 `OpWrite`
now share a failure-settlement path. Before releasing ownership or clearing
the sentinel, it verifies the P0 rollback and the captured Info/Control
layout, restores any changed Core point controls to their pre-operation
values, and verifies P0 again. The same GPU lock remains held through the
transaction, coupled rollback and ownership decision. This path uses no
V/F effective Status reads, preserving the existing return-to-zero behavior
when Status is invalid. Restoring a captured snapshot selects only changed
Core offsets in the native request; full curve tuning retains its normal
Status and firmware prerequisites.

An unreadable or refused coupled rollback is reported as `RestoreFailed`,
with `state_known=false` where exposed and an unknown write outcome. Both
originals and the blocked sentinel remain available for recovery. A failed
write whose P0 and curve controls returned verifiably reports nothing left
written. Existing journal originals always take precedence over a later
pre-operation snapshot.

An implicit daily core target restores a uniform absolute displacement.
Its preflight no longer adds that target to the old point controls that
restoration will discard. The raw P0 target still passes the driver's rules;
the actual replacement controls still pass individual and global-plus-point
firmware checks. Relative shape-preserving requests retain the old-curve
preflight. A valid +90 MHz daily therefore replaces an existing +240 MHz
point curve under a +300 MHz limit, while an actually excessive replacement
or relative request continues to fail.

`P0FailureSettlementTests` adds 18 cases covering both control models,
all three ordinary entry points, core and memory side effects, refused and
unreadable control rollback, lock/sentinel/original retention, recovery,
and preservation of existing nonstock originals. `P0DailyBudgetTests` adds
seven cases for replacement budgets, repeated restoration, both models,
negative targets and continued actual/relative budget refusal.

The full .NET software suite passes 980 cases, with zero failures or skips.
No hardware operations or GPU tests were performed for these review fixes.

## Coordinated native validation of the original branch

With explicit user authorization, the original combined build at `078c8ab`
was validated on an ASUS ROG Astral RTX 5080 OC, driver 621.01 and VBIOS
98.03.3b.c0.70 on 2026-10-26. The diagnostic derivative captured reads around
native P0 writes; the normal installed build was `0.4.0-p0.078c8ab`.

Sixteen idle states covered isolated core and memory changes, raising and
lowering the core target, preservation of a nonuniform curve, restoration
of the true originals, and the saved +240 core / +3000 memory / +300 XBAR /
450 W daily. All 127 effective displacements and the ownership journal
were checked. No manual +240 point-curve workaround was needed.

On this card, a core P0 write mirrored its target into the point controls.
With the curve set aside, native core and memory P0 writes produced the
uniform raw core target; preservation repaired a custom shape afterward.
Raw core +240 and point controls +240 represented an effective +240,
not +480. These observations do not establish every card's native semantics.

The normal build then passed active-daily broker startup restoration and a
user-managed cold boot. Both automatically restored memory +3000 and all
127 effective core offsets +240 from the original four-entry daily without
an explicit `vf/core` entry. Generated offsets had the captured stock V/F
original and Clocktail ownership; no trial or blocked sentinel remained.

One authorized Speed Way run completed at 10,890 points / 108.90 FPS.
Settings and ownership were unchanged afterward, with no new recorded
reset or crash. A single run does not establish broad stability or a
performance gain. Native fault injection and failed-rollback drills were
not performed; their coverage is software-only.

## Validation after rebasing onto upstream 0.4.1

The upstream assessment reproduced the exact startup memory refusal and
the MCP effective-readback errors using fake drivers. The rebased branch
retains the domain regression suites and moves the two MCP readback tests
into `P0EffectiveReadbackTests`, independent of the abandoned policy tests.
A small internal `Card` injection constructor keeps those tests entirely
in memory without opening a GPU manager, native library or broker pipe.

Validation on 2026-10-27 after the upstream rebase:

- .NET regression suite: 955 passed, 0 failed, 0 skipped.
- Release solution build: 0 warnings, 0 errors.
- Installer unit tests: 11 passed, 0 failed.
- `git diff --check`: passed.

The test count excludes the abandoned feature's policy tests. The crash-log
timestamp assertion now uses the same invariant format as the production
log, so its seven theory cases also pass under Danish culture.

The rebase itself performs software validation only. The preceding native
results belong to the original combined build and do not validate a new
installed upstream-based binary. Deployment and hardware revalidation
require separate user authorization.

Historical journal entries with a wrongly captured original cannot be
reconstructed from the current card alone; this fix preserves existing
originals rather than guessing replacements.

## Game-recovery integration after upstream 0.5.0 (2026-10-30)

Upstream's guarded game session treated every omitted daily cell as its ownership
original. A daily with P0 core +240 MHz and no explicit V/F entry could therefore
restore correctly but fail the game's final check: the original curve was zero,
while the driver-generated daily controls were +240 MHz.

Game snapshots now include independently reported regional effective-minus-base
frequency offsets. For an enabled daily with a core P0 target and no explicit V/F
entry, game recovery verifies the raw P0 target and every effective curve point
(with the coupled restoration's sub-MHz tolerance), instead of comparing generated
V/F controls with the pre-tuning original. Other cells still require their expected
values. Explicit curves, suspended profiles, and memory-only profiles retain their
raw-control checks. Missing independent status, including legacy derived bases,
cannot prove an implicit daily curve.

The same proof is required when starting a game on that daily. Interrupted recovery
can recognize an already-restored generated curve; an unrelated shape cannot gain
that exemption. Effective offsets participate in snapshot and completion-receipt
comparisons. Older recovery records can still return to daily using fresh proof;
older completion receipts without effective evidence are not silently accepted as
matching a newly measured regional snapshot.

`GameImplicitDailyTests` adds 22 fake-data cases covering both control models,
recover/failed/reset completion paths, partial or missing readback, retry after a
lost reply, explicit/suspended/memory-only profiles, old recovery records, receipt
replay, startup proof, and rounding. Validation: 1,152 .NET tests and 11 installer
tests passed; Release solution build had zero warnings/errors. No GPU tests,
broker restarts, installation, or game workloads were performed.
