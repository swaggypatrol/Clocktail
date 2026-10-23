# Clocktail

English | [简体中文](README.zh-CN.md)

**Overclock your RTX 50 card by talking to an AI agent, over MCP.** You play. The agent tunes. NVIDIA's own stress tester settles the arguments.

## So you want to overclock a card that's already this fast

We get it. We won't ask.

### Set up Clocktail

Paste this into your coding agent:

```
Install Clocktail and connect it to this coding agent using npx clocktail@latest setup. Show me the setup plan for approval, then verify the installation.
```

Or run it yourself (you'll need Node.js):

```
npx -y clocktail setup
```

Either way, setup shows you what it's going to change before it changes anything. You pick the agents, it backs up their config, installs the service, and Windows asks for admin rights once. Restart your agent when it's done. Your card stays at stock until you say otherwise.

It doesn't update itself. When a new version is out, "How's my card doing?" mentions it at the bottom. Run the same setup command again (or ask your agent to), restart, done. Your settings and profiles stay where they are. The update check reads exactly one thing from npm, the latest version number, and sends nothing about you or your card. `CLOCKTAIL_NO_UPDATE_CHECK=1` turns it off.

It works with Claude Code, Claude Desktop, Codex, Cursor, OpenCode, VS Code, Gemini CLI, Windsurf, GitHub Copilot CLI and about a dozen more (the list is borrowed from [REA](https://github.com/morluto/rea), thanks). Anything else that speaks MCP can point at `%LocalAppData%\Programs\Clocktail\clocktail-mcp.exe`.

And it doesn't have to be Claude. Any model that's decent with tools will do, even a local one running on the card it's talking about:

![A local model in OpenCode answering "what's my graphics cards OC status" with card_report: a verdict, what changed from stock with ✅ marks, and the live core clock](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/local-model-card-report.jpg)

*Qwen3.8 27B in llama.cpp, through OpenCode, on the same RTX 5090 it's reporting on. The card took it well.*

### Then just talk to it

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

Ask how your card's doing and you get a table: every setting, what it was and what it is now, with a ✅ if it's inside NVIDIA's own limits and a ⚠️ if it isn't. Under that, your V/F curve, drawn right in the terminal in Braille dots, btop-style, with a ● where the card is running.

![card_report on a card at stock: each setting's original and current value, its mark and a note, then the V/F curve in Braille dots](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/card-report-at-stock.jpg)

*At stock. Now and stock are the same line. For now.*

![Claude Sonnet drawing the tuned V/F curve in Braille dots, now over stock with a dot where the card runs, and explaining its shape](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/vf-curve-braille.jpg)

*Later, on a tuned card: Claude Sonnet draws the curve and explains why it looks the way it does.*

It also reads the stuff most tools don't show you: the memory temperature, how many watts go through the 16-pin connector, whether the PCIe link has been resending data, which driver limit is really holding your clock back, how long Windows waits before it resets a hung GPU. It comes with a manual on Blackwell and on every control it has, written from a lot of measuring, so it can tell you why it's doing what it's doing instead of guessing.

While you play, it watches the card four times a second and tells you what it sees. If the game crashes, that's not a setback, that's data. It notes the clock, voltage, power and temperature it went down at, puts your daily settings back, and lowers that part of the curve next time. There's no other app to open.

## The short version

- **The agent does the tuning.** Power limit, fans, the V/F curve, memory, then the boost and XBAR, in that order, and it can tell you why. One small step at a time, because big steps are how graphics cards end up on the secondhand market.
- **Every control, no "expert mode".** If the command line can set it, so can your agent: the curve (a band at a time or the whole thing), the core offset, the voltage boost (both rails' limits), XBAR and its ratio to the core, SYS and Video, clock locks, memory, power and current limits, the fans. Each tool moves a little per call, stops at the card's hard limits, and tells you why when it says no.
- **NVIDIA's stress tester is the judge.** `nvgwls.exe` ships with your driver; NVIDIA App's auto-tune uses the same program. It checks every result bit for bit. You can't argue with it, and you can't bribe it.
- **Game-stable is stress-stable minus 30–60 MHz.** The stress tester finds the edge. Games decide how far back from it you have to sit, and in our runs that was 30–60 MHz. So you play, the agent watches, and every crash becomes a data point. We didn't make this rule up. We crashed into it.
- **A lab for what games can't measure.** Memory and the uncore get their own load engine, `clocktail-lab` (StabilityLab's, moved in). It checks every result on the CPU and times the DRAM and the crossbar. Your agent can leave it sweeping the memory offset, or hunting for the best mix of XBAR, ratio and MSVDD, each step compared A B B A against the best so far. The winner is measured once more against your daily settings before it's named, and it keeps nothing until you say so. If something else is using the GPU, a screen saver included, it waits until it's gone instead of trusting the numbers, and a comparison something disturbed anyway is run again.
- **It counts your frames.** While you play, the agent sees your fps, the 1 % and 0.1 % lows and the frame times, through Intel's PresentMon. Nothing gets injected into the game. "Did that help?" gets answered in frames, not vibes.
- **Keep as many profiles as you like.** "Quiet", "games", whatever you want. Switch with a sentence.
- **Nothing sticks unless you say so.** Every write is read back and can be undone. A background service keeps your daily settings and puts them back after every reboot, every wake-up and every driver reset, including the ones you caused. It also checks that the firmware is really holding the power limit you asked for, because once every reading said 600 W while the card quietly ran at 575 W for hours. Flash a new VBIOS and it waits for your OK instead of stacking old offsets on a new curve. If the driver drops out mid-sentence, your agent stays connected.
- **It knows when to stop.** Nothing gets written while the GPU looks hung. Stress runs stop at 85 °C. Leave it mapping overnight and it stops after two crashes, rather than hanging the card over and over with nobody watching. Clock locks that would keep the card high at idle are for testing only and never end up in your daily settings.

![The agent laying out the tuning plan: power limit, fans and temperature, the V/F edge mapped by voltage region, the daily curve, then games](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/tuning-plan.jpg)

*Before the first write, the plan: the power limit (your call), fans and temperature, the V/F edge mapped one voltage region at a time, the daily curve, then your heaviest games.*

## What it found on the one card we tested

RTX 5090 Founders Edition, driver 617.42 (and then 621.01, see the last row), Windows 11. Your card is different. Your card is always different.

| Setting | Result |
|---|---|
| MSVDD REL −50 → 0 mV (NVIDIA's own limit) | Blender +2 %. Free, which is the best price. |
| XBAR +300 MHz with +20 mV of voltage demand | Blender +6–7 % over stock. |
| XBAR past ~+300 MHz without the extra voltage | MSVDD drops to its floor and memory-heavy work loses up to 16 %. Don't. We did, so you don't have to. |
| XBAR +400 MHz / +50 mV, ratio 0.95, MSVDD REL +50 / ALT +30 mV | The lab's crossbar test 5–6 % faster than the card's previous daily settings, and it passed the stress tester at full and light load. It was that card's daily on driver 617.42. Games not measured yet. |
| Memory +1758 MHz, on top of that | Not a single error in the lab all the way to +3000, but bandwidth stops climbing around +1758. 3DMark Speedway liked it a little better than +358; +2900 was no faster in Speedway or Steel Nomad. Past the plateau you only buy latency, and frames don't feel it. |
| Driver 621.01 | It takes every NVVDD and MSVDD voltage write, says OK and changes nothing, reboot or not. So MSVDD stays stock and the ratio stops paying (0.925 and 0.95 only slowed the core). XBAR still pays on its own: +400 MHz / +50 mV at ratio 0.9 got the crossbar test about 10 % faster than stock. Clocktail puts the rest of a profile on and tells you which part the driver wouldn't take. |
| Voltage boost 100 %, on 621.01 | The one voltage control 621.01 still takes: every rail's limit +20 mV, 5 mV per 25 %. It turned out to be what the ratio was missing: XBAR +500 MHz / +50 mV, ratio 0.95, boost 100 % got the crossbar test another 7 % and memory bandwidth 4.5 % over the +400 / 0.9 settings, and passed the lab and the stress tester at full and half load. Clocktail dropped its MSVDD and NVVDD offset tools for it. |

For a while we thought XBAR offsets past +300 bought nothing. Turns out they do, just not on their own. XBAR sits squeezed between 0.9 × the core clock below and whatever MSVDD's voltage allows above, and the two are only about 30 MHz apart. Raise one and nothing happens. Raise the ratio and MSVDD together and the window opens up, and then the XBAR offset starts paying. The tuner figured that out, not us. Higher numbers still aren't always better numbers. Sometimes they just need friends.

And once the crossbar stopped holding it back, the memory had room too: 7.8 % more bandwidth at the very same offset, and for the first time it actually scaled with the memory clock.

The founders preset has the first two rows. The last two are new.

![3DMark Steel Nomad result page: score 15 757, valid result, RTX 5090 with an Intel Core i9-14900K, average clock frequency 2,878 MHz](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/steel-nomad-result.jpg)

*3DMark Steel Nomad on an RTX 5090 tuned with Clocktail: 15 757, a valid result, at an average 2,878 MHz. The reference card's earlier run on the founders profile scored 15 177.*

## Uninstall

```
npx -y clocktail uninstall
```

Your card goes back to stock, and the service, the files and the agent connections are removed. Your profiles and tuning logs stay in `%LocalAppData%\Clocktail` unless you add `--purge`. The card won't remember any of this.

## The zip

Clocktail is built around your agent, and we don't recommend installing it by hand. The zip on the Releases page is for reading, poking at and keeping: the MCP server and service that setup installs, plus the command line, there to be scanned, taken apart or kept in a drawer.

If you really want to be the robot yourself, here's how.

1. Right-click the zip → Properties → Unblock. Otherwise Windows will want a word about unsigned programs from the internet, and honestly it has a point.
2. Unzip it into `%LocalAppData%\Programs\Clocktail`, where setup would have put it, so `npx` can take over later without a fuss.
3. Connect your agent to `clocktail-mcp.exe`. It's a stdio server with no arguments. For Claude Code:
   ```
   claude mcp add clocktail -s user -- "C:\Users\<you>\AppData\Local\Programs\Clocktail\clocktail-mcp.exe"
   ```
4. In that folder, run `.\Clocktail.Broker.exe install` and approve admin rights once. Or skip it: your agent calls `install_service` the first time it needs to write something.
5. Restart your agent.

To update, close every agent first (Windows won't overwrite a program that's running; setup has a way around that, but you're doing this by hand), unzip the new version over the old one, and run `.\Clocktail.Broker.exe install` again, or the service stays on the old version. To remove it, run `.\Clocktail.Broker.exe stock`, then `.\Clocktail.Broker.exe uninstall`, take clocktail out of your agent's config and delete the folder. Your profiles and tuning logs are in `%LocalAppData%\Clocktail` if you want those gone too.

Or paste the line under "Set up Clocktail" into your agent and go make a cup of tea. Robot work is best left to robots.

## What's in the box

| Folder | What it is |
|---|---|
| `src/Clocktail.Hw` | The only code that talks to the driver (NVAPI, NVML). Every setter has a write policy, because trust is earned. |
| `src/Clocktail.Domain` | Transactions, the ownership journal (the original value of every setting it's touched), write rules, the daily profile, presets. |
| `src/Clocktail.Broker` | The service: an elevated logon task that does every write over a named pipe and quietly puts your settings back after crashes and driver resets. |
| `src/Clocktail.Cli` | The command line, for people who'd rather break things by hand. |
| `tools/Clocktail.Mcp` | The MCP server your agent talks to: 54 tools and the manual. See its README. |
| `tools/Clocktail.Lab` | The lab: a CUDA load engine for the memory and the crossbar, every result checked on the CPU. It writes nothing itself; its trials go through the service. |
| `npm/` | The setup genie behind `npx clocktail`. |

To build from source (Windows, .NET 10 SDK): `dotnet test Clocktail.slnx` runs the tests against a fake driver. It never touches the GPU.

## Sources

Frame rates come from Intel's [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT), shipped unchanged; see THIRD_PARTY_NOTICES.md. The NVAPI details come from public sources on the internet. Every value the code relies on was checked by measuring it on the card above. Some of that measuring involved driver resets. The card is fine.

If your card answers differently, ask your agent for a diagnostic report and attach it to an [issue](https://github.com/swaggypatrol/Clocktail/issues). It's one plain JSON file: versions, the card, what each control can do on it, and how the driver answered every read. Making it writes nothing to the card, it has no user names, paths or files in it, and it goes nowhere until you post it.

## Warning

This is unofficial software. Overclocking can crash your games, mess up your output and shorten the life of your hardware. Use it at your own risk. If something goes wrong, the tuning log will tell you exactly what happened. It won't say sorry.
