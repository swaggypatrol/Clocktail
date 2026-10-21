# Memory (GDDR7)
What the memory offset does and does not buy, its units, and the trap that comes with writing it.

- The memory offset is a P0 offset (MHz). The reference card's daily profile uses **+358 MHz**: memory clock 13801 -> 14159 MHz as NVML reports it.
- **NVML reports twice the real offset** (+358 shows as +716). Clocktail's readings are the real value.
- What it buys on the reference card: **latency, not bandwidth.** +0 / +179 / +358 gave 1437 / 1440 / 1439 GB/s, and 531 / 524 / 518 ns per hop (-2.4 %). Later runs at +358: 1454-1461 GB/s, 504-509 ns.
- The +358 value was carried over from an earlier hand tune, not searched for. Nobody has measured where this card's memory gives out; treat higher values as untested.
- **Writing the memory offset zeroes every V/F point offset** (it is a P0 write). The curve must be written again afterwards. Presets and the daily profile apply it before the curve, so loading one is safe; a hand-made change is not.
- Memory errors show up as artefacts or as a data error in `stress_test` (exit code 6), not always as a crash.
- `set_memory_offset` changes it as a trial (at most 250 MHz per call; the curve is written back right after). How to step it and judge it: `manual method`, phase 6.
- **Memory temperature:** `memory_c` in `gpu_status`, the P10/P50/P90 of `stress_test` and `watch`, and every CSV. It is the memory chip temperature (HWiNFO's "memory chip", in whole degrees), not the junction, which reads about 4 °C hotter at idle and meets it at the peak. On the reference card: 52 °C idle, 80 °C after 90 s of full load, 82 °C at the peak. Watch it while raising the offset: a memory step that only fails warm shows up here first.
