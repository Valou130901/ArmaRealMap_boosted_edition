using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GameRealisticMap.Arma3.Assets;
using GameRealisticMap.Arma3.GameEngine.Roads;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Geometries;
using Pmad.HugeImages;
using Pmad.HugeImages.Processing;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.BeamNG
{
    internal enum BeamNGForestKind { Tree, Bush, Rock }

    internal record struct BeamNGForestInstance(float X, float Y, float YawRad, float Scale, BeamNGForestKind Kind);

    internal record struct BeamNGPond(float X, float Y, float SurfaceZ, float Size, float YawRad);

    internal record struct BeamNGBuildingBox(float X, float Y, float YawRad, float Width, float Depth, float Height);

    internal record BeamNGRoadInput(List<TerrainPoint> Points, float Width, bool IsDirt);

    /// <summary>
    /// Writes a playable BeamNG.drive level zip from an elevation grid, satellite/id imagery,
    /// the road network and the vegetation objects. Level structure and .ter binary format
    /// (version 9) follow BeamNG official levels (validated by the mapng project).
    /// </summary>
    internal class BeamNGLevelWriter
    {
        private readonly ElevationGrid grid;
        private readonly float cellSize;
        private readonly string levelName;
        private readonly string levelTitle;
        private readonly HugeImage<Rgb24>? satMap;
        private readonly HugeImage<Rgba32>? satMapRgba;
        private readonly HugeImage<Rgb24>? idMap;
        private readonly List<TerrainMaterialDefinition>? materials;
        private readonly List<BeamNGRoadInput>? roads;
        private readonly List<BeamNGForestInstance>? vegetation;
        private readonly List<BeamNGPond>? ponds;
        private readonly List<BeamNGBuildingBox>? buildings;
        private readonly byte[]? presetLayerMap;
        private readonly List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>? buildingMeshes;
        // Buildings are merged per zone: one object per zone keeps the editor responsive while
        // still letting a district be edited area by area
        private const float BuildingZoneSize = 500f;
        private const int MaxBuildingZones = 4_000;

        private const int BaseTextureSize = 4096;
        private const int PreviewSize = 512;
        private const int HeightmapPngMaxSize = 2048;
        private const int MaxForestInstancesPerType = 500_000;
        private const int MaxDecalRoadNodes = 150;
        private const float MinRoadNodeSpacing = 4f;
        private const float RoadWidthFactor = 1.3f;
        // Light smoothing of the elevation grid: removes the faceted look of the terrain mesh
        // without flattening the real relief
        private const float TerrainSmoothing = 0.55f;

        // Terrain layers: physics (groundmodel) varies per painted surface, visuals stay the satellite image
        private static readonly (string Name, string GroundModel)[] TerrainLayers =
        {
            ("grm_grass", "GRASS"),
            ("grm_asphalt", "ASPHALT"),
            ("grm_dirt", "DIRT"),
            ("grm_gravel", "GRAVEL"),
            ("grm_sand", "SAND"),
            ("grm_rock", "ROCK"),
            ("grm_mud", "MUD"),
        };

        public BeamNGLevelWriter(ElevationGrid grid, float cellSize, string levelName, string levelTitle,
            HugeImage<Rgb24>? satMap, HugeImage<Rgb24>? idMap, List<TerrainMaterialDefinition>? materials,
            List<BeamNGRoadInput>? roads, List<BeamNGForestInstance>? vegetation,
            List<BeamNGPond>? ponds = null, List<BeamNGBuildingBox>? buildings = null,
            HugeImage<Rgba32>? satMapRgba = null, byte[]? presetLayerMap = null,
            List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>? buildingMeshes = null)
        {
            this.buildingMeshes = buildingMeshes;
            this.grid = grid;
            this.cellSize = cellSize;
            this.levelName = Sanitize(levelName);
            this.levelTitle = levelTitle;
            this.satMap = satMap;
            this.satMapRgba = satMapRgba;
            this.idMap = idMap;
            this.materials = materials;
            this.roads = roads;
            this.vegetation = vegetation;
            this.ponds = ponds;
            this.buildings = buildings;
            this.presetLayerMap = presetLayerMap;
        }

        private float[]? smoothedHeights;
        private int smoothedSize;

        /// <summary>
        /// Blend the elevation grid with a 3x3 average. At 2-4 m per cell the raw grid renders as
        /// visible triangular facets; a light blend keeps the real relief but softens the mesh.
        /// Used for the terrain and for object placement alike, so nothing floats or sinks.
        /// </summary>
        private void BuildSmoothedHeights(int size)
        {
            smoothedSize = size;
            var source = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    source[y * size + x] = grid[x, y];
                }
            }
            var result = new float[size * size];
            Parallel.For(0, size, y =>
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = 0f;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var sy = y + dy;
                        if (sy < 0 || sy >= size) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var sx = x + dx;
                            if (sx < 0 || sx >= size) continue;
                            sum += source[sy * size + sx];
                            count++;
                        }
                    }
                    var average = sum / count;
                    var original = source[y * size + x];
                    result[y * size + x] = original + (average - original) * TerrainSmoothing;
                }
            });
            smoothedHeights = result;
        }

        private float Height(int x, int y)
        {
            if (smoothedHeights == null)
            {
                return grid[x, y];
            }
            return smoothedHeights[Math.Clamp(y, 0, smoothedSize - 1) * smoothedSize + Math.Clamp(x, 0, smoothedSize - 1)];
        }

        /// <summary>
        /// Bilinear sample of the smoothed elevation, in terrain meters.
        /// </summary>
        private float ElevationAt(float terrainX, float terrainY)
        {
            if (smoothedHeights == null)
            {
                return grid.ElevationAt(new TerrainPoint(terrainX, terrainY));
            }
            var gx = terrainX / cellSize;
            var gy = terrainY / cellSize;
            var x0 = (int)MathF.Floor(gx);
            var y0 = (int)MathF.Floor(gy);
            var tx = gx - x0;
            var ty = gy - y0;
            var h00 = Height(x0, y0);
            var h10 = Height(x0 + 1, y0);
            var h01 = Height(x0, y0 + 1);
            var h11 = Height(x0 + 1, y0 + 1);
            var top = h00 + (h10 - h00) * tx;
            var bottom = h01 + (h11 - h01) * tx;
            return top + (bottom - top) * ty;
        }

        private static string Sanitize(string name)
        {
            var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars).Trim('_');
        }

        public async Task WriteAsync(string targetZipFile, IProgressScope scope)
        {
            var size = grid.Size;
            if ((size & (size - 1)) != 0 || size > 8192)
            {
                throw new ApplicationException($"BeamNG terrains must be a power-of-two size up to 8192 (map grid is {size}).");
            }

            using (scope.CreateSingle("Smoothing elevation"))
            {
                BuildSmoothedHeights(size);
            }

            var min = float.MaxValue;
            var max = float.MinValue;
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    var v = Height(x, y);
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            var floor = MathF.Floor(min);
            var range = Math.Max(1f, MathF.Ceiling(max) - floor);
            var worldSize = size * cellSize;
            var half = worldSize / 2f;

            var basePath = $"levels/{levelName}";

            byte[]? layerMap = null;
            if (presetLayerMap != null || (idMap != null && materials != null && materials.Count > 0) || (roads != null && roads.Count > 0))
            {
                using (scope.CreateSingle("Surface layer map"))
                {
                    layerMap = presetLayerMap ?? await BuildLayerMap(size).ConfigureAwait(false) ?? new byte[size * size];
                    // GRM id maps do not paint asphalt under the roads (roads are separate objects in
                    // Arma): burn the road network into the layer map so roads get road physics
                    BurnRoadsIntoLayerMap(layerMap, size);
                }
            }
            var useLayers = layerMap != null;
            var materialNames = useLayers ? TerrainLayers.Select(l => l.Name).ToArray() : new[] { "DefaultMaterial" };

            var forestByType = BuildForestPlacements(floor, half, scope);
            var decalRoads = BuildDecalRoads(floor, half);

            using var zipStream = File.Create(targetZipFile);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);

            var directories = new List<string>
            {
                "levels/", $"{basePath}/", $"{basePath}/art/", $"{basePath}/art/terrains/",
                $"{basePath}/main/", $"{basePath}/main/MissionGroup/",
                $"{basePath}/main/MissionGroup/Level_objects/", $"{basePath}/main/MissionGroup/Level_objects/Other/",
                $"{basePath}/main/MissionGroup/PlayerDropPoints/", $"{basePath}/main/MissionGroup/Water/"
            };
            if (decalRoads.Count > 0)
            {
                directories.Add($"{basePath}/main/MissionGroup/Decal_Roads/");
                directories.Add($"{basePath}/art/roads/");
            }
            if (forestByType.Count > 0)
            {
                directories.Add($"{basePath}/main/MissionGroup/Level_objects/vegetation/");
                directories.Add($"{basePath}/art/forest/");
                directories.Add($"{basePath}/forest/");
            }
            if (forestByType.Count > 0 || (buildings != null && buildings.Count > 0) || (buildingMeshes != null && buildingMeshes.Count > 0))
            {
                directories.Add($"{basePath}/art/shapes/");
            }
            if (buildingMeshes != null && buildingMeshes.Count > 0)
            {
                directories.Add($"{basePath}/art/shapes/buildings/");
                directories.Add($"{basePath}/main/MissionGroup/Buildings/");
            }
            foreach (var dir in directories)
            {
                zip.CreateEntry(dir);
            }

            using (scope.CreateSingle("Terrain (.ter)"))
            {
                WriteTer(zip, $"{basePath}/theTerrain.ter", size, floor, range, layerMap, materialNames);
            }

            using (scope.CreateSingle("Heightmap preview"))
            {
                WriteHeightmapPng(zip, $"{basePath}/theTerrain.terrainheightmap.png", size, floor, range);
            }

            var baseTexSize = (satMap != null || satMapRgba != null) ? BaseTextureSize : 1024;
            Image<Rgba32>? baseTexture = null;
            try
            {
                if (satMap != null || satMapRgba != null)
                {
                    using (scope.CreateSingle("Base texture (satellite)"))
                    {
                        baseTexture = satMap != null
                            ? await DownscaleHugeImage(satMap, BaseTextureSize, false).ConfigureAwait(false)
                            : await DownscaleHugeImage(satMapRgba!, BaseTextureSize, false).ConfigureAwait(false);
                    }
                }
                WriteBaseTextureAndPreview(zip, basePath, baseTexture, size, floor, range);
            }
            finally
            {
                baseTexture?.Dispose();
            }

            using (scope.CreateSingle("Level definition"))
            {
                WriteLevelDefinition(zip, basePath, size, floor, range, worldSize, half, materialNames, useLayers, baseTexSize, decalRoads, forestByType, scope);
            }
        }

        private void WriteLevelDefinition(ZipArchive zip, string basePath, int size, float floor, float range,
            float worldSize, float half, string[] materialNames, bool useLayers, int baseTexSize,
            List<Dictionary<string, object>> decalRoads, Dictionary<string, List<BeamNGForestInstance>> forestByType, IProgressScope scope)
        {
            WriteJson(zip, $"{basePath}/info.json", new Dictionary<string, object>
            {
                ["authors"] = "GameRealisticMap",
                ["defaultSpawnPointName"] = "spawn_default",
                ["description"] = $"Generated by GameRealisticMap from {levelTitle}",
                ["previews"] = new[] { "preview.png" },
                ["size"] = new[] { size, size },
                ["spawnPoints"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "Default",
                        ["objectname"] = "spawn_default",
                        ["preview"] = "preview.png",
                        ["translationId"] = "Default Spawnpoint",
                    }
                },
                ["title"] = levelTitle,
            });

            WriteText(zip, $"{basePath}/mainLevel.lua", "-- Auto-generated by GameRealisticMap\nlocal M = {}\nreturn M\n");

            WriteJson(zip, $"{basePath}/theTerrain.terrain.json", new Dictionary<string, object>
            {
                ["binaryFormat"] = "version(char), size(unsigned int), heightMap(heightMapSize * heightMapItemSize), layerMap(layerMapSize * layerMapItemSize), layerTextureMap(layerMapSize * layerMapItemSize), materialNames",
                ["datafile"] = $"/levels/{levelName}/theTerrain.ter",
                ["heightMapItemSize"] = 2,
                ["heightMapSize"] = size * size,
                ["heightmapImage"] = $"/levels/{levelName}/theTerrain.terrainheightmap.png",
                ["layerMapItemSize"] = 1,
                ["layerMapSize"] = size * size,
                ["materials"] = materialNames,
                ["size"] = size,
                ["version"] = 9,
            });

            // Terrain materials: full PBR (satellite base + official detail/macro textures) when the
            // layer map is available, single stretched satellite otherwise
            var terrainMaterials = new Dictionary<string, object>();
            var textureSetName = "";
            var satellitePath = $"/levels/{levelName}/art/terrains/terrain.png";
            string TerrainFile(string f) => $"/levels/{levelName}/art/terrains/{f}";
            if (useLayers)
            {
                textureSetName = $"{levelName}TerrainMaterialTextureSet";
                terrainMaterials[textureSetName] = new Dictionary<string, object>
                {
                    ["name"] = textureSetName,
                    ["class"] = "TerrainMaterialTextureSet",
                    ["persistentId"] = Guid.NewGuid().ToString(),
                    ["baseTexSize"] = new[] { baseTexSize, baseTexSize },
                    ["detailTexSize"] = new[] { 1024, 1024 },
                    ["macroTexSize"] = new[] { 1024, 1024 },
                };
                var templates = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(LoadTerrainMaterialsJson())!;
                // Layer -> (proven material template, ground model, detail colour, noise, tile size in meters)
                var mapping = new (string Layer, string Template, string GroundModel, Rgb24 Color, int Noise, int TileMeters)[]
                {
                    ("grm_grass", "Grass", "GRASS", new Rgb24(96, 112, 62), 26, 3),
                    ("grm_asphalt", "asphalt", "ASPHALT", new Rgb24(64, 64, 68), 12, 4),
                    ("grm_dirt", "Dirt", "DIRT", new Rgb24(118, 95, 68), 24, 3),
                    ("grm_gravel", "GRAVEL", "GRAVEL", new Rgb24(132, 126, 116), 34, 2),
                    ("grm_sand", "BeachSand", "SAND", new Rgb24(196, 180, 148), 16, 3),
                    ("grm_rock", "ROCK", "ROCK", new Rgb24(124, 120, 114), 30, 4),
                    ("grm_mud", "Dirt", "MUD", new Rgb24(82, 68, 52), 20, 3),
                };
                foreach (var (layer, templateName, groundModel, color, noise, tileMeters) in mapping)
                {
                    // Keep the proven field set of the official template, but repoint every texture
                    // slot to files generated inside this level: textures owned by other levels are
                    // not mounted when a generated level runs and render as plain white.
                    var def = templates[templateName].ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                    def["name"] = layer;
                    def["internalName"] = layer;
                    def["persistentId"] = Guid.NewGuid().ToString();
                    def["groundmodelName"] = groundModel;

                    WriteDetailTextures(zip, basePath, layer, color, noise);

                    foreach (var key in def.Keys.Where(IsTextureSlot).ToList())
                    {
                        def[key] = ResolveTextureSlot(key, layer, satellitePath, TerrainFile);
                    }
                    def["baseColorBaseTexSize"] = baseTexSize;
                    def["aoBaseTexSize"] = baseTexSize;
                    def["normalBaseTexSize"] = baseTexSize;
                    def["roughnessBaseTexSize"] = baseTexSize;
                    def["heightBaseTexSize"] = baseTexSize;
                    def["baseColorDetailTexSize"] = tileMeters;
                    def["normalDetailTexSize"] = tileMeters;
                    def["roughnessDetailTexSize"] = tileMeters;
                    def["heightDetailTexSize"] = tileMeters;
                    def["aoDetailTexSize"] = tileMeters;
                    def["diffuseSize"] = worldSize;
                    terrainMaterials[layer] = def;
                }
                // Shared neutral base slots (AO white, flat normal, mid roughness)
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_ao.png", 64, new Rgb24(255, 255, 255));
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_nm.png", 64, new Rgb24(128, 128, 255));
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_r.png", 64, new Rgb24(180, 180, 180));
            }
            else
            {
                terrainMaterials["DefaultMaterial"] = new Dictionary<string, object>
                {
                    ["class"] = "TerrainMaterial",
                    ["internalName"] = "DefaultMaterial",
                    ["diffuseMap"] = $"levels/{levelName}/art/terrains/terrain.png",
                    ["diffuseSize"] = worldSize,
                    ["groundmodelName"] = "GROUNDMODEL_ASPHALT1",
                };
            }
            WriteJson(zip, $"{basePath}/art/terrains/main.materials.json", terrainMaterials);

            WriteNdJson(zip, $"{basePath}/main/items.level.json",
                Item("SimGroup", "MissionGroup", null));

            var missionGroupItems = new List<Dictionary<string, object>>
            {
                Item("SimGroup", "PlayerDropPoints", "MissionGroup"),
                Item("SimGroup", "Level_objects", "MissionGroup"),
                Item("SimGroup", "Water", "MissionGroup"),
            };
            if (decalRoads.Count > 0)
            {
                missionGroupItems.Add(Item("SimGroup", "Decal_Roads", "MissionGroup"));
            }
            if (buildingMeshes != null && buildingMeshes.Count > 0)
            {
                missionGroupItems.Add(Item("SimGroup", "Buildings", "MissionGroup"));
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/items.level.json", missionGroupItems.ToArray());

            var levelInfo = Item("LevelInfo", "theLevelInfo", "Level_objects");
            levelInfo["canvasClearColor"] = new[] { 0, 0, 0, 1 };
            levelInfo["fogAtmosphereHeight"] = 1000;
            levelInfo["fogDensity"] = 0.0001;
            levelInfo["fogDensityOffset"] = 0;
            levelInfo["globalEnviromentMap"] = "BNG_Sky_02_cubemap";
            levelInfo["gravity"] = -9.81;
            levelInfo["nearClip"] = 0.1;
            levelInfo["visibleDistance"] = 4000;

            var tod = Item("TimeOfDay", "tod", "Level_objects");
            tod["startTime"] = 0.15;

            var sky = Item("ScatterSky", "sunsky", "Level_objects");
            sky["ambientScaleGradientFile"] = "art/sky_gradients/default/gradient_ambient.png";
            sky["colorizeGradientFile"] = "art/sky_gradients/default/gradient_colorize.png";
            sky["enableFogFallBack"] = false;
            sky["fogScaleGradientFile"] = "art/sky_gradients/default/gradient_fog.png";
            sky["shadowDistance"] = 1500;
            sky["skyBrightness"] = 40;
            sky["sunScaleGradientFile"] = "art/sky_gradients/default/gradient_sunscale.png";
            sky["texSize"] = 2048;

            var levelObjects = new List<Dictionary<string, object>> { levelInfo, tod, sky, Item("SimGroup", "Other", "Level_objects") };
            if (forestByType.Count > 0)
            {
                levelObjects.Add(Item("SimGroup", "vegetation", "Level_objects"));
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/Level_objects/items.level.json", levelObjects.ToArray());

            var terrainBlock = Item("TerrainBlock", "theTerrain", "Other");
            terrainBlock["position"] = new object[] { -half, -half, 0 };
            terrainBlock["squareSize"] = cellSize;
            terrainBlock["maxHeight"] = range;
            terrainBlock["baseTexSize"] = size;
            terrainBlock["terrainFile"] = $"/levels/{levelName}/theTerrain.ter";
            terrainBlock["materialTextureSet"] = textureSetName;
            terrainBlock["minimapImage"] = "";

            var otherItems = new List<Dictionary<string, object>> { terrainBlock };
            var buildingCount = 0;
            var buildingItems = new List<Dictionary<string, object>>();
            if (buildingMeshes != null && buildingMeshes.Count > 0)
            {
                // Real swissBUILDINGS3D meshes (roof shapes included), merged per zone so the
                // editor stays usable: one object per BuildingZoneSize square
                var zones = new Dictionary<(int, int), List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>>();
                foreach (var mesh in buildingMeshes)
                {
                    if (mesh.Triangles.Count == 0)
                    {
                        continue;
                    }
                    var first = mesh.Triangles[0].A;
                    var key = ((int)MathF.Floor(first.X / BuildingZoneSize), (int)MathF.Floor(first.Y / BuildingZoneSize));
                    if (!zones.TryGetValue(key, out var list))
                    {
                        zones.Add(key, list = new List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>());
                    }
                    list.Add(mesh);
                }
                var zoneIndex = 0;
                foreach (var zone in zones.OrderBy(z => z.Key.Item2).ThenBy(z => z.Key.Item1).Take(MaxBuildingZones))
                {
                    zoneIndex++;
                    var triangles = zone.Value.SelectMany(m => m.Triangles).ToList();
                    // Geometry relative to the zone centre so the engine can cull it properly
                    var cx = (zone.Key.Item1 + 0.5f) * BuildingZoneSize;
                    var cy = (zone.Key.Item2 + 0.5f) * BuildingZoneSize;
                    var cz = triangles.Min(t => MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)));
                    var shapeFile = $"art/shapes/buildings/zone_{zoneIndex:0000}.dae";
                    WriteText(zip, $"{basePath}/{shapeFile}", BuildBuildingsColladaFromTriangles(triangles, cx, cy, cz));

                    var item = Item("TSStatic", $"buildings_zone_{zoneIndex:0000}", "Buildings");
                    item["position"] = new object[] { MathF.Round(cx - half, 3), MathF.Round(cy - half, 3), MathF.Round(cz - floor, 3) };
                    item["shapeName"] = $"levels/{levelName}/{shapeFile}";
                    item["collisionType"] = "Visible Mesh";
                    item["decalType"] = "Visible Mesh";
                    item["prebuildCollisionData"] = 0;
                    item["useInstanceRenderData"] = true;
                    buildingItems.Add(item);
                }
                buildingCount = buildingMeshes.Count;
                scope.WriteLine($"Buildings: {buildingCount} swissBUILDINGS3D buildings in {buildingItems.Count} zone objects ({BuildingZoneSize} m zones)");
            }
            else if (buildings != null && buildings.Count > 0)
            {
                var kept = buildings;
                if (kept.Count > 12000)
                {
                    var stride = (double)kept.Count / 12000;
                    var thinned = new List<BeamNGBuildingBox>(12000);
                    for (double i = 0; i < kept.Count && thinned.Count < 12000; i += stride)
                    {
                        thinned.Add(kept[(int)i]);
                    }
                    kept = thinned;
                }
                buildingCount = kept.Count;
                WriteText(zip, $"{basePath}/art/shapes/buildings.dae", BuildBuildingsCollada(kept, floor, half));
                var buildingsStatic = Item("TSStatic", "buildings", "Other");
                buildingsStatic["position"] = new object[] { 0, 0, 0 };
                buildingsStatic["shapeName"] = $"levels/{levelName}/art/shapes/buildings.dae";
                buildingsStatic["collisionType"] = "Visible Mesh";
                buildingsStatic["decalType"] = "Visible Mesh";
                buildingsStatic["prebuildCollisionData"] = 0;
                buildingsStatic["useInstanceRenderData"] = true;
                otherItems.Add(buildingsStatic);
                scope.WriteLine($"Buildings: {buildingCount} collision boxes");
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/Level_objects/Other/items.level.json", otherItems.ToArray());

            if (buildingItems.Count > 0)
            {
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Buildings/items.level.json", buildingItems.ToArray());
            }

            if (decalRoads.Count > 0)
            {
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Decal_Roads/items.level.json", decalRoads.ToArray());
                WriteRoadMaterials(zip, basePath);
                scope.WriteLine($"Roads: {decalRoads.Count} DecalRoad segments (self-contained materials)");
            }

            if (forestByType.Count > 0 || (buildings != null && buildings.Count > 0) || (buildingMeshes != null && buildingMeshes.Count > 0))
            {
                // Materials of the official tree/rock shapes: they are level-scoped in the game files,
                // so they must be re-declared inside this level or the shapes render orange.
                // The buildings material is appended to the same file.
                var shapeMaterials = JsonSerializer.Deserialize<Dictionary<string, object>>(LoadShapeMaterialsJson())!;
                shapeMaterials["grm_building"] = new Dictionary<string, object>
                {
                    ["name"] = "grm_building",
                    ["mapTo"] = "grm_building",
                    ["class"] = "Material",
                    ["persistentId"] = Guid.NewGuid().ToString(),
                    ["Stages"] = new object[]
                    {
                        new Dictionary<string, object> { ["diffuseColor"] = new[] { 0.62, 0.6, 0.56, 1 }, ["specularPower"] = 1 },
                        new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                    },
                    ["materialTag0"] = "beamng",
                };
                WriteJson(zip, $"{basePath}/art/shapes/main.materials.json", shapeMaterials);
            }

            if (forestByType.Count > 0)
            {
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Level_objects/vegetation/items.level.json",
                    ForestObject());
                WriteJson(zip, $"{basePath}/art/forest/managedItemData.json", ManagedForestItemData(forestByType.Keys));
                foreach (var (type, instances) in forestByType)
                {
                    var sb = new StringBuilder();
                    foreach (var instance in instances)
                    {
                        sb.Append(SerializeForestInstance(instance, type, floor, half));
                        sb.Append('\n');
                    }
                    WriteText(zip, $"{basePath}/forest/{type}.forest4.json", sb.ToString());
                    scope.WriteLine($"Forest: {instances.Count} x {type}");
                }
            }

            // Sea plane when the map contains ocean (altitude 0 above the lowest point),
            // plus one WaterBlock per lake pond tile of the Arma map
            var waterItems = new List<Dictionary<string, object>>();
            if (floor < -0.5f)
            {
                waterItems.Add(SeaWaterPlane(-floor));
            }
            if (ponds != null && ponds.Count > 0)
            {
                var pondIndex = 0;
                foreach (var pond in ponds.Take(2000))
                {
                    waterItems.Add(PondWaterBlock(pond, floor, half, ++pondIndex));
                }
                scope.WriteLine($"Water: {Math.Min(ponds.Count, 2000)} lake WaterBlocks");
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/Water/items.level.json", waterItems.ToArray());

            var center = grid.Size / 2;
            var spawnZ = Height(center, center) - floor + 3f;
            var spawn = Item("SpawnSphere", "spawn_default", "PlayerDropPoints");
            spawn["dataBlock"] = "SpawnSphereMarker";
            spawn["position"] = new object[] { 0, 0, spawnZ };
            spawn["rotationMatrix"] = new[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            spawn["radius"] = 5;
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/PlayerDropPoints/items.level.json", spawn);

            WriteText(zip, $"{basePath}/export_report.txt", FormattableString.Invariant(
$@"BeamNG.drive level generated by GameRealisticMap
================================================
Level name:     {levelName}
Terrain:        {grid.Size} x {grid.Size} @ {cellSize} m ({worldSize / 1000:0.##} km x {worldSize / 1000:0.##} km)
Altitude range: {floor} m .. {floor + range} m (TerrainBlock maxHeight = {range})
Sea level:      {(floor < -0.5f ? FormattableString.Invariant($"z = {-floor} (WaterPlane included)") : "no ocean in this map")}
Surface layers: {(useLayers ? string.Join(", ", materialNames) : "single material (no id map available)")}
Roads:          {decalRoads.Count} DecalRoad segments (plain asphalt/dirt base, grass edge blend, dashed center line on wide roads)
Forest:         {forestByType.Sum(kv => kv.Value.Count)} instances ({string.Join(", ", forestByType.Select(kv => $"{kv.Value.Count} {kv.Key}"))})
Lakes:          {Math.Min(ponds?.Count ?? 0, 2000)} WaterBlocks
Buildings:      {buildingCount} objects{(buildingMeshes != null && buildingMeshes.Count > 0 ? " (individual swissBUILDINGS3D shapes, editable one by one in MissionGroup/Buildings)" : " (merged OSM footprint boxes)")}

Install: copy this zip into Documents\BeamNG.drive\<version>\mods\
The level then appears in Freeroam as '{levelTitle}'.
"));
        }

        // ── Forest ─────────────────────────────────────────────────────────────

        private static readonly Dictionary<BeamNGForestKind, string> ForestTypeNames = new()
        {
            [BeamNGForestKind.Tree] = "tree_aspen_small_a",
            [BeamNGForestKind.Bush] = "tree_beech_bush_a",
            [BeamNGForestKind.Rock] = "eca_rock_small",
        };

        private Dictionary<string, List<BeamNGForestInstance>> BuildForestPlacements(float floor, float half, IProgressScope scope)
        {
            var result = new Dictionary<string, List<BeamNGForestInstance>>();
            if (vegetation == null || vegetation.Count == 0)
            {
                return result;
            }
            foreach (var group in vegetation.GroupBy(v => v.Kind))
            {
                var list = group.ToList();
                if (list.Count > MaxForestInstancesPerType)
                {
                    // Deterministic thinning to stay within engine-friendly instance counts
                    var stride = (double)list.Count / MaxForestInstancesPerType;
                    var thinned = new List<BeamNGForestInstance>(MaxForestInstancesPerType);
                    for (double i = 0; i < list.Count && thinned.Count < MaxForestInstancesPerType; i += stride)
                    {
                        thinned.Add(list[(int)i]);
                    }
                    scope.WriteLine($"Forest {group.Key}: {list.Count} instances reduced to {thinned.Count}");
                    list = thinned;
                }
                result[ForestTypeNames[group.Key]] = list;
            }
            return result;
        }

        private string SerializeForestInstance(BeamNGForestInstance instance, string type, float floor, float half)
        {
            var z = ElevationAt(instance.X, instance.Y) - floor;
            var c = MathF.Cos(instance.YawRad);
            var s = MathF.Sin(instance.YawRad);
            var scale = Math.Clamp(instance.Scale, 0.4f, 2.5f);
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ctxid"] = 0,
                ["pos"] = new object[] { MathF.Round(instance.X - half, 3), MathF.Round(instance.Y - half, 3), MathF.Round(z, 3) },
                ["rotationMatrix"] = new object[] { MathF.Round(c, 6), MathF.Round(s, 6), 0, MathF.Round(-s, 6), MathF.Round(c, 6), 0, 0, 0, 1 },
                ["scale"] = MathF.Round(scale, 6),
                ["type"] = type,
            });
        }

        private static Dictionary<string, object> ManagedForestItemData(IEnumerable<string> usedTypes)
        {
            var all = new Dictionary<string, object>
            {
                ["tree_aspen_small_a"] = new Dictionary<string, object>
                {
                    ["name"] = "tree_aspen_small_a",
                    ["internalName"] = "tree_aspen_small_a",
                    ["class"] = "TSForestItemData",
                    ["branchAmp"] = 0.03,
                    ["detailAmp"] = 0.2,
                    ["detailFreq"] = 4,
                    ["mass"] = 20,
                    ["rigidity"] = 17,
                    ["shapeFile"] = "levels/east_coast_usa/art/shapes/trees/trees_aspen/tree_aspen_small_a.dae",
                    ["trunkBendScale"] = 0.1,
                    ["windScale"] = 0.4,
                },
                ["tree_beech_bush_a"] = new Dictionary<string, object>
                {
                    ["name"] = "tree_beech_bush_a",
                    ["internalName"] = "tree_beech_bush_a",
                    ["class"] = "TSForestItemData",
                    ["branchAmp"] = 0.02,
                    ["dampingCoefficient"] = 0.4,
                    ["detailAmp"] = 0.3,
                    ["detailFreq"] = 4,
                    ["mass"] = 1,
                    ["radius"] = 0.5,
                    ["rigidity"] = 11,
                    ["shapeFile"] = "levels/east_coast_usa/art/shapes/trees/trees_beech/tree_beech_bush_a.dae",
                    ["tightnessCoefficient"] = 4,
                    ["trunkBendScale"] = 0.05,
                    ["windScale"] = 0.4,
                },
                ["eca_rock_small"] = new Dictionary<string, object>
                {
                    ["name"] = "eca_rock_small",
                    ["internalName"] = "eca_rock_small",
                    ["class"] = "TSForestItemData",
                    ["annotation"] = "ROCK",
                    ["radius"] = 0.1,
                    ["shapeFile"] = "/levels/east_coast_usa/art/shapes/rocks/eca_rock_small.dae",
                },
            };
            return all.Where(kv => usedTypes.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        /// <summary>
        /// One Collada mesh with a rotated box per building footprint: visible grey mass and
        /// collision at the same time (TSStatic collisionType "Visible Mesh").
        /// </summary>
        private string BuildBuildingsCollada(List<BeamNGBuildingBox> boxes, float floor, float half)
        {
            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var indices = new StringBuilder();
            var vertexCount = 0;
            var normalCount = 0;
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            void AddFace((float x, float y, float z)[] corners, (float x, float y, float z) normal)
            {
                foreach (var (x, y, z) in corners)
                {
                    positions.Append(x.ToString("0.###", culture)).Append(' ')
                             .Append(y.ToString("0.###", culture)).Append(' ')
                             .Append(z.ToString("0.###", culture)).Append(' ');
                }
                normals.Append(normal.x.ToString("0.###", culture)).Append(' ')
                       .Append(normal.y.ToString("0.###", culture)).Append(' ')
                       .Append(normal.z.ToString("0.###", culture)).Append(' ');
                // Two triangles per quad, each vertex paired with the face normal
                int v0 = vertexCount, n0 = normalCount;
                indices.Append($"{v0} {n0} {v0 + 1} {n0} {v0 + 2} {n0} {v0} {n0} {v0 + 2} {n0} {v0 + 3} {n0} ");
                vertexCount += 4;
                normalCount += 1;
            }

            foreach (var box in boxes)
            {
                var cx = box.X - half;
                var cy = box.Y - half;
                var groundZ = ElevationAt(box.X, box.Y) - floor;
                var z0 = groundZ - 0.5f; // sink slightly into the terrain
                var z1 = groundZ + Math.Clamp(box.Height, 2.5f, 40f);
                var hw = Math.Clamp(box.Width, 1.5f, 100f) / 2f;
                var hd = Math.Clamp(box.Depth, 1.5f, 100f) / 2f;
                var c = MathF.Cos(box.YawRad);
                var s = MathF.Sin(box.YawRad);

                (float x, float y, float z) P(float lx, float ly, float z) => (cx + lx * c - ly * s, cy + lx * s + ly * c, z);
                (float x, float y, float z) N(float lx, float ly) => (lx * c - ly * s, lx * s + ly * c, 0);

                // Bottom corners a,b,c,d counter-clockwise
                var a0 = P(-hw, -hd, z0); var b0 = P(hw, -hd, z0); var c0 = P(hw, hd, z0); var d0 = P(-hw, hd, z0);
                var a1 = P(-hw, -hd, z1); var b1 = P(hw, -hd, z1); var c1 = P(hw, hd, z1); var d1 = P(-hw, hd, z1);

                AddFace(new[] { a1, b1, c1, d1 }, (0, 0, 1)); // roof
                AddFace(new[] { a0, b0, b1, a1 }, N(0, -1)); // south wall
                AddFace(new[] { b0, c0, c1, b1 }, N(1, 0)); // east wall
                AddFace(new[] { c0, d0, d1, c1 }, N(0, 1)); // north wall
                AddFace(new[] { d0, a0, a1, d1 }, N(-1, 0)); // west wall
            }

            return ColladaDocument(positions.ToString(), normals.ToString(), indices.ToString(), vertexCount, normalCount, vertexCount / 4 * 2);
        }

        /// <summary>
        /// swissBUILDINGS3D buildings of one zone as a single Collada shape. Vertices are relative
        /// to the zone centre so the TSStatic can be culled, moved or deleted per area.
        /// </summary>
        private string BuildBuildingsColladaFromTriangles(List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle> meshTriangles, float cx, float cy, float cz)
        {
            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var indices = new StringBuilder();
            var vertexCount = 0;
            var normalCount = 0;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var triangle in meshTriangles)
            {
                var a = new System.Numerics.Vector3(triangle.A.X - cx, triangle.A.Y - cy, triangle.A.Z - cz);
                var b = new System.Numerics.Vector3(triangle.B.X - cx, triangle.B.Y - cy, triangle.B.Z - cz);
                var c = new System.Numerics.Vector3(triangle.C.X - cx, triangle.C.Y - cy, triangle.C.Z - cz);
                var normal = System.Numerics.Vector3.Cross(b - a, c - a);
                var length = normal.Length();
                if (length < 0.0001f)
                {
                    continue;
                }
                normal /= length;
                foreach (var vertex in new[] { a, b, c })
                {
                    positions.Append(vertex.X.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Y.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Z.ToString("0.###", culture)).Append(' ');
                }
                normals.Append(normal.X.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Y.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Z.ToString("0.###", culture)).Append(' ');
                indices.Append($"{vertexCount} {normalCount} {vertexCount + 1} {normalCount} {vertexCount + 2} {normalCount} ");
                vertexCount += 3;
                normalCount += 1;
            }
            return ColladaDocument(positions.ToString(), normals.ToString(), indices.ToString(), vertexCount, normalCount, vertexCount / 3);
        }

        private static string ColladaDocument(string positions, string normals, string indices, int vertexCount, int normalCount, int triangleCount)
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<COLLADA xmlns=""http://www.collada.org/2005/11/COLLADASchema"" version=""1.4.1"">
 <asset><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name=""meter"" meter=""1""/><up_axis>Z_UP</up_axis></asset>
 <library_effects>
  <effect id=""grm_building-effect""><profile_COMMON><technique sid=""common""><lambert><diffuse><color>0.62 0.6 0.56 1</color></diffuse></lambert></technique></profile_COMMON></effect>
 </library_effects>
 <library_materials>
  <material id=""grm_building-material"" name=""grm_building""><instance_effect url=""#grm_building-effect""/></material>
 </library_materials>
 <library_geometries>
  <geometry id=""buildings-mesh"" name=""buildings"">
   <mesh>
    <source id=""buildings-pos"">
     <float_array id=""buildings-pos-array"" count=""{vertexCount * 3}"">{positions}</float_array>
     <technique_common><accessor source=""#buildings-pos-array"" count=""{vertexCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <source id=""buildings-nrm"">
     <float_array id=""buildings-nrm-array"" count=""{normalCount * 3}"">{normals}</float_array>
     <technique_common><accessor source=""#buildings-nrm-array"" count=""{normalCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <vertices id=""buildings-verts""><input semantic=""POSITION"" source=""#buildings-pos""/></vertices>
    <triangles material=""grm_building"" count=""{triangleCount}"">
     <input semantic=""VERTEX"" source=""#buildings-verts"" offset=""0""/>
     <input semantic=""NORMAL"" source=""#buildings-nrm"" offset=""1""/>
     <p>{indices}</p>
    </triangles>
   </mesh>
  </geometry>
 </library_geometries>
 <library_visual_scenes>
  <visual_scene id=""Scene"" name=""Scene"">
   <node id=""buildings"" name=""buildings"" type=""NODE"">
    <instance_geometry url=""#buildings-mesh"">
     <bind_material><technique_common><instance_material symbol=""grm_building"" target=""#grm_building-material""/></technique_common></bind_material>
    </instance_geometry>
   </node>
  </visual_scene>
 </library_visual_scenes>
 <scene><instance_visual_scene url=""#Scene""/></scene>
</COLLADA>";
        }

        private static string LoadShapeMaterialsJson() => LoadResource("BeamNGShapeMaterials.json");

        private static string LoadTerrainMaterialsJson() => LoadResource("BeamNGTerrainMaterials.json");

        private static string LoadResource(string name)
        {
            using var stream = typeof(BeamNGLevelWriter).Assembly.GetManifestResourceStream(
                "GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.BeamNG." + name)
                ?? throw new ApplicationException($"{name} resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static bool IsTextureSlot(string key)
        {
            return key.EndsWith("Tex", StringComparison.Ordinal)
                || key == "macroMap" || key == "normalMap" || key == "detailMap" || key == "diffuseMap";
        }

        /// <summary>
        /// Point a texture slot of an official template to the equivalent file generated in this level.
        /// </summary>
        private static string ResolveTextureSlot(string key, string layer, string satellitePath, Func<string, string> terrainFile)
        {
            if (key == "baseColorBaseTex" || key == "diffuseMap")
            {
                return satellitePath; // far view keeps the real satellite imagery
            }
            if (key.StartsWith("baseColor", StringComparison.Ordinal) || key == "macroMap" || key == "detailMap")
            {
                return terrainFile($"{layer}_d.png");
            }
            if (key.StartsWith("normal", StringComparison.Ordinal))
            {
                return key == "normalBaseTex" ? terrainFile("shared_nm.png") : terrainFile($"{layer}_n.png");
            }
            if (key.StartsWith("roughness", StringComparison.Ordinal))
            {
                return key == "roughnessBaseTex" ? terrainFile("shared_r.png") : terrainFile($"{layer}_r.png");
            }
            if (key.StartsWith("height", StringComparison.Ordinal))
            {
                return key == "heightBaseTex" ? terrainFile("shared_r.png") : terrainFile($"{layer}_h.png");
            }
            // ao*
            return key == "aoBaseTex" ? terrainFile("shared_ao.png") : terrainFile("shared_ao.png");
        }

        /// <summary>
        /// Generate the tileable colour / normal / roughness / height detail textures of one surface.
        /// </summary>
        private static void WriteDetailTextures(ZipArchive zip, string basePath, string layer, Rgb24 color, int noiseAmount)
        {
            const int size = 256;
            var height = TileableNoise(size, layer.GetHashCode());

            using (var colorImage = new Image<Rgb24>(size, size))
            using (var roughImage = new Image<L8>(size, size))
            using (var heightImage = new Image<L8>(size, size))
            {
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var n = height[x, y];
                        var delta = (int)((n - 0.5f) * 2f * noiseAmount);
                        colorImage[x, y] = new Rgb24(
                            (byte)Math.Clamp(color.R + delta, 0, 255),
                            (byte)Math.Clamp(color.G + delta, 0, 255),
                            (byte)Math.Clamp(color.B + delta, 0, 255));
                        roughImage[x, y] = new L8((byte)Math.Clamp(150 + (n - 0.5f) * 90f, 0f, 255f));
                        heightImage[x, y] = new L8((byte)Math.Clamp(n * 255f, 0f, 255f));
                    }
                }
                SavePng(zip, $"{basePath}/art/terrains/{layer}_d.png", colorImage);
                SavePng(zip, $"{basePath}/art/terrains/{layer}_r.png", roughImage);
                SavePng(zip, $"{basePath}/art/terrains/{layer}_h.png", heightImage);
            }

            // Normal map from the height gradient (wrapping, so the tile stays seamless)
            using (var normalImage = new Image<Rgb24>(size, size))
            {
                const float strength = 3.5f;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var left = height[(x - 1 + size) % size, y];
                        var right = height[(x + 1) % size, y];
                        var up = height[x, (y - 1 + size) % size];
                        var down = height[x, (y + 1) % size];
                        var nx = (left - right) * strength;
                        var ny = (up - down) * strength;
                        var normal = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(nx, ny, 1f));
                        normalImage[x, y] = new Rgb24(
                            (byte)((normal.X * 0.5f + 0.5f) * 255f),
                            (byte)((normal.Y * 0.5f + 0.5f) * 255f),
                            (byte)((normal.Z * 0.5f + 0.5f) * 255f));
                    }
                }
                SavePng(zip, $"{basePath}/art/terrains/{layer}_n.png", normalImage);
            }
        }

        /// <summary>
        /// Seamless value noise (two octaves, wrap-around interpolation).
        /// </summary>
        private static float[,] TileableNoise(int size, int seed)
        {
            var result = new float[size, size];
            foreach (var (cells, weight) in new[] { (8, 0.6f), (32, 0.3f), (64, 0.1f) })
            {
                var random = new Random(seed ^ cells);
                var grid = new float[cells, cells];
                for (var gy = 0; gy < cells; gy++)
                {
                    for (var gx = 0; gx < cells; gx++)
                    {
                        grid[gx, gy] = (float)random.NextDouble();
                    }
                }
                var scale = (float)cells / size;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var fx = x * scale;
                        var fy = y * scale;
                        var x0 = (int)fx;
                        var y0 = (int)fy;
                        var tx = fx - x0;
                        var ty = fy - y0;
                        // Smoothstep for a softer, less blocky result
                        tx = tx * tx * (3f - 2f * tx);
                        ty = ty * ty * (3f - 2f * ty);
                        var x1 = (x0 + 1) % cells;
                        var y1 = (y0 + 1) % cells;
                        var top = grid[x0, y0] + (grid[x1, y0] - grid[x0, y0]) * tx;
                        var bottom = grid[x0, y1] + (grid[x1, y1] - grid[x0, y1]) * tx;
                        result[x, y] += (top + (bottom - top) * ty) * weight;
                    }
                }
            }
            return result;
        }

        private static void SavePng<TPixel>(ZipArchive zip, string entryName, Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        private static void WriteUniformPng(ZipArchive zip, string entryName, int size, Rgb24 color)
        {
            using var image = new Image<Rgb24>(size, size, color);
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        private Dictionary<string, object> ForestObject()
        {
            var forest = Item("Forest", "theForest", "vegetation");
            forest["lodReflectScalar"] = 0;
            return forest;
        }

        // ── Roads ──────────────────────────────────────────────────────────────

        private List<Dictionary<string, object>> BuildDecalRoads(float floor, float half)
        {
            var result = new List<Dictionary<string, object>>();
            if (roads == null)
            {
                return result;
            }
            var roadIndex = 0;
            foreach (var road in roads)
            {
                var points = road.Points;
                if (points == null || points.Count < 2)
                {
                    continue;
                }
                var isDirt = road.IsDirt;
                // Real widths feel cramped behind the wheel: widen a bit and enforce a drivable
                // minimum. Pedestrian ways are dropped by the callers.
                var width = Math.Max(isDirt ? 5f : 6.5f, road.Width * RoadWidthFactor);
                var halfWidth = width / 2f;

                // Decimate then chunk so BeamNG splines stay smooth and objects stay small
                var nodes = new List<float[]>();
                TerrainPoint? last = null;
                for (var i = 0; i < points.Count; i++)
                {
                    var pt = points[i];
                    if (last != null && i < points.Count - 1)
                    {
                        var dx = pt.X - last.X;
                        var dy = pt.Y - last.Y;
                        if (Math.Sqrt(dx * dx + dy * dy) < MinRoadNodeSpacing)
                        {
                            continue;
                        }
                    }
                    last = pt;
                    var z = ElevationAt(pt.X, pt.Y) - floor + 0.1f;
                    nodes.Add(new[] { pt.X - half, pt.Y - half, z });
                    if (nodes.Count == MaxDecalRoadNodes && i < points.Count - 1)
                    {
                        EmitRoadDecals(result, nodes, halfWidth, isDirt, ref roadIndex);
                        nodes = new List<float[]> { nodes[^1] };
                    }
                }
                if (nodes.Count >= 2)
                {
                    EmitRoadDecals(result, nodes, halfWidth, isDirt, ref roadIndex);
                }
            }
            return result;
        }

        /// <summary>
        /// One road chunk. Uses only BeamNG global decalroad materials (art_shapes.zip), which resolve
        /// in every level. Base = plain asphalt/dirt (no baked markings, so narrow roads stay grey),
        /// soft grass edge blend, thin dashed center line on wide asphalt roads only.
        /// </summary>
        private void EmitRoadDecals(List<Dictionary<string, object>> result, List<float[]> centerNodes, float halfWidth, bool isDirt, ref int roadIndex)
        {
            roadIndex++;
            // Self-contained materials shipped in this level (see WriteRoadMaterials): materials of
            // other official levels resolve their textures from those levels' packages, which are
            // not mounted here, and would render plain white.
            result.Add(MakeDecalRoad(ToNodes(centerNodes, 0f, halfWidth), isDirt ? "grm_road_dirt" : "grm_road_asphalt",
                $"road_{roadIndex}", 10, isDirt ? 10 : 12));

            // Center dashed white line on wide asphalt roads (2-lane)
            if (!isDirt && halfWidth >= 3f)
            {
                result.Add(MakeDecalRoad(ToNodes(centerNodes, 0f, 0.16f), "grm_line_white",
                    $"road_{roadIndex}_center", 15, 8));
            }
        }

        /// <summary>
        /// Road textures and materials generated into the level itself, so roads never depend on
        /// assets owned by another level.
        /// </summary>
        private void WriteRoadMaterials(ZipArchive zip, string basePath)
        {
            WriteNoisePng(zip, $"{basePath}/art/roads/asphalt.png", 512, new Rgba32(58, 58, 62, 255), 14);
            WriteNoisePng(zip, $"{basePath}/art/roads/dirt.png", 512, new Rgba32(122, 101, 78, 255), 22);
            WriteDashedLinePng(zip, $"{basePath}/art/roads/line_white.png", 64, 512);

            var materials = new Dictionary<string, object>
            {
                ["grm_road_asphalt"] = RoadMaterial("grm_road_asphalt", $"levels/{levelName}/art/roads/asphalt.png", "ASPHALT", false),
                ["grm_road_dirt"] = RoadMaterial("grm_road_dirt", $"levels/{levelName}/art/roads/dirt.png", "DIRT", false),
                ["grm_line_white"] = RoadMaterial("grm_line_white", $"levels/{levelName}/art/roads/line_white.png", "SOLID_LINE", true),
            };
            WriteJson(zip, $"{basePath}/art/roads/main.materials.json", materials);
        }

        private static Dictionary<string, object> RoadMaterial(string name, string texture, string annotation, bool transparent)
        {
            var stage = new Dictionary<string, object>
            {
                ["colorMap"] = texture,
                ["specularPower"] = 1,
                ["useAnisotropic"] = true,
            };
            var material = new Dictionary<string, object>
            {
                ["name"] = name,
                ["mapTo"] = name,
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["Stages"] = new object[] { stage, new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>() },
                ["annotation"] = annotation,
                ["materialTag0"] = "beamng",
                ["materialTag1"] = "Road",
            };
            if (transparent)
            {
                material["translucent"] = true;
                material["translucentBlendOp"] = "LerpAlpha";
                material["alphaTest"] = false;
                material["alphaRef"] = 0;
            }
            return material;
        }

        private static void WriteNoisePng(ZipArchive zip, string entryName, int size, Rgba32 baseColor, int noise)
        {
            var random = new Random(entryName.GetHashCode());
            using var image = new Image<Rgba32>(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var delta = random.Next(-noise, noise + 1);
                    image[x, y] = new Rgba32(
                        (byte)Math.Clamp(baseColor.R + delta, 0, 255),
                        (byte)Math.Clamp(baseColor.G + delta, 0, 255),
                        (byte)Math.Clamp(baseColor.B + delta, 0, 255),
                        255);
                }
            }
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        /// <summary>
        /// Transparent strip with a white dash in the middle, tiled along the road by textureLength.
        /// </summary>
        private static void WriteDashedLinePng(ZipArchive zip, string entryName, int width, int height)
        {
            using var image = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 0));
            var dashStart = height / 4;
            var dashEnd = height * 3 / 4;
            for (var y = dashStart; y < dashEnd; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    image[x, y] = new Rgba32(245, 245, 245, 255);
                }
            }
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        /// <summary>
        /// Convert center nodes into DecalRoad nodes [x, y, z, halfWidth], laterally offset by
        /// <paramref name="offset"/> meters (perpendicular to the local road direction).
        /// </summary>
        private static List<object[]> ToNodes(List<float[]> centerNodes, float offset, float halfWidth)
        {
            var nodes = new List<object[]>(centerNodes.Count);
            for (var i = 0; i < centerNodes.Count; i++)
            {
                var current = centerNodes[i];
                var x = current[0];
                var y = current[1];
                if (offset != 0f)
                {
                    var previous = centerNodes[Math.Max(0, i - 1)];
                    var next = centerNodes[Math.Min(centerNodes.Count - 1, i + 1)];
                    var dx = next[0] - previous[0];
                    var dy = next[1] - previous[1];
                    var length = MathF.Sqrt(dx * dx + dy * dy);
                    if (length > 0.001f)
                    {
                        x += -dy / length * offset;
                        y += dx / length * offset;
                    }
                }
                nodes.Add(new object[]
                {
                    MathF.Round(x, 3),
                    MathF.Round(y, 3),
                    MathF.Round(current[2], 3),
                    halfWidth
                });
            }
            return nodes;
        }

        private static Dictionary<string, object> MakeDecalRoad(List<object[]> nodes, string material, string name, int renderPriority, double textureLength)
        {
            var decal = Item("DecalRoad", name, "Decal_Roads");
            decal["position"] = new object[] { nodes[0][0], nodes[0][1], nodes[0][2] };
            decal["improvedSpline"] = true;
            decal["material"] = material;
            decal["nodes"] = nodes;
            decal["breakAngle"] = 1.0;
            decal["renderPriority"] = renderPriority;
            decal["textureLength"] = textureLength;
            decal["startEndFade"] = new[] { 1, 1 };
            decal["detail"] = 0.1;
            return decal;
        }

        // ── Surface layer map ──────────────────────────────────────────────────

        /// <summary>
        /// Rasterize the road network into the layer map (asphalt or gravel at real width),
        /// so that roads get road physics whatever the ground below is painted with.
        /// </summary>
        private void BurnRoadsIntoLayerMap(byte[] layerMap, int size)
        {
            if (roads == null || roads.Count == 0)
            {
                return;
            }
            // Pixel value = layer index + 1, 0 = untouched. Same axes as the layer map
            // (x east, row y south to north) — only read back by the loop below.
            using var image = new Image<L8>(size, size);
            var options = new DrawingOptions
            {
                GraphicsOptions = new GraphicsOptions { Antialias = false }
            };
            image.Mutate(ctx =>
            {
                foreach (var road in roads)
                {
                    var points = road.Points;
                    if (points == null || points.Count < 2)
                    {
                        continue;
                    }
                    var layer = (byte)(road.IsDirt ? 3 : 1); // gravel or asphalt
                    var widthPixels = Math.Max(1f, road.Width / cellSize);
                    var pen = Pens.Solid(Color.FromPixel(new L8((byte)(layer + 1))), widthPixels);
                    var line = points.Select(p => new PointF(p.X / cellSize, p.Y / cellSize)).ToArray();
                    ctx.DrawLine(options, pen, line);
                }
            });
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var value = image[x, y].PackedValue;
                    if (value > 0)
                    {
                        layerMap[y * size + x] = (byte)(value - 1);
                    }
                }
            }
        }

        private async Task<byte[]?> BuildLayerMap(int size)
        {
            if (idMap == null || materials == null)
            {
                return null;
            }
            var known = new Dictionary<Rgb24, byte>();
            foreach (var definition in materials)
            {
                known[definition.Material.Id] = ClassifyMaterial(definition.Material.ColorTexture);
            }
            using var small = await DownscaleHugeImage(idMap, size, true).ConfigureAwait(false);
            var layerMap = new byte[size * size];
            var cache = new Dictionary<Rgba32, byte>();
            for (var y = 0; y < size; y++)
            {
                var imgY = size - 1 - y; // PNG row 0 is north, layer map row 0 is south
                for (var x = 0; x < size; x++)
                {
                    var color = small[x, imgY];
                    if (!cache.TryGetValue(color, out var layer))
                    {
                        var rgb = new Rgb24(color.R, color.G, color.B);
                        if (!known.TryGetValue(rgb, out layer))
                        {
                            layer = NearestKnown(known, rgb);
                        }
                        cache[color] = layer;
                    }
                    layerMap[y * size + x] = layer;
                }
            }
            return layerMap;
        }

        private static byte NearestKnown(Dictionary<Rgb24, byte> known, Rgb24 color)
        {
            var best = (byte)0;
            var bestDistance = int.MaxValue;
            foreach (var (candidate, layer) in known)
            {
                var dr = candidate.R - color.R;
                var dg = candidate.G - color.G;
                var db = candidate.B - color.B;
                var distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = layer;
                }
            }
            return best;
        }

        private static byte ClassifyMaterial(string colorTexture)
        {
            var name = colorTexture.ToLowerInvariant();
            if (name.Contains("asphalt") || name.Contains("concrete") || name.Contains("tarmac") || name.Contains("road")) return 1;
            if (name.Contains("gravel") || name.Contains("pebble")) return 3;
            if (name.Contains("sand") || name.Contains("beach")) return 4;
            if (name.Contains("rock") || name.Contains("stone") || name.Contains("cliff")) return 5;
            if (name.Contains("mud") || name.Contains("wet")) return 6;
            if (name.Contains("forest") || name.Contains("leaf") || name.Contains("needle") || name.Contains("pine")) return 2;
            if (name.Contains("soil") || name.Contains("dirt") || name.Contains("earth") || name.Contains("dry")) return 2;
            return 0; // grass
        }

        // ── Shared primitives ──────────────────────────────────────────────────

        private static Dictionary<string, object> Item(string cls, string name, string? parent)
        {
            var item = new Dictionary<string, object>();
            if (parent != null)
            {
                item["__parent"] = parent;
            }
            item["class"] = cls;
            item["name"] = name;
            item["persistentId"] = Guid.NewGuid().ToString();
            return item;
        }

        private static Dictionary<string, object> PondWaterBlock(BeamNGPond pond, float floor, float half, int index)
        {
            var c = MathF.Cos(pond.YawRad);
            var s = MathF.Sin(pond.YawRad);
            var water = Item("WaterBlock", $"lake_{index}", "Water");
            // Position z is the water surface (top of the block)
            water["position"] = new object[] { MathF.Round(pond.X - half, 3), MathF.Round(pond.Y - half, 3), MathF.Round(pond.SurfaceZ - floor, 3) };
            water["rotationMatrix"] = new object[] { MathF.Round(c, 6), MathF.Round(s, 6), 0, MathF.Round(-s, 6), MathF.Round(c, 6), 0, 0, 0, 1 };
            water["scale"] = new object[] { MathF.Round(pond.Size + 0.5f, 3), MathF.Round(pond.Size + 0.5f, 3), 3 };
            water["cubemap"] = "cubemap_ocean_reflection";
            water["depthGradientTex"] = "/levels/italy/art/water/depthcolor_ramp_italy_muddy.png";
            water["foamTex"] = "levels/italy/art/water/foam2.dds";
            water["rippleTex"] = "/levels/italy/art/water/ripple.dds";
            water["baseColor"] = new[] { 253, 254, 254, 0 };
            water["clarity"] = 0.3;
            water["depthGradientMax"] = 10;
            water["fresnelBias"] = -0.1;
            water["fresnelPower"] = 0.8;
            water["reflectivity"] = 0.25;
            water["specularPower"] = 210;
            water["underwaterColor"] = new[] { 70, 160, 170, 253 };
            water["viscosity"] = 0.001;
            water["waterFogDensity"] = 1.2;
            water["waterFogDensityOffset"] = 0.1;
            return water;
        }

        private static Dictionary<string, object> SeaWaterPlane(float seaLevelZ)
        {
            var water = Item("WaterPlane", "ocean", "Water");
            water["position"] = new object[] { 0, 0, MathF.Round(seaLevelZ, 3) };
            water["cubemap"] = "cubemap_ocean_reflection";
            water["depthGradientTex"] = "/levels/italy/art/water/depthcolor_ramp_italy_muddy.png";
            water["foamTex"] = "levels/italy/art/water/foam2.dds";
            water["rippleTex"] = "/levels/italy/art/water/ripple.dds";
            water["Foam"] = new object[]
            {
                new Dictionary<string, object> { ["foamDir"] = new[] { 0, 1 }, ["foamSpeed"] = 0.01 },
                new Dictionary<string, object> { ["foamDir"] = new[] { 0, -1 }, ["foamOpacity"] = 5, ["foamSpeed"] = 0.01, ["foamTexScale"] = new[] { 4, 4 } },
            };
            water["Ripples (texture animation)"] = new object[]
            {
                new Dictionary<string, object> { ["rippleDir"] = new[] { 0, -1 }, ["rippleMagnitude"] = 0.5, ["rippleSpeed"] = 0.008, ["rippleTexScale"] = new[] { 12, 12 } },
                new Dictionary<string, object> { ["rippleDir"] = new[] { 0.707, 0.707 }, ["rippleMagnitude"] = 0.5, ["rippleSpeed"] = 0.05, ["rippleTexScale"] = new[] { 2, 2 } },
                new Dictionary<string, object> { ["rippleDir"] = new[] { -0.5, 0.86 }, ["rippleMagnitude"] = 0.35, ["rippleSpeed"] = 0.003, ["rippleTexScale"] = new[] { 120, 120 } },
            };
            water["Waves (vertex undulation)"] = new object[]
            {
                new Dictionary<string, object> { ["waveDir"] = new[] { 0, -1 }, ["waveMagnitude"] = 0.5, ["waveSpeed"] = 1 },
                new Dictionary<string, object> { ["waveDir"] = new[] { 0.25, 0.2 }, ["waveMagnitude"] = 0.2, ["waveSpeed"] = 2 },
                new Dictionary<string, object> { ["waveDir"] = new[] { 0.1, -0.7 }, ["waveMagnitude"] = 0.2, ["waveSpeed"] = 3 },
            };
            water["baseColor"] = new[] { 253, 254, 254, 0 };
            water["clarity"] = 0.25;
            water["depthGradientMax"] = 70;
            water["fresnelBias"] = -0.1;
            water["fresnelPower"] = 0.8;
            water["gridSize"] = 100;
            water["overallFoamOpacity"] = 3.5;
            water["overallRippleMagnitude"] = 1;
            water["overallWaveMagnitude"] = 0.15;
            water["reflectivity"] = 0.2;
            water["specularPower"] = 210;
            water["underwaterColor"] = new[] { 60, 223, 254, 253 };
            water["viscosity"] = 0.001;
            water["waterFogDensity"] = 0.8;
            water["waterFogDensityOffset"] = 0.1;
            return water;
        }

        private void WriteTer(ZipArchive zip, string entryName, int size, float floor, float range, byte[]? layerMap, string[] materialNames)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)9); // version
            writer.Write((uint)size);

            // Heightmap: row 0 = south edge; elevation grid y axis points north
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var value = (Height(x, y) - floor) / range * 65535f;
                    writer.Write((ushort)Math.Clamp(value, 0f, 65535f));
                }
            }

            if (layerMap != null)
            {
                writer.Write(layerMap);
            }
            else
            {
                var zeros = new byte[size];
                for (var y = 0; y < size; y++)
                {
                    writer.Write(zeros);
                }
            }

            writer.Write((uint)materialNames.Length);
            foreach (var name in materialNames)
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
            }
        }

        private void WriteHeightmapPng(ZipArchive zip, string entryName, int size, float floor, float range)
        {
            var outSize = Math.Min(size, HeightmapPngMaxSize);
            var scale = (float)size / outSize;
            using var image = new Image<L8>(outSize, outSize);
            for (var y = 0; y < outSize; y++)
            {
                var srcY = Math.Min(size - 1, (int)(y * scale));
                for (var x = 0; x < outSize; x++)
                {
                    var srcX = Math.Min(size - 1, (int)(x * scale));
                    // PNG row 0 is north
                    var value = (Height(srcX, size - 1 - srcY) - floor) / range * 255f;
                    image[x, y] = new L8((byte)Math.Clamp(value, 0f, 255f));
                }
            }
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        private void WriteBaseTextureAndPreview(ZipArchive zip, string basePath, Image<Rgba32>? baseTexture, int size, float floor, float range)
        {
            var ownTexture = false;
            if (baseTexture == null)
            {
                // No imagery: hypsometric shading so the terrain is still readable
                ownTexture = true;
                baseTexture = new Image<Rgba32>(1024, 1024);
                var scale = (float)size / 1024;
                for (var y = 0; y < 1024; y++)
                {
                    var srcY = Math.Min(size - 1, (int)(y * scale));
                    for (var x = 0; x < 1024; x++)
                    {
                        var srcX = Math.Min(size - 1, (int)(x * scale));
                        var altitude = Height(srcX, size - 1 - srcY);
                        var t = Math.Clamp((altitude - floor) / range, 0f, 1f);
                        baseTexture[x, y] = altitude < 0.1f
                            ? new Rgba32(70, 140, 160)
                            : new Rgba32((byte)(90 + t * 120), (byte)(120 + t * 80), (byte)(70 + t * 60));
                    }
                }
            }
            try
            {
                var texEntry = zip.CreateEntry($"{basePath}/art/terrains/terrain.png");
                using (var stream = texEntry.Open())
                {
                    baseTexture.SaveAsPng(stream);
                }

                using (var preview = baseTexture.Clone(c => c.Resize(PreviewSize, PreviewSize)))
                {
                    var previewEntry = zip.CreateEntry($"{basePath}/preview.png");
                    using var stream = previewEntry.Open();
                    preview.SaveAsPng(stream);
                }
            }
            finally
            {
                if (ownTexture)
                {
                    baseTexture.Dispose();
                }
            }
        }

        /// <summary>
        /// Downscale a huge image window by window (never materializes the full image).
        /// Read-only access: works with the render-on-read storages used by satmap/idmap.
        /// </summary>
        private static async Task<Image<Rgba32>> DownscaleHugeImage<TPixel>(HugeImage<TPixel> source, int targetSize, bool nearestNeighbor)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var target = new Image<Rgba32>(targetSize, targetSize);
            var srcSize = source.Size.Width;
            var scale = (double)targetSize / srcSize;
            const int windowSize = 4096;
            for (var wy = 0; wy < srcSize; wy += windowSize)
            {
                for (var wx = 0; wx < srcSize; wx += windowSize)
                {
                    var w = Math.Min(windowSize, srcSize - wx);
                    var h = Math.Min(windowSize, srcSize - wy);
                    var tx = (int)Math.Round(wx * scale);
                    var ty = (int)Math.Round(wy * scale);
                    var tw = Math.Max(1, (int)Math.Round((wx + w) * scale) - tx);
                    var th = Math.Max(1, (int)Math.Round((wy + h) * scale) - ty);
                    using var window = new Image<TPixel>(w, h);
                    var sourcePoint = new Point(wx, wy);
                    await window.MutateAsync(async ctx => await ctx.DrawHugeImageAsync(source, sourcePoint, new Point(0, 0), new Size(w, h)).ConfigureAwait(false)).ConfigureAwait(false);
                    window.Mutate(c => c.Resize(new ResizeOptions
                    {
                        Size = new Size(tw, th),
                        Sampler = nearestNeighbor ? KnownResamplers.NearestNeighbor : KnownResamplers.Bicubic,
                        Mode = ResizeMode.Stretch,
                    }));
                    target.Mutate(c => c.DrawImage(window, new Point(tx, ty), 1f));
                }
                await source.OffloadAsync().ConfigureAwait(false);
            }
            return target;
        }

        private static void WriteJson(ZipArchive zip, string entryName, object value)
        {
            WriteText(zip, entryName, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void WriteNdJson(ZipArchive zip, string entryName, params Dictionary<string, object>[] items)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.Append(JsonSerializer.Serialize(item));
                sb.Append('\n');
            }
            WriteText(zip, entryName, sb.ToString());
        }

        private static void WriteText(ZipArchive zip, string entryName, string content)
        {
            // Small entries are stored uncompressed: when deflate does not shrink a file, the
            // compressed and raw sizes are equal and BeamNG's VFS then reads the raw deflate
            // bytes as if the entry was stored, corrupting the file
            var entry = zip.CreateEntry(entryName, content.Length < 512 ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
