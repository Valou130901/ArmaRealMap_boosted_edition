# Game Realistic Map (Boosted Edition)

![](./GameRealisticMap.Studio/Resources/Icons/grms128.png)

This fork is a heavily upgraded version of Game Realistic Map, specifically tailored to maximize performance, add high-resolution data support, and introduce advanced terrain generation features.

**Download: see [Releases](https://github.com/Valou130901/ArmaRealMap_boosted_edition/releases) — unzip and run `GameRealisticMap.Studio.exe` (.NET 8 Desktop Runtime required).**

📖 **[Complete user guide](docs/user-guide.md)** — full walkthrough of every feature. Island mode internals & performance tuning: [docs/boosted-edition.md](docs/boosted-edition.md).

## 🚀 Boosted Edition Exclusive Features

### ⚡ Maximum Performance (100% CPU Utilization)
All arbitrary thread limits have been removed: object generation, image conversion, geometry filling, satellite water tinting, island elevation processing and PBO model preparation all run fully parallel on every logical core.

### 🏔️ Swisstopo swissALTI3D Support
Added experimental, automatic integration for **Swisstopo swissALTI3D** high-resolution elevation data. When generating Swiss maps, the engine utilizes ultra-precise topography for breathtaking realism.

### 🏝️ Advanced Island Mode
Turn any OSM administrative boundary into an island with natural coastlines:
* **Continuous coast profile**: the terrain blends smoothly from the real inland elevation down to a seabed profile — gentle beaches on low coasts, progressive cliffs on high coasts (blend ramp scales with coast elevation, capped at ~12% slope). The ocean floor (-50m) is reached ~500m from the boundary. No more trenches or walls along the coast.
* **Anti-Flooding Security**: land inside the boundary is guaranteed to stay above sea level (0.2m minimum), enforced again after the road/river constraint solver so river mouths and coastal roads cannot dig below the ocean.
* **Proper seabed rendering**: outside the boundary, the ground texture is forced to ocean ground (OSM land-use no longer leaks onto the seabed) and the satellite image is depth-tinted water.
* **Fast on any grid size**: the boundary polygon is rasterized once instead of millions of point-in-polygon tests — the island elevation pass takes seconds even on 8192x8192 grids.

### 📦 Fast Built-in PBO Compiler
The built-in mod packaging tool (Options → Arma 3 → uncheck *Use PboProject*) has been heavily optimized:
* Model preparation is parallel and reuses cached copies between runs (near-instant on re-runs).
* Model class detection reads only the P3D header instead of parsing the full ODOL.
* No dependency on Mikero's tools, and immune to the MakePbo lake-on-map-edge crash.

### 🗺️ Enhanced SatMap & IdMap Workflow
* **SatMap Reconstruction**: Regenerate a corrected satellite map (`satmap_corrected.png`) from your edited `IdMap` via a dedicated button. It fills each surface with its **real in-game ground texture** (grass, asphalt, sand…) then Gaussian-blurs the result, matching the soft look of a natively generated GRM satmap — ideal after painting new roads or water.
* **Improved UI & Nominatim Search**: The Nominatim search shows full boundary names instead of raw IDs. Selecting an island boundary auto-centers the map and sizes it to fit (+20% margin).

### 📥 Import Existing Maps (game & mods)
Import any Arma 3 map (official or from a mod) to edit it: **Fichier → Import a map from game or mods**.
* Scans the game, active mods and the whole Workshop; **junk entries from protected/obfuscated PBOs are filtered out**.
* Extracts the wrp (binarized OPRW supported), config, roads and **imagery layers wherever they live** — `.paa` tiles are decoded to PNG, binarized rvmat converted back to text, so satmap & id map become editable.
* Optional **custom PBO prefix / world name** to build your own independent version instead of overriding the original map.

### 🌲 Reduce Objects by Type
The **Réduire** tool can now thin out a whole category at once — tick *"Par type (motif)"* or use the quick buttons **Arbres / Buissons / Herbe / Rochers**. One rule reduces every matching model (e.g. remove half of all trees) instead of one model at a time. A **"Supprimer tous les objets"** button clears the map to start from scratch.

### ⛏️ Minecraft / WorldPainter Export
Export the elevation grid as a **16-bit grayscale heightmap PNG** (+ a readme documenting altitudes, sea level and scale) ready to import in WorldPainter for a 1:1 Minecraft world.

### 📦 Upgraded Engine Dependencies
Integrated an upgraded `bis-file-formats` library bringing:
* Drastically reduced memory usage for WRP files.
* Support for Arma 3's **ODOL v75** models and **Sqfc** compiled formats.
* PAA encoder fixes and migration to modern ImageSharp for robust texture processing.

---

*(All base features from the original Game Realistic Map toolchain are still supported.)*

## Data sources used in this edition
  - NASA SRTM (automatic)
  - JAXA AW3D30 (automatic)
  - OpenStreetMap (automatic)
  - Sentinel-2 cloudless (automatic)
  - **Swisstopo swissALTI3D (exclusive to this fork)**
