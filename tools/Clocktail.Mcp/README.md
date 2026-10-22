# Clocktail MCP server

The agent's hands. Each change is a **trial** in a session; the person plays, the agent records the outcome, and only what survives becomes the daily profile.

`npx -y clocktail setup` installs it and connects your agents. By hand: it is a stdio MCP server, so point any MCP client at `clocktail-mcp.exe` (`%LocalAppData%\Programs\Clocktail` after setup, or `tools\Clocktail.Mcp\bin\Release\net10.0` after `dotnet build -c Release tools/Clocktail.Mcp`). The service must be installed: it does every write, and `install_service` installs it.

What the agent starts is a relay that loads no driver library and runs the same program again with `--worker`, which holds the GPU. When the driver goes away (a reset, an update, a device restart) the worker ends or dies in it, and the relay answers the calls it had in flight, starts a new worker and goes on: the agent keeps its tools. `CLOCKTAIL_MCP_DIRECT=1` runs it as one process. A read waits for the driver at most 5 s (`CLOCKTAIL_MCP_DRIVER_WAIT_S`) and then says so.

## Tools

| Tool | What |
|---|---|
| `card_report` | 🩺 How's my card doing: every setting as original → now, the change and a ✅ / ⚠️ / ⛔ safety mark, live readings, one verdict; between the tables the V/F curve drawn in Braille dots (now, stock, and ● where the card runs). |
| `manual` | The agent's manual: the method phase by phase and why, every control and how to combine them, Blackwell, reading the card, stability, presets, troubleshooting. Its short version is the server's instructions. |
| `install_service` | Install or update the service (one UAC prompt). Every write tool needs it. |
| `gpu_status` | Where the card runs now: voltages (and what each GPC measures), clock, the curve point, power vs limit, temperature headroom, VRAM, the 16-pin input, the PCIe link. |
| `card_settings` | Everything set on the card, next to stock and the daily profile. |
| `diagnostic_report` | A plain-JSON file for a bug report: versions, the card, what each control can do, how the driver answered every read. GET only. |
| `limiter` | What holds the clock back right now. |
| `curve_around` | Curve points near a voltage. |
| `watch` | Sample one stretch of play: p10/p50/p90 of clock, voltage, XBAR, power, temperature, VRAM; time on the wall; the game's fps and its 1 % and 0.1 % lows (PresentMon, run by the service). |
| `live_start` / `live_read` / `live_stop` | Watch a whole session of play live: 4 samples a second, a line every 10 s under load (clock, voltage and curve point, MSVDD, XBAR, power vs the wall, temperature, fans, what held the clock), and a line when load starts or stops, the clock freezes, the card stops answering or runs hot; each line ends with the game's fps. The log can be followed as it is written (Claude Code's Monitor); every sample goes to a CSV. |
| `list_presets` / `load_preset` | The profiles that ship with Clocktail and the ones saved by name; load one as the daily profile. Ask the person first. |
| `save_profile` / `delete_profile` | Save the daily profile under a name ("quiet", "games") to switch back to later; delete a saved one. |
| `set_daily_power_limit` | The daily power limit. Ask the person first. |
| `begin_session` / `end_session` | Start a session for a game / end it and go back to the daily profile. |
| `preview_daily` | What `keep_as_daily` (or `load_preset`) would make the daily profile, before asking. Writes nothing. |
| `adjust_curve` | Raise or lower the V/F points in a voltage band around the working point (one trial). |
| `flatten_curve` | The curve flat from a voltage up; only lowers (one trial). |
| `set_curve` | The whole V/F curve at once, every point's offset (one trial). |
| `shift_curve` | The curve on the card moved left by N mV: the same clocks at less voltage (one trial). |
| `set_core_offset` | The core P0 offset, every point at once; the curve is written back right after (one trial). |
| `adjust_nvvdd` | NVVDD's REL, ALT, OV and VMIN offsets, within the 1.15 V ceiling (one trial). |
| `set_clock_lock` | A core clock cap, floor, V/F point lock or Boost Lock (the record `nvidia-smi -lgc` sets; one trial). |
| `set_voltage_boost` | The voltage boost percentage (one trial). |
| `set_fan_curve` | The firmware fan curve (one trial). |
| `set_fan_start` | The temperature the stopped fans start at; only earlier (one trial). |
| `set_memory_offset` | The memory offset; the curve is written back right after (one trial). |
| `adjust_msvdd` | Raise or lower MSVDD's REL and VMIN offsets (one trial). |
| `adjust_xbar` | XBAR's clock offset and voltage demand (one trial). |
| `set_clock_ratio` | XBAR's clock ratio to the core, 0.8999 stock (one trial). |
| `adjust_clock_domain` | SYS's or Video's clock offset and voltage demand, or the core's voltage demand (one trial). |
| `set_power_limit` | Move the power limit for a trial, to run the curve lower down. Never kept. |
| `set_current_limit` | Set the NVVDD or MSVDD current limit for a trial, up to the driver's maximum. Never kept. |
| `set_power_cap` | The second board power request (F8) for a trial; it cannot be deleted once made. Never kept. Ask the person first. |
| `set_fan_speed` | Hold the fans at a fixed duty for a trial (thermal tests). Never kept. |
| `stress_test` | Run NVIDIA's `nvgwls.exe` on what is on the card; a failure is recorded as the trial's crash. |
| `lab_test` | Run the lab (StabilityLab's engine) on what is on the card: burn, light, pulse, memory in five access orders, latency; every result checked bit for bit; a failure is the trial's crash. |
| `tune_memory` / `lab_status` / `lab_stop` | The memory sweep (ST02) in the background: a step at a time, each a trial the lab measures, stopping where the memory gets slower or fails; follow it, end it. |
| `mark_stable` / `mark_crash` | Record the trial's outcome. |
| `back_to_daily` | Put the daily profile back now. |
| `stock` / `load_daily` | The card to stock and kept there (the daily profile suspended) / the daily profile back on. |
| `keep_as_daily` | Keep what the trials set as the daily profile (not the trial wall, the F8 request, current limits or fixed fans). Ask the person first. |
| `data_points` | Every trial so far, summarized per curve point. |
| `log_note` / `read_log` | The tuning log. |

Rule of thumb: the highest clock `stress_test` passes, minus 30–60 MHz, is what games hold.
