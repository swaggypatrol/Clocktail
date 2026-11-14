# User-owned profiles
There are no built-in tuning presets or default daily settings.

Clocktail targets the RTX 50 series across board vendors. It does not ship one board's curve, offsets, fan policy or power limit as a starting point.
A fresh install writes no GPU tuning settings and creates no daily profile.
## Keep and switch
- Daily/saved JSON files are editable but are not proof of approval. The Windows broker keeps per-user/GPU receipts under `%ProgramData%\Clocktail\Approvals`, writable only by Administrators/SYSTEM. On upgrade, use `load_daily` once and confirm the complete profile; there is no automatic first-start trust. An edited or unapproved Daily is not applied by startup, reset recovery or `back_to_daily`: owned settings are returned to their originals and the answer asks for approval.
- Only one broker confirmation may wait at a time; other confirmation requests fail immediately. Declining starts a short cooldown. The dialog holds no card operation lock, so recovery, `back_to_daily` and lab writes can continue; changes during the question invalidate its snapshot.
- `preview_daily`: inspect exactly what would be kept, or preview a user-saved profile. Its `values_fingerprint` is in the question `keep_as_daily` and `load_preset` ask. After MCP confirmation (`CLOCKTAIL_CONFIRM`), the elevated `Clocktail.Broker` independently asks the person via its native Windows confirmation dialog (60 s timeout, failing closed if declined or non-interactive) and checks the complete target profile fingerprint/state again before committing: any change while confirmation is pending writes nothing and requires a new preview and confirmation.
- `keep_as_daily`: after explicit agreement, keep eligible settings from this GPU as its daily profile. `adopt=true` can include settings left by another utility; preview them first.
- `save_profile name`: save the user's existing daily profile under a chosen name.
- `list_presets`: retained API name; the built-in `presets` list is empty, and `saved` contains only profiles this user saved for this GPU.
- `load_preset name`: retained API name for loading a user-saved profile after approval. A missing name writes nothing.
- `delete_profile name`: delete a user-saved entry after approval; deleting it does not remove the daily profile already kept.
- `load_daily`: apply an existing user-kept daily profile; fail without writing when none exists.
## Recovery and updates
Service startup and driver-reset recovery reapply only the user's existing, eligible daily profile.
`back_to_daily` restores that profile or returns Clocktail's changes to their captured baseline when no daily is kept.
Existing profiles and logs are user data: removing a former bundled preset does not silently delete or replace them.
