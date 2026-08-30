# BeamNG.drive export

Two ways to get a BeamNG level out of this fork. *(Français : [beamng.fr.md](beamng.fr.md).)*

| | **Direct generation** | **Export from an Arma world** |
|---|---|---|
| Where | Map config editor → *Generate a BeamNG.drive level* | World editor → *Export → BeamNG.drive level* |
| Source | Real-world data (OSM, elevation, swisstopo) | An existing `.wrp` |
| Buildings | swissBUILDINGS3D volumes, textured | The map's own Arma models |
| Best for | Building a region from scratch | Converting a map you already have |

Both write a mod zip. Drop it in `Documents\BeamNG.drive\<version>\mods\` and the level appears in Freeroam.

## Direct generation

Set the map up as usual — centre, grid size, cell size — then use **Generate a BeamNG.drive level**. The Arma map style and its missing-mod warning do not apply here: this path never touches Arma assets.

Tick **Swisstopo high-res elevation** for a Swiss map. That one switch also turns on everything below.

### What Swisstopo gives you

* **Terrain** — swissALTI3D, the real relief.
* **Buildings** — swissBUILDINGS3D volumes with their real roof shapes, one editable object per zone. They carry no texture of their own, so walls and roofs are told apart by the tilt of each face and unwrapped in metres: one facade tile covers three metres of wall, which is a storey, so a window is the same size on a hangar as on a cottage. The windows themselves are invented — no open Swiss dataset says what is on a wall.
* **Trees** — swissSURFACE3D minus the terrain gives the canopy, and every local high point in it is a tree that really stands there, at the height it really has. The species is picked from that height: conifers hold the tall canopy, broadleaves the middle, scrub the bottom. Crowns standing on a roof or over a carriageway are dropped.
* **Ground** — SWISSIMAGE instead of Sentinel-2. Zoom 16 and no finer: the level carries its ground on a 4096 pixel texture, so anything sharper is fetched only to be thrown away by the resize.

First run downloads a lot and caches it: about 1 MB per square kilometre for the elevation, 19 MB for the surface model. A 16 km map is roughly 12 GB fetched once.

### Real Arma meshes

Trees, rocks and bridges are drawn with Arma models converted by the model port (**Asset browser → Build the Reforger/BeamNG model library**), shared by every map. Whatever is in the library gets used; anything missing falls back to a generated billboard, so the export never fails for want of a model.

## Sizing

BeamNG's own largest level, Italy, is 4.1 km square. This exporter caps the terrain at 8192 cells, which at 2 m is 16.4 km — a hundred times Italy's area. That works, but object counts and the zip size grow with it, and so does the download.

| Grid | Cell | Map | Note |
|---|---|---|---|
| 4096 | 2 m | 8.2 km | Comfortable, close to vanilla practice |
| 8192 | 2 m | 16.4 km | Whole small district, heavy |
| 8192 | 3 m | 24.6 km | Large district, terrain detail suffers |

## Checking an export without launching the game

`tools/check-beamng-export.py` reads the zip and reports. Every check in it exists because a real defect shipped past it.

```bash
python tools/check-beamng-export.py <map name> --dll GameRealisticMap.Studio/bin/Debug/net8.0-windows/GameRealisticMap.Studio.dll
```

The map name is enough — `malden`, `romont` — it is matched against the mods folder. Passing `--dll` adds a freshness check that catches the commonest mistake of all: testing an export made before the build you meant to test.

It covers inverted normals, untextured geometry, road profile steps, missing texture references, junction gaps and tearing, folded roads, bridge deck height and clearance, forest altitude, road specular, night lights and spawn points. `--absent <name>` asserts that a family of objects really is gone.

## Known limits

* Facades are generated, not real. swissBUILDINGS3D is geometry only, and the 3.0 beta covers just under half the tiles in CityGML — the rest is DWG and Esri geodatabase.
* Roads come from OSM. Where OSM continues a road as a track or a path, the export stops it: only carriageways at least 3 m wide are drivable.
* Photogrammetric facades would need a source with oblique imagery. swisstopo publishes none, and Google's photorealistic tiles forbid deriving a dataset from them.
