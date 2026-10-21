# Blackwell, the parts that matter
Voltage rails, offset slots and ceilings, clock domains, the V/F curve, how the firmware picks a clock, and the write-order traps.

## Voltage rails
- **NVVDD**: the core rail (GPCs, the graphics clock). Its voltage is what the V/F curve sets.
- **MSVDD**: the on-chip interconnect rail: XBAR (crossbar), L2, SYS and Video. Not the memory chips.
- **FBVDD / FBVDDQ**: the memory chips (GDDR7). Visible only as power-monitor channels; not tunable.
- NVML cannot read voltages on this card. Clocktail reads each rail's requested voltage from the driver (5 mV resolution); `gpu_status` shows both.
- Per-GPC ADCs confirm the core is not sagging under load: their mean reads 5-10 mV above reported NVVDD even at 422 A.

Typical readings on the reference card:
| State | NVVDD | MSVDD |
|---|---|---|
| Idle | ~800-835 mV | ~825 mV (its floor) |
| Stock curve, heavy load, 575 W | ~1025 mV | ~975 mV |
| Stock curve, light load | ~1090 mV | ~1040 mV |
| Tuned curve, full load at 600 W | ~915-945 mV | ~940-960 mV |
| Tuned curve, games | 925-1015 mV | ~970 mV, up to its MAX at light load |

On the stock curve MSVDD sits about 50 mV under NVVDD. On an undervolted curve it often sits above NVVDD; that is normal.

## Rail offset slots: REL, ALT/OP, OV, VMIN
Each rail has four offsets (mV, 5 mV steps):
- **REL**: the reliability limit. The lowest of REL, ALT/OP and OV is the rail's **MAX**.
- **ALT/OP**: the alternate reliability (operating) limit.
- **OV**: the overvoltage limit.
- **VMIN**: the floor. Raising it raises the minimum voltage the rail ever runs.

Stock on the reference card: **MSVDD REL is -50 mV** (not 0), so MSVDD MAX is 1045 mV; REL 0 makes it 1095 mV (ALT/OP 1115, OV 1200). NVVDD MAX is 1095 mV. Always take the card's readback as stock, never assume 0.

Ceilings Clocktail holds and you must respect:
- No raise may push a rail's MAX above min(the domain's own max, **1150 mV**). Hardware already above it (stock NVVDD OV is 1200 mV) may stay or go down.
- REL above 0 is past NVIDIA's own reliability limit. REL +20 on MSVDD tops out at 1115 mV unless ALT/OP is raised too.
- "Voltage boost" percentages (`set_voltage_boost`) had no measurable effect on this card at 1 % and 10 %. Do not sell them as a feature.
- `adjust_nvvdd` and `adjust_msvdd` move these offsets; `card_settings` shows each slot and the MAX it gives.

## Clock domains
- **GPC / graphics (core)**: follows the V/F curve on NVVDD.
- **XBAR, SYS, Video**: on MSVDD. Under load **XBAR is held to about 0.9 x the core clock** (Blender: core 3030-3052 MHz, XBAR 2722-2737): that is the driver's clock propagation ratio from NVVDD to MSVDD, 0.8999 stock, which `set_clock_ratio` moves. Without an offset, XBAR is limited by the MSVDD voltage it gets; that is why raising MSVDD REL speeds it up.
- **Memory**: its own offset (P0 memory offset); see `manual memory`.
- XBAR/SYS/Video each take a **clock offset** (MHz) and a **voltage demand** (-25..+50 mV, applied to MSVDD for that domain): `adjust_xbar`, `adjust_clock_domain`. The core takes a voltage demand too (on NVVDD: every V/F point's voltage moves by it) and a P0 clock offset (every point's clock moves by it). See `manual xbar-msvdd`.

## The V/F curve
- 127 editable core points (index 0-126), about 6.25 mV apart, roughly 450 mV to 1240 mV. Clocks land on a 7.5 MHz grid; the regulator steps 6.25 mV.
- Each point has an offset (MHz) on top of its stock frequency. "Effective" frequency = stock + offset, adjusted by the driver's temperature compensation.
- **The firmware climbs the curve and stops at the first limit**: the wall, a current limit, MAX voltage, the curve's own top, temperature. At a given clock it runs the lowest voltage that reaches it.
- Under a power wall it lowers the clock; the voltage is whatever the curve gives at that clock, interpolated between two points (usually 7-15 MHz under a point).
- Stock curve: knee near 870 mV (point 67), then about 22 MHz per 5 mV, climbing all the way to MAX. A stock card that runs out of curve is at its **voltage limit**.
- A tuned curve usually has a **flat top** below MAX. A card on a flat top is at its **curve cap**, not a voltage limit.
- **Temperature compensation**: 875-915 mV runs faster when hot (+15 to +37 MHz at 82 vs 41 C); 915-940 mV barely moves; above 950 mV it runs slower when hot (-30 to -45 MHz). Steps are coarse with hysteresis (up near 57/62/72/82 C, down near 70/61/53/43 C). A cool card runs 10-40 MHz higher at the same voltage, so a flat top is flat only at the temperature it was set at.
- **Leakage**: at 600 W (930-990 mV) heat costs about 2 MHz/C; at 450 W near the knee (870-885 mV) about 9 MHz/C.

## The driver's own clock ceilings (perf limits)
Besides the curve and the wall, the driver arbitrates a ceiling and a floor for each clock domain (GPC, XBAR, memory), each set by one named limit; `limiter` and `card_settings` show them as `perf_limits`. On the reference card: at idle the GPC ceiling was 3082 MHz, set by RELIABILITY_ALT_NVVDD_0 (the curve's top under NVVDD's MAX); with a clock lock (`nvidia-smi -lgc 210,2000`, or `set_clock_lock`) 1995 / 210 MHz, set by CLIENT_LOW_STRICT_GPC_MAX / MIN; at full load on the 600 W wall the performance controller raised the floor to the ceiling, 3007 MHz, while the card ran 2812. The power wall is not in this table: it acts below these, so a card at its wall runs under its ceiling. A ceiling far below the curve with a CLIENT_ name is a lock some tool left.

## What limits the clock above ~1.0 V
On the reference card the chip's own stability boundary flattens above ~980 mV: at light load it sits near 2960 MHz at 930 mV, 3030 at 955, 3115 at 980-985, and no higher at 1000-1005. Under heavy load the 600 W wall rarely lets NVVDD past ~955-1015 mV at all. So "stuck around 2950-3000 MHz at ~1.0 V" is usually the wall (heavy load) or the curve's top (light load), not a hidden limit; check `limiter`.

## Write order and persistence (traps)
- **Any P0 offset write (the core's or the memory's) zeroes every V/F point offset.** Order: power, fan curve and fan start, P0 core/memory, rails, the rest (voltage boost, clock lock, clock ratio), voltage demands, XBAR/SYS/Video offsets, the curve last. Presets and the daily profile apply in this order, and `set_core_offset` / `set_memory_offset` write the curve again right after themselves in the same trial.
- The P0 core offset and the point offsets share one budget. Clocktail tunes with the curve: the core offset (`set_core_offset`) moves every point by the same MHz, and the chip's edge is not the same at every voltage.
- A driver reset (TDR) keeps the offsets but puts the wall back to 575 W; the service restores the daily profile. A reboot clears everything; the service writes the daily profile again at logon.
- The daily profile is bound to the VBIOS it was kept on: a flash changes the stock curve under the offsets, so after one the profile waits for `load_daily` (after a `stress_test`) instead of going on by itself. A driver update does not stop it; `card_report` then suggests a new `stress_test`.
