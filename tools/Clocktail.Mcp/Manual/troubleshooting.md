# Troubleshooting
Diagnose the actual device and operation; do not fall back to a supplied profile.

| Symptom | Meaning and next step |
|---|---|
| No daily profile is kept | Normal on a fresh install. Read the GPU and propose trials; do not offer a built-in starter profile. |
| A profile name is missing | Only user-saved profiles can be loaded. Use `list_presets`; missing names write nothing. |
| Service does not answer | Explain it, then offer `install_service` with approval. |
| `confirm_in_chat` | Nothing changed. Present the question, wait for explicit agreement, then call with the returned one-time code. |
| `written: none` | No setting was written or the requested value was already in force. |
| `written: unknown` | Outcome is not established. Read `card_settings`; never blindly repeat a write. |
| Driver rejects or readback differs | Report the actual rejection or mismatch. Do not claim success or substitute another card's value. |
| Capability or sensor unavailable | Report missing support for that control or layout; do not copy a stock value from another board. |
| Busy measurement or write window | Wait for it or stop the relevant job; concurrent changes invalidate attribution. |
| Partial profile application | Explain `not_applied` and the affected groups; inspect the actual card state. |
| Driver reset | Let recovery settle, then inspect trial data and current settings before further changes. |
| Test stops hot | It reached the user's `maxTempC`, or (stress_test, lab_test) came within 2 °C of NVIDIA's slowdown point; the answer says which. Neither a pass nor proof the setting failed. |
| Benchmark is shared or disturbed | Close competing workloads or wait and repeat; do not select a tuning result from contaminated timing. |

Update MCP, broker and lab together. Preserve recovery evidence and user profiles; do not delete them as a troubleshooting shortcut.
