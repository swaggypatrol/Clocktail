# Why settings behave this way
Explain the current GPU from its measured limits and workload, not a copied daily profile.

## Why does a clock offset not raise the observed clock?
A power, current, voltage or clock limit may bind first. Under a wall the card may use less voltage for a similar clock. Read `limiter` and the operating V/F point.
## Why tune the curve rather than use one global offset?
Headroom can vary across voltage and temperature. Measure the actual bands used by the target workload; no universal curve shape is supplied.
## Why can more memory or XBAR offset be slower?
Retries, thermal behavior, power sharing and clock-domain coupling can trade one resource against another. Compare correctness, sustained performance and frame times.
## Why does voltage boost do little?
A different limit may bind, the requested percentage may not change a hardware voltage step, or the driver may ignore it. Readback and measurements decide.
## Why test both light and heavy loads?
They can reach different parts of the curve and exercise different transitions. Steady stress success alone does not prove a game's transitions will survive.
## Why no starter daily profile?
Different GPUs, boards, firmware, cooling and silicon have different stock settings and headroom. Only the user's explicitly approved settings become daily.
## Why is there no hard P-state lock?
NVIDIA's diagnostics (MODS `-pstate_hard`) can force a P-state so the driver no longer lowers clocks for voltage, temperature or power. Clocktail does not write that lock. It removes the driver protection that the two-guardrail model relies on, and on the production driver it has been seen to hold the core while forcing XBAR, SYS and Video very low, and to hang the GPU until a reboot (LACT issue #1177). `set_clock_lock` writes the client limit record instead: a request the driver still derates under.
## Why did the clock run under a lock?
A floor, a point lock or Boost Lock is a request. The driver still lowers the clock under it for power, temperature and voltage. `stress_test`, `watch` and `live_read` report `clock_locks`: how many of the samples under load ran off the lock, and what share. One sample off is not held. The live watch checks each period a lock was in force on its own (a lock set, changed or released mid-watch, or a curve moved under a point lock), with its times. A run that ran under its lock did not test the locked clock throughout. When the lock record could not be read, `clock_locks_unread` says so: a lock may have been in force.
## Why can a driver return OK without changing a setting?
The setter may ignore or clamp a request. Clocktail compares readback instead of treating an API return code as proof.
## Why did settings return after reboot?
Only a daily profile the user previously kept is reapplied. `stock` suspends it; `load_daily` explicitly enables it again.
