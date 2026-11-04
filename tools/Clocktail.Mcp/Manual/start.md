# Getting started
RTX 50 series tuning starts from this GPU's own reported state, not a supplied profile.

Clocktail is for the NVIDIA GeForce RTX 50 series, across GPU models and board vendors, not a single board design.
There is no built-in tuning preset and no default daily profile. A fresh install does not write GPU tuning settings.
Read this GPU's current settings, reported capabilities, stock values and limits before proposing changes. Do not substitute another card's clocks, voltages, power limits, fan settings or stability margins.
Support for each control is discovered at runtime; an unavailable sensor or unrecognized driver layout is reported, not guessed. Series-wide scope is not a claim that every control has been hardware-validated on every card.

## Baseline and daily
"Stock" means this GPU's NVIDIA/board firmware settings, not a bundled set of numbers. Existing settings from another utility are not stock: report them and ask whether to tune from them, adopt them, or explicitly clear them.
"Daily" means only the profile this user chose to keep for this GPU. When none exists, say so; do not recommend or manufacture a starter profile.
`back_to_daily` and session cleanup restore the user's kept profile when present; otherwise they return Clocktail-owned changes to the recorded baseline. They do not implicitly erase another utility's settings.
`stock` explicitly clears known non-stock settings and suspends any kept daily profile; original values for some controls are known only from recorded snapshots.

## Authorization
Only two tuning guardrails apply:
1. Explicit user agreement to a change or a clearly described session of trials. `begin_session` asks for session consent; persistent changes ask separately.
2. NVIDIA driver/firmware acceptance, verified by readback.
No extra software tuning envelope, mandatory clock/voltage pairing or model-specific preset is imposed. Encoding, GPU identity, concurrency and readback checks are execution requirements, not substitute tuning policy.
A confirmation code is not consent: present the returned question, wait for the person's yes, then use that one-time code. Legacy `CLOCKTAIL_CONFIRM=off` does not bypass consent.

## Tools
Choose the goal first: `lab_test` for V/F verification (default core profile, 150 s), `tune_memory` for memory search, `tune_uncore` for XBAR/ratio/boost search, and `game_test` or the user's game for graphics confirmation. Explicit phase lists remain available for advanced diagnosis. Use `lab_status` / `lab_stop` for background jobs; a memory scan boundary (`edge_mhz`) is separate from a confirmed recommendation (`best_mhz`).

| Area | Tools |
|---|---|
| Read the current GPU | `card_report`, `card_settings`, `gpu_status`, `limiter`, `curve_around`, `diagnostic_report` |
| Sessions and records | `begin_session`, `end_session`, `data_points`, `read_log`, `log_note`, `mark_stable`, `mark_crash` |
| Core curve and clocks | `adjust_curve`, `flatten_curve`, `set_curve`, `shift_curve`, `set_core_offset`, `set_clock_lock`, `curve_editor` |
| Memory and clock domains | `set_memory_offset`, `adjust_xbar`, `adjust_clock_domain`, `set_clock_ratio`, `set_voltage_boost` |
| Power and cooling | `set_power_limit`, `set_daily_power_limit`, `set_power_cap`, `set_current_limit`, `set_fan_curve`, `set_fan_start`, `set_fan_speed` |
| Measurements | `stress_test`, `lab_test`, `watch`, `live_start`, `live_read`, `live_stop`, `tune_memory`, `tune_uncore`, `game_test`, `lab_status`, `lab_stop` |
| User-owned profiles | `preview_daily`, `keep_as_daily`, `back_to_daily`, `stock`, `load_daily`, `list_presets`, `save_profile`, `load_preset`, `delete_profile` |
| Setup and help | `install_service`, `manual` |

`install_service` installs the component that performs writes; Windows may request elevation. Reading settings does not require changing them.
On a fresh install, inspect first. There is nothing to load with `load_daily` or `load_preset` until the user has kept or saved a profile.
For tuning, follow `manual method` and `manual controls`.
