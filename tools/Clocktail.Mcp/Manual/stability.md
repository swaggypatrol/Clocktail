# Stability: the judge, the margin, the crash
What nvgwls is and how to run it, why games need 30-60 MHz more margin, what a crash looks like, and what to do after one.

## nvgwls.exe, NVIDIA's own stressor
- Ships with the driver (inside the driver store's NvContainer folder); NVIDIA App's auto-tune runs it. It only adds load; it never writes to the GPU.
- Work: graphics (tessellation, Blinn-Phong, texturing, full-screen depth overdraw, mesh shaders, integer ALU), compute (all SMs in lockstep, FP16/32/64) and ray tracing (procedural spheres, triangles, shadow rays).
- Judging: it renders once as the reference; every later result must match bit for bit (float images within 1e-6). It catches silent errors a game never shows.
- Exit codes: 0 pass, 1 cancelled, 2 runtime error, 3 TDR, 4 crashed, 5 RC/Xid, 6 data error (silent corruption). Not exited 30 s after its run time: hung (killed). A hang the driver recovers from also ends in code 2: `stress_test` reads the System log for a driver reset during the run (nvlddmkm 153, Display 4101) and calls that a hang, whatever the code (`driver_resets` in its answer).
- `stress_test load=100`: continuous full load. On the reference card it pins the 600 W wall within 1 s (~2880-2917 MHz on the tuned curve, 930-945 mV). A good fast judge: it reproduced a bad curve's hang within 30 s.
- `load` under 100: a 250 Hz square wave at that duty. `load=50` ran ~385 W at ~3055 MHz, the curve's top. Use it to test high-voltage points the wall never lets full load reach.
- Combine with `set_power_limit` / `set_current_limit` to pin it lower down the curve.

## Why games need more margin than the stressor
Measured, not assumed:
- Moving the curve down in 15 MHz steps from the nvgwls boundary: "boundary -15" crashed a game (550 W, 915-930 mV, after about a minute); "boundary -30" held.
- Kingdom Come: Deliverance II crashed at 970 mV / 2940 MHz / 460 W / 73 C, where a 55 % duty nvgwls load had passed 2977 MHz at the same voltage, temperature and power for 150 s: the game was at least 37 MHz stricter. A 73-minute, 60-run nvgwls torture of that profile did not predict it.
- On the kept curve, games' p90 clock sat 39-74 MHz below the crash line (2940 + 2 x (mV - 967) MHz); 3DMark Speed Way 40-60 MHz.
- Crash points line up: game crashes and nvgwls full-load hangs fall on about 2 MHz per mV.

Hence the rule: **game-stable = the highest stress-passing clock minus 30-60 MHz**. A stress pass is necessary, not sufficient. The person playing their own games is the final test.

## Stability boundaries on the reference card (nvgwls, for orientation)
Full load, 60 s per step (passed / hung, MHz): 855 mV 2415 / -; 870-875 mV 2745 / -; 885 mV 2820 / 2820 (one run each way); 890 mV 2827 / 2835-2842; 895 mV 2842 / 2857; 910 mV 2880 / -; 915 mV - / 2887 (once). Slope ~16-20 MHz/mV at 855-875 mV, ~3-5 at 875-885, ~2-3 above: the curve's bend is 875-885 mV.
Light load (40 % duty, ~65 C): 930 mV 2947 / 2970; 940 mV 2970 / 3000; 955 mV 3015 / 3045; 975-985 mV 3105 / 3127; 1000-1005 mV 3097 / 3120. Light load is about 55 MHz looser than heavy load at 930 mV. These light-load points were pinned with `nvidia-smi -lgc` clock locks (`set_clock_lock` minMhz/maxMhz sets the same record); without one, a load lands where its power puts it on the curve.
Which nvgwls load reaches which voltage on the founders curve at 600 W: load 30 ~950 mV; 40-45 975-990 mV; 50 1005-1020 mV; 55-64 965-985 mV at 430-550 W; 72 and up the wall, 905-930 mV. Kingdom Come: Deliverance II crashed in the 970 mV middle band, which load 55-64 reaches: use it for that band.

## What a crash looks like
- **V/F hang (the usual one)**: the screen freezes for about 30 s (the TDR delay), the readings freeze at high clocks (3105-3180 MHz at 995-1090 mV, power falling to 125-200 W, utilization 100 %), then the driver resets (event `nvlddmkm 153`, Display 4101). nvgwls exits with code 2. No blue screen. The service records the trial as a crash and puts the daily profile back by itself.
- **Game crash to desktop or freeze, no driver reset**: the service cannot see it. Call `mark_crash` with what the person saw.
- **Whole-system crash** (bug check, reboot): rare; once from a 250 mV whole-curve left shift. The service checks for an unclean shutdown after boot.
- **Artefacts, flickering, odd colors**: treat as a crash: `back_to_daily`, then `mark_crash`.

## After a crash
1. A driver reset: the service has recorded the crash and puts the daily profile back once the GPU is idle (about 15 s; card_report says when it is on). A crash to the desktop with no reset: `mark_crash` first, which records it and puts the daily profile back. Then tell the person their card is safe.
2. `data_points`: which trial, which points, how long it ran.
3. The crash voltage the flight recorder reports is its pick of the last full-load sample before the freeze: it skips the frozen tail (with a 30 s TDR delay, ~30 s of the same clock and voltage at ~125 W) and the one sample of the limit letting go (power down, clock and voltage up), so on the reference hang it gives 2895 MHz at 910 mV / 598 W, not 2947 at 970. `data_points` puts the crash on the point nearest it. Check it when you can: If a live watch was running, its CSV has every sample (4 a second): find that last one there (`live_read` with tailSeconds shows the last 30 s). Otherwise use the trial's operating point.
4. Step that region down 15-30 MHz below the crash value; never retry the same value.
5. Note it with `log_note`, including the game and scene.

## Durations
A 60 s `stress_test` at load 100 catches a bad curve fast; 120-300 s before keeping something. Games: at least 5-10 minutes in the heaviest scene they know, per change.

## Windows' timeout (TDR)
card_report's "Driver timeout (TDR)" row reads Windows' own settings, never writes them. With TdrLevel 3 (the default) a hung GPU is reset after TdrDelay seconds (2 by default, 30 on the reference machine); until then the readings freeze at the curve's top, which is why a crash's frozen tail lasts that long. stress_test waits TdrDelay + 15 s (at least 30) past its run time before it calls a run hung. With TdrLevel 0 or 1 a hang does not recover by itself (the PC freezes, or bug-checks): do not map the curve's edge until it is 3.
