# The method
How the reference card was tuned to its safe maximum, phase by phase: what to do, what to tell the person, and why the order matters. Follow it from stock.

## The idea, in one paragraph
Under heavy load a modern GPU is held by its power limit, not by its clock. At a fixed wall the only way to more clock is less voltage per MHz: a V/F curve that runs each clock at a lower voltage lets the same watts buy a higher point. How far each voltage can go is a property of this one chip, found by stress testing at the temperature the card will actually run at; the game-safe curve sits 30-60 MHz under what the stressor passes. The wall and the temperature decide where on the curve the card runs, so they are set first. The uncore (MSVDD, XBAR and XBAR's ratio to the core) and then the memory come after the curve, in that order, and are judged by speed, not by a clock: the lab measures them by itself (`tune_uncore`, `tune_memory`), and a benchmark or a game confirms.

## Before you start, tell the person
- Time: one to three evenings. Stress runs need nobody at the keyboard; game checks need them for 5-10 minutes each.
- Expect a few driver resets: a 30-second freeze or black screen, then the desktop. Harmless; the service puts the daily profile back by itself.
- What the reference RTX 5090 got: about +200 MHz under the wall at the same power; Blender 6-7 % faster with MSVDD and XBAR's first settings; then, with the ratio and MSVDD raised along with XBAR, the crossbar 5-6 % faster again and the memory's bandwidth 7.8 % higher at the same offset; the memory error-free in the lab up to the driver's +3000, kept at +1758, where bandwidth stops rising (3DMark agreed). Their chip may do more or less; that is what the testing finds out.
- Close other tools that write clocks (MSI Afterburner, NVIDIA App's auto-tune) for the whole process.
- For the lab's jobs (phases 6 and 7) also close whatever else uses the GPU (a local AI model, a game, a launcher's download) and set the screen saver to none: a screen saver drawing cost the crossbar test 19 % on the reference card. The lab notices and measures again, for up to 10 minutes a run.
- Case airflow matters more than fan speed: every degree cooler case air is a degree cooler GPU.

## Phase 0: baseline at stock
Do: `card_settings` (confirm stock), `begin_session` ("baseline"), `stress_test load=100 seconds=180` to bring the card to its running temperature, then `stress_test load=100 seconds=60` and `limiter` during load.
Record: p50 clock, NVVDD, board power, temperature, and what holds it.
Expect (5090): ~2700 MHz at ~1025 mV, on the 575 W wall, 75-80 C.
Why first: every later gain is measured against this, at the same load and temperature. The clock under full load at the wall is the number games feel.

## Phase 1: the power limit
Do: ask what they want, then `set_daily_power_limit`. Maximum performance: the board's maximum (600 W on a 5090). Quieter, cooler, more efficient: 475-500 W.
Why before the curve: under heavy load the wall decides where on the curve the card sits. A higher wall moves heavy load up to higher voltages. A curve tested at 575 W and then run at 600 W runs points it was never tested at.
Data: GTA V 86 fps at 600 W, 78 at 500 W, 67 at 400 W. The last 100 W buy about 10 % frames for 17 % more power.
Check: under full load board power reaches the wall. `card_report`'s power row marks a firmware limit that differs from the request (575 W held under a 600 W request); `back_to_daily` writes it again.

## Phase 2: fans and temperature
Do: pick the temperature to hold at the wall, `set_fan_curve`, then `stress_test load=100 seconds=240`: steady means the last minute moves less than 1 C. Optionally show the trade first with a fan sweep: `set_fan_speed` 100 / 75 / 60, `stress_test` 150 s each (never hold 45 % or less at 600 W), then `set_fan_speed auto`.
Target on the reference: 78-80 C at 600 W, fan ~60 %. Curve `50:20,79:60,86:100`. `keep_as_daily` once it holds (ask).
Why before the curve: the V/F curve moves with temperature.
- The driver shifts it in coarse steps, rising near 57/62/72/82 C and falling near 70/61/53/43 C.
- Above ~950 mV a hot card runs 30-45 MHz slower than a cool one. Around 875-915 mV it runs faster hot.
- Leakage costs ~2 MHz per C at 600 W and ~9 MHz per C near the knee at 450 W.
A curve tuned on a cool card is tuned on a different curve. Test at the temperature the fan curve gives in real use. Staying under 82 C avoids the last step, which costs 8-15 MHz at 940-990 mV.
Why not colder: under ~72 C needs 75 %+ fan for about 15 MHz. The stock curve's 83 C target trades ~20 MHz for quiet.
Model: steady temperature ≈ case air + power x (0.023 + 0.030 / fan fraction) C. The reference had ~35 C case air: 600 W at 60 % gives ~79 C.

## Phase 3: map the chip's V/F boundary
Goal: for each voltage, the highest clock `stress_test` passes. That is this chip's edge, and everything after is built on it.
Three loads reach three regions of the curve:
| Region | How to reach it | Reference edge |
|---|---|---|
| Knee, 850-900 mV | `set_power_limit` 450-550 W + `stress_test load=100` (or `set_current_limit` nvvdd 300-350 A for lower) | 2415 MHz at 855 mV, 2745 at 870-875 (stock ~2230 at 870) |
| Middle, 900-950 mV | the daily wall + `stress_test load=100` | 2850 MHz at 900 mV, 2880 at 910 |
| Top, 950-1035 mV | `stress_test load=30-50` (light load climbs to the top of the curve); `set_clock_lock` minMhz = maxMhz holds one clock there | 3015 at 955 mV, 3105 at 975-985 |
Per region:
1. `begin_session` ("stress").
2. Bring the card to temperature.
3. `adjust_curve +15` around the point the last run held: pass `fromMv`/`toMv` around its p50 `nvvdd_mv` (about -15/+10 mV). Between runs the card is idle, so there is no operating point of its own to centre on.
4. `stress_test` 60 s.
5. Pass: `mark_stable` and go to step 3. Fail: the trial is recorded as a crash and the daily profile goes back by itself.
The last pass is the edge there. `data_points` -> `by_point` keeps the score per point.
What the reference showed:
- The knee (850-885 mV) has the most headroom, because the stock curve is conservative there: 16-20 MHz per mV up to 875 mV.
- From 885 mV the chip only gains 2-3 MHz per mV.
- Above ~980-1035 mV it stops scaling at all.
Why a curve, not a core offset: an offset raises every point by the same MHz. The knee could take +500 while the top takes almost nothing. An offset big enough for the knee crashes the top; one safe for the top wastes the knee. The curve lets every voltage go to its own edge.
Why small steps at temperature: the edge moves with temperature (6 C moved a 450 W landing by a full point), and 2 of 3 hangs on the reference began right after a write under full load.
Note: `adjust_curve` goes at most 200 MHz above the daily profile. From stock, map in rounds: raise, test, `keep_as_daily` (ask) a margin below the last pass, continue. Release a test cap (`set_clock_lock maxMhz=0`) before `keep_as_daily`: a cap is kept (a floor, point lock or Boost Lock never is). For a run nobody watches (overnight), `begin_session unattended=true`: after `maxCrashes` crashes (2 by default) it writes no new trial.

## Phase 4: shape the daily curve
- At each voltage: the boundary minus 30-60 MHz, the game margin.
- Smooth and rising. On the reference: stock to 800 mV; +30 MHz per point to the knee; +405..+510 at 850-885 mV; then a straight line of ~+2 MHz per mV (2775 MHz at 885 mV to 3071 at 1035 mV); flat above.
- A flat top where the chip stops scaling: voltage there buys no clock, only power and heat. Under the wall it even costs clock, because the watts spent on voltage are taken from frequency. `flatten_curve fromMv` does it in one trial at idle (it only lowers): every point from that voltage up runs at most what the point there runs now, less `marginMhz`.
- Leave points under ~800 mV stock: the card only runs there idle.
- Write it in one trial at idle with `set_curve` (every point's offset, as `card_settings` shows them), or reach it with `adjust_curve` down in bands; then `flatten_curve` for the top. `shift_curve` moves the curve on the card left (the same clocks at less voltage): a raise everywhere, tested like one.
Why the margin: games are stricter than the stressor. On the reference, "boundary -15" crashed a game and "boundary -30" held. Kingdom Come: Deliverance II crashed at least 37 MHz under an nvgwls pass at the same voltage, temperature and power.
The margin depends on where the game runs: 15-30 MHz under the wall, 37 MHz or more at 970 mV in the middle band on the reference card. To measure this card's own: hold the game in one voltage band (the wall, or `set_power_limit` / `set_current_limit` for the middle), play it with `live_start`, and on each crash lower that band 15 MHz (`adjust_curve -15` with fromMv/toMv around the crash voltage); the total lowered until it holds is the margin there. Do it once at the wall and once in the middle band; the daily curve is then the boundary minus the larger margin, minus another 15.

## Phase 5: games
Do: 2-4 of their most demanding games, the heaviest scenes, 5-10 minutes each. Before they start, `live_start` with the game: it samples the card 4 times a second and writes a line every 10 s under load (clock range, voltage and the curve point, MSVDD, XBAR, power against the wall, temperature, fans, what held the clock), plus a line when load starts or stops, the clock freezes, the card stops answering or runs hot. Each line under load ends with the game's frame rate over those seconds (average, 1 % and 0.1 % lows), and the summary has the whole run's: the service captures it with Intel's PresentMon, nothing injected into the game. When a line says there are no frame rates, ask the person what they see. In Claude Code, follow its log with the Monitor tool (the command comes back in `follow`) and say what you see as lines arrive, as the reference card was tuned; any other agent calls `live_read` when the person talks. `live_stop` at the end puts the summary in the tuning log. For one fixed stretch you wait for, `watch` 120-300 s. Judge the p90 clock at its voltage against what `stress_test` passed there. Afterwards, `log_note` what they saw.
- A crash: the service puts the daily profile back. Use `mark_crash` if the driver did not reset. Lower that region 15-30 MHz and never retry the same value.
- All hold: `keep_as_daily` (ask).
Why: a stress pass is necessary, not sufficient. Their own games are the final judge.

## Phase 6: the uncore (MSVDD, XBAR, the ratio)
Why before the memory: the crossbar XBAR clocks carries every byte between the GPCs, the L2 and the memory, and it can hold the memory's bandwidth. On the reference card at XBAR +250 / MSVDD REL 0 the memory's bandwidth stayed flat from +0 to +358 (1437-1440 GB/s) and +358 to +858 gave only 2.9 %; after the uncore below, the same +358 read 7.8 % more (1571 → 1693 GB/s) and the memory kept climbing to about +1758. A memory sweep before the uncore measures the crossbar.
How XBAR is held: under load it runs in a window about 30 MHz wide, its floor the ratio x the core (0.9 stock) and its ceiling the clock MSVDD's reliability limit allows (`card_settings` perf_limits, `RELIABILITY_MSVDD_0`). Raise one alone and little moves; the ratio alone can even pull the core down (0.95 at REL 0: -3.5 %). They go up together, and then XBAR's offset pays.
Do:
1. Close other GPU work, screen saver to none, `begin_session` (`unattended=true` if they will be away).
2. `tune_uncore` (20-40 minutes; it keeps nothing): XBAR with its voltage demand, the ratio, MSVDD REL up to +50 mV (ALT goes up with it past +20), and the ratio and MSVDD together, each candidate A B B A against the best so far on the lab's crossbar (L2) test. `lab_status` names the best. To check particular settings, `tune_uncore candidates=...` compares each A B B A against the settings on the card.
3. Put the best on in the tools' steps: `adjust_msvdd` REL +25 per call, `set_clock_ratio` 0.025 per call, `adjust_xbar` demand +10 per call first, then the offset +50 per call.
4. `stress_test` at load 100 and 50, `lab_test phases=l2,memory,latency`, then `keep_as_daily` (ask). Their games confirm it, judged on frames.
By hand instead: `adjust_msvdd` REL to 0 (NVIDIA's own limit), `adjust_xbar` +20 mV and +250..+300, each judged by a benchmark A B B A (Blender: REL 0 ~2 % faster, XBAR +300 / +20 mV 6-7 %).
Reference result: XBAR +400 / +50 mV, ratio 0.95, MSVDD REL +50 / ALT +30 (1145 mV): the crossbar test 5-6 % faster than XBAR +250 / REL 0 / ratio 0.9, the memory's bandwidth 7.8 % higher at the same offset. +500 was 0.2 % more; +400 was kept for margin. It passed `stress_test` at 100 and 50; games not measured yet.
The trap: above +250 with no voltage demand, memory-bound work drops MSVDD to its floor and pins the core near 1610 MHz. The tools refuse it. If something is unstable here, step XBAR down 50 MHz before touching the curve.

## Phase 7: memory
Do:
1. `tune_memory` from the daily offset in 100 MHz steps up to the driver's limit (+3000 on a 5090), 30 s a step. It stops where the memory fails, or gets slower and stays slower (two steps in a row; one slower step is only watched). `trend=true` draws the whole curve; then only a failure stops it.
2. Read the steps for where bandwidth stops rising: that is the pick, not the sweep's "best" (the edge's side) and not the lowest latency (a GPU hides latency; on the reference card -6 % of it bought no frames). A fine sweep around it confirms it: `tune_memory fromMhz=<pick-300> toMhz=<pick+300> stepMhz=30 seconds=60 trend=true`. The offset need not be round: nothing on the reference card cared for multiples of 15 or 30.
3. `set_memory_offset` to it (250 MHz per call), `stress_test` at load 100 and 50 (a data error is exit code 6), `lab_test phases=memory,latency,l2`.
4. Judge it against the daily offset with a memory-sensitive benchmark A B B A: a ray-traced one (3DMark Speedway, a ray-traced game's benchmark); Steel Nomad judges the core, not the memory. Then `keep_as_daily` (ask). Their games have the last word: artifacts, a crash or stutter, step down; the start of the plateau costs only latency.
Reading a sweep: GDDR7 hides its errors by retrying them, so past its edge the memory gets slower before it fails: bandwidth falling while the clock rises, latency rising and not coming back down. One jump is not that. The memory changes its timings at some clocks (latency +17 ns at +958 on the reference, the same in three sweeps, then falling with the clock again), and one run can dip (+1558: -0.9 to -1.2 %, then the best yet at +1658). The spread between runs rising (1 % at +358, 6-9 % past +2700 on the reference) may be the memory starting to retry where the lab cannot see it, which is what the games are for.
Reference (on the uncore above): +358 to +3000 with no error; bandwidth 1693 → about 1795 GB/s at +1758, then flat; latency 475 → 429 ns, about 2.2 ns per 100 MHz. 30 MHz steps +1458 to +2058: no timing change, bandwidth flat from ~+1550. 3DMark: Speedway a little better at +1758 than +358, Steel Nomad barely moved, +2900 no better than +1758 in either. Daily +1758.
Why after the curve: a memory write zeroes the curve (the tool writes it back in the same trial), and a failure should not be confused with the curve's.

## Comparing two settings: A B B A at one temperature
Use it whenever the question is "is B faster than A" (an undervolt, MSVDD, XBAR, memory, two curves), not "does B hold".
1. Warm the card up first: `stress_test load=100 seconds=120`, or the game, until the temperature stops climbing. The curve drops as the card heats (steps near 57/62/72/82 C, ~2 MHz per C at 600 W), so a comparison run across a warm-up measures the warm-up.
2. Alternate, back to back, each step the same length: A, B, B, A (twice for a small effect). Each setting then sees the same average temperature. Never A A A then B B B.
3. Measure one repeatable thing per step: `stress_test` at a fixed load (its p50 clock and power), a game's built-in benchmark, or a render from the command line (Blender Cycles, the median of several renders). In their own game: the same scene, with `watch`.
4. Report each difference with the temperature beside it, and the spread between the two A runs: a difference smaller than that spread is noise.
On the reference card:
- MSVDD REL -50 → -175 under the 600 W wall, run as a ladder while the card heated from 68 to 83 C: "no gain" (2835-2850 MHz throughout). The same change A B B A at 83-84 C: +40-60 MHz, one to two curve points. The ladder had measured the warm-up.
- Blender (2560x1440, 384 samples, OptiX, median of 7), REL -50 / -25 / 0 in A B C C B A: 6.774 / 6.697 / 6.646 s, with repeats within 0.4 %, so the 2 % is real. A ray-tracing score that moved between 0.95 and 1.05 from one round to the next decided nothing.
Use `log_note` for each step's number and temperature as you go, so the comparison survives the session.
The lab's jobs do this by themselves: `tune_uncore` runs each candidate A B B A, and both jobs measure a run again when other work shared the GPU.

## Phase 8: keep it, live with it
- `keep_as_daily` (ask). The service puts it back after every boot and driver reset.
- A new game crashes later: `data_points`, lower that region 15-30 MHz in a session, keep again.
- After a driver update: `stress_test` 60 s at load 100 and at load 50. The driver owns the stock curve and the temperature compensation; `card_report` says when the driver is not the one the profile was kept on.
- After a VBIOS flash: the daily profile does not go on by itself (its offsets were found on the old stock curve). `load_daily` puts it on and binds it to the new VBIOS; `stress_test` at load 100 and 50 right after, and lower in a session where it fails.
- They want stock for a while: `stock`. Back: `load_daily`.

## Guiding the person
- Before each phase: what you will do, how long it takes, what they will see (fans, a possible freeze), and what you need from them.
- After each phase: one sentence with the number ("at the 600 W wall: 2842 MHz at 930 mV, 71 C; the wall holds it").
- Run stress tests while they are away. Ask them to play only for the game checks.
- Never set the daily curve at the edge, even if they ask; show them the crash data instead.
