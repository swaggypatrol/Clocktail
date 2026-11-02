# Power and cooling
Use the current board's reported limits, fan capabilities and temperature thresholds.

Board power range and stock power depend on the GPU, board vendor and VBIOS. Read them before setting a power target.
`set_power_limit` is a trial; `set_daily_power_limit` explicitly creates or updates the user's persistent wall after consent. Neither has a model-specific default.
`set_current_limit` uses a rail's reported range. The result depends on which limiter binds the workload.
`set_power_cap` is an additional request; it can have a driver-lifetime side effect and asks separately before creation.
## Fans
Read the actual fan channels, editable policies, RPM pairing and zero-RPM band. Do not assume a stock curve, number of fans, start temperature or duty floor.
`set_fan_curve` sets three encoded points on editable policies; optional explicit RPM values are user choices.
`set_fan_start` sets the stop/start band. `set_fan_speed` temporarily holds fans within their reported range.
The service does not watch the temperature and take a fixed fan duty back; the duty goes back with the trial and when the agent goes quiet.
## Temperature
Use NVIDIA's reported headroom and slowdown reasons. By default `stress_test` and `lab_test` stop only when the card comes within 2 °C of NVIDIA's slowdown point; with a `maxTempC` they also stop there. `game_test` stops only at a `maxTempC` it is given, and the background sweeps (`tune_memory`, `tune_uncore`) make no thermal stop. NVIDIA's own protection stays active throughout.
Evaluate power, temperature, noise and performance together on the actual card, at a representative thermal equilibrium.
