# Lab tests and automatic tuning

Use four main workflows. Workload profiles select tests and time budgets; they supply no GPU tuning settings.

| Goal | Entry | What it establishes |
|---|---|---|
| Validate a V/F change | `lab_test`, profile `core` | Correctness under full load, a held high point and heavy/light transitions |
| Find a useful memory offset | `tune_memory` | A screened range and a paired, confirmed performance recommendation |
| Compare XBAR/ratio/boost changes | `tune_uncore` | L2 gains with DRAM/core cost checks, using A B B A and final confirmation |
| Confirm graphics behavior and actual benefit | `game_test`, or the person's game with `watch` / `live_*` | Graphics-path behavior and repeatable application performance |

`stress_test` uses NVIDIA nvgwls as an optional independent check. Custom `light` and `pulse` phases remain available.
There is no need to run every backend at every offset. A synthetic pass covers the work that ran; a fixed game or benchmark
comparison is still needed to establish a useful application gain.

## Test profiles

`lab_test` accepts `profile` or custom `phases`. Omit both for `core`.

| Profile | Phase budgets | Total |
|---|---|---|
| `core` | burn 30 s, solo 60 s, leap 60 s | 150 s |
| `memory` | memory 60 s, latency 30 s | 90 s |
| `uncore` | l2 30 s, memory 30 s | 60 s |
| `all` | burn 30 s, solo 60 s, leap 60 s, memory 60 s, latency 30 s, l2 30 s | 270 s |

A supplied `seconds` can lengthen the profile, scaling its budgets proportionally, up to the tool's 600 s limit.
To run a shorter screening test, select custom phases. A profile cannot silently squeeze several important phases into
a short total. The answer reports `profile`, `planned_seconds` and the exact `schedule` beside the measured results.

Explicit bare phase lists keep the legacy equal split. For example, `phases: "burn,pulse,light,memory,latency"` with
`seconds: 60` runs each for 12 s; omitting `seconds` with custom phases still means 60 s. Explicit schedules are also
accepted: `phases: "burn:30,solo:60,leap:60"`, `seconds: 150`. Every item must have a positive duration and the durations
must sum to the stated total. Giving both a profile and custom phases is an error.

The isolated CLI supports the same profile budgets with `clocktail-lab run --profile core`. Existing direct CLI phase
lists still work; schedules also travel through `--phases` without losing individual phase durations.

## Why retain the phases?

- Burn, light and pulse share an FP32 kernel but impose different activity and timing. They are modes of one workload
  family, rather than three mandatory tests for every candidate.
- Solo exercises integer/INT8 and FP32 paths while fitting the load below the power wall. Its effective-clock and
  occupancy checks decide whether the point actually held. Leap uses that workload through repeated heavy/light jumps
  and checks whether the jumps happened.
- Memory tests a large buffer in five access orders. Latency uses dependent pointer chasing. L2 repeatedly accesses a
  small cache-oriented region for XBAR comparisons. These measure different bottlenecks and keep separate results.
- Graphics work exercises paths the CUDA kernels do not. The project's recorded game failures include curves that
  survived synthetic jumps, so game validation remains a separate workflow.

## Memory search

1. Warm the current daily/baseline settings and obtain an actual memory-clock reference. Missing telemetry or shared
   work ends the job without changing offsets.
2. Scan the requested offsets. Each step checks bits, bandwidth, latency and whether the real memory clock followed
   the requested offset. A failed, shared or unjudgeable step cannot be marked stable.
3. Two successive bandwidth/latency regressions are a recheck trigger. Before ending a non-trend scan, measure the
   reference and suspect setting A B B A. A loss must repeat; otherwise update only the effective scan readings and
   continue. Original scan readings remain intact. `trend:true` scans through performance dips, with correctness and
   measurement validity still required.
4. Shortlist at most three measured settings: the lowest offset on the joint bandwidth/latency plateau, the bandwidth
   winner and the latency winner, with duplicates removed. They are candidates, not recommendations.
5. Compare candidates A B B A against the same daily offset. A useful gain repeats in both pairs and does not incur a
   material regression in the other metric. A repeated-setting spread over 2% in bandwidth or latency retries the
   comparison once, then leaves it unjudged if it is still disturbed. Equivalent performance prefers the lower offset.
6. Independently compare a selected change against daily once more. Restore the user's daily profile/captured baseline
   before publishing the recommendation. Failed or unfinished confirmation and unverified restoration leave no best.

Bandwidth uses a 0.5% material-change threshold, latency 1%. These are comparison parameters, not hardware stability
margins. A B B A reduces steady drift and exposes disagreement between repeated runs; it cannot identify the physical
cause of a slowdown. Lower frequency is a tie-breaker for equivalent measured performance, not a stability guarantee.

The memory and uncore searches share positive-metric validation and the paired geometric-mean calculation. They keep
their own objectives: memory considers bandwidth and latency, while uncore targets L2 bandwidth and constrains DRAM and
core costs. No combined synthetic score is treated as game FPS.

## Status and evidence

`lab_status` / `lab_stop` serve all background jobs. The memory status adds:

| Field | Meaning |
|---|---|
| `steps` | Original scan readings and correctness outcomes |
| `edge_mhz` | The scan-side boundary candidate, including effective paired rechecks; not a performance recommendation |
| `candidate_mhz` | A current scan shortlist candidate, still unconfirmed |
| `comparisons` | Raw A B B A runs, outcomes, bandwidth and latency changes, and disturbance flags |
| `best_mhz` | A paired recommendation after confirmation and verified restoration; null otherwise |
| `confirmed` | Whether the job completed that comparison/restoration path |

`steps.jsonl` and `comparisons.jsonl` retain scan and comparison readings separately. No scan reading is rewritten to
hide a recheck. `scan_minutes_estimate` covers only the scan; slowdown rechecks, candidate confirmations, retries and
waiting for other GPU work take additional time. Up to three candidate comparisons and a final confirmation use at
most 32 individual runs including one disturbance retry per comparison. Slowdown rechecks are additional.

User cancellation, another trial replacing the measured settings, missing clocks, an interrupted measuring process or
an incomplete recovery cannot produce a confirmed recommendation. Correctness failures name the trial actually measured;
runner/telemetry failures remain unjudged. Nothing is kept as daily automatically.

## Compatibility in 0.9.0

- `lab_test()` now selects `core` (150 s), rather than the old five-phase 60 s mixture. A bare `seconds` now applies to
  that profile and must meet its minimum. Give an explicit phase list to retain an old short call.
- Existing phase names, explicit equal splits, tool names and `lab_status` / `lab_stop` remain supported.
- `best_mhz` now means a paired recommendation. Consumers wanting the previous scan-side value should read `edge_mhz`.
- `minutes_at_most` is replaced by `scan_minutes_estimate`, with comparison work described separately.
- Update MCP, lab and broker together with setup; old lab binaries do not parse explicit phase-duration schedules.

This release is validated with CPU-only algorithm and complete-workflow tests over simulated GPU results and broker
responses. New hardware failure coverage and end-to-end tuning speed have not been measured.
