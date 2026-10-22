# MSVDD and XBAR
The interconnect rail and the crossbar clock: the free +2 %, the +6-7 % with XBAR, and the cliff to stay away from.

## MSVDD REL: NVIDIA's own reliability limit
- Stock MSVDD REL is -50 mV (MAX 1045 mV). Raising it to 0 (MAX 1095 mV) lets XBAR run faster where MSVDD was holding it: XBAR at 50 % load 2340 -> 2385 -> 2430 MHz and at full load 2190 -> 2240 -> 2285 MHz for REL -50 -> -25 -> 0 (about +45 MHz per 25 mV).
- Result on the reference card: **Blender Cycles 1.9-2.3 % faster at REL 0**, memory bandwidth unchanged, MSVDD about 2 W more, so the core runs 15-23 MHz lower under the 600 W wall; net still about +2 %. It passed every bit-exact test and nvgwls at 50 % and 100 % load.
- REL +20 adds about 1 % more and is past NVIDIA's limit (MAX 1115 mV via ALT/OP). The reference profile stays at REL 0.
- The opposite, an MSVDD undervolt (`adjust_msvdd`): REL -175 saved ~10 W (MSVDD 935 -> 830 mV) that the wall gave to the core (+40-60 MHz), but XBAR fell ~300 MHz (2182 -> ~1880). Games that lean on L2/crossbar bandwidth may lose more than they gain; untested. MSVDD tracks the core's demand minus REL, down to its VMIN floor (825 mV stock).

## XBAR clock offset and voltage demand
- The XBAR offset raises XBAR at the same voltage (+75 MHz moved XBAR 2340 -> 2422 MHz with nothing else changing).
- Under load XBAR is held to **about 0.9 x the core clock**: the driver's clock ratio (0.8999 stock). About +250 MHz of offset reaches that; more only lowers the MSVDD voltage XBAR needs.
- **The XBAR offset pulls the SYS clock down** in the memory phase: XBAR 0 / +200 / +300 / +400 gave SYS 2182 / 2017 / 1912 / 1830 MHz. Giving SYS +150 back (`adjust_clock_domain sys`) at XBAR +300 changed nothing in a Blender render (A B B A, 5.846 / 5.846 / 5.832 / 5.834 s, 2026-10-21); its effect on games is not measured. Read `sys_mhz` in stress_test and watch when judging an XBAR step.
- The **voltage demand** (-25..+50 mV) raises MSVDD for XBAR's points. It is what keeps MSVDD off its floor at large offsets.

Blender Cycles (2560x1440, 384 spp, median of 7, lower is better), stock 6.774 s:
| Setting | Time | vs stock |
|---|---|---|
| REL 0 | 6.62-6.65 s | ~ -2 % |
| REL 0, XBAR +250, demand 0 | 6.345 s | -6.3 % |
| REL 0, XBAR +300, demand +20 mV | 6.33-6.39 s | -6 to -7 % |
| REL 0, XBAR +400, demand +30 mV | 6.322 s | -6.7 % |

## The ratio: XBAR's ceiling
- `set_clock_ratio` moves the 0.9: the driver's clock propagation ratio from the core (NVVDD) to XBAR (MSVDD). A higher ratio lets XBAR follow the core closer, about +29 MHz of XBAR per +0.01 at a 2900 MHz core, and asks MSVDD for the voltage that clock needs.
- On the reference card at the 600 W wall: 0.92 ran XBAR 2595 MHz against a 2850 MHz core (0.911); stock ran 2535 / 2865 (0.885). MSVDD rose 980 → 1005 mV and the core gave 15 MHz back to the wall. Its gain in frames or Blender is not measured.
- When: after MSVDD REL 0 and XBAR +250..+300 with its demand, if XBAR under load still sits at 0.9 x the core. 0.025 per call. The demand rules below apply to the clock XBAR then reaches; judge each step A B B A with a benchmark that uses memory.

## The cliff
- With a large XBAR offset and no extra demand, memory-bound work lets MSVDD fall to its floor (825 mV). When it sits there, the **core is pinned near 1610 MHz and bandwidth drops 14-16 %** (1460 -> 1221-1259 GB/s). At demand 0: clean from +0 to +250, marginal at +300 (an occasional drop to 1687 MHz), the cliff at +350 and +400. No hysteresis: back down, back to normal.
- Raising VMIN does not fix it (MSVDD then sits on the new floor, core still ~1620 MHz). **The voltage demand does**: at +400, demand +20 mV kept MSVDD at 845 mV and restored 1446 GB/s and a 2445 MHz core.
- +20 mV holds only up to +300. At +400 the memory phase's P10 core still fell to 1942 MHz with +20 mV and 1830 MHz with +30; only +50 mV kept it clean (2392). So past +300 the demand must be +50 mV, and +400 was no faster than +300 in Blender anyway.
- The firmware mechanism is unknown. Stay at +250-300 with +20 mV, which is what the founders preset holds (XBAR +300, +20 mV).
- An overclocker reported +450 MHz without extra voltage giving a grey screen and artefacts.
- **If anything is unstable with XBAR raised, step XBAR down 50 MHz at a time** before touching the curve.

## The tuner (`tune_uncore`)
- It searches what this topic describes, on this card: XBAR's offset a step at a time with the demand the cliff rule asks for (+20 mV above +250, +50 above +300; more when a step still hits the cliff), then the ratio, then MSVDD REL, each candidate a trial compared A B B A against the best so far.
- The measure is the lab's L2 test: a 32 MiB region (a third of the 5090's L2) filled and verified over and over, so the traffic is the crossbar's and not the DRAM's (~4780 GB/s at the daily profile on the reference card, against ~1575 for DRAM). A candidate is kept when it is faster in both pairs and by 0.5 % or more, DRAM bandwidth does not fall, and the memory phase's core clock does not drop 5 % (the cliff).
- It keeps nothing: `lab_status` names the best setting; set it with `adjust_xbar`, `set_clock_ratio` and `adjust_msvdd`, then `lab_test`, `stress_test` and games, then `keep_as_daily` (ask). Its gains in games are not measured; the reference card's XBAR +300 with +20 mV made Blender 6-7 % faster.

## Reading it
`card_settings` shows each domain's offset and demand and MSVDD's REL/ALT/OV/VMIN offsets and limits; `gpu_status` shows MSVDD's mV, amps and watts; `stress_test` and `watch` report the XBAR clock under load. `card_settings` `clock_ratio` shows the ratio. `adjust_msvdd`, `adjust_xbar`, `set_clock_ratio` and `adjust_clock_domain` change them (`manual method`, phase 7). A memory-bound game on a large XBAR offset with MSVDD at exactly 825 mV and the core stuck near 1600 MHz is the cliff.
