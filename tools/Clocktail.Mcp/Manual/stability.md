# Correctness and stability
Validate this GPU's settings with tests and real workloads; there is no universal stable offset or margin.

`stress_test` runs NVIDIA's nvgwls when it is installed and usable. Its load parameter controls the stress workload.
`lab_test` checks core, memory and cache results exactly. Burn, light and pulse cover different loads; solo isolates high curve points; leap exercises heavy-to-light transitions.
Verify that the intended clock/voltage point actually ran. An inconclusive test is not a pass.
`watch` and `live_start` / `live_read` / `live_stop` measure the user's workload and frame times without injecting into the game.
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
