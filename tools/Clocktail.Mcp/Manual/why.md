# Why
Short answers to the questions people ask, each with the measurement behind it. Answer in plain words first, then the number.

## Why is my card stuck at this clock?
Run `limiter` while it is loaded. Under heavy load it is almost always the power wall: board power sits within 10 W of the limit, and the card climbs the curve only as far as the watts allow. Under light load it is the top of the curve (a tuned, flattened curve) or MAX voltage (a stock curve). Temperature throttling never happened on the reference card.

## Why doesn't more voltage give me more clock?
- Under a wall, voltage costs watts: power rises roughly with frequency x voltage squared. At a fixed wall, a higher voltage means a lower clock.
- The chip itself stops scaling: on the reference, ~16-20 MHz per mV at 855-875 mV, 2-3 MHz per mV above 885 mV, and nothing above ~980-1035 mV.
More clock under load comes from less voltage per MHz, not from more voltage.

## Why does an undervolt make it faster?
Same reason. A curve that runs each clock at a lower voltage uses fewer watts per MHz, so at the same wall the card reaches a higher clock. On the reference card, shifting the curve 25 / 50 / 75 mV left gave +30 / +60 / +97 MHz under a 575 W wall.

## Why does the clock drop when the card gets hot?
- The driver compensates the curve for temperature in coarse steps (near 57, 62, 72 and 82 C going up).
- Leakage rises with heat.
- Above ~950 mV a hot card runs 30-45 MHz slower than a cool one. At the 600 W wall heat costs ~2 MHz per C. Near the knee at 450 W it costs ~9 MHz per C, because the voltage cannot drop further.

## Why set the power limit and fans before tuning the curve?
They decide where on the curve the card runs and what the curve looks like at that moment. The wall sets which voltage heavy load sits at. The temperature shifts the whole curve. A curve tested at another wall or temperature was tested on points or clocks the card will not actually run.

## Why tune the curve instead of a core offset?
A core offset (`set_core_offset`) adds the same MHz everywhere, but this chip's headroom is not the same everywhere: +400-500 MHz at the knee (850-885 mV), almost nothing at the top. An offset large enough for the knee crashes the top; one safe for the top wastes the knee. The curve lets each voltage go to its own edge.

## Why is the top of my curve flat?
Above ~1035 mV the reference chip gained no clock: more voltage there only adds power and heat, and under the wall it takes watts from the clock. A flat top stops the card from climbing into voltage that buys nothing.

## Why 30-60 MHz below what the stress test passed?
Games are stricter than any stress test. Measured on the reference:
- From the stress boundary, "-15 MHz" crashed a game and "-30" held.
- Kingdom Come: Deliverance II crashed 37+ MHz under a stress pass at the same voltage, temperature and power.
- On the kept curve, games' p90 clock sat 39-74 MHz under the crash line.

## Why NVIDIA's stress tester (nvgwls)?
It is the driver's own: NVIDIA App's auto-tune runs it. It renders and computes once as a reference, then compares every later result bit for bit across graphics, compute and ray tracing. It catches silent errors, not just crashes. Most benchmarks only show a crash.

## It passed the stress test but crashed in a game. Why?
Games load the chip in ways a stress test does not: sudden load changes, other mixes of work. The stress test finds the edge; the margin and the game checks find what is safe. Lower that region 15-30 MHz, and never retry the crash value.

## Why does my game sit at 550 W when the limit is 600 W?
The wall tracks short peaks, not the one-second average. In one game, 10-times-a-second samples had a median of 552 W, p90 588 W and max 593 W, with NVML's power cap set 83 % of the time. A stress test is flat, so its average is its peak.

## Why does the overlay show a lower clock than the curve?
Under a tight wall the card throttles faster than an overlay samples: at 450 W, games showed ~300 MHz less than the curve point. The voltage still follows the curve point, so stability is a property of the point, not of the overlay's number.

## NVML says "idle" or "power cap" while I'm gaming. Why?
On a tuned curve with a flat top, NVML reports gpu_idle at full load. It reports sw_power_cap far under the wall when a power sub-policy (often memory-heavy work) holds the clock. Read board power against the limit instead; `limiter` does.

## Why this memory offset?
+358 was carried over from an earlier hand tune: it lowered latency 2.4 % and left bandwidth flat, because the crossbar held it. Once the uncore was raised, the lab swept the reference card's memory error-free to the driver's +3000; bandwidth stopped rising near +1758 and latency kept falling, so it went to +2918, the best of a fine sweep around +2858. Games decide: if one shows artifacts or stutter, step it down (`manual method`, phase 7).

## Why raise MSVDD? Is that safe?
NVIDIA ships the 5090's MSVDD 50 mV inside its own reliability limit (REL -50). REL 0 is that limit itself, not past it. It made the crossbar faster and Blender ~2 % faster for ~2 W. REL +20, past NVIDIA's limit, bought ~1 % more; Clocktail goes no further.

## Why does XBAR need a voltage demand?
Above about +250 MHz of XBAR offset, memory-bound work lets MSVDD fall to its 825 mV floor, and then the core is pinned near 1610 MHz with 14-16 % less bandwidth. The firmware's reason is unknown. +20 mV of demand keeps MSVDD off the floor up to +300; past +300 it takes +50 mV (at +400 with +20/+30 mV the memory phase still dropped to 1830-1940 MHz). Raising VMIN does not help.

## Why does XBAR stay at 0.9 x the core clock?
The driver holds XBAR to the core clock times a ratio, 0.8999 stock. Past about +250 MHz the XBAR offset runs into it and stops paying. `set_clock_ratio` raises the ratio: on the reference card 0.92 ran XBAR 60 MHz higher at the 600 W wall, for 25 mV more MSVDD and 15 MHz of core. Whether that buys frames is not measured.

## Why does my card stop at 575 W when the limit says 600?
The firmware's own board limit did not follow the request. It happened on the reference card after the service started and found the request already at 600 W: NVML said 600, the card held 575 (its VBIOS default) for hours. Every write of the request moves the firmware's limit, so the service checks the two whenever it puts the daily profile on and writes it again if they differ; `card_report` marks it, `back_to_daily` fixes it.

## Why 600 W and not more?
600 W is the board's own maximum power request on the 5090 FE. Going past it would need another VBIOS, which Clocktail does not touch.

## Will this damage my card?
The limits hold:
- Voltage never goes past NVIDIA's own ceilings (1150 mV at most; MSVDD at most 20 mV past its reliability limit).
- Power never goes past the board's own maximum. A rail's current limit may go above its VBIOS default for a trial (up to the driver's maximum, marked ⚠️), but on a 5090 the 600 W wall binds long before.
- The fans never go to full later than on the stock curve.
Most of the gain comes from running each clock at less voltage, which is gentler on the chip, not harsher. The real risk is a crash, which the service undoes. Warranty terms on overclocking vary by card maker; say so if asked.

## Why did my settings come back after a crash or reboot?
The service keeps the daily profile and writes it again after every boot and every driver reset. A driver reset alone puts the wall back to 575 W; the service restores the daily one first. `stock` turns that off until `load_daily`.

## Why does the agent go so slowly?
Each step is one change and one test, so a crash points at exactly one thing. Large steps were how the reference card hung: 2 of 3 hangs began right after a write under full load.
