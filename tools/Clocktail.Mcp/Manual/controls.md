# Every control
What each write tool changes on the card, how far one call goes, what it is for, what it interacts with, and how to combine them.

## How every write works
- Every change except the daily wall is a **trial** inside a session: `begin_session` first (a trial without one is refused). Each trial ends as stable (`mark_stable`), survived (replaced by the next one, or `back_to_daily`) or crash, and becomes a data point.
- The service does the write, reads it back and records the value it found (the ownership journal), so `back_to_daily`, `end_session`, a crash and `stock` can always put it back.
- Every tool moves a bounded step per call and refuses what passes NVIDIA's own limits for the card (1150 mV, the board's 400-600 W, the firmware's ranges). A refusal says why: explain the limit, never work around it.
- `keep_as_daily` keeps what the trials left on the card, not just the last step; `preview_daily` shows exactly what it would keep. Walls, current limits, the F8 request, fixed fan duties and clock locks other than a cap are never kept.
- `card_settings` shows each control now, stock and daily; its `controls` section says, for this card and driver, which ones can be written and why not.

## The core clock: the V/F curve
The firmware climbs the curve until a limit stops it (`manual blackwell`). Every point is a voltage and a clock; what you change is the clock offset at each voltage.
- `adjust_curve deltaMhz [fromMv toMv]`: the points in a voltage window, at most 15 MHz per call. The default window is around where the card runs under load (20 mV under to 10 mV over); an idle card needs `fromMv`/`toMv`. The tool for mapping the boundary one band at a time.
- `flatten_curve fromMv [marginMhz | ceilingMhz]`: every point from that voltage up at most the clock there. Only lowers. The flat top, in one call, at idle.
- `set_curve curve`: every point's offset at once, in `card_settings`' encoding (kHz, runs `offset*count`, '0*127' is stock). For a curve worked out on paper, or one a data point recorded. At idle.
- `shift_curve mv`: the curve on the card moved left by `mv` (at most 25 mV per call), from the knee up: each point runs the clock the curve ran `mv` higher. The same clocks at less voltage, so under a wall more clock. It is a raise at every voltage: test it like one.
- `set_core_offset mhz`: the core's P0 offset, the same MHz on every point (at most 15 MHz per call). The points' offsets and this share one budget, and the curve is written again after it in the same trial. Use it only when the whole curve should move by the same amount; the edge differs by voltage, so mapping is the curve's job.
- `adjust_clock_domain core vdemandDeltaMv`: the core's voltage demand, every point's voltage moved by the same mV (-25..+50, 10 per call): the same curve at more voltage (margin) or at less (an undervolt of the whole curve). `card_settings` reads it back as `vf_curve.voltage_demand_mv`; on the reference card only that readback was measured, not what it does under load.
- Shared rules: no point more than 200 MHz above the daily profile's (map in rounds from stock: raise, test, keep a margin below, continue); points under 850 mV are never touched by `adjust_curve`; the curve lands on a 7.5 MHz grid.

## The core rail: NVVDD
- `adjust_nvvdd`: REL, ALT and OV each set a limit and the lowest is the rail's MAX; the curve's top is cut where it sits above MAX. VMIN is the floor. Per call 10 mV down or 25 up; MAX never above 1150 mV. On the reference card REL and ALT moved MAX 1:1; REL -25 only clipped the top. Lowering MAX is a way to cap the top without touching the curve; raising it lets top points that sat above MAX run at their own voltage.
- `set_voltage_boost percent`: the driver's voltage boost (0..100 %, 25 per call). On the reference card 1 % and 10 % changed nothing measurable: not MAX, not the curve, not where the card ran. Offer it only if the person asks.

## Holding the clock in place: clock locks
- `set_clock_lock`: the driver's client clock-limit record, the one `nvidia-smi -lgc`, a V/F point lock and Boost Lock use. `maxMhz` caps the core, `minMhz` holds it at least there, `lockMv` pins it to the V/F point at that voltage, `boostLock` holds the curve's top even at idle. 0 (or false) releases one; a point lock and a range cannot be in force together.
- What it is for: test one point exactly (`lockMv` + `stress_test` at light load, which otherwise lands where its power puts it), or keep a run in one band while the wall would move it (`minMhz`/`maxMhz`). The light-load boundary in `manual stability` was measured this way.
- A lock changes what every later measurement means: `limiter`'s `perf_limits` names it (CLIENT_LOW_STRICT_GPC_MAX/MIN on the reference card). Release it (`lockMv 0`, or `maxMhz 0 minMhz 0`) before mapping the curve.
- **Only a cap is ever kept.** A floor, a point lock and Boost Lock are trial-only, like a current limit: `keep_as_daily`, `back_to_daily`, `end_session`, a crash and the agent going away all release them, because any of them would hold the card at a high clock or voltage at idle after every boot (mVolt+ never keeps one either). A cap (`maxMhz`) only ever lowers, so `keep_as_daily` keeps it: release one set only for a test (`maxMhz 0`) before keeping, or the test clock becomes the daily ceiling.
- `card_report` flags a lock another tool left on the card (they outlive the tool that set them).

## Power
- `set_power_limit watts`: the board power request (FE) for the session, 400-600 W on a 5090. A lower wall runs heavy load lower down the curve: the way to reach the knee and the middle band. Never kept.
- `set_daily_power_limit watts`: the daily wall (ask first). The firmware's own board limit (`card_settings` `power.firmware_w`) must match it; the service checks every time it puts the profile on and writes the request again if not (`manual troubleshooting`).
- `set_current_limit rail amps`: a rail's current limit (OCP). Closed-loop: current holds 1-2 % under it, no trip. A lowered NVVDD limit reaches points below what 400 W does (350 A ~2145 MHz at 850 mV on the reference card). Raising it above the VBIOS default (600 A NVVDD, 180 A MSVDD) is allowed up to the driver's maximum, changes nothing on a 5090 (the 600 W wall binds near 430 A) and `card_report` marks it ⚠️. Never kept.
- `set_power_cap watts`: the second board request (F8), ask first. The lower of FE and F8 holds. It does not exist until the first call creates it and cannot be deleted: after the trial it goes back to the board's maximum (600 W, limiting nothing) until the next boot. On a 5090 FE it does nothing `set_power_limit` cannot: use it only when the person asks for it. Never kept.

## The uncore: MSVDD, XBAR, SYS, Video
MSVDD feeds the crossbar (XBAR), L2, SYS and Video; it runs at what the core asks for minus its REL offset, down to its VMIN floor. Details and measurements: `manual xbar-msvdd`.
- `adjust_msvdd relDeltaMv [vminDeltaMv]`: up to REL 0 is NVIDIA's own limit (stock is -50 on a 5090), +20 the most Clocktail allows. Down is an MSVDD undervolt: watts the wall gives to the core, XBAR slower.
- `adjust_xbar offsetDeltaMhz vdemandDeltaMv`: XBAR's clock offset (50 MHz per call) and its voltage demand on MSVDD (-25..+50 mV, 10 per call). Above +250 the demand must be +20 mV, above +300 +50 mV (the cliff).
- `set_clock_ratio ratio`: the ceiling XBAR's offset runs into. Under load XBAR is held to the core clock x this ratio (0.8999 stock), which is why the offset stops paying past +250. 0.025 per call, 0.5..1.0. A higher ratio asks MSVDD for more voltage, so the demand rules apply to the clock XBAR then reaches.
- `adjust_clock_domain sys|video offsetDeltaMhz vdemandDeltaMv`: SYS (falls as XBAR's offset rises) and Video (NVENC/NVDEC, which games do not use). SYS +150 back at XBAR +300 changed nothing in Blender. Judge either by its own clock (`gpu_status`, `stress_test`'s `sys_mhz`) and a benchmark that uses it.

## Memory
- `set_memory_offset mhz`: the GDDR7 P0 offset (250 MHz per call; NVML shows it doubled). The write zeroes the curve's offsets, so the curve is written again right after it in the same trial. Judge by a benchmark as well as `stress_test` (exit code 6 is a data error): past its edge memory retries errors and gets slower before it crashes. Watch `memory_c`. `manual memory`.

- `tune_memory toMhz [stepMhz fromMhz seconds]`: the memory sweep (ST02), each step a trial the lab measures; it runs in the background and stops where the memory gets slower or fails. `lab_status` follows it, `lab_stop` ends it. It keeps nothing. `manual memory`.

## Fans
- `set_fan_curve`: the firmware fan curve, three points `degC:duty%`; the last must be 100 % at 88 C or lower. It sets the temperature the curve is tested at: set it before the curve (`manual method`, phase 2).
- `set_fan_start startC [stopC]`: the zero-RPM band (fans stop under 43 C and start at 50 C on a 5090 FE). Only earlier: a start no later than the curve's first point, stop at least 3 C under the start. A card with stopped fans warms itself at idle until the start temperature; a lower one keeps it cooler.
- `set_fan_speed percent|auto`: every fan at a fixed duty for a test (30-100 %). Never kept. The service gives the fans back to the curve by itself at 85 C, or when the agent goes away.

## What each tool changes, and what is kept
| Tool | Changes | One call | Kept by keep_as_daily |
|---|---|---|---|
| `adjust_curve` | the V/F curve in a voltage window | 15 MHz | yes |
| `flatten_curve` | the curve flat from a voltage up (only lowers) | any, down | yes |
| `set_curve` | the whole curve, every point's offset | 200 MHz over daily at most | yes |
| `shift_curve` | the curve on the card moved left by N mV (from the knee up) | 25 mV | yes |
| `set_core_offset` | the core P0 offset: every point at once (the curve is written again after it) | 15 MHz | yes |
| `set_fan_curve` | the firmware fan curve | any | yes |
| `set_fan_start` | the temperature the stopped fans start at (and stop under) | any, earlier only | yes |
| `set_memory_offset` | the memory offset (the curve is written again after it) | 250 MHz | yes |
| `adjust_msvdd` | MSVDD's REL and VMIN offsets, up or down | 25 up, 10 down | yes |
| `adjust_nvvdd` | NVVDD's REL, ALT, OV and VMIN offsets | 25 up, 10 down | yes |
| `adjust_xbar` | XBAR's clock offset and voltage demand | 50 MHz, 10 mV | yes |
| `adjust_clock_domain` | SYS's or Video's clock offset and voltage demand; the core's voltage demand | 50 MHz, 10 mV | yes |
| `set_clock_ratio` | XBAR's clock ratio to the core | 0.025 | yes |
| `set_voltage_boost` | the voltage boost percentage | 25 % | yes |
| `set_clock_lock` | a clock cap, a clock floor, a V/F point lock or Boost Lock | any | the cap only; a floor, point lock or Boost Lock never |
| `set_power_limit` | the wall, for the session (to reach other points) | 400-600 W | no: `set_daily_power_limit` sets the daily wall |
| `set_power_cap` | the F8 power request, for the session (ask first: it cannot be deleted) | 400-600 W | never |
| `set_current_limit` | a rail's current limit, for the session | up to the driver's max | never |
| `set_fan_speed` | a fixed fan duty, for the session | 30-100 % | never |

`keep_as_daily` keeps everything a trial set, not just the last change. Call `back_to_daily` first if only some of it should stay. The daily profile writes them in a fixed order (power, fans, P0 offsets, rails, the rest, voltage demands, XBAR/SYS/Video offsets, the curve last), because a P0 write zeroes the curve.

## Combining them
- **Map a band of the curve at full load**: `set_power_limit` (or `set_current_limit nvvdd` below 400 W) to put full load there, warm up, then `adjust_curve +15` with `fromMv`/`toMv` around the voltage it ran at, `stress_test load=100`, `mark_stable`, repeat. After each raise, loosen the wall a little until the clock reaches the point's new frequency, or the run only shows the same clock at a lower voltage (`manual tuning`).
- **Test one clock exactly**: `set_clock_lock minMhz=<MHz> maxMhz=<MHz>` and `stress_test load=40`: the card runs that clock at the voltage the curve gives it, wherever the load's power would have put it. Raise the lock 15 MHz per pass (or the curve there, which tests the same clock at less voltage); `lockMv` pins a point by its voltage instead. Release both with 0 when done: the floor goes by itself at the end of the trial, the cap would be kept.
- **Write the daily curve worked out from the boundary**: `set_curve` with the whole curve at idle (or `adjust_curve` down in bands), then `flatten_curve` where the chip stopped scaling; `stress_test` at load 100 and 50, then games, then `keep_as_daily` (ask).
- **Undervolt (the same clocks for less power)**: `shift_curve 25` at a time, tested like a raise; under a wall it turns into clock. To keep the clocks and only lower the voltage, `adjust_clock_domain core` with a negative demand, tested the same way.
- **The uncore, in order**: `adjust_msvdd` REL to 0, `adjust_xbar` demand +20 mV, offset +50 per call to +250..+300, then `set_clock_ratio` up 0.025 at a time if XBAR still sits at 0.9 x the core. Judge each by a benchmark, A B B A (`manual method`).
- **A quiet profile**: `set_daily_power_limit` lower (ask) and a gentler `set_fan_curve`; the curve stays, since a lower wall only moves the card down it. `save_profile` it under a name.
- **Read before and after every step**: `gpu_status` for one look, `limiter` for what holds the clock (and `perf_limits` for the driver's own ceilings), `card_settings` for what is set.

## What the newer controls did on the reference card
5090 FE, driver 617.42, 2026-10-21; each written at idle, read back exactly and put back by `back_to_daily`:
- `set_clock_lock`: Boost Lock held 3052 MHz in P0 at idle; a 2000 MHz cap held 1980 MHz at full load; a 900 mV point lock held 2775 MHz at 890 mV at load 50. A lock changes what a stress run measures: release it before mapping the curve.
- `set_clock_ratio`: stock 0.8999. At 0.92 XBAR ran 2595 MHz against a 2850 MHz core (0.911) at the 600 W wall, against 2535 / 2865 (0.885) at stock; MSVDD rose 980 → 1005 mV and the core gave 15 MHz back. Its gain in frames is not measured.
- `adjust_clock_domain` core: +5 mV read back as the curve's `voltage_demand_mv` +5.
- `set_power_cap`: the lower of FE and F8 holds (F8 500 W under FE 600 W held the board at 500 W), so F8 at the board maximum limits nothing. NVML's enforced limit in `gpu_status` shows a change a few seconds late.
- `set_current_limit` 650 A on NVVDD (above the 600 A default) and `set_voltage_boost` 1 % landed and changed nothing visible at idle.
- `set_fan_start`: 38:45 landed on the two curve policies only; the firmware's backstop policies were left alone.
