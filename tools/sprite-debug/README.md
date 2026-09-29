# Sprite Debug Bench

Standalone developer tool for reviewing generated 2D character animation sheets before wiring them into gameplay.

## Launch

From the repository root:

```powershell
python -m http.server 8127 --directory gk-fusion/tools/sprite-debug
```

Open <http://127.0.0.1:8127>.

Opening `index.html` directly also works with the two checked-in fallback assets, but the local server is preferred because it loads `assets/manifest.json` reliably.

## Add a generated sheet

1. Copy the PNG into `gk-fusion/tools/sprite-debug/assets/<character>/`.
2. Add a row to `gk-fusion/tools/sprite-debug/assets/manifest.json` with `file`, `columns`, `rows`, and `fps`.
3. Refresh the bench and inspect the filmstrip, baseline, centerline, timing, and loop.

The import dropzone is useful for reviewing an uncommitted local image. Imported files stay in browser memory; copy accepted assets into `assets/` and add them to the manifest when they are ready to review as part of the repo.

## Controls

- `Space`: play/pause
- `Left` / `Right`: step frames
- `G`: toggle center/baseline guides
- `O`: toggle previous-frame onion skin
- Inspection panel: playback speed, zoom, atlas columns/rows, and preview background

The atlas is read row-major: left to right across the first row, then left to right across the next row.
