# Explicit tuning policies
Mandatory session consent for device limits and configured legacy relaxations.

Clocktail starts with conservative effective limits. Device-reported adjustment ranges are capabilities, not proof of stability. Policy selection and approval perform zero GPU writes, start no workload, mark nothing stable and save no daily profile.

Use begin_session first. It reports requested/pending policy without prompting. Call set_tuning_policy with policy:
- conservative: immediately revoke any approval, without prompting.
- overrides: ask for only the configured legacy relaxations listed in the prompt. Other conservative restrictions remain.
- device: explicitly request and approve the in-scope device-limit policy.

Only set_tuning_policy opens the mandatory confirmation. Until approval, ordinary requests within conservative effective limits continue; a pending relaxation returns consent_required and points to that tool. CLOCKTAIL_MCP_TUNING_POLICY=device requests a policy but grants nothing. Invalid values are configuration errors. CLOCKTAIL_CONFIRM=off cannot skip this confirmation; dialog still selects the Windows prompt. An unchecked/missing checkbox, decline, dismissal, timeout, cancellation or unavailable UI grants nothing. A declined form never falls back to another prompt. Mandatory confirmation also stops on an ambiguous transport/decoding error; only unavailable elicitation (including MethodNotFound) can fall back to the Windows dialog. Cancellation prevents fallback.

Approval belongs to this worker, GPU, broker session and driver/recovery generation. It expires on session end/replacement (including another client), crash, reset/recovery, GPU change or worker restart. A delayed answer cannot approve a replacement session. Returning to conservative revokes permission only: use the existing restoration tools to restore settings. No automatic re-prompt or retry occurs. Widening settings requires fresh consent; configuration is an immutable startup snapshot and restarting loses approval.

Device mode covers explicit core P0, memory, XBAR offsets, V/F operations and the ordinary trial power limit (set_power_limit). It removes default step/daily allowance bounds and the policy floor for explicitly selected low-voltage curve windows. Omitted voltages retain the normal operating window; idle checks remain. The reference RTX 5090 XBAR demand heuristic becomes a structured advisory and never adds demand by itself. Its voltage-demand encoding, range and step remain unchanged.

F8 (set_power_cap), persistent set_daily_power_limit, other clock domains, current limits, fans, voltage boost and stress limits keep their existing policies and consent. Automatic memory/uncore tuners retain their previous candidates, search bounds and steps even after device approval. Daily-profile confirmation remains separate; saving a profile never saves approval. Crash allowance remains enforced.

## Environment precedence

All numeric names below have prefix CLOCKTAIL_MCP_. Explicit settings always remain restrictions after device approval. An omitted value means the shipped default in conservative/overrides mode and no extra software bound in device mode. Device bounds always apply.

| Setting | Shipped default | Tightening without consent | Relaxation after overrides consent |
| --- | --- | --- | --- |
| STEP_MHZ | 15 | smaller positive step | larger configured step |
| MEMORY_STEP_MHZ | 250 | smaller positive step | larger configured step |
| XBAR_STEP_MHZ | 50 | smaller positive XBAR step | larger configured XBAR step |
| SHIFT_STEP_MV | 25 | smaller positive shift | larger configured shift |
| MAX_ABOVE_DAILY_MHZ | 200 | smaller allowance, including zero | larger configured allowance |
| MIN_MV | 850 | higher floor for curve window/flatten operations | lower configured floor |
| MIN_POWER_W | 400 | higher ordinary trial minimum | lower configured ordinary trial minimum |
| MAX_POWER_W | 600 | lower ordinary trial maximum | higher configured ordinary trial maximum |
| MAX_XBAR_MHZ | no extra cap | any explicit nonnegative cap | no relaxation beyond the device range |

Example: STEP_MHZ=30 remains 15 until overrides consent, then permits 30 while memory steps remain 250, the daily allowance remains 200 and the XBAR heuristic still vetoes. Selecting device instead removes omitted defaults but still honors the explicit 30 MHz step.

This intentionally changes compatibility for users who previously widened these in-scope settings through environment variables alone. The old names no longer silently grant those relaxations. Out-of-scope uses of shared legacy configuration (SYS/Video steps, F8/daily power checks, automatic tuner caps) retain their previous behavior.

card_settings, diagnostic_report and session responses show requested/effective policy, pending/approved overrides, approval scope, queried ranges, configured restrictions and effective bounds, including the core P0 shared global-plus-point offset budget. Device reads are fresh for explicit writes; missing, invalid or unreadable bounds refuse the affected operation. shift_curve specifically requires a readable NVVDD MAX to cap its shift; missing voltage limits never expand the shift to the curve table maximum. Reports contain no reusable authorization token. Broker lifecycle checks run under its operation lock before a relaxed write. Approved writes use a distinct try-scoped operation, so a broker downgrade between the scope read and write is rejected rather than silently ignoring the scope field. There is no unscoped retry; broker scope/protocol/connection refusal revokes approval. Older brokers without policy-scope cannot enable relaxations. Normal conservative operations still work with older brokers.

Errors distinguish consent_required, policy_limit and device_limit. Firmware validation, global-plus-point offset budgets, editable-point checks, transactions, readback, curve preservation across P-state writes and crash recovery remain active. This is MCP workflow consent; other applications and the CLI have their own write paths.
