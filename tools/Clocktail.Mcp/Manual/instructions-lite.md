# Clocktail (lite)
RTX 50 series, across models and board vendors. Read this GPU's capabilities, stock values and current state; never copy another board's clocks, voltages, power limits, cooling or stability margin.
There is no built-in tuning preset and no default daily profile. A fresh install writes no tuning settings. Capability discovery is not a claim of complete hardware validation on every card.

## Authorization
Only explicit user agreement and NVIDIA driver/firmware acceptance authorize tuning; verify writes by readback.
begin_session asks for trial consent. Persistent changes (stock, load_daily, load_preset, keep_as_daily, set_daily_power_limit) ask separately: present the question, wait for yes, then use its one-time code. Legacy CLOCKTAIL_CONFIRM=off does not bypass consent.
Encoding, identity, concurrency and readback checks remain; do not invent another tuning envelope.

## Workflow
1. Use card_report for status; show its table and curve. Marks are advice, not stability or hardware-safety guarantees.
2. Another utility's settings mean NOT at stock. Ask whether to tune from them, explicitly clear with stock, or adopt via preview_daily adopt=true and keep_as_daily adopt=true.
3. After begin_session, try adjust_curve, flatten_curve or set_memory_offset, measure with stress_test, and record mark_stable or mark_crash. Read data_points for evidence.
4. back_to_daily and end_session restore the user's kept daily or return Clocktail-owned changes to their captured baseline. No daily is manufactured.
5. Show preview_daily and ask before keep_as_daily. set_daily_power_limit separately asks to create/update the user's persistent wall.
6. list_presets has an empty built-in list; load_preset loads only this user's saved profiles. load_daily requires an existing daily.
7. Offer install_service with approval if the service is unavailable. manual contains the full controls and method.

No fixed MHz stability margin applies. stress_test stops by default only within 2 °C of NVIDIA's slowdown point, and also at a maxTempC the user gives. NVIDIA's protections remain active.
