# Boosted Edition — User Guide

This page documents the features exclusive to the Boosted Edition fork and the recommended settings to get the best results in the shortest time. *(Français : [boosted-edition.fr.md](boosted-edition.fr.md).)*

## Island Mode

Island Mode turns any OSM administrative boundary (canton, district, commune...) into an island surrounded by ocean.

### How to use it

1. In the map configuration editor, check **Island Mode (experimental)**.
2. Click **Search...** and pick the OSM boundary (e.g. *"District de la Glâne, Fribourg"*).
   The map center and size are set automatically from the boundary bounding box (+20% margin).
3. Generate as usual.

### What it does

* **No terrain deformation inside the boundary**: the whole map is translated vertically so the *lowest* point of the district (0.1th percentile, to ignore bad DEM pixels) sits just above sea level (+0.5m). The real relief is preserved 1:1 — nothing inside the boundary is bent, flattened or flooded. High boundary edges simply become cliffs or long coastal slopes.
* **Coast profile outside the boundary**: the terrain descends from the elevation of the *nearest boundary point* (propagated by a feature/distance transform, not the raw outside terrain) down to a seabed profile:
  * The seabed follows a smoothstep curve reaching the **ocean floor (-50m) at ~500m** from the boundary.
  * The blend ramp scales with the edge elevation (up to ~2 km for high boundaries): low boundaries become beaches, high boundaries become progressive slopes.
  * The edge-elevation field is **smoothed** so a hilly boundary (hills/valleys alternating along the edge) no longer produces vertical walls or seams radiating from the coast.
* **No holes**: any "ocean" area enclosed inside the island (a rasterization or geometry artifact) is turned back into land — the island can never contain trenches carved to the ocean floor.
* **Anti-flooding**: every cell inside the boundary is kept at or above **0.2m**, enforced *after* the road/river constraint solver so river beds and smoothed roads can never sink land below the ocean.
* **Seabed rendering**:
  * Outside the boundary the id map is forced to **sand** (clean beach/seabed, no ocean-ground algae clutter); the coastline strip is re-drawn on the boundary edge. OSM land-use no longer leaks onto the seabed.
  * The satellite image is depth-tinted water (tropical turquoise near the coast, deep lagoon offshore). Swisstopo "No Data" transparent tiles are handled gracefully.

If you want real beaches where the boundary is perched high, sculpt them yourself afterwards — the automatic coast is a buffer that never touches the real inland terrain.

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
