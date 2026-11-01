# Running a session
Trials are measured changes; a daily profile is an explicitly approved result for this GPU.

1. Read `card_report`, `card_settings` and `read_log`. Determine whether there is a user-owned daily profile or only a captured baseline.
2. Obtain the user's agreement with `begin_session`.
3. Choose a trial that serves the user's goal; tools take driver-reported ranges rather than another card's settings.
4. Check readback, run correctness tests and compare repeatable performance.
5. Record `mark_stable`, `mark_crash` and notes. `back_to_daily` restores the kept settings, or the original baseline when no daily exists.
6. For a result to persist, show `preview_daily` and ask before `keep_as_daily`. End with `end_session`.

Under a wall, changing the curve can change voltage instead of clock. To isolate frequency headroom, loosen the wall or the current limit after each raise if that is within the approved scope, and verify which point ran.
No fixed voltage band, offset, power target or stability margin is assumed.
Every write tool and its persistence behavior is in `manual controls`.
