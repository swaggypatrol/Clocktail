# Every control
Controls use the current RTX 50 GPU's reported capabilities; there is no supplied tuning profile.

## Write and recovery model
Read this GPU's settings first and obtain explicit consent. A trial is read back and recorded through the broker.
Unknown layouts, changed GPU identity, conflicting operations and readback errors are reported rather than guessed around.
The daily profile contains only settings the user explicitly chose to keep for this GPU. With no daily profile, trial cleanup returns Clocktail-owned changes to their recorded baseline.
`preview_daily` shows what will be retained and what remains temporary; `keep_as_daily` asks before saving it. Another utility's settings require explicit adoption or explicit clearing.
There are no stock clock, voltage, power, fan or offset values to copy from a bundled preset.

## Trial controls
| Tool | Changes | Kept by keep_as_daily |
|---|---|---|
| `adjust_curve` | V/F offsets in a voltage band | yes |
| `flatten_curve` | Lower a flat top from the selected voltage | yes |
| `set_curve` | Whole per-point V/F curve | yes |
| `shift_curve` | Move the current curve left by the selected mV | yes |
| `set_core_offset` | Core P0 offset; restore the V/F curve afterwards | yes |
| `set_memory_offset` | Memory P0 offset; restore the V/F curve afterwards | yes |
| `adjust_xbar` | Independent XBAR clock offset and voltage demand | yes |
| `adjust_clock_domain` | Available domain offsets/demands; core demand only | yes |
| `set_clock_ratio` | Driver-exposed propagation ratio | yes |
| `set_voltage_boost` | Driver-exposed boost percentage | yes |
| `set_clock_lock` | Cap, floor, point lock or Boost Lock | cap only |
| `set_fan_curve` | Editable firmware fan curves | yes |
| `set_fan_start` | Editable fan stop/start bands | yes |
| `set_fan_speed` | Temporary fixed fan duty or auto | no |
| `set_power_limit` | Ordinary board power request for a trial | no; use set_daily_power_limit separately |
| `set_current_limit` | Temporary rail current limit | no |
| `set_power_cap` | Additional board power request (F8) | no |

## Curves and offsets
Point count, offset budget, regulator step and available voltage points come from the current driver layout.
Core and memory P0 writes can reset V/F point offsets; the curve is restored in the same trial.
`flatten_curve` only lowers by definition. `shift_curve` changes clocks for a requested voltage shift, rather than programming a universal undervolt recipe.
A memory offset's units are those of the driver interface; preserve units when comparing it with telemetry.

## Clock locks
`set_clock_lock` writes the client limit record that nvidia-smi locked clocks and Afterburner's L also use (NVAPI's partner "Client" interface, stable across architectures). A cap, floor, point lock or Boost Lock is a request, not a hard P-state lock: the driver still lowers the clock for power, temperature and voltage, and XBAR, SYS and Video are not held with the core. A lock outlives the program that set it; `card_settings` and `gpu_status` show any in force. `stress_test`, `watch` and `live_read` report `clock_locks` against the clock that actually ran.

## Clock domains and voltage
Topology identifies available domains and their rails. Clock offset, voltage demand, propagation ratio and boost are independent requests, not mandatory pairings.
NVIDIA decides which values and combinations it accepts; readback verifies the result.
Domain offsets, demands and the ratio go through private driver layouts that change with GPU architecture; a layout that is not the confirmed one is refused, not guessed. `gpu_status` and the measurement tools report XBAR, SYS and Video clocks so a change to one domain can be checked against the others.
Boost percentages do not imply a fixed mV change across models. Low-level rail offsets may be ignored by a driver; reporting that is different from imposing a tuning ceiling.

## Power
`set_power_limit` moves the ordinary request (FE); `set_power_cap` moves the additional request (F8), where available: the lower of FE and F8 holds.
FE here is the request identifier, not a board-edition requirement.
Use this board's reported minimum, maximum and stock power. An additional request can persist until restart even after trial cleanup returns it to a non-limiting value.
`set_daily_power_limit` separately asks to create/update the user's persistent wall. No daily power limit is supplied by Clocktail.
Current limits use each rail's reported range; useful values depend on the workload and active limiter.

## Cooling
Read the actual fan channels, editable policies and their ranges. No universal duty floor, stock curve, full-speed temperature or zero-RPM band is assumed.
The firmware curve uses three encoded temperature/duty/RPM points. An omitted RPM is resolved against this policy's own pairing, not another board's curve.
Fixed fan duty is temporary. The service does not watch the temperature and take it back; it goes back with the trial (back_to_daily, end_session, a crash) and when the agent goes quiet.
A zero-RPM band (`set_fan_start`) is checked for being one: stop under start, both within 0..120 °C. Where it sits is the user's choice.

## Measurements and jobs
`stress_test` and `lab_test` judge current settings; `watch` and live tools observe real workloads.
`tune_memory` and `tune_uncore` use search parameters and measurements, not predefined daily targets. Explicit candidates are not filtered by another card's voltage/clock advice.
`game_test` is optional local Quake II RTX automation. Jobs are followed with `lab_status` and stopped with `lab_stop`.
By default `stress_test` and `lab_test` stop only when the card comes within 2 °C of NVIDIA's slowdown point; with a `maxTempC` they also stop there. `game_test` stops only at a `maxTempC` it is given, and the background sweeps (`tune_memory`, `tune_uncore`) make no thermal stop. NVIDIA's own protection stays active throughout.

## User profiles
`list_presets` and `load_preset` retain their API names for compatibility, but only user-saved profiles are available; the built-in list is empty.
`save_profile`, `delete_profile`, `load_daily`, `back_to_daily` and `stock` manage the user's own settings and recorded baseline. A missing profile is not replaced by a starter preset.
