# RTX 50 architecture and capabilities
Interpret Blackwell controls from this GPU's reported topology, not another model's board limits.

Clocktail is for the NVIDIA GeForce RTX 50 series, across GPU models and board vendors, not a single board design.
There is no built-in tuning preset and no default daily profile. A fresh install does not write GPU tuning settings.
Read this GPU's current settings, reported capabilities, stock values and limits before proposing changes. Do not substitute another card's clocks, voltages, power limits, fan settings or stability margins.
Support for each control is discovered at runtime; an unavailable sensor or unrecognized driver layout is reported, not guessed. Series-wide scope is not a claim that every control has been hardware-validated on every card.

## Clocks and rails
The core uses a voltage/frequency curve and a global clock offset. Memory and other domains have separate controls where the driver exposes them.
NVVDD and MSVDD name voltage rails; which domains demand from which rail comes from topology. REL, ALT/OP, OV and VMIN are reported limits, not numbers to copy between boards.
Clock offsets and voltage demand are different controls. A propagation ratio can couple domain clocks; read the current ratio rather than assuming its stock value.
## What holds performance
Board power, rail current, voltage limits, clock locks, utilization, temperature and workload behavior can each bind performance.
Use `limiter`, telemetry and `card_settings perf_limits`; do not infer a limit solely from a model name or a single throttle bit.
## Driver and firmware changes
Capabilities and layouts can change with driver or VBIOS updates. Readback is required even when a setter returns OK.
A profile kept on another VBIOS requires explicit loading before automatic reuse. Diagnostics distinguish a supported series from the subset of combinations previously validated on hardware.
