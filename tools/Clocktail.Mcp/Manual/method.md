# Tuning method
Measure the current GPU, change one variable where useful, and compare repeatable workloads.

Clocktail is for the NVIDIA GeForce RTX 50 series, across GPU models and board vendors, not a single board design.
There is no built-in tuning preset and no default daily profile. A fresh install does not write GPU tuning settings.
Read this GPU's current settings, reported capabilities, stock values and limits before proposing changes. Do not substitute another card's clocks, voltages, power limits, fan settings or stability margins.
Support for each control is discovered at runtime; an unavailable sensor or unrecognized driver layout is reported, not guessed. Series-wide scope is not a claim that every control has been hardware-validated on every card.

## Suggested method
1. Read settings and capabilities. Ask about the goal: performance, power, noise, temperature, or a specific workload.
2. Measure a baseline on this GPU at representative temperature and load.
3. Agree on the scope of trials with the user. Choose power and cooling based on this board's reported controls and the user's goal.
4. Change an appropriate control, read it back, then compare repeatable workloads. Map voltage bands actually reached under load; do not assume a universal knee or voltage floor.
5. Use both correctness tests and the user's workloads. No fixed MHz safety margin or stress-test duration proves stability on every chip.
6. Record results and failures. Show `preview_daily` and obtain separate approval before keeping settings.
By default `stress_test` and `lab_test` stop only when the card comes within 2 °C of NVIDIA's slowdown point; with a `maxTempC` they also stop there. `game_test` stops only at a `maxTempC` it is given, and the background sweeps (`tune_memory`, `tune_uncore`) make no thermal stop. NVIDIA's own protection stays active throughout.

## Curve exploration
To measure this card's own frequency/voltage behavior, use the curve and telemetry returned by the tools.
A power-limited workload may move down the curve after a positive offset, rather than run at a higher clock. To test the new frequency, loosen the wall or the current limit after each raise when the user wants that and the driver supports it, then verify the operating point.
Use full, light and transient loads to cover the operating bands. A steady-load pass does not prove a heavy-to-light transition or a game will hold.
`set_clock_lock` can help isolate a point; read it back and release test locks when finished.
## Uncore and memory
Clock-domain frequency, voltage demand, propagation ratio and boost interact, but their useful combination is specific to the GPU and driver.
Compare performance, core clock, power and correctness. More bandwidth on one test may cost frames in another.
Memory errors can appear as retries or lower performance before a crash. Compare sustained bandwidth and real workloads rather than selecting the largest offset.
## Keeping a result
A tuner's candidate is not a universal recommendation. Recheck it under the user's workloads, show `preview_daily`, and ask before `keep_as_daily`.
No daily profile is supplied or created automatically.
