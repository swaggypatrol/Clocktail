# Game test review and completion record

## Decision

The original planner / runner / adapter split remains. Its first review exposed real defects, not just style issues.
Stopping there with warnings about target identity and restoration was insufficient. Those code-level gaps are now
implemented in the card host and covered by offline regressions. **First hardware runs: 2026-10-28** (below and
[game-test.md](game-test.md#hardware-runs-2026-10-28-reference-rtx-5090-at-000001000)).

## Behavior corrected

The first review reproduced six offline failures: NaN logging throwing before classification; stop ignored during hang
grace; missing thermal checks during startup/load; missing telemetry surviving; short-step time overcredit; and a wall
deadline enforced only between steps. Shared safety checks, bounded credit, raw HDR-text logging and telemetry freshness
now cover these paths. Malformed HDR is not fabricated into a NaN, infinity alone is not failed, and known allocation
errors are not recorded as overclock crashes. DRS bounds/target and depth of field are read back.

The window now covers 90 % of **area** (90 % per axis was only 81 %). The launch explicitly selects single-player/cheats.
Sampler native calls/disposal stay on their own thread; a blocked join never unloads a native library under an active call.
Rcon requests have fresh markers and monotonic timeouts. Short transition windows no longer borrow following steps' clocks.

## Previously deferred gaps, now implemented

| Gap | Implementation |
|---|---|
| Model/ordinal fallback and guessed PCI slot | `GpuManager.Open/Choose`, `GpuTarget`, `DisplayInventory`: unique supported GPU or exact `CLOCKTAIL_GPU_PCI`; PnP/NVML/NVAPI address/device/subsystem agreement, capability 12.0, no fallback |
| No independent CUDA/game-device proof | Contained `clocktail-lab game-info`: PnP first, CUDA PCI lookup/address/capability readback without a context, then unique matching Vulkan RT device without a logical device |
| Curves read by the client, stale preflight power | The job spec carries no restoration values. The card host verifies identity and reads matching snapshots under the actual measurement lease before launching the game |
| Only acknowledged values remembered | Durable `game-recovery.json` before writes, including exact pending target values and a unique operation marker. Lost replies and interrupted writes retain their obligations |
| Success inferred from a write acknowledgement | Independent post-write snapshot of controlled and unrelated measured cells; full baseline/daily verification before completion |
| Reset counts attributed to every adapter | Provider/driver/PnP attribution ignores only resets provably from another vendor's driver; a doubtful or unreadable event is answered like any reset, because recovery only lowers settings. Game tests refuse multiple NVIDIA adapters instead of guessing |
| A reset blamed on the wrong trial after a worker dies | Data points retain `TrialAt`; pending operation markers recover the exact candidate identity even if its acknowledgement was never written |
| Recovery before the game is gone | Suspended launch, containment and durable PID/start registration before resume; the host refuses restoration while that process is alive or uncheckable |
| Interrupted restoration stranded the record | A persisted restoring phase permits only recognized partial progress, including interruption between hardware restoration and session-metadata completion |
| Lost final reply or missing record misread as success | Durable completion receipt before deletion; replay requires a fresh matching hardware/session snapshot. No baseline/receipt means no verified success |
| Recovery only on the next manual test | Startup/background recovery takes the card mutex and judge lease, waits for live jobs/processes, records pending resets before daily application, and retains records on every verification failure |
| Fan hand-back undone at the end | A recognized service fan hand-back (its agent gone quiet) falls back to verified daily, never reinstates fixed fans and claims the original trial passed |

These paths use the existing ownership journal, write registry, transaction checks and firmware limits. The protocol
exposes typed operations, not arbitrary driver slots. New brokers must be installed with matching MCP/lab executables;
old brokers are refused before game launch. No installation was performed during this work.

## Code map

- `src/Clocktail.Hw/GpuTarget.cs`, `src/Clocktail.Core/Gpu/GpuManager.cs`: target policy, metadata and guarded selection.
- `src/Clocktail.Broker/WindowsGameAccess.cs`: real card-host verification and snapshots.
- `src/Clocktail.Domain/Game/GameSession.cs`, `Broker/BrokerDispatcher.Game.cs`: durable state, guarded mutations,
  restoration, exact-trial checks and idempotent completion.
- `Platform/ResetAttribution.cs`, `Platform/SystemLog.cs`, broker event subscription: scoped reset evidence.
- `tools/Clocktail.Lab/Game/`: script, state parser, runner, process/desktop/rcon control and background job.
- `tools/Clocktail.Mcp/TuningTools.Game.cs`: request validation and background start, with no client-side curve reads.

## Validation

- Solution Release build: **0 warnings, 0 errors**.
- Full offline .NET regression suite: **1013 passed, 0 failed, 0 skipped** (944 at the initial handover).
- Installer tests: **11 passed**.
- `build/install-local.ps1` was exercised in an isolated sandbox with `cmd.exe` standing in for old programs: plan mode,
  replacing a running (locked) server, an unchanged second run, refusal while a lab job runs, refusal of an incomplete
  bundle, and rollback (three consecutive full passes). The published broker's installer refuses a bundle without the lab
  before any prompt. The elevated service installation itself was **not** run.
- Read-only checks on the reference machine, with the new code: PnP lists exactly one display adapter (the RTX 5090 at
  `0000:01:00.0`); the service's identification (`GpuManager.Open`) returns it with the same stable ID as before
  (`pci-2b8510de-205710de-00002b85-b01-s00`); and all 150 real driver-reset events of the last 60 days render and attribute
  correctly. No workload, no write.
- Matching self-contained single-file broker/MCP/lab/CLI builds publish to `out/game-test/`. The published lab's
  `game-plan --seconds 60 --seed 42 --levers none` preview runs offline; it does not inventory or load a GPU.
- `GameSessionTests` exercises the actual durable transaction with fake hardware/transport: stale baselines/session
  requests, identity/lease refusal, power limits, locks, independent readback, lost replies, interrupted writes/restores,
  exact reset-trial identity, foreign-card records, external modifications, late reset evidence, live game-process guards,
  corrupted records and completion receipts. It does not load a GPU library.
- A Windows **CPU-only** process drill uses `cmd.exe /d /c exit 0` to verify suspended creation, containment, resume, exit
  and cancellation before resume. No game is started. This also caught and corrected shell argument quoting.
- The real loopback UDP transport is tested against a local fake responder for stale/fresh query handling. Sampler
  blocking/startup failures use fake transports. The broader existing domain/broker tests remain green.

**Actual GPU testing in this work: none.** No Quake launch, hardware inventory command, curve/power/fan/lock write,
real TDR/recovery drill, installation or commit. Earlier read-only inventory belongs to the initial implementation's
record, not a claim of new hardware acceptance.

## Installation record (2026-10-28, about 17:34 local)

Installed on the reference machine (RTX 5090, `0000:01:00.0`) with `build/install-local.ps1` at the person's request. This is an
**installation**, not a hardware acceptance run: no game was started and no game-test lever was written.

- Replaced: `clocktail-mcp.exe`, `clocktail-lab.exe` and `Clocktail.Broker.exe` in `%LocalAppData%\Programs\Clocktail`, and the
  service in `C:\Program Files\Clocktail` (broker, plus `clocktail-lab.exe`, which the broker's device proof runs from there).
  The logon task `ClocktailBroker` was re-registered and started. Backups: `%LocalAppData%\Clocktail\install-backup-20261028-173439-394`
  (the rollback to the previous service) and `...-173825-804` (agent files only).
- Before the install: no session, no trial in force, no lab job, no Quake. The old service held its cells; the new one recovered
  them at start and re-applied the 9-write daily profile ('配置一') within 10 s, as it does at every start. That restart is the only
  card write this installation caused.
- Checked afterwards, read-only: all five installed files are byte-identical to the tested bundle; the new service answers
  with `game_protocol` 1; the log shows no error; `card_settings` before and after differ only in live readings (curve, power
  wall, memory offset, boost, ratio, rails, locks, daily profile, journal holds and trial are identical, and the daily profile
  matches the card); `npm verify` passes (53 tools, `game_test` listed); through the pipe the running service returns the verified
  target, refuses every guarded game operation without the caller's `judge.lock` before touching hardware, and answers `no_gpu`
  for another GPU id.
- Found while installing: the agent folder kept a stale `Clocktail.Broker.exe`, and the MCP server's `install_service` runs the
  broker beside it, so an agent updating an "older" service would have put the old one back. The script now replaces all three files in
  the agent folder, the sandbox suite checks it (20 checks, repeated), and the real folder was fixed with `-NoService`.
- Not done: no commit; no `game_test`. The first real acceptance run still needs the person's go-ahead. (The runs followed that evening: see "Hardware runs" below.)

## Hardware runs (2026-10-28, about 17:51-18:12 local)

With the person's go-ahead, three runs on the reference RTX 5090 at `0000:01:00.0`, same seed, default patterns and levers:
the known bad curve failed in `leap` at 87.5 s (device lost, then a target reset), the same curve lowered from its freeze
voltage failed at the same step (83.5 s, a reset), and the whole curve 15 MHz lower survived 462.5 s until `lab_stop`
(68 heavy → light jumps, 59 leapt; wall and fan levers applied and given back). Both resets were recorded against the
trial in force and the daily profile went back with no recovery error. Details: [game-test.md](game-test.md).

This is real GPU validation of the launch, foreground, rcon, scheduled steps, power/fan levers, stop/restore and reset
recovery paths. It does not cover `gpu=none`, `boost`, `restart`, an interrupted worker or a full run on a known-good
curve; those remain open. Offline validation above is unchanged.

The test deliberately refuses unsupported/ambiguous hardware and unreadable state. It does not add unmeasured V/F layouts.
The centre-pixel check is not full image validation, near-top time is a telemetry estimate, and external overclock tools
must remain closed: a cooperative lease cannot physically prevent another program from accessing the driver.

See [game-test.md](game-test.md) for the protocol, deployment requirements, supported paths and acceptance sequence.
