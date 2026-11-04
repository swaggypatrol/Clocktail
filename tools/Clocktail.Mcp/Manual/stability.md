# Correctness and stability
Validate this GPU's settings with tests and real workloads; there is no universal stable offset or margin.

`stress_test` runs NVIDIA's nvgwls when it is installed and usable. Its load parameter controls the stress workload.
Choose a goal with `lab_test(profile: "core" | "memory" | "uncore" | "all")`. These are test workloads and time budgets, not GPU tuning presets.
The default core profile gives burn 30 s, solo 60 s and leap 60 s (150 s total). Memory gives memory 60 s + latency 30 s; uncore gives l2 30 s + memory 30 s; all takes 270 s.
Profiles can be lengthened with `seconds`; a shorter screening run uses explicit `phases`. Bare phase lists keep the old equal split and 60 s default. Explicit `phase:seconds` schedules must add up to `seconds`.
Burn, light and pulse are modes of the FP32 load family. Solo isolates high curve points; leap exercises heavy-to-light transitions. Memory, latency and l2 preserve their different access paths and results.
Use targeted screening first, paired comparisons of a few candidates next, then graphics/application validation. NVIDIA nvgwls and custom light/pulse loads are optional cross-checks, not a mandatory suite for every step.
Verify that the intended clock/voltage point actually ran. An inconclusive test is not a pass.
`watch` and `live_start` / `live_read` / `live_stop` measure the user's workload and frame times without injecting into the game. The live watch's whole-run frame rate (`fps_under_load`) covers only its windows under load; `fps_whole_capture` includes menus and loading screens.
`game_test` is optional automation for Quake II RTX. It needs matching supported devices, an open session and the required local game installation. Its seed allows repeatable comparisons.
## Failure handling
A data error, device loss, driver reset or confirmed crash belongs to the trial that ran, not a later setting.
Use `mark_crash` for a game crash without a driver reset, then inspect the recorded readings.
Interrupted runs, unavailable telemetry, shared workloads and failure to reach the target point are reported honestly.
A measurement lease prevents settings from changing underneath a result; this is attribution correctness, not a tuning ceiling.
## Stops and margins
By default `stress_test` and `lab_test` stop only when the card comes within 2 °C of NVIDIA's slowdown point; with a `maxTempC` they also stop there. `game_test` stops only at a `maxTempC` it is given, and the background sweeps (`tune_memory`, `tune_uncore`) make no thermal stop. NVIDIA's own protection stays active throughout.
The Windows TDR configuration affects reset delay. Clocktail reads it for interpretation; it does not disable driver recovery.
Measure useful stability margin on this chip across temperature, load and transitions. A fixed number of MHz below a test edge is not a series-wide guarantee.
