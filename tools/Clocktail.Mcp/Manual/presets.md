# User-owned profiles
There are no built-in tuning presets or default daily settings.

Clocktail targets the RTX 50 series across board vendors. It does not ship one board's curve, offsets, fan policy or power limit as a starting point.
A fresh install writes no GPU tuning settings and creates no daily profile.
## Keep and switch
- `preview_daily`: inspect exactly what would be kept, or preview a user-saved profile.
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
