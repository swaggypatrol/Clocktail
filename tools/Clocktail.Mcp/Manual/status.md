# Reading the card
What each status field means, what is normal, how to name the limiter, and the telemetry quirks that mislead.

## gpu_status, field by field
- `nvvdd_mv`, `msvdd_mv`: the two rails' requested voltages (5 mV steps). `nvvdd_max_mv` is NVVDD's MAX (lowest of REL/ALT/OV).
- `clock_mhz`: core clock now. `memory_mhz`: the memory clock as NVML reports it (13801 MHz stock on the 5090, 14159 at +358).
- `power_w` against `power_limit_w`: board power against the wall in force.
- `temp_c`, `util_pct`, `limit_reasons`: NVML's own (see the quirks below before trusting `limit_reasons`).
- `temp_margin_c`: NVIDIA's own headroom (nvidia-smi's "T.Limit"): how many °C until the GPU slows itself down, at 90 °C on a 5090 (hardware slowdown 93, shutdown 96; `card_settings` has all three). It is the number to watch, not a fixed temperature.
- `vram_used_gib` of `vram_total_gib` (31.8 on a 5090; the driver's reservation left out, as nvidia-smi does) and `memory_controller_pct` (how busy the memory bus is). A game near the total starts swapping over PCIe, which shows as stutter no clock fixes. `stress_test` and `watch` give its P10/P50/P90, the CSVs every sample.
- `memory_c`: the memory chip temperature (`manual memory`).
- `rails`: per-rail amps and watts from the power monitor, with the current limit in force.
- `pcie`: the PCIe link: `gen` and `width` now, `max_gen` / `max_width` (the most the card and its slot allow together), `gpu_max_gen` (the card's own) and `replays` (data the link resent since boot). The link drops its generation at idle by design, so judge it under load. Gen 5 x16 and 0 replays on the reference card.
- `input`: the 16-pin connector's watts, the PCIe slot's, and the board's input current against its limit, with a `concern` when the connector passes 600 W or the current comes within 10 % of the limit (`manual power-thermal`).
- `gpc_mv`: the voltage the 11 GPCs measure at their own sensors (mean, min, max, and the mean against NVVDD). On the reference card the mean sits 3-9 mV above NVVDD and the lowest GPC within 7 mV below it, at idle and at the wall: the core gets what the rail is asked for, so a crash is the point's frequency, not a sagging rail. `stress_test` gives its P10/P50/P90, the CSVs every sample. Read only on the layout verified on an RTX 5090; absent elsewhere.
- `point`: the V/F point the card sits on: index, mV, stock MHz, current offset, the daily profile's offset.
- `session`: the experiment in force, if any.

## card_settings
Everything Clocktail can write, as it is on the card now, next to stock and the daily profile: power limit, core and memory P0 offsets, the V/F curve (as offsets, with the core's voltage demand), each rail's REL/ALT/OV/VMIN offsets and the resulting limits, XBAR/SYS/Video clock offsets and voltage demands, the voltage boost, the XBAR/core clock ratio, fans, the fan curve and the zero-RPM band, rail current limits, and the daily profile itself. Use it for "what is set on my card", "what is different from stock", and before any change. Beside the settings:
- `power.firmware_w`: the board limit the firmware enforces; it must equal `limit_w`, the request (`manual power-thermal`).
- `current_limits.policies`: the driver's whole power-policy table, each record's limit and reading now; one near 100 % under load can hold the clock.
- `clock_locks`: locks in the driver's clock-limit record (`set_clock_lock`, `nvidia-smi -lgc`, Boost Lock), whoever set them.
- `perf_limits`: the driver's ceiling and floor per clock domain and the limit that set each (`manual blackwell`).
- `controls`: the capability map: each control, the tool that sets it, whether it can be set on this card and driver, and why not.
- `pcie`: the PCIe link, as in `gpu_status`.
- `tdr`: Windows' driver timeout (`manual stability`); `vbios` and `driver`, which the daily profile is bound to (`manual blackwell`).
- `daily_differs_on_card`: what the daily profile keeps that the card does not hold now.

## limiter: naming what holds the clock
The firmware climbs the curve and stops at the first limit; each leaves a trace:
- **Power wall** (`power_wall`): board power within 10 W of the wall, or `sw_power_cap` with the instantaneous power peaking within 15 W of it. The limiter watches the instantaneous power every 20 ms between its samples (`peak_w`), because the wall answers peaks (below).
- `topology_w` in the limiter's readings: the driver's power controller's own two readings of board power, in W (steady, and one with a wider spread). They agree with NVML at a steady load (601 / 592 W against 600 W at the wall); a difference only says the load is changing fast.
- **Current limit** (`current_limit`): `sw_power_cap` with NVVDD or MSVDD drawing within 3 % of its current limit (`nvvdd_a` against `nvvdd_limit_a`). NVIDIA's own limits (600 A, 180 A) are far away on a 5090; a trial's lowered limit is the usual cause.
- **Power sub-policy** (`power_policy`): `sw_power_cap` with none of those. Memory-heavy work sets it the whole time at 336-361 W on a 575-600 W wall; that is normal.
- **Voltage limit**: NVVDD within 12.5 mV of MAX. The firmware parks one step under MAX; under the wall it would sit 55-75 mV lower, so the two are easy to tell apart. NVML's "reliability voltage" bit alone means nothing on a 5090: it is set even at idle.
- **Curve cap**: the clock is at the curve's highest point below MAX (within 15 MHz). Only a tuned curve has this; a stock curve that runs out is a voltage limit.
- **Thermal / hardware slowdown**: only from NVML's own reasons. Never seen on the reference card.
- **Idle**: under 120 W, or `gpu_idle` with utilization under 10 %.
- **No limit reached**: loaded, nothing hit (often a CPU-bound or frame-capped game).
- `perf_limits` beside the verdict: the driver's own ceiling and floor for GPC, XBAR and memory, each with the limit that set it. A CLIENT_ limit is a clock lock; RELIABILITY_ at the top is the curve under NVVDD's MAX. The wall acts below these, so a card at its wall runs under its ceiling.

## Quirks that mislead
- **`gpu_idle` at full load**: on a curve with a flat top, NVML reports `gpu_idle` on almost every sample at 280-410 W and 100 % utilization. Never call a card idle from that bit alone. A hung GPU also reports it.
- **`sw_power_cap` far below the wall**: see sub-policy above. Read `power_w` against `power_limit_w`, not the bit.
- **The wall tracks short peaks**, not the 1 s average: a game can sit at 516-566 W average on a 600 W wall and still be wall-limited (10 Hz samples: median 552 W, p90 588 W, max 593 W). nvgwls full load is flat, so there average equals peak (600 W average, peaks 598-604 W). The limiter counts the peaks, so it calls that game the wall, not a sub-policy.
- **NVML's max graphics clock (3090 MHz) is not a limit**; tuned cards run past it.
- **NVML's memory offset reads double** the real P0 offset (+358 shows as +716).
- **Game overlays show a lower clock than the curve** under a tight wall (very fast power throttling), but the voltage still follows the curve point, so stability is a property of the point, not of the overlay's number.
- **Third-party tools** (HWiNFO and similar) misread rail voltages and rail power on this card; trust Clocktail's readings.
- NVML temperature runs 1.5-2.5 C above the temperature the fan curve uses while heating.
- **NVML's time-in-limit counters do not count the wall** on this card (driver 617.42): 30 s at the 600 W wall with `sw_power_cap` set added nothing to the power one, and nvidia-smi's "Clocks Event Reasons Counters" read the same. Clocktail does not use them; the limiter's samples and the live CSV are the record.

## Normal ranges on the reference card
- Idle: ~800-835 mV, ~45 W, fans stopped below ~43 C.
- Full load, stock, 575 W: ~2700 MHz at ~1025 mV, 75-81 C.
- Full load, founders profile, 600 W: ~2840-2920 MHz at 930-945 mV, 61-82 C.
- Games, founders profile: 2775-2985 MHz at 925-1015 mV, 315-600 W.
- Current: NVVDD 250-330 A in games, 422 A at nvgwls full load (limit 600 A); MSVDD ~110 A (limit 180 A).

## Explaining it to the person
Lead with the one-line answer (what limits it, what it runs at), then the two or three numbers that prove it, then what you would change, if anything. Keep units. Do not dump raw JSON on them.
