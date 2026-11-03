Run on Windows with the .NET 10 SDK:

```powershell
dotnet run --project tools/Clocktail.UiPreview -c Release -- --output out/ui-preview
```

This developer tool opens the actual editor with deterministic synthetic data, exercises its controls and saves PNGs of the client area. It never creates a card connection or calls the broker. Success and failure saves use local callbacks. It is not included in release packages.

The checks cover exact opening/restore, numeric edits, undo/redo, cancelled drags, history-layer caching, control bounds and keyboard accessibility, text committed before saving, and saved/error/details states. `layout-150pct.png` exercises the editor's explicit 150% layout; it does not simulate moving between physical monitors. Windows CI retains the images as the `curve-editor-preview` artifact.

For a reviewer who can read GitHub logs but cannot download binary artifacts, a manual CI run can enable `visual_logs`. This adds `--emit-previews` to the tool and emits selected synthetic PNGs as tagged base64 chunks. Normal CI runs keep images in artifacts only.
