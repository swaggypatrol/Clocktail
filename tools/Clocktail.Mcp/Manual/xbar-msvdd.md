# XBAR and MSVDD
Treat clock offset, voltage demand, propagation ratio and voltage boost as independent measured controls.

Read the available clock domains, their rails, offset ranges, demands and current propagation ratio from `card_settings`.
`adjust_xbar` moves the crossbar clock offset and/or voltage demand. There is no mandatory demand at any particular MHz offset.
`adjust_clock_domain` handles available domain offsets and demands; core clock offsets use the P0 path.
`set_clock_ratio` requests a representable ratio; `set_voltage_boost` uses the path this GPU exposes. The voltage change resulting from a percentage is not assumed to be identical across boards.
Higher crossbar clocks can help a cache/memory-sensitive workload but also change core clock, power or efficiency. Compare measured outcomes rather than requiring a particular rail/clock combination.
## Automated comparisons
`tune_uncore` searches from the current card settings, or compares explicit `candidates`.
Search bounds and default increments describe its algorithm, not a hardware tuning envelope or a supplied daily profile.
Candidates are compared A B B A using L2 bandwidth, DRAM bandwidth and core clock. Runs of one setting more than 2 % apart were disturbed: repeat the comparison before judging it.
A search that ends early (lab_stop, disturbed twice) names nothing. A named best result is still a candidate for this GPU and workload, not a setting to apply to the whole series.
A failure record without acknowledgement, or unconfirmed driver-reset recovery, ends the search before more trials. `failure_recording` retains `confirmed`/`rejected`/`unknown`, the detail and the measured trial ID. Verify unresolved records and recovery before another job. Failed final restoration also suppresses the recommendation; a failed job reports `best` equal to `start`.
Follow `lab_status`; validate and obtain approval before `keep_as_daily`.
