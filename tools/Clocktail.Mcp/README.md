# Clocktail MCP server

MCP tools for the NVIDIA RTX 50 series, across models and board vendors. Each change is a **trial**; only settings the user explicitly approves become their own daily profile. Clocktail ships no tuning preset or default daily profile, and a fresh install writes no tuning settings.

`npx -y clocktail setup` installs it and connects your agents. By hand: it is a stdio MCP server, so point any MCP client at `clocktail-mcp.exe` (`%LocalAppData%\Programs\Clocktail` after setup, or `tools\Clocktail.Mcp\bin\Release\net10.0-windows` after `dotnet build -c Release tools/Clocktail.Mcp`). The service must be installed: it does every write, and `install_service` installs it.

`--lite` (or `CLOCKTAIL_TOOLS=lite`) lists 22 tools and short instructions (Manual/instructions-lite.md) instead of the full catalogue and instructions, for small local models; a tool left out still answers when called by name. `CLOCKTAIL_CONFIRM` sets how the tools that need the person's yes (`begin_session`, `stock`, `load_daily`, `keep_as_daily`, `load_preset`, `set_daily_power_limit`, `set_power_cap`, `delete_profile`) ask the person themselves: `chat` (the default: the first call changes nothing and answers with the question and a one-time code, and the agent calls again with `confirm` set to the code after the person's yes in chat), `form` (an MCP elicitation where the client offers it, else a Windows dialog; `auto` still means this), or `dialog` (always the Windows dialog). The legacy `off` asks in chat too: nothing bypasses the question.

What the agent starts is a relay that loads no driver library and runs the same program again with `--worker`, which holds the GPU. When the driver goes away (a reset, an update, a device restart) the worker ends or dies in it, and the relay answers the calls it had in flight, starts a new worker and goes on: the agent keeps its tools. `CLOCKTAIL_MCP_DIRECT=1` runs it as one process. A read waits for the driver at most 5 s (`CLOCKTAIL_MCP_DRIVER_WAIT_S`) and then says so.

## Tuning policy

Only explicit user consent and NVIDIA driver/firmware acceptance authorize tuning. `begin_session` asks for session approval; persistent changes ask separately. Clocktail adds no clock/voltage envelope or mandatory XBAR voltage coupling. Driver-reported capabilities, encoding, ownership/concurrency and exact readback checks remain. stress_test and lab_test stop within 2 °C of NVIDIA's slowdown point, and at a maxTempC when one is given; firmware protection remains active. Stock settings and ranges are discovered on the current GPU, never copied from another board. Per-control availability and hardware validation may vary.

## Tools

| Tool | What |
|---|---|
| `card_report` | 🩺 How's my card doing: every setting as stock → now, the change and a ✅ / ⚠️ / ⛔ safety mark, live readings, one verdict; between the tables the V/F curve drawn in Braille dots (now, stock, and ● where the card runs). A "Not set by Clocktail" row names another tool's settings (the card is then not at stock), and "Overclocking tool running" a program that writes the card back. |
| `manual` | The agent's manual: the method phase by phase and why, every control and how to combine them, Blackwell, reading the card, stability, presets, troubleshooting. Its short version is the server's instructions. |
| `install_service` | Install or update the service (one UAC prompt). Every write tool needs it. |
| `gpu_status` | Where the card runs now: voltages (and what each GPC measures), clock, the curve point, power vs limit, temperature headroom, VRAM, the 16-pin input, the PCIe link, the XBAR/SYS/Video clocks and any clock lock in force. |
| `card_settings` | Everything set on the card, next to stock and the daily profile. |
| `diagnostic_report` | A plain-JSON file for a bug report: versions, the card, what each control can do, how the driver answered every read. GET only. |
| `limiter` | What holds the clock back right now. |
| `curve_around` | Curve points near a voltage. |
| `watch` | Sample one stretch of play: p10/p50/p90 of clock, voltage, XBAR, SYS, Video, power, temperature, VRAM; a clock lock in force set against the clock that actually ran; time on the wall; the game's fps and its 1 % and 0.1 % lows (PresentMon, run by the service). |
| `live_start` / `live_read` / `live_stop` | Watch a whole session of play live: 4 samples a second, a line every 10 s under load (clock, voltage and curve point, MSVDD, XBAR and SYS, power vs the wall, temperature, fans, what held the clock), and a line when load starts or stops, the clock freezes, the card stops answering or runs hot; each line ends with the game's fps. The log can be followed as it is written (Claude Code's Monitor); every sample goes to a CSV. |
| `list_presets` / `load_preset` | User-saved profiles for the current GPU; no built-in profiles. Load one as daily only after explicit approval. |
| `save_profile` / `delete_profile` | Save the daily profile under a name ("quiet", "games") to switch back to later; delete a saved one (the person confirms that themselves). |
| `set_daily_power_limit` | The daily power limit; the person confirms it themselves. |
| `begin_session` / `end_session` | Start a session for a game / end it and go back to the daily profile. |
| `preview_daily` | What `keep_as_daily` (or `load_preset`, or `keep_as_daily adopt=true`) would make the daily profile, before asking. Writes nothing. |
| `adjust_curve` | Raise or lower the V/F points in a voltage band around the working point (one trial). |
| `flatten_curve` | The curve flat from a voltage up; only lowers (one trial). |
| `set_curve` | The whole V/F curve at once, every point's offset (one trial). |
| `curve_editor` | Open the English V/F curve window on the person's desktop (dark, crisp at any DPI): drag points by hand; crashes marked on the points they happened at (solid: in a curve trial), the highest setting each point was marked stable at, and where the card actually ran as a heat of dots (observed time over 60 days from the broker's always-on V/F history above 825 mV and the residency samples; observed operation, not a stability result). The low-voltage cliff is reshaped as a smooth transition band between two draggable lines that lifts nothing under its foot; above it the clock never falls (the driver's own rule) and one button smooths. Right-click a curve, crash or evidence point to select and confirm deletion of a historical test (immediate, no GPU write; original journal retained for recovery). Confirm & Save as Daily writes the curve as a trial, checks the readback, keeps it as the daily profile and closes the window. Windows build only. |
| `shift_curve` | The curve on the card moved left by N mV: the same clocks at less voltage (one trial). |
| `set_core_offset` | The core P0 offset, every point at once; the curve is written back right after (one trial). |
| `set_clock_lock` | A core clock cap, floor, V/F point lock or Boost Lock (the record `nvidia-smi -lgc` sets; one trial). A request, not a hard P-state lock: the driver still derates under it, and the measurement tools report what actually ran against it. |
| `set_voltage_boost` | The driver-exposed voltage boost; voltage effects depend on the GPU and driver (one trial). |
| `set_fan_curve` | The firmware fan curve (one trial). |
| `set_fan_start` | The stopped fans' user-selected start/stop temperatures (one trial). |
| `set_memory_offset` | The memory offset; the curve is written back right after (one trial). |
| `adjust_xbar` | XBAR's clock offset and voltage demand (one trial). |
| `set_clock_ratio` | The driver-exposed XBAR/core propagation ratio (one trial). |
| `adjust_clock_domain` | SYS's or Video's clock offset and voltage demand, or the core's voltage demand (one trial). |
| `set_power_limit` | Move the power limit for a trial, to run the curve lower down. Never kept. |
| `set_current_limit` | Set the NVVDD or MSVDD current limit for a trial, up to the driver's maximum. Never kept. |
| `set_power_cap` | The second board power request (F8) for a trial; it cannot be deleted once made. Never kept. The person confirms it themselves. |
| `set_fan_speed` | Hold the fans at a fixed duty for a trial (thermal tests). Never kept. |
| `stress_test` | Run NVIDIA's `nvgwls.exe` on what is on the card; a failure is recorded as the trial's crash. |
| `lab_test` | Goal-based validation: core (default, 150 s: burn 30, solo 60, leap 60), memory (90 s), uncore (60 s), or all (270 s). Custom phases retain the old equal split and accept individual durations. Reports the planned schedule and measured coverage. |
| `tune_memory` / `lab_status` / `lab_stop` | Screen a memory range, recheck suspected slowdowns A B B A, compare up to three candidates and confirm a winner. Equivalent performance prefers a lower offset. Status separates `edge_mhz` / `candidate_mhz` from confirmed `best_mhz`; raw comparisons are kept and restoration must verify. |
| `tune_uncore` | The uncore tuner in the background: XBAR with its voltage demand, the XBAR/core ratio and MSVDD, each candidate a trial compared A B B A on the crossbar's (L2) bandwidth; keeps nothing. |
| `game_test` | The game test in the background: Quake II RTX (free on Steam) in a window of 90 % of the primary screen or more, played unattended by a seeded script of load patterns (heavy path tracing, heavy/light leaps with the light half still drawing, frame-capped bursts, idle then flat out, resolution ramps, the game's DRS, camera cuts, feature toggles, panoramas, a sun time-lapse, photo mode, a live map it plays itself; restart opt-in) with the wall and the fans (Boost Lock opt-in) moved under it as trials. The card host owns the locked baseline, exact-trial failure recording and independently verified recovery; no client-supplied restoration values. Needs one NVIDIA adapter and one matching RT-capable Vulkan device. Update broker/MCP/lab together. Offline-validated only; no setting is kept or marked automatically. |
| `mark_stable` / `mark_crash` | Record the trial's outcome. |
| `back_to_daily` | Put the daily profile back now. |
| `stock` / `load_daily` | The card to NVIDIA's stock and kept there, another tool's settings cleared too (the person confirms that: nothing keeps a copy; the daily profile is suspended) / the daily profile back on. |
| `keep_as_daily` | Keep what the trials set as the daily profile (not the trial wall, the F8 request, current limits or fixed fans); `adopt=true` keeps another tool's settings too. The person confirms it themselves. |
| `data_points` | Every trial so far, summarized per curve point (by_point weighs the residency samples and the broker's V/F history inside each trial by observed time). |
| `log_note` / `read_log` | The tuning log. |

Measure stability margin on the actual GPU and workloads; no fixed MHz margin or default daily settings apply across the series.
