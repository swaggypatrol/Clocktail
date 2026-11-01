# Reading status
Distinguish actual measurements, firmware stock values, captured originals and user-kept daily settings.

`card_report` summarizes settings and live readings; show its table and curve, then explain uncertainty.
`gpu_status` reports operating clock, voltages, temperature, utilization, board power and limit reasons where available.
`card_settings` exposes offsets, V/F points, rail limits, domain topology, fans, current limits and the user's daily profile.
## Baselines
- `stock_*` values come from NVIDIA's known defaults or the original recorded state, as described for each control.
- `daily_*` describes only this user's saved settings. When no daily exists, report that rather than imagining a default.
- `not_set_by_clocktail` identifies settings from another utility. They are not evidence that the card is stock.
- `daily_differs_on_card` indicates a saved profile is not currently fully in force.
## Live interpretation
Compare power with this board's own request and firmware limit; inspect rail current limits and `perf_limits`.
Use reported temperature headroom and throttle reasons rather than assuming a universal temperature threshold.
Memory clocks may be reported in different units by different interfaces; preserve units.
GPC voltage sensors, memory temperature, connector power, PCIe information and frame statistics may be unavailable on some boards or layouts. Missing is not zero.
Validation-history information is provenance, not a model whitelist or proof that all controls work on every card.
