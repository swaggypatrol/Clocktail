# Clocktail

English | [简体中文](README.zh-CN.md)

**Agent-assisted overclocking for NVIDIA RTX 50 (Blackwell), served over MCP.** You play games. An AI agent tunes your graphics card. NVIDIA's own stress tester decides who was right. This is the correct division of labor.

## Congratulations

You have chosen to overclock a graphics card that was already very fast. We respect this decision and will not ask you to explain it.

### Set up Clocktail

Copy this into your coding agent:

```
Install Clocktail and connect it to this coding agent using npx clocktail@latest setup. Show me the setup plan for approval, then verify the installation.
```

Or run it in a terminal yourself (Node.js required):

```
npx -y clocktail setup
```

Either way you choose your agents, review the proposed changes, and approve them. Setup installs Clocktail, connects your agents (with backups of their configuration), and installs the service; Windows asks for admin rights once. Restart your agent afterward. Your card stays at stock until you say otherwise.

Updates do not install themselves. When a new version is out, "How's my card doing?" says so in its last row: run the same `npx -y clocktail@latest setup` again (your agent can run it for you), then restart your agents. Your card, your daily profile and your saved profiles stay as they are. To check, Clocktail reads one thing from the npm registry, the latest version number, and sends nothing about your computer or card. `CLOCKTAIL_NO_UPDATE_CHECK=1` turns it off.

Setup supports Claude Code, Claude Desktop, Codex, Cursor, OpenCode, VS Code, Gemini CLI, Windsurf, GitHub Copilot CLI and a dozen more (the list is borrowed from [REA](https://github.com/morluto/rea), with thanks). Any other MCP client works too: point it at `%LocalAppData%\Programs\Clocktail\clocktail-mcp.exe`.

It does not have to be Claude, either. Any model that is good with tools will do, including a local one running on the very card it is asked about:

![A local model in OpenCode answering "what's my graphics cards OC status" with card_report: a verdict, what changed from stock with ✅ marks, and the live core clock](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/local-model-card-report.jpg)

*Qwen3.8 27B in llama.cpp, through OpenCode, on the RTX 5090 it is reporting on. The card took it well.*

### Ask your agent

```
Let's tune my GPU. Start from the founders preset, stress test it, then find me more.
```

```
I'm at 2970 MHz and it won't go higher no matter what voltage. What's holding it back, and what would you change?
```

```
How's my card doing?
```

```
I'm playing Cyberpunk tonight on the new curve. Watch the card the whole time. If it crashes, write it down and tune from it.
```

Ask how your card is doing and you get every setting, original → now, with a ✅ where it sits inside NVIDIA's own limits and a ⚠️ where it does not, and your V/F curve drawn right there in the terminal in Braille dots, btop-style: stock, now, and a ● where the card is running.

![card_report on a card at stock: each setting's original and current value, its mark and a note, then the V/F curve in Braille dots](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/card-report-at-stock.jpg)

*At stock: every setting original → now, a mark and a note, then the curve. Now and stock are the same line. For the moment.*

![Claude Sonnet drawing the tuned V/F curve in Braille dots, now over stock with a dot where the card runs, and explaining its shape](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/vf-curve-braille.jpg)

*Later, on a tuned card: Claude Sonnet draws the curve, now over stock with a ● where the card runs, and explains the shape.*

Ask further and it reads what other tools leave out: the memory temperature, the watts on the 16-pin connector and the board's input current, the PCIe link and whether it has been resending data, the driver's own clock ceilings and which limit set each one, and how long Windows waits before it resets a hung GPU. Your agent comes with a manual on Blackwell (rails, clock domains, the V/F curve, what limits what), on the method and on every control it has, written from five days of measurements, so it can explain every step and does not have to guess. While you play it watches the card four times a second and tells you what it sees as it happens. If the game crashes, that is not a setback but a measurement: the agent records the clock, voltage, power and temperature it fell over at, your daily profile goes back on, and the next step lowers that part of the curve. There is no other window to open. Most people find this arrangement agreeable.

## Bottom line

- **The agent does the tuning.** Power limit, fan curve, V/F curve, memory, MSVDD and XBAR, in that order, each for a reason it can explain. One small step at a time, because large steps are how graphics cards end up on the secondhand market.
- **Every control, no expert mode.** Whatever the command line can set, your agent can set too: the V/F curve a band at a time or all at once, the core offset, the NVVDD and MSVDD rails, XBAR and its ratio to the core clock, SYS and Video, clock locks, voltage boost, memory, current limits, power limits and the fans' zero-RPM band. The manual says what each one writes, how far one call goes and what it does to the others. Every tool moves a small step per call, stops at the card's hard limits (1150 mV, the board's maximum power, the firmware's own ranges) and says why when it refuses.
- **NVIDIA's stress tester is the judge.** `nvgwls.exe` ships with your driver; NVIDIA App's auto-tune runs the same program. It checks every graphics, compute and ray-tracing result bit for bit. It has no feelings to hurt and cannot be bribed.
- **Game-stable = stress-stable minus 30–60 MHz.** The stress tester maps the shape of the edge, voltage by voltage. Only games can say how far under it they need to stay: in our runs, 30–60 MHz under the highest clock `nvgwls` passed. So you play, the agent watches, and every crash goes into the data. We did not make this rule. We merely crashed into it.
- **It counts your frames itself.** While you play, the agent sees the game's frame rate, the 1 % and 0.1 % lows and the frame times, through Intel's PresentMon run by the service; nothing is injected into the game. "Did that help?" gets answered in frames, not in vibes.
- **Keep as many profiles as you like.** Save the daily one as "quiet" or "games" and switch with a sentence. The card only ever runs one at a time, which is how it prefers it.
- **Nothing is permanent unless you say so.** Every write is read back and can be undone. A background service keeps your daily profile and puts it back after every boot, every wake-up and every driver reset, including the ones you caused. It also checks that the firmware really holds the power limit you asked for, because once every reading said 600 W while the card held 575 W for hours. Flash another VBIOS and the profile waits for your word instead of putting old offsets on a new stock curve. If the driver goes away mid-sentence, your agent's connection does not: Clocktail starts its reader again and carries on.
- **It knows when to stop.** No trial is written while the GPU looks hung. A stress run stops at 85 °C. A mapping run you leave going overnight stops writing new trials after two crashes, rather than hanging the card again and again with nobody there to see it. Clock locks that would hold the card high even at idle (a floor, a pinned V/F point, Boost Lock) are for tests only and are never kept; a cap is.

![The agent laying out the tuning plan: power limit, fans and temperature, the V/F edge mapped by voltage region, the daily curve, then games](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/tuning-plan.jpg)

*Before the first write, the plan: the power limit (yours to choose), fans and temperature, the V/F edge mapped one voltage region at a time, the daily curve, then your heaviest games.*

## What it found on the one card we tested

RTX 5090 Founders Edition, driver 617.42, Windows 11. Your card is different. Your card is always different.

| Setting | Result |
|---|---|
| MSVDD REL offset −50 → 0 mV (NVIDIA's own reliability limit) | Blender Cycles +2 %. Free, which is the best price. |
| XBAR clock +300 MHz, XBAR voltage demand +20 mV | Blender Cycles +6–7 % over stock. Memory bandwidth unchanged. |
| XBAR past ~+300 MHz without the extra voltage | MSVDD drops to its floor and memory-bound work loses up to 16 %. Do not do this. We did it so you don't have to. |
| XBAR +400 MHz | Needs +50 mV of voltage demand to stay off that floor, not +20, and was no faster than +300 in Blender. The tools refuse it with less. |

Under load, XBAR is capped at about 0.9 × the core clock. Offsets past ~+300 MHz therefore buy nothing. Higher numbers are not always better numbers. This may be the most important sentence in this document.

All of it is in the founders preset.

That 0.9 is a setting too, and your agent can raise it. At 0.92 XBAR ran 60 MHz higher, for 25 mV more on MSVDD and 15 MHz off the core. Whether that buys frames, nobody has measured. Yet.

![3DMark Steel Nomad result page: score 15 757, valid result, RTX 5090 with an Intel Core i9-14900K, average clock frequency 2,878 MHz](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/steel-nomad-result.jpg)

*3DMark Steel Nomad on an RTX 5090 tuned with Clocktail: 15 757, a valid result, at an average 2,878 MHz. The reference card's earlier run on the founders profile scored 15 177.*

## Uninstall

```
npx -y clocktail uninstall
```

The card goes back to stock, and the service, the files and the agent connections are removed. Your profiles and tuning logs stay in `%LocalAppData%\Clocktail` unless you add `--purge`. The card will not remember any of this.

## The zip

Clocktail is designed around your agent, and we do not recommend installing it by hand. The zip on the Releases page is for reading, inspecting and archiving: the MCP server and the service that setup installs, plus the command line, there to be scanned, taken apart or kept in a drawer.

If you insist on being the robot yourself, here is the procedure.

1. Right-click the zip → Properties → Unblock. Otherwise Windows may want a word about unsigned programs from the internet, and it has a point.
2. Unzip it into `%LocalAppData%\Programs\Clocktail`, where setup would have put it, so `npx` can take over later without a fuss.
3. Connect your agent to `clocktail-mcp.exe`: an stdio server, no arguments. For Claude Code:
   ```
   claude mcp add clocktail -s user -- "C:\Users\<you>\AppData\Local\Programs\Clocktail\clocktail-mcp.exe"
   ```
4. In that folder, run `.\Clocktail.Broker.exe install` and approve admin rights once. Or skip it: your agent calls `install_service` the first time it has something to write.
5. Restart your agent.

To update, close every agent first (Windows will not overwrite a program that is running; setup knows a way around that, but you are doing this by hand), unzip the new version over the old one, and run `.\Clocktail.Broker.exe install` again, or the service stays on the old version. To remove it, run `.\Clocktail.Broker.exe stock`, then `.\Clocktail.Broker.exe uninstall`, take clocktail out of your agent's configuration and delete the folder. Your profiles and tuning logs are in `%LocalAppData%\Clocktail`, should you want those gone as well.

Or paste the line under "Set up Clocktail" into your agent and go make a cup of tea. Robot work is best left to robots.

## What is in the box

| Folder | Contents |
|---|---|
| `src/Clocktail.Hw` | The only code that talks to the driver (NVAPI, NVML). Every setter has a write policy, because trust is earned. |
| `src/Clocktail.Domain` | Transactions, the ownership journal (the original value of every setting it has touched), write rules, the daily profile, presets. |
| `src/Clocktail.Broker` | The service: an elevated logon task that does every write over a named pipe and restores your settings after crashes and driver resets, without comment. |
| `src/Clocktail.Cli` | Command line, for people who prefer to break things by hand. |
| `tools/Clocktail.Mcp` | The MCP server your agent talks to: 49 tools and the manual. See its README. |
| `npm/` | The setup genie behind `npx clocktail`. |

Build from source (Windows, .NET 10 SDK): `dotnet test Clocktail.slnx` runs the tests on a fake driver; it never touches the GPU.

## Sources

Frame rates come from Intel's [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT), shipped unchanged; see THIRD_PARTY_NOTICES.md. The NVAPI interface details come from public sources on the internet. Every value the code relies on was verified by measurement on the card above. Some of those measurements involved driver resets. The card is fine.

If your card answers differently, ask your agent for a diagnostic report and attach it to an [issue](https://github.com/swaggypatrol/Clocktail/issues). It is one plain JSON file: the versions, the card, what each control can do on it and how the driver answered every read. Making it writes nothing to the card, it holds no user names, paths or files, and it goes nowhere until you post it.

## Warning

This is unofficial software. Overclocking can crash your games, corrupt your output and shorten the life of your hardware. You use it at your own risk. If something goes wrong, the tuning log will tell you exactly what happened. It will not tell you it's sorry.
