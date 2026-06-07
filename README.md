# Game Realistic Map (Boosted Edition)

![](./GameRealisticMap.Studio/Resources/Icons/grms128.png)

This fork is a heavily upgraded version of Game Realistic Map, specifically tailored to maximize performance, add high-resolution data support, and introduce advanced terrain generation features.

## 🚀 Boosted Edition Exclusive Features

### ⚡ Maximum Performance (100% CPU Utilization)
The original arbitrary thread limits have been completely removed. This fork fully utilizes 100% of your available logical cores, unlocking maximum performance and drastically reducing the time required for object generation, image conversion, and geometry filling processes.

### 🏔️ Swisstopo swissALTI3D Support
Added experimental, automatic integration for **Swisstopo swissALTI3D** high-resolution elevation data. When generating Swiss maps, the engine utilizes ultra-precise 2-meter resolution topography for breathtaking realism.

### 🏝️ Advanced Island Mode
The Island Mode has been overhauled to produce much more natural coastlines:
* **Realistic Coastal Buffers**: Automatically generates realistic flat beaches and shallow coastlines (150m buffer) that smoothly fade into the ocean floor over 200 meters, replacing the abrupt underwater cliffs of the original version.
* **Anti-Flooding Security**: A new algorithm guarantees that no terrain inside the island boundaries will accidentally sink below sea level (minimum 0.2m elevation enforcement).

### 🗺️ Enhanced SatMap & IdMap Workflow
* **SatMap Reconstruction**: You can now regenerate and export a corrected satellite map (`satmap_corrected.png`) directly from your edited `IdMap` and ground textures via a dedicated button in the World Editor.
* **Improved UI & Nominatim Search**: Upgraded the Nominatim search interface to display full boundary names instead of raw IDs, making map area selection much more intuitive.

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
