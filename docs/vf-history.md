# Always-on V/F history

The broker keeps a lightweight record of where the GPU actually runs: whenever the broker is up, whether or not any AI
client or MCP server is connected. This record feeds the curve editor's "Observed V/F" dots and `data_points`'
`by_point`, alongside the residency samples the MCP server already records.

## What is recorded

- **Source:** the broker's flight recorder (`FlightSampler`). It reads every GPU every 250 ms. The history uses the same
  reads and adds none of its own.
- **Gate:** each look is checked on its own. A look is kept only when the measured NVVDD is **strictly above 825 mV**
  (825 000 µV) and NVML returned a core clock. Nothing is stored for a look whose voltage is 825 mV or less, whose
  voltage or clock was missing, whose read blocked for a second or more, or that came while the driver was gone. Load,
  power, a game process and a tuning session are not required: light loads and transitions are kept.
- **Stored fields:** the time, the NVVDD the rail reader returned (µV, before the flight ring rounds it to mV) and the
  core clock (MHz) NVML returned. Nothing else is stored: no requested voltage, curve clock, utilization, power or
  temperature.

## Files

`%LocalAppData%\Clocktail\<GPU stable id>\vf-history\yyyyMMdd.vfh`. The file sits beside `ownership.json` and
`datapoints.jsonl`, and there is one file per **UTC** day. All values are little-endian:

| Part | Size | Content |
|---|---|---|
| header | 16 B | `"CTVF"`, u16 version = 1, u16 header size = 16, u16 record size = 12, u16 reserved = 0, u32 UTC day as yyyymmdd |
| record | 12 B | u32 milliseconds since that UTC midnight, u32 NVVDD µV, u32 core MHz |

The worst case is 4 records/s × 86 400 s × 12 B ≈ 4 MiB per GPU per day, which needs the voltage above 825 mV for the
whole day. Real use is smaller. Nothing is deleted automatically: there is no retention or rollup, so disk use grows
until the person removes old day files.

Readers treat a version-1 record at or under 825 mV as damage and skip it, because the version-1 writer never writes
one. Changing the gate therefore requires a new format version.

### Robustness

- **Writer:** the broker writes in batches every 2 s from a task of its own. The sampler only enqueues. Each batch is
  handed to the operating system with a plain flush, not forced to disk, so a power loss can cost the last few seconds.
- **Failed writes:** if a write fails (disk full, access denied, a lock held by another process), the batch is kept
  and retried with a growing pause of up to 60 s. Up to 2 h of looks per GPU are kept in memory. When that fills, the
  oldest looks are dropped and counted. A failure is logged once when it starts and once when writing resumes.
- **Status:** `ping` and experiment `show` report `vf_history`, in snake_case: `written`, `queued`, `held`,
  `withheld` and `dropped` counts, `degraded`, `error`, `degraded_since`, `last_write`, `min_uv_exclusive` and `dirs`.
- **Torn records:** a torn final record (a crash or power loss mid-write) is ignored by readers. It is cut away before
  the next append, so later records stay aligned. A failed append is rolled back to the whole records it had.
- **Retries are idempotent.** A batch is written one UTC day at a time. When a later day fails (for example, its file
  is held), the recorder keeps the whole batch and retries it, together with any looks queued since. On each append,
  the store compares the batch with the file's last records (as many as the batch has) and skips milliseconds already
  there. A day that was already written is therefore not written again, and a record that landed whole before a torn
  write is not repeated. Each look is stored once on disk, so the size estimate above holds.
- **Unusable day files:** a day file with another version's header, or a damaged one, is never rewritten. The day
  continues in `yyyyMMdd-1.vfh`, up to 15 extra parts, and readers read every part.
- **Concurrent readers:** readers share the file with the writer and read whole records up to the length they found.
  A record written twice by a retry has the same millisecond and is read once.

### Hangs

A hung GPU can keep answering with frozen readings until the driver resets it, so looks are held in memory for 60 s
(the flight recorder's window) before they can be written.

- While the window shows a hang (`FlightRecorder.HungNow`: a frozen tail that power fell into, or a blocked read), the
  held looks from the hang's start are withheld.
- When a driver reset is seen, the looks from where the GPU stopped answering are withheld (`FlightRecorder.Hang`, the
  same boundary the crash trace uses, or 2 s before the reset when the window shows no hang).
- At a clean stop, held looks are written up to 2 s before the stop, or up to any earlier doubt the window raises.
- If the broker is killed, or hands over to a fresh process after losing the driver, up to the last 60 s are lost.
  They are never credited.

Withholding is only as good as the flight recorder's hang recognition. A frozen tail counts only when utilization
stays at 60 % or more and power has fallen into it. A hang the recorder does not recognize (and that the reset's
`Hang` boundary does not catch) has only its last 2 s before the reset withheld, which is the same limit the crash
trace has.

The history never infers a crash or a stable result.

## How it is weighed

`VfTimeline` and `VfDwell` (in `Clocktail.Domain.Vf`) merge every source into one time order:

- **Residency:** one sample per second, the earliest when several MCP servers wrote the same second.
- **Lightweight:** every look. Several looks within a second are all kept.

Each observation counts for the time until the next observation of **any** source, as long as that next look came
within 1.5× its own source's interval (250 ms for lightweight, 1 s for residency). Otherwise it counts for one interval
of its own source. As a result:

- A 250 ms stream and a 1 s stream over the same minute each count one minute.
- When both sources cover the same seconds, those seconds count once.
- Gaps count as nothing: voltage at or under the gate, sleep, a restart, a driver reset or dropped looks. A single
  missed look is not filled in.
- Neither source gets a reliability penalty.

Percentiles (the P90 clock on a point) are weighted by time. With residency alone and equal weights they use the
same sample-count rule as before.

### Windows

Each observation stands for the span `[At, At + seconds)`. A trial's window is `[start, end)` and is half-open.
`VfTimeline.Within` cuts every span to the window:

- A look exactly at the trial's end lends it nothing.
- A look just before the start counts only for the part of its span inside the window. A span can reach back at most
  1.5 s.
- The window's total is exactly the observed time inside it, and adjacent windows split time without loss or overlap.

Everything per trial uses these cut spans: stable seconds, each point's share, the time-weighted P90, the 10 s minimum,
and crash attribution's samples.

Several marks of one trial at the same setting cover the **union** of their cut spans, per observation:

- Overlapping marks count the overlap once.
- A mark that covers less of a span never shrinks what another mark covered.

This changes some old numbers. Residency samples sit at some offset inside each second (`+0.5 s` in the old tests), so
a trial window starting on the second now has that first part unobserved, and its last sample is cut at the end. A
30 s trial sampled at `+0.5 s` counts 29.5 s, where before every sample counted a whole second, even past the end.
Nothing is rounded to make up the difference.

## Where it is used

- **Curve editor, "Observed V/F" layer:** the last 60 days, binned by 1 mV and 7.5 MHz, each bin showing its observed
  time. The span is streamed one UTC day at a time, so months of ticks are never held in memory. This haze is not cut to
  a window: the 60-day ends can be off by under 1.5 s.
- **`data_points` `by_point` and the editor's evidence marks:** only the trials' own windows of the history are read.
  An observation says where the card ran during a trial. Only a trial's recorded outcome (stable, survived, crash) says
  how that went. An observation outside every stable trial is never stable evidence, and nothing turns observed
  operation into a stable result.
- **Crash attribution:** `CrashAt` still cuts a hang's frozen tail using the residency samples, which carry utilization
  and power. Every observation from that point on, of either source, is cut. The broker's history never wrote a hang's
  looks in the first place.

## What it does not include

- **Live CSV:** the MCP live watch's CSV is not merged into this history. It stays the diagnostic record it was.
- **Operating-point medians:** residency's in-memory "under load" medians (`adjust_curve`'s operating point) and the
  trial notes' "Under load" summary still use residency alone, because they mean load.
- **Flight recorder:** its 60 s ring and the crash traces are unchanged.
