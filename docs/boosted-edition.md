# Boosted Edition — User Guide

This page documents the features exclusive to the Boosted Edition fork and the recommended settings to get the best results in the shortest time.

## Island Mode

Island Mode turns any OSM administrative boundary (canton, district, commune...) into an island surrounded by ocean.

### How to use it

1. In the map configuration editor, check **Island Mode (experimental)**.
2. Click **Search...** and pick the OSM boundary (e.g. *"District de la Glâne, Fribourg"*).
   The map center and size are set automatically from the boundary bounding box (+20% margin).
3. Generate as usual.

### What it does

* **Global elevation offset**: the whole terrain is shifted so that the 2nd percentile of the elevation inside the boundary lands slightly above sea level (+0.5m). The lowest real-world terrain becomes the coast.
* **Coast profile outside the boundary**: terrain blends from the coast elevation down to a seabed profile:
  * The blend ramp scales with the coast elevation (30m to 300m, ~12% slope max): low terrain turns into beaches right at the boundary, high terrain descends as progressive cliffs.
  * The seabed follows a smoothstep curve reaching the **ocean floor (-50m) at 500m** from the boundary.
  * Terrain outside the boundary is clamped so valleys and depressions cannot dig trenches along the coast.
* **Anti-flooding**: every cell inside the boundary is kept at or above **0.2m**. The clamp is applied twice: before *and after* the road/river constraint solver, so river beds and smoothed roads can never sink land below the ocean.
* **Seabed rendering**:
  * The id map (ground textures) is forced to the *OceanGround* material outside the boundary, then the coastline strip is re-drawn on the boundary edge. OSM land-use (fields, forests...) no longer leaks onto the seabed.
  * The satellite image is tinted with depth-based water colors outside the boundary (shallow turquoise near the coast, dark blue at depth). Swisstopo "No Data" transparent tiles are handled gracefully.

## Recommended settings

| Setting | Recommendation | Why |
|---|---|---|
| Cell size | **2 to 2.5 m** | Below 2m: 4x the work for almost no visual gain (the engine smooths anyway). The elevation detail comes from the source data, not the grid density. |
| Grid size | Map size ÷ cell size | e.g. 8192m map → 4096 grid × 2m; 20480m map → 8192 grid × 2.5m |
| Swisstopo high-res elevation | **On** for Swiss maps | This is where the terrain detail actually comes from. First run downloads a lot of data (cached afterwards). |
| Texture mask multiplier | 2 | Finer surface transitions, moderate cost. |
| Satellite resolution | 1 m/pixel | Only tested value. |
| PBO tool | **Built-in** (Options → Arma 3 → uncheck *Use PboProject*) | Optimized in this fork, no Mikero dependency. |

## Performance tips

* **Windows Defender exclusions** — biggest free win. Binarize and PBO packaging read thousands of small files; real-time scanning can double the duration. Exclude (PowerShell as admin):
  ```powershell
  Add-MpPreference -ExclusionPath 'P:\'
  Add-MpPreference -ExclusionPath "$env:USERPROFILE\Documents\GameRealisticMap"
  Add-MpPreference -ExclusionPath 'C:\Program Files (x86)\Steam\steamapps\common\Arma 3 Tools'
  ```
  Exclusions apply immediately, no restart needed.
* **Keep P:\ and the caches on an SSD/NVMe.**
* **Re-runs are much faster**: satellite tiles, Swisstopo elevation and prepared models are all cached.
* **Iterate without binarize**: use *Generate map file for Arma 3* (WRP only) to test terrain changes; only build the full mod for final versions. Binarize is the one single-threaded step nobody can parallelize (closed BI tool).

## What was fixed/optimized vs the original

| Area | Change |
|---|---|
| Island elevation pass | Boundary rasterized once instead of per-cell point-in-polygon tests: hours → seconds on 8192 grids |
| Island coast | Continuous shore-to-seabed profile, no trench along the boundary, -50m ocean floor |
| Satellite (island mode) | Water tint parallelized (was single-threaded, looked frozen after downloads), alpha fix for No Data tiles |
| Object filling | 75% CPU cap removed |
| Road constraints | Sampling step floored at 0.5m (node count exploded below 2m cell size) |
| Built-in PBO compiler | Parallel model prep, cached copies reused between runs, header-only model reads |
| PboFileSystem | Fixed a race condition in the lazy PBO index |
