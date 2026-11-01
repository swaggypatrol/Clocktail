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
## Why can a driver return OK without changing a setting?
The setter may ignore or clamp a request. Clocktail compares readback instead of treating an API return code as proof.
## Why did settings return after reboot?
Only a daily profile the user previously kept is reapplied. `stock` suspends it; `load_daily` explicitly enables it again.
