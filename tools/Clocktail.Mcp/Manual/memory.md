# Memory tuning
Find a useful offset on this GPU with correctness checks and repeatable performance.

Read the memory P0 offset and its range from the driver. Neither a factory memory clock nor a useful offset is shared by every RTX 50 board.
A memory-offset write can reset V/F point offsets; Clocktail restores the curve in the same trial.
`tune_memory` measures a user-requested range. Its `stepMhz`, run length and stopping strategy are search parameters, not stable settings for other cards.
The search watches correctness and sustained bandwidth; `trend=true` continues through performance dips as documented by the tool.
An interrupted, shared or inconclusive measurement marks nothing stable. Use `lab_status` and `lab_stop` to follow or stop the job.
Validate the proposed offset with the user's workloads; retry behavior can hide errors as lower performance, artifacts or stutter.
The sweep restores the user's daily profile or captured baseline and keeps nothing automatically.
