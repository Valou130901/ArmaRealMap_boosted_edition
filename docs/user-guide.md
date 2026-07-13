# Game Realistic Map — Boosted Edition — User Guide

Complete guide for the Boosted Edition fork. For the quick feature list see the [README](../README.md); for the internals of island mode and performance tuning see [boosted-edition.md](boosted-edition.md).

## Table of contents

1. [Install & requirements](#install--requirements)
2. [Generating a map from real-world data](#generating-a-map-from-real-world-data)
3. [Island mode](#island-mode)
4. [Swisstopo high-resolution elevation](#swisstopo-high-resolution-elevation)
5. [Importing an existing map (game or mods)](#importing-an-existing-map-game-or-mods)
6. [The World Editor](#the-world-editor)
   - [Imagery: satmap & id map](#imagery-satmap--id-map)
   - [Regenerating the satmap from surfaces](#regenerating-the-satmap-from-surfaces)
   - [Elevation grid & Minecraft heightmap export](#elevation-grid--minecraft-heightmap-export)
   - [Objects: import, replace, reduce, clear](#objects-import-replace-reduce-clear)
7. [Generating the mod (packaging)](#generating-the-mod-packaging)
8. [Performance tuning](#performance-tuning)
9. [Recommended settings](#recommended-settings)

---

## Install & requirements

- Download the latest build from [Releases](https://github.com/Valou130901/ArmaRealMap_boosted_edition/releases).
- Unzip and run `GameRealisticMap.Studio.exe`.
- Requires the **.NET 8 Desktop Runtime**.
- For packaging a playable Arma 3 mod you also need **Arma 3 Tools** (Steam) installed, so the project drive (`P:`) can be mounted and Binarize is available. The built-in PBO compiler removes the dependency on Mikero's tools.

---

## Generating a map from real-world data

1. **Accueil → new map config** (`.grma3m`).
2. Set the **center coordinates** (or draw a zone on the OSM map with Ctrl+drag / Alt+drag).
3. Set the **map size** and grid. The editor shows `grid × cell size → total meters`. Aim for a **2 to 2.5 m** cell size.
4. Pick a **map style** (`builtin:CentralEurope.grma3a` for European countryside). Click **Modifier** to change the asset library (buildings, vegetation…).
5. Optionally enable **Island Mode** and **Swisstopo** (see below).
6. **Générer un aperçu** for a fast preview, **Générer un fichier carte pour Arma 3** for the WRP, or **Générer un mod pour Arma 3** for the full playable mod.

All terrain, roads, building footprints, forests and fields come from OpenStreetMap; elevation from SRTM / AW3D30 (or Swisstopo in Switzerland); satellite colors from Sentinel-2.

---

## Island mode

Turns any OSM administrative boundary (district, commune, canton…) into an island surrounded by ocean.

**How to use:**
1. Check **Island Mode (experimental)**.
2. Click **Search…**, pick the OSM boundary (e.g. *"District de la Glâne, Fribourg"*). The map center and size are set automatically to fit the boundary (+20% margin).
3. Generate as usual.

**What it does (fully automatic):**
- **No terrain deformation inside the boundary.** The whole map is translated vertically so the lowest point of the district sits just above sea level; the real relief is preserved 1:1.
- **Natural coasts outside the boundary.** The terrain descends from the boundary edge elevation to the ocean floor (-50 m, reached ~500 m out) following a smoothstep profile. The edge elevation field is smoothed so hilly boundaries no longer create vertical walls or radiating seams. Low boundaries become beaches, high boundaries become progressive slopes/cliffs.
- **Anti-flooding.** Land inside the boundary is kept above sea level (0.2 m minimum), enforced again *after* the road/river solver so river mouths and coastal roads never dig below the ocean.
- **No holes.** Any "ocean" area enclosed inside the island (a rasterization or geometry artifact) is turned back into land.
- **Seabed rendering.** Outside the boundary the ground texture is forced to sand (clean beach/seabed, no algae clutter) and the satellite image is depth-tinted water (tropical turquoise near the coast, deep lagoon offshore).

If you want real beaches where the boundary is perched high, you sculpt them yourself afterwards — the automatic coast is a buffer that never touches the real inland terrain.

---

## Swisstopo high-resolution elevation

For Swiss maps, check **Swisstopo high-res elevation**. The engine downloads swissALTI3D high-resolution topography instead of the ~30 m global DEM.

- This is where real terrain detail comes from — cliffs, riverbanks, talus.
- First run downloads a large amount of data (cached afterwards).
- Only works for maps located in Switzerland.

---

## Importing an existing map (game or mods)

**Menu Fichier → "Import a map from game or mods…"**

1. The tool scans all Arma 3 PBOs, active mods, and the whole Workshop content for maps. Protected/obfuscated PBOs inject decoy entries; these are filtered out (only real maps with a valid name and non-zero size are listed).
2. Each row has an **Import** button. Optionally fill:
   - **Custom PBO prefix** (e.g. `myname\malden_custom`) — creates an independent version so your mod does not override the original map.
   - **Custom world name** — the world name of your version.
   - Leave both empty to keep the original prefix.
3. On import the tool:
   - Extracts the whole map PBO (wrp, config, roads shapefiles, map data).
   - Converts the binarized `.wrp` (OPRW) back to editable format.
   - Extracts the imagery layers wherever they live (the wrp's own material paths are used, so layers shipped in a separate PBO or another mod are found), decodes the `.paa` mask/satmap tiles to PNG, and converts binarized rvmat back to text.
   - Opens the map in the World Editor with editable imagery, elevation, objects and materials.

**Known limits:**
- The binarized road network of official maps cannot be edited.
- Binarize can crash on some official maps once un-binarized (a BI tool bug). If packaging fails, edit the terrain/objects and re-generate a fresh GRM map instead, or use the map only for personal editing.
- Publishing a mod that contains a copied BI map wrp is a legal gray area — personal use only.
- Importing/deobfuscating **protected** PBOs is intentionally not supported.

---

## The World Editor

Open a `.wrp` (double-click, recent files, or after an import). The editor has sections: Imagery, Ground materials, Elevation grid, Objects, Dependencies, plus the visual **map editor** (Ouvrir l'éditeur de carte).

### Imagery: satmap & id map

- **Exporter l'image satellite / le masque de texture** — export the assembled satmap or id map to PNG.
- **Importer l'image satellite / le masque de texture** — re-import an edited PNG (updates the tiles).
- Tile size, total image size and resolution are shown. The tile overlap is measured from the map files, so imported maps with a non-GRM land grid work correctly.

### Regenerating the satmap from surfaces

**Generate SatMap from IdMap** rebuilds the satellite image from the painted surfaces, matching the soft look of a GRM-generated satmap:
- Each surface is filled with the low-resolution tile of its **real in-game ground texture** (grass, asphalt, sand…), then the whole image is Gaussian-blurred so surfaces blend smoothly.
- Use this after editing the texture mask (Surface Painter, or painting the id map) so the satellite photo matches your new roads / water / canals.
- Workflow: export the mask to `<prefix>\IdMap.png` → **Generate SatMap from IdMap** → it writes `…-satmap_corrected.png` → **Importer l'image satellite** to apply it.
- Note: for a few localized edits, editing the existing satmap directly in an image editor preserves the real satellite detail better than regenerating the whole image.

### Elevation grid & Minecraft heightmap export

- **Importer / Exporter (Esri ASCII .asc)** — round-trip the elevation grid.
- **Export heightmap PNG (Minecraft)** — exports a 16-bit grayscale heightmap plus a `…readme.txt` documenting the value mapping (min/max altitude, sea-level gray value, pixel size). Import it in [WorldPainter](https://www.worldpainter.net/) for a Minecraft world:
  - Scale so 1 pixel = 1 block for a 1:1 world (recommended; keeps real proportions and vertical scale within Minecraft's -64…320 range).
  - Set the water level to the gray value given in the readme.

### Objects: import, replace, reduce, clear

- **Importer depuis un fichier** — import objects (Terrain Builder / Eden export).
- **Exporter vers un fichier** — export objects + a `.tml` library.
- **Remplacer** — mass replace one model by another.
- **Réduire** — mass thin-out objects. **Reduce by type:** tick *"Par type (motif)"* so the model field becomes a substring pattern, or use the quick-add buttons **Arbres / Buissons / Herbe·clutter / Rochers**. One rule can reduce *all* tree models at once (e.g. factor 0.5 removes half). The initial and estimated-remaining counts update live.
- **Supprimer tous les objets** — removes every object to start from scratch (with confirmation). Save the wrp to persist.
- **Prendre les images aériennes** — take aerial screenshots.

---

## Generating the mod (packaging)

**Générer un mod pour Arma 3** builds the playable `@mod`.

Two packaging back-ends (Outils → Options → Arma 3):
- **Built-in tool** (default, uncheck *Use PboProject*) — optimized in this fork: parallel model preparation, cached model copies reused between runs, header-only model reads, immune to the MakePbo lake-on-edge crash.
- **PboProject** (Mikero) — the classic external tool.

Binarize itself (the BI terrain compiler) is single-threaded and cannot be parallelized; it is usually the longest step.

---

## Performance tuning

- **Windows Defender exclusions** — the biggest free win. Binarize/packaging read thousands of small files. Exclude `P:\`, your mods folder and the Arma 3 Tools folder (PowerShell as admin):
  ```powershell
  Add-MpPreference -ExclusionPath 'P:\'
  Add-MpPreference -ExclusionPath "$env:USERPROFILE\Documents\GameRealisticMap"
  Add-MpPreference -ExclusionPath 'C:\Program Files (x86)\Steam\steamapps\common\Arma 3 Tools'
  ```
  Effective immediately, no restart.
- **Keep `P:\` and caches on SSD/NVMe.**
- **Re-runs are much faster** — satellite tiles, Swisstopo elevation and prepared models are cached.
- **All CPU cores are used** — object filling, image processing, satellite tinting, island processing and PBO model prep all run fully parallel (no arbitrary thread caps).
- **Iterate without binarize** — use *Generate map file* (WRP only) to test terrain, only build the full mod for final versions.

---

## Recommended settings

| Setting | Recommendation | Why |
|---|---|---|
| Cell size | **2 to 2.5 m** | Below 2 m: 4× the work, almost no visual gain. Detail comes from the source data, not grid density. |
| Grid size | Map size ÷ cell size | e.g. 8192 m → 4096 grid × 2 m; 20480 m → 8192 grid × 2.5 m |
| Swisstopo | **On** for Swiss maps | Where the terrain detail comes from. |
| Texture mask multiplier | 2 | Finer surface transitions, moderate cost. |
| Satellite resolution | 1 m/pixel | Only tested value. |
| PBO tool | **Built-in** | Optimized, no Mikero dependency. |
