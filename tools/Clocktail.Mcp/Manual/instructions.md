# Clocktail
Clocktail is for the NVIDIA GeForce RTX 50 series, across GPU models and board vendors, not a single board design.
There is no built-in tuning preset and no default daily profile. A fresh install does not write GPU tuning settings.
Read this GPU's current settings, reported capabilities, stock values and limits before proposing changes. Do not substitute another card's clocks, voltages, power limits, fan settings or stability margins.
Support for each control is discovered at runtime; an unavailable sensor or unrecognized driver layout is reported, not guessed. Series-wide scope is not a claim that every control has been hardware-validated on every card.

## Authorization
Only two tuning guardrails apply:
1. Explicit user agreement to a change or a clearly described session of trials. `begin_session` asks for session consent; persistent changes ask separately.
2. NVIDIA driver/firmware acceptance, verified by readback.
No extra software tuning envelope, mandatory clock/voltage pairing or model-specific preset is imposed. Encoding, GPU identity, concurrency and readback checks are execution requirements, not substitute tuning policy.
A confirmation code is not consent: present the returned question, wait for the person's yes, then use that one-time code. Legacy `CLOCKTAIL_CONFIRM=off` does not bypass consent.
Ask the person before stock, load_preset, keep_as_daily, set_daily_power_limit, set_power_cap, delete_profile and install_service. begin_session and load_daily ask too: like all of these but install_service, through their own confirmation code.

## Baseline and daily
"Stock" means this GPU's NVIDIA/board firmware settings, not a bundled set of numbers. Existing settings from another utility are not stock: report them and ask whether to tune from them, adopt them, or explicitly clear them.
"Daily" means only the profile this user chose to keep for this GPU. When none exists, say so; do not recommend or manufacture a starter profile.
`back_to_daily` and session cleanup restore the user's kept profile when present; otherwise they return Clocktail-owned changes to the recorded baseline. They do not implicitly erase another utility's settings.
`stock` explicitly clears known non-stock settings and suspends any kept daily profile; original values for some controls are known only from recorded snapshots.

## Suggested method
1. Read settings and capabilities. Ask about the goal: performance, power, noise, temperature, or a specific workload.
2. Measure a baseline on this GPU at representative temperature and load.
3. Agree on the scope of trials with the user. Choose power and cooling based on this board's reported controls and the user's goal.
4. Change an appropriate control, read it back, then compare repeatable workloads. Map voltage bands actually reached under load; do not assume a universal knee or voltage floor.
5. Use both correctness tests and the user's workloads. No fixed MHz safety margin or stress-test duration proves stability on every chip.
6. Record results and failures. Show `preview_daily` and obtain separate approval before keeping settings.
By default `stress_test` and `lab_test` stop only when the card comes within 2 °C of NVIDIA's slowdown point; with a `maxTempC` they also stop there. `game_test` stops only at a `maxTempC` it is given, and the background sweeps (`tune_memory`, `tune_uncore`) make no thermal stop. NVIDIA's own protection stays active throughout.

## Tool map
Choose a testing goal: `lab_test` for V/F validation (default core profile, 150 s), `tune_memory` for memory search, `tune_uncore` for XBAR/ratio/boost search, and `game_test` or the user's game for graphics confirmation. `lab_test` also has memory/uncore/all workload profiles; explicit custom phases retain the old equal split. Follow background jobs with `lab_status` / `lab_stop`. Memory `best_mhz` is a confirmed recommendation; `edge_mhz` is the scan boundary. Other backends are optional cross-checks.

| Area | Tools |
|---|---|
| Read the current GPU | `card_report`, `card_settings`, `gpu_status`, `limiter`, `curve_around`, `diagnostic_report` |
| Sessions and records | `begin_session`, `end_session`, `data_points`, `read_log`, `log_note`, `mark_stable`, `mark_crash` |
| Core curve and clocks | `adjust_curve`, `flatten_curve`, `set_curve`, `shift_curve`, `set_core_offset`, `set_clock_lock`, `curve_editor` |
| Memory and clock domains | `set_memory_offset`, `adjust_xbar`, `adjust_clock_domain`, `set_clock_ratio`, `set_voltage_boost` |
| Power and cooling | `set_power_limit`, `set_daily_power_limit`, `set_power_cap`, `set_current_limit`, `set_fan_curve`, `set_fan_start`, `set_fan_speed` |
| Measurements | `stress_test`, `lab_test`, `c01_test`, `watch`, `live_start`, `live_read`, `live_stop`, `tune_memory`, `tune_uncore`, `game_test`, `lab_status`, `lab_stop` |

`c01_test` checks current GPU settings with sustained FP32 FMA and exact CPU comparison. Use the 180 s default near a suspected core V/F boundary. Compare the measured voltage, frequency and board power when changing `intensity`, which scales the thread-block grid.
| User-owned profiles | `preview_daily`, `keep_as_daily`, `back_to_daily`, `stock`, `load_daily`, `list_presets`, `save_profile`, `load_preset`, `delete_profile` |
| Setup and help | `install_service`, `manual` |

When the person asks for the curve editor (the V/F window, "open the curve"), call `curve_editor`: it opens a window on their desktop and returns at once; they drag the curve there (the window smooths the low-voltage cliff itself and shows crashes and evidence on the points), and their click on its save button is their own approval to keep it as daily, so do not ask again in chat. Call it again to read how it ended.
The window is in English. Its right-click history menu lets the person select and confirm deletion of an individual historical test; that takes effect immediately, even if curve edits are canceled. GPU settings, raw residency samples and the broker's V/F history files are unchanged. The window's "Observed V/F" dots are where the card ran (measured voltage and clock above 825 mV from the always-on broker history, plus residency samples), by observed time: observed operation, not a stability result. The original journal remains for recovery, and session crash budgets still include deleted crashes.
For "how is my card" call `card_report`, show its table and curve as returned, then explain the readings. Marks are advisory, not hardware-safety or stability guarantees.
Read `manual start`, `manual method` and `manual controls` before tuning; other topics explain status, tests and user-owned profiles.
If the service does not answer, explain it before `install_service`. Never claim a missing reading, interrupted measurement or failed readback is success.
