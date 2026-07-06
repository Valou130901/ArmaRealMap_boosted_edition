# Game Realistic Map (Boosted Edition)

![](./GameRealisticMap.Studio/Resources/Icons/grms128.png)

This fork is a heavily upgraded version of Game Realistic Map, specifically tailored to maximize performance, add high-resolution data support, and introduce advanced terrain generation features.

**Download: see [Releases](https://github.com/Valou130901/ArmaRealMap_boosted_edition/releases) — unzip and run `GameRealisticMap.Studio.exe` (.NET 8 Desktop Runtime required).**

Full guide for the fork-exclusive features: [docs/boosted-edition.md](docs/boosted-edition.md)

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
* **SatMap Reconstruction**: You can now regenerate and export a corrected satellite map (`satmap_corrected.png`) directly from your edited `IdMap` and ground textures via a dedicated button in the World Editor.
* **Improved UI & Nominatim Search**: Upgraded the Nominatim search interface to display full boundary names instead of raw IDs, making map area selection much more intuitive. Selecting an island boundary automatically centers the map and sizes it to fit (+20% margin).

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
