# Troubleshooting
When a tool fails, when the card or the service misbehaves, and where things live.

| Symptom | What it means | What to do |
|---|---|---|
| "the Clocktail service does not answer" | the service is not installed or not running | `install_service` (one admin prompt); if it was installed, tell the person to run `npx -y clocktail verify` |
| A write's answer has `written` | whether it reached the card: `none` (refused, or nothing changed), `applied` (it landed and was read back), `unknown` (a write could not be confirmed or undone, or the service stopped answering after the request went out) | on `unknown`, read `card_settings` before anything else and tell the person; never repeat the write blind. `code` names a refusal (`judge_busy`, `window_busy`: wait and try again) |
| A write is refused with a limit in the message | the firmware's own limit or Clocktail's ceiling (1150 mV, current defaults, 400-600 W) | do not work around it; explain the limit |
| A write is refused because a stress run is measuring | the service stands down while a stability run holds its lock | wait for the run to end |
| `load_preset` refuses | the card is not the model the preset was tuned on | tune from stock |
| Screen froze ~30 s then came back | a driver reset (TDR); the daily profile is already back on | `data_points`, step down, see `manual stability` |
| Game closed or froze, no driver reset | a crash the service cannot see | `mark_crash` with what they saw |
| A tool answers "Clocktail's reader process ended while this call ran" or "is not running" | the driver went away (a reset, an update, a device restart) or the reader crashed in it; the server itself stays up and starts a new reader, which opens the driver that came back | wait a few seconds and call again; nothing needs reconnecting. A write cut off this way says `written: unknown`: read `card_settings` first. A live watch that was running ended with it: `live_start` again |
| Readings all zero right after a driver reset | the driver is still reloading | wait 10-20 s |
| card_report: Power limit ⚠️ "the card enforces 575 W, not the 600 W asked for" (card_settings: `power.firmware_w` ≠ `limit_w`) | the request reads the daily wall but the firmware's board power policy holds another, the VBIOS default; seen on the reference card on 2026-10-21 after the service started and found the request already at 600 W | `back_to_daily`: it writes the request again (a step away and back) whenever the two differ, as the service does at every start, wake-up and driver reset |
| card_report: "Daily profile … not on the card" (card_settings: `daily_differs_on_card`) | the card does not hold what the daily profile keeps: after a boot or a reset the service writes it once the GPU has idled a few seconds; or something else wrote over it (another overclocking tool) | if a boot or reset was just now, wait a minute and look again; otherwise `back_to_daily` puts it on now. If it never comes back by itself after a boot, tell the person to run `npx -y clocktail verify` and read `broker.log` |
| Power limit back to 575 W after a crash | normal: a driver reset restores the stock wall | the service puts the daily wall back first |
| card_report: the daily profile was kept on another VBIOS | the VBIOS was flashed; the profile's offsets were found on the old stock curve, so it does not go on by itself and the card stays at stock | tell the person; `load_daily` puts it on and binds it to this VBIOS, then `stress_test` at load 100 and 50 right after, and lower in a session where it fails |
| card_report: a clock lock another tool left (card_settings `clock_locks`) | `nvidia-smi -lgc`, Afterburner or NVIDIA App set a lock in the driver's clock-limit record; it outlives the tool | tell the person; it changes every measurement. `set_clock_lock` with 0 releases it in a trial, or they release it in the tool that set it (`nvidia-smi -rgc`) |
| card_report: 16-pin input ⚠️ | the connector passed 600 W or the input current came within 10 % of its limit | not a setting: tell the person to check that the cable is seated and not bent near the plug; a lower wall lowers it |
| card_report: PCIe link ⚠️ | under load the link runs a lower generation or width than the card and slot allow, or it resent data since boot (replays) | not a setting: tell the person to check the card is fully seated and any riser or extension cable, and the BIOS's PCIe slot setting (Auto or the slot's generation). |
| card_report: current limit ⚠️ above the VBIOS default | a trial raised it (allowed up to the driver's maximum) | normal during that trial; it goes back to the default when the trial ends |
| A write refused with `gpu_hung` | the GPU looks hung; a driver reset usually follows | wait; the service records the reset. Then `data_points` |
| A write refused with `crash_budget` | an unattended session had its allowed crashes | `end_session`, read `data_points`, then `begin_session` again |
| `stress_test` stopped at 85 C | the card got too hot (neither pass nor crash) | warmer than the run should be: check the fan curve and the case air, then run again |
| `back_to_daily`, `load_daily` or `end_session` answers with `not_applied` (broker.log: "partly applied") | one write of the daily profile did not go on (refused, vetoed, or dropped by the driver), so the writes tuned with it, its group, stay at stock and the other groups are on. The groups are the wall, the fans, the core (the curve with NVVDD's offsets, the P0 core offset, the boost), the uncore (MSVDD with XBAR, SYS, Video and the XBAR/core ratio) and the memory offset: XBAR +400 and ratio 0.95 were found with MSVDD REL +50, and without it the ratio slows the core | tell the person which group stays at stock and the reason in `why`. Before 0.3.3 the profile stopped at that write instead, leaving the curve flat after a memory write |
| Every NVVDD and MSVDD write fails: "readback differs from the request", the driver said ok | driver 621.01 (2026-10-22, reference card) takes every voltage-rail write and changes nothing: both rails, up and down, also after a reboot. XBAR's offset and voltage demand, the ratio, the curve, memory, power and fans still write | tell the person: the uncore group stays at stock on that driver. Either go back to 617.42 (where the rails were measured) with a clean install, or stay and tune XBAR and the ratio again with MSVDD at stock, then keep that |
| broker.log: the worker crashed a few seconds after a driver update or device restart | known, not fixed yet: the driver's NVML crashes the service's worker when the device restarts under it | nothing: the service starts a new worker 2 s later and puts the daily profile back |
| card_report's last row says a newer Clocktail is out | updates never install themselves | tell the person; with their yes, run `npx -y clocktail@latest setup --plan` and follow it (nothing needs closing), then they restart their agents; the card, the daily profile and saved profiles stay. The check reads only the latest version number from registry.npmjs.org; `CLOCKTAIL_NO_UPDATE_CHECK=1` turns it off |
| Fan curve looks stock after a reset or reboot | not yet verified whether the firmware keeps it | the daily profile writes it again |
| Settings look different after the PC woke from sleep | whether the card keeps them through sleep is not measured (sleep is off on the reference machine); the service puts the daily profile on again once the GPU is idle after a wake-up, writing only what changed, and leaves a trial in force alone | wait a minute; `card_report` says whether it is on the card; under a trial, `back_to_daily` if they differ |
| `watch` or a live line says there are no frame rates | the capture did not start (an older service: run the setup again; PresentMon missing), the process name given matches nothing, the game draws nothing (a menu in the background), or its anti-cheat refuses the capture | leave out the process name (the busiest app is taken), bring the game to the front; otherwise ask the person for the number |

## Where things live
- Programs: `%LocalAppData%\Programs\Clocktail` (server, service); the running service: `C:\Program Files\Clocktail`, scheduled task `ClocktailBroker`.
- Per-card state: `%LocalAppData%\Clocktail\<gpu-id>\`: `profile.json` (daily profile), `tuning-log.md` (every change, readable), `datapoints.jsonl`, `ownership.json` (the original value of every setting Clocktail has written), `crashes.jsonl`; `live\` (each live watch: a `.log` of its lines and a `.csv` of every sample, 4 a second); `residency\` (where the card ran under load, once a second, whenever the server is up).
- Service log: `%LocalAppData%\Clocktail\broker.log`.

## Stock and back
- `stock`: the card to stock now and kept there (the daily profile is suspended, not deleted). `load_daily` puts the kept profile back.
- `back_to_daily` puts the daily profile on, not stock; while suspended it leaves the card at stock. A fresh install with no daily profile is stock.
- A reboot alone returns the card to stock until the service writes the daily profile again at logon.
- To remove Clocktail entirely: `npx -y clocktail uninstall` (card to stock, service, files and agent configs removed; the logs stay unless `--purge`).

## Never
- Never work around the service (other overclocking tools writing at the same time fight over the same settings; ask the person to close MSI Afterburner, NVIDIA App's auto-tune and similar while tuning).
- Never edit the profile or journal files by hand.
- Never promise stability you have not measured.
