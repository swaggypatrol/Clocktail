# Unattended Quake II RTX test (`game_test`)

**Status: validated offline, and run on the reference RTX 5090 (2026-10-28, below).** It reproduced a known bad curve's
hang and survived the same curve 15 MHz lower. A survival is evidence, never a proof of stability.

**It is optional.** It is the stand-in for the person's games while nobody is at the PC. When the person will play, their own
games under `live_start` judge better, light settings included. The other judges stay as they are: nvgwls (`stress_test`) and
the lab's `solo`/`leap` for the V/F curve; the lab's bandwidth with the core's real clock not falling for XBAR, its ratio,
the voltage boost and the memory.

## Purpose and use

The compute lab does not exercise every graphics workload. This test drives a real Vulkan path tracer through heavy/light
transitions, bursts and rendering changes before the person's own games have the final word.

1. Install Quake II RTX (free on Steam; its bundled shareware content is enough).
2. Open a Clocktail session; use `unattended=true` when the person is away. Put the trial to test in force, or test daily.
3. Call `game_test minutes=20`. `lab_status` follows the background job; `lab_stop` requests its stop.
4. Read the outcome **and recovery status**. Nothing is kept or marked stable automatically.

The game takes the foreground. Its client area covers at least **90 % of primary-screen area**, not merely 90 % on each
axis (81 % area). `windowPct=90..100` is a per-axis lower bound; sizes are raised as needed to meet the area requirement.
No desktop-resolution change, input injection or modification of the person's game configuration is used.

Options: `minutes=1..240`, `patterns`, `gpu` (card levers), `windowPct`, `maxScale=100..200`, `seed`, `maxTempC` (1..120 C), `note`.
Defaults: 10 minutes, all patterns except renderer restart, power/fans, random repeatable seed, and no thermal stop of the
test's own (with no `maxTempC` it leaves temperature to NVIDIA's protection). Maximum internal
resolution defaults to 200 % with at least 23 GiB VRAM, 150 % with at least 15 GiB, otherwise 125 %, from the locked snapshot.

## Installation

**The broker (service), the MCP server and the lab are updated together.** The service now owns the baseline, mutations,
process registration and recovery. An old service is refused before the game starts; replacing only the MCP server and the
lab is not enough. `npx clocktail setup` installs the *published* release, so use the local installer for a local build.

1. Build the bundle (the four exes land in `out\game-test`):
   `dotnet publish <project> -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=0.6.0 -o out\game-test`
   for `src\Clocktail.Broker`, `tools\Clocktail.Mcp`, `tools\Clocktail.Lab` and `src\Clocktail.Cli`; for `tools\Clocktail.Mcp` add
   `-f net10.0-windows` (it is built twice: `net10.0` for the tests, `net10.0-windows` with the `curve_editor` window).
2. Stop any lab job and close Quake II RTX. Agents may stay open (their old server is moved aside and keeps running).
3. `powershell -ExecutionPolicy Bypass -File build\install-local.ps1 -Plan` shows what would change and changes nothing.
4. `powershell -ExecutionPolicy Bypass -File build\install-local.ps1` backs up what it replaces to
   `%LocalAppData%\Clocktail\install-backup-<time>`, installs the three agent-folder files (server, lab and the service installer
   that `install_service` runs, so an older copy cannot put the old service back), installs the service (one Windows admin prompt),
   and waits until the *new* service answers (`Clocktail.Broker.exe status` shows `game_protocol`; the old one does not).
5. Restart your agent(s). A trial in force ends when the service restarts: the daily profile goes back on.
6. Undo: `build\install-local.ps1 -Rollback "%LocalAppData%\Clocktail\install-backup-<time>"`.

The service installer also copies `clocktail-lab.exe` into its protected folder (the broker runs the device proof from
there) and refuses an incomplete bundle before any prompt or any change. If the service does not start after an update,
the reason is in `%LocalAppData%\Clocktail\broker.log`: identification now fails closed instead of guessing a GPU.

Installing is not a hardware acceptance run: it starts no game and writes no card setting beyond what the service does at
every start (it puts the daily profile on).

## Target contract

- Windows PnP metadata is read before vendor identification. Selection is the only supported GPU, or the exact address in
  `CLOCKTAIL_GPU_PCI`. No model-name preference, ordinal choice or fallback to another GPU.
- Supported identity: NVIDIA compute capability 12.0. NVML's whole PCI address/device/subsystem IDs, PnP and the NVAPI
  handle's bus/slot/device/subsystem IDs must agree. Domain 0 and function 0 are required; other functions cannot be proved
  through this NVAPI selector and are refused rather than guessed.
- The card host starts a contained `game-info` child. It checks PnP, uses CUDA's PCI lookup, reads the address/capability back,
  and inventories Vulkan. **No CUDA context or Vulkan logical device** is created. Exactly one RT-capable Vulkan device
  must match the address, NVIDIA vendor and PCI device ID. SLI is disabled in the game.
- Game tests currently require exactly **one NVIDIA display adapter**, even if another NVIDIA adapter cannot ray trace:
  nvlddmkm events do not reliably identify an adapter. Other-vendor adapters may remain. Recovery only gives settings back,
  so the service answers every reset that is not provably another vendor's driver (and a doubtful or unreadable event is
  answered, never ignored); the job counts it the same way. Only a reset provably from another vendor's driver is ignored.
- Only the card host reads the game's V/F curve, through `NvClient` declared structures, guard bytes and loss latch. The
  existing Clocktail parser and revision-1 setter range must accept it before a workload. Unsupported layouts are refused;
  this feature does not add unmeasured compact-layout support. Non-5090 models require a one-time confirmation in a window.

## Card-host transaction

The job takes its card mutex and `judge.lock`, then asks the broker to begin a guarded game session. The broker serializes
that operation with all its other operations. **The job spec contains no curve, power ceiling or restoration values.**

The host performs identity/probe checks, reads the actual settings twice, and refuses a moving/inconsistent snapshot.
The curve, board requests (including F8), clock locks, fan control, standard tuning cells, held cells and daily-profile
cells form the baseline. Trial/session identity and the actual trial curve must agree. Existing locks cannot be replaced
by Boost Lock. Other GPU work, unsupported state, and TdrLevel 0/1 are refused.

The host persists `game-recovery.json` before any card change. It records the selected stable ID/address, original and
expected settings, exact trial identity, authorized levers and pending writes. Every lever rechecks the actual state,
uses existing broker write paths and their ownership/setter guards, independently reads back the result, and checks that
unrelated measured cells did not move. A unique operation marker links an interrupted write to its exact trial/crash point.
Periodic checks detect changes to measured settings; external overclock controllers must remain closed.

The game is created **suspended**. It is contained and its PID/start time persisted by the host before it can execute game
code. Failure to contain or register it terminates it without resuming. Restoration is refused while that recorded process
is alive or cannot be confirmed gone.

At completion:

- Normal completion/stop restores only the controls this job attempted, verifies the full baseline, then restores the
  starting session metadata. The service handing a fixed fan duty back (its agent gone quiet) falls back to verified daily instead of reinstating fixed fans.
- A plain failure is recorded only against the same trial with matching measured settings. A target reset is recorded by
  the service; the job releases its lease so recovery can proceed. The exact `TrialAt` crash point and daily readback are
  required before reset recovery is called complete. No timeout erases pending evidence.
- Recovery records survive lost replies, partial restoration and worker death. Startup and background recovery take the
  card mutex/lease, wait for any recorded game process to end, and refuse foreign-card records or unrecognized changes.
- A durable completion receipt is written **before** removing recovery obligations. Retrying a lost completion reply
  requires fresh hardware/session verification. A missing record alone is not proof that restoration succeeded.
- A recovery error downgrades a survival to unjudged and forbids marking stability. No automatic clock increase is performed.

## Rendering script

Seeded patterns: `heavy`, `leap`, `pulse`, `burst`, `ramp`, `drs`, `cuts`, `toggles`, `wide`, `sky`, `photo`, `play`, `restart`.
They mix sustained path tracing, 0.5-3 s heavy/light alternation, capped-frame bursts, idle-to-heavy spikes, resolution
sweeps/DRS, camera cuts, feature toggles, panoramas, sunlight changes, accumulated photo rendering and scripted combat.
Live maps explicitly use single-player mode, god mode and supplied weapons. Restart switches the ray-tracing path and is
opt-in. Every rendering option used by a step, including DRS bounds/target and depth of field, is read back.

Card levers run more slowly: power stays between the board minimum and the locked starting cap; fans go to 100 %, then
50 % until min(76, maxTempC - 6) C (76 C with no `maxTempC`), then auto. Boost Lock is opt-in. Starting values come from the host, never the client.

A second is credited only while the game is foreground, answering, drawing, progressing and holding the requested
settings. Credit is capped to the remaining step/run duration. Startup, scene loads, hang waits and regular play share
stop, temperature and wall-time checks. Missing/stale telemetry ends the run unjudged. Rcon uses a random per-run password
on IPv4 loopback and fresh query markers with monotonic timeouts; the game uses an isolated job-local home.

## Outcomes and evidence

Reset, nonzero game exit, persistent silence/hang and repeated centre-pixel NaNs are failure signals (hardware **or engine**
fault). Known allocation failures, malformed replies, incompatible settings, thermal stops and clean exits are unjudged.
Infinity alone is logged, not failed. The exact HDR text is logged without invalid JSON floating-point literals.

Job directory: `spec.json`, `baseline.json`, `plan.json`, `status.json`, `sweep.log`, `steps.jsonl`, `frames.jsonl`,
`samples.jsonl`, `events.jsonl`, and `q2home/baseq2` configs/logs. The host's recovery record/completion receipt live beside
its per-card ownership journal. Do not delete a pending recovery record to bypass a refusal.

Near-top seconds are sampled telemetry estimates, not foreground-credited play time. Transition windows are clipped to
step boundaries. A centre-pixel sample is not image validation; another engine, finite pixel corruption or an unobserved
external setting change can still defeat this test. Consult [the review record](game-test-review.md) for code/validation.

## Hardware runs (2026-10-28, reference RTX 5090 at `0000:01:00.0`)

Driver 621.01, 600 W wall, XBAR +560 / +50 mV, memory +1758. Quake II RTX 1.8.0 in a 3644x2050 window on a 3840x2160
screen (90.1 % of area). Default patterns and levers (power, fans), 10 minutes asked, the same seed for all three runs.

| Run | Curve (trial in force) | Result |
|---|---|---|
| `game-20261028-175114` | 待验证: the curve that hung two games on 2026-10-27, flat 3127 MHz from 995 mV | **failed** at 87.5 s in `leap` (leap-heavy, 125 %): the game exited with Vulkan "device lost", a target GPU reset followed. 6 jumps, 5 leapt, peak 3120 MHz, 604 W, 77 C |
| `game-20261028-175724` | the same, 15 MHz lower from the voltage it froze at up | **failed** at 83.5 s in `leap`, the same step of the same seed: a driver reset. 4 jumps, all leapt, peak 3105 MHz |
| `game-20261028-180334` | the same, the whole curve 15 MHz lower | **survived** 462.5 s until `lab_stop`: 68 jumps, 59 leapt, peak 3120 MHz, 74 C. Levers applied on hardware: wall 535 → 400 → 600 → 465 W, fans 100 % then 50 % until 76 C |

Each failure was recorded as its trial's crash; the service put the daily profile back after each reset, with no recovery
error. The stopped run gave back its starting controls with no recovery error and left the trial it began on in force.

Seen on hardware and now documented: the fan lever's 100 % stretch is loud and surprised the person; the window, rcon, the
scheduled steps and both levers work as designed; the same seed replays the failing moment within seconds.

Not yet exercised on hardware: `gpu=none` and `boost`, the opt-in `restart` pattern, a full run to its end on a known-good
curve, interruption of the worker mid-write, and a second NVIDIA adapter (refused by design). The same curve, 15 MHz lower,
later crashed Cyberpunk 2077 at a light DLSS setting that parks the clock at the top (no driver reset), a load this script
does not play: the person's games keep the last word.
