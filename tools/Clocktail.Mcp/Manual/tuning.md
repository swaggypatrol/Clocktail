# The tuning method
How a session runs, how big a step is, how to test a part of the curve the game never reaches, and when something becomes daily.

## The loop
1. `begin_session` with the game, and `live_start` with it: the card is watched for the whole session, a line every 10 s under load. In Claude Code, follow its log with the Monitor tool and say what you see as lines arrive; otherwise `live_read` when the person talks.
2. Where the card runs: the live lines (clock, voltage and the curve point, what held it), or `gpu_status` for one look (NVVDD and the curve point at that instant; `adjust_curve` centres its window on the median under load over the last 3 s, or over the last 2 minutes once the load has stopped).
3. One change: usually `adjust_curve` (default window: 20 mV under the operating point to 10 mV above; at most 15 MHz per call; never more than 200 MHz above the daily profile; points under 850 mV are never touched).
4. Judge it: 5-10 minutes of play in a demanding scene (the live lines show the clock and voltage it held, and flag a suspected hang as it happens), or `stress_test`. Avoid writing under full load mid-fight: 2 of 3 hangs on the reference card began right after a write under load.
5. Record it: `mark_stable` with what was played, or `mark_crash` with what was seen. A driver reset is recorded by the service by itself.
6. Repeat from 3, or `back_to_daily` on any doubt (artefacts, stutter).
7. When a curve has survived real play, ask the person, then `keep_as_daily`. `end_session` and `live_stop` when done.

## Steps and margins
- Raise in 15 MHz steps. Near the edge, 7.5 MHz.
- The stress-stable edge is not the game-stable edge: **game-stable = the highest stress-passing clock minus 30-60 MHz** (see `manual stability` for the evidence). Do not keep anything at the edge.
- After a crash, step down at least 15-30 MHz from the crash value at that voltage and never retry the crash value.
- `data_points` -> `by_point` tells, per curve point, the highest offset proved stable, the highest that survived and the lowest that crashed, and the clock actually run there. A point is proved only when the card ran at (or near) its curve frequency.

## Reaching points the game does not
Under the wall, a game runs a narrow band of the curve. To test lower points:
- `set_power_limit` (400-600 W) moves the card down the curve. On the reference card's tuned curve under nvgwls full load: 600 W ~2842 MHz at 930 mV; 550 W ~2752 at 900; 500 W ~2625 at 875; 450 W ~2467 at 865; 400 W ~2272 at 860.
- Below what 400 W reaches, cap the frame rate in the game, or use `set_current_limit` on NVVDD (closed-loop, no trip; 350 A ~2145 MHz at 850 mV; 300 A ~1837 at 835; 250 A ~1530 at 820).
- Caveat: NVVDD current caps from about 380-400 A upward raise XBAR and MSVDD for unknown reasons (wall-only at 400 W was ~90 MHz faster than wall + 400 A). Prefer the wall above 400 W.
- Caveat: pinning a point with a limit and then raising the curve mostly tests whether a lower voltage holds the same clock, not frequency headroom. Under the wall a raise shows as the same clock about 1 mV lower. To test the new clock, loosen the wall or the current limit after each raise until the clock reaches the point's new curve frequency: on the reference card +15 MHz needed 480 → 490 W, or 350 → 360 A. At the edge the same setting passes one run and hangs the next; repeat the last pass before trusting it.

## What the reference curve looks like (for orientation)
Stock up to 800 mV; +30 MHz per point to +210 by ~840 mV; +405 to +510 through 850-885 mV (the knee is where an RTX 5090 gains most); then a straight line of about +2 MHz per mV from 2775 MHz at 885 mV to 3071 MHz at 1035 mV, flat above. Under the 600 W wall that is roughly +200 MHz over stock at the same power (+195 to +221 measured).

## Goals other than maximum clock
- **Undervolt / same clock for less power**: lower the curve's top half or shift it left; at the same wall the saved power turns into clock. Shifting the curve left 25/50/75 mV from the knee gave +30/+60/+97 MHz under a 575 W wall. Do not shift the whole curve left by 250 mV: that once crashed the whole system (bug check 0x133).
- **Efficiency**: board MHz/W peaks at 250-300 W (6.05 MHz/W) and is 4.74 at 600 W. Cutting the wall 600 -> 500 W cost GTA V only 9 % frames (86 -> 78 fps) for 17 % less power. Offer this when they care about heat or noise.
- **MSVDD undervolt** (`adjust_msvdd`): saves a few watts the wall gives back to the core, but slows XBAR; see `manual xbar-msvdd`.

## What each tool changes, and what is kept
| Tool | Changes | Kept by keep_as_daily |
|---|---|---|
| `adjust_curve` | the V/F curve around the operating point | yes |
| `flatten_curve` | the curve flat from a voltage up (only lowers) | yes |
| `set_curve` | the whole curve, every point's offset | yes |
| `shift_curve` | the curve on the card moved left by N mV (from the knee up) | yes |
| `set_core_offset` | the core P0 offset: every point at once (the curve is written again after it) | yes |
| `set_fan_curve` | the firmware fan curve | yes |
| `set_memory_offset` | the memory offset (the curve is written again after it) | yes |
| `adjust_msvdd` | MSVDD's REL and VMIN offsets, up or down | yes |
| `adjust_nvvdd` | NVVDD's REL, ALT, OV and VMIN offsets | yes |
| `adjust_xbar` | XBAR's clock offset and voltage demand | yes |
| `adjust_clock_domain` | SYS's or Video's clock offset and voltage demand; the core's voltage demand | yes |
| `set_clock_ratio` | XBAR's clock ratio to the core | yes |
| `set_voltage_boost` | the voltage boost percentage | yes |
| `set_clock_lock` | a clock cap, a clock floor, a V/F point lock or Boost Lock | yes |
| `set_power_limit` | the wall, for the session (to reach other points) | no: `set_daily_power_limit` sets the daily wall |
| `set_power_cap` | the F8 power request, for the session (ask first: it cannot be deleted) | never |
| `set_current_limit` | a rail's current limit, for the session | never |
| `set_fan_speed` | a fixed fan duty, for the session | never |

`keep_as_daily` keeps everything a trial set, not just the last change. Call `back_to_daily` first if only some of it should stay.

What the newer controls did on the reference card (5090 FE, 617.42, 2026-10-21; each written at idle, read back exactly and put back by `back_to_daily`):
- `set_clock_lock`: Boost Lock held 3052 MHz in P0 at idle; a 2000 MHz cap held 1980 MHz at full load; a 900 mV point lock held 2775 MHz at 890 mV at load 50. A lock changes what a stress run measures: release it before mapping the curve.
- `set_clock_ratio`: stock 0.8999. At 0.92 XBAR ran 2595 MHz against a 2850 MHz core (0.911) at the 600 W wall, against 2535 / 2865 (0.885) at stock; MSVDD rose 980 → 1005 mV and the core gave 15 MHz back.
- `adjust_clock_domain` core: +5 mV read back as the curve's `voltage_demand_mv` +5.
- `set_power_cap`: the lower of FE and F8 holds (F8 500 W under FE 600 W held the board at 500 W), so F8 at the board maximum limits nothing. NVML's enforced limit in `gpu_status` shows a change a few seconds late.
- `set_current_limit` 650 A on NVVDD (above the 600 A default) and `set_voltage_boost` 1 % landed and changed nothing visible at idle.
The order the phases go in, and why, is `manual method`.
