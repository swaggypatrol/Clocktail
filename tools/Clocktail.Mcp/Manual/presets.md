# Presets
The starting points that ship with Clocktail, what each part of one does, how to offer it, and the profiles the person saves under a name.

## founders (RTX 5090 only)
The reference card's daily profile, kept after four days of measurement. In write order:
| Setting | Value | Why |
|---|---|---|
| Power limit | 600 W | the board maximum; the last 100 W buy ~10 % frames |
| Fan curve | 50 C 20 %, 79 C 60 %, 86 C 100 % | holds ~79-80 C at 600 W, under the 82 C compensation step |
| Memory offset | +358 MHz | ~2.4 % lower memory latency; bandwidth unchanged |
| MSVDD offsets | REL 0 (stock -50), ALT 0, OV 0, VMIN 0 | XBAR runs faster; ~+2 % in Blender |
| XBAR voltage demand | +20 mV | keeps MSVDD off its floor (no cliff) |
| XBAR clock offset | +300 MHz | with the demand: Blender ~6-7 % faster than stock |
| V/F curve | stock to 800 mV, +405..+510 MHz at 850-885 mV, then +2 MHz/mV to 3071 MHz at 1035 mV, flat above | ~+200 MHz over stock under the wall |

Verified on the reference card: bit-exact compute tests, nvgwls at 50 % and 100 % load, and games (Kingdom Come: Deliverance II, Resident Evil Requiem, The Last of Us Part II, Cyberpunk 2077; 5-8 minutes each, no crash; 39-74 MHz under the crash line).

## Offering it
- `list_presets` says whether this card is the model a preset was tuned on; `load_preset` refuses any other model.
- Ask first. Say what it changes (the table above, in a sentence), that it replaces their daily profile (the old one is kept in the tuning log), and that their chip may not hold it.
- After loading: `stress_test load=100` for 60-120 s, then load 50 for 60 s, then have them play. If anything fails, it is their silicon, not the preset (and `back_to_daily` now means the preset). Lower the curve where it failed (`begin_session`, `adjust_curve` -15 MHz with `fromMv`/`toMv` around the voltage it failed at, from `data_points` or the stress run's p50 `nvvdd_mv`; the card is idle after a crash, test, `keep_as_daily` once it holds).
- If lowering the curve does not cure it and the failure was memory-bound or showed artefacts, XBAR +300 may be too much for this chip: `adjust_xbar` -50 MHz per step, test, `keep_as_daily` once it holds.
- From stock without a preset is equally valid; it just takes more sessions.

## Saved profiles
The service keeps one daily profile: the one it puts back after every boot and driver reset. A person who wants several ("quiet" for late nights, "games", "benchmark") saves the daily profile under a name and switches by name.
- `save_profile name`: saves the daily profile as it is kept. A trial on the card is not saved: `keep_as_daily` first when they want the card's current settings. A name already used is replaced: say so.
- `list_presets`: the presets and the saved profiles, each with its settings.
- `load_preset name`: a saved profile becomes the daily profile and goes on now, exactly as a preset does. It replaces the daily profile: if the current one is not saved yet and they may want it back, `save_profile` it first. Ask first.
- `delete_profile name`: deletes a saved one; the daily profile and the presets are untouched. Ask first.
- Names: whatever the person calls it, in any language, spaces and symbols included (not a preset's name, no line breaks, at most 64 characters). Case does not matter when loading. Saved profiles belong to this card: they live in its state folder (saved-profiles.json).
- A quiet profile is usually the daily one with a lower wall (`set_daily_power_limit`) and a gentler fan curve: the curve can stay, since a lower wall only moves the card down it.
