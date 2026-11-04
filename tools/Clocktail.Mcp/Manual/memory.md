# Memory tuning
Find a useful offset on this GPU with correctness checks and repeatable performance.

Read the memory P0 offset and its range from the driver. Neither a factory memory clock nor a useful offset is shared by every RTX 50 board.
A memory-offset write can reset V/F point offsets; Clocktail restores the curve in the same trial.
`tune_memory` measures a user-requested range. Its `stepMhz`, run length and stopping strategy are search parameters, not stable settings for other cards.
The search screens correctness, bandwidth and latency; `trend=true` continues through performance dips. Missing actual memory clocks or unusable metrics leave a step unjudged, even when the computation returned no error.
Two slower scan steps trigger an A B B A recheck before ending a non-trend scan. A slowdown that does not repeat updates effective scan readings and the scan continues; raw readings stay intact.
Up to three candidates (a low-offset performance plateau, a bandwidth winner and a latency winner) are compared A B B A against the daily offset, then a winning change is confirmed again. Equivalent measured performance prefers a lower offset. A comparison disturbed by over 2% repeated-setting spread retries once; if still unjudged, it names no best.
`lab_status` reports the scan boundary in `edge_mhz`, an unconfirmed `candidate_mhz`, raw `comparisons` and `confirmed`. `best_mhz` means a paired recommendation after confirmation and verified restoration; it is null on incomplete confirmation or recovery. The scan-time estimate excludes rechecks, confirmations and waits.
An interrupted, shared or inconclusive measurement marks nothing stable. Use `lab_status` and `lab_stop` to follow or stop the job.
Validate the proposed offset with the user's workloads; retry behavior can hide errors as lower performance, artifacts or stutter.
The sweep restores the user's daily profile or captured baseline and keeps nothing automatically.
