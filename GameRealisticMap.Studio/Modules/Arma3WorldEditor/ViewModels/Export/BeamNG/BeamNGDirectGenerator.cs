using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameRealisticMap;
using GameRealisticMap.Arma3;
using GameRealisticMap.Configuration;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Geometries;
using GameRealisticMap.ManMade.Buildings;
using GameRealisticMap.ManMade.Roads;
using GameRealisticMap.Nature.Forests;
using GameRealisticMap.Osm;
using GameRealisticMap.Satellite;
using GameRealisticMap.Studio.Modules.Reporting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.BeamNG
{
    /// <summary>
    /// Generates a BeamNG level directly from a map configuration (real-world data), without
    /// creating an Arma 3 map first. Uses the game-agnostic GameRealisticMap pipeline:
    /// elevation, satellite image, roads, forests, buildings and lakes from OSM.
    /// </summary>
    internal static class BeamNGDirectGenerator
    {
        // Large enough for a whole Swiss district (~168 km²) at reasonable forest density
        private const int MaxTreeInstances = 600_000;

        public static async Task Generate(IProgressTaskUI task, Arma3MapConfig config, ISourceLocations sources, string targetFile)
        {
            var scope = task.Scope;
            var terrainArea = config.TerrainArea;

            var catalog = new BuildersCatalog(new DefaultBuildersConfig(), sources);
            var loader = new OsmDataOverPassLoader(scope, sources);
            var osmSource = await loader.Load(terrainArea);
            var context = new BuildContext(catalog, scope, terrainArea, osmSource, config.Imagery);

            var grid = context.GetData<ElevationData>().Elevation;
            var size = grid.Size;
            var cellSize = grid.CellSize.X;

            // Roads
            var roadInputs = context.GetData<RoadsData>().Roads
                .Where(r => r.Path?.Points != null && r.Path.Points.Count >= 2)
                .Select(r => new BeamNGRoadInput(r.Path.Points.ToList(), (float)r.RoadTypeInfos.Width, IsDirt(r)))
                .ToList();
            scope.WriteLine($"Roads: {roadInputs.Count}");

            // Surface layer map from all OSM land-use polygons (roads burned later by the writer).
            // Layer indices: 0 grass, 1 asphalt, 2 dirt, 3 gravel, 4 sand, 5 rock, 6 mud
            var forestPolygons = context.GetData<ForestData>().Polygons;
            var layerMap = BuildLayerMap(size, cellSize, new (List<TerrainPolygon>, byte)[]
            {
                (context.GetData<GameRealisticMap.Nature.Surfaces.MeadowsData>().Polygons, 0),   // meadows = grass
                (context.GetData<GameRealisticMap.ManMade.Farmlands.FarmlandsData>().Polygons, 2), // fields = dirt (ploughed)
                (context.GetData<GameRealisticMap.ManMade.Farmlands.VineyardData>().Polygons, 2),
                (context.GetData<GameRealisticMap.ManMade.Farmlands.OrchardData>().Polygons, 0),
                (context.GetData<GameRealisticMap.Nature.Scrubs.ScrubData>().Polygons, 2),
                (context.GetData<GameRealisticMap.Nature.Surfaces.SandSurfacesData>().Polygons, 4),
                (forestPolygons, 2),                                                              // forest floor = dirt
                (context.GetData<GameRealisticMap.Nature.RockAreas.RocksData>().Polygons, 5),
            });

            // Trees sampled inside forest polygons (deterministic)
            var trees = SampleForestTrees(forestPolygons);
            scope.WriteLine($"Trees: {trees.Count} sampled in {forestPolygons.Count} forest polygons");

            // Buildings: real swissBUILDINGS3D meshes for Swiss maps, OSM footprint boxes otherwise
            List<SwissBuildings3dDownloader.BuildingMesh>? buildingMeshes = null;
            if (config.UseSwisstopoElevation)
            {
                try
                {
                    buildingMeshes = await SwissBuildings3dDownloader.LoadAsync(scope, terrainArea);
                    if (buildingMeshes.Count == 0)
                    {
                        buildingMeshes = null;
                    }
                }
                catch (Exception ex)
                {
                    scope.WriteLine($"swissBUILDINGS3D unavailable ({ex.Message}), fallback to OSM footprints.");
                }
            }
            var buildings = buildingMeshes != null ? new List<BeamNGBuildingBox>() : context.GetData<BuildingsData>().Buildings
                .Select(b => new BeamNGBuildingBox(
                    b.Box.Center.X, b.Box.Center.Y,
                    MathHelper.ToRadians(b.Box.Angle),
                    b.Box.Width, b.Box.Height,
                    EstimateHeight(b.TypeId)))
                .ToList();
            scope.WriteLine($"Buildings: {(buildingMeshes != null ? $"{buildingMeshes.Count} swissBUILDINGS3D buildings" : $"{buildings.Count} OSM boxes")}");

            // Lakes: one best-effort WaterBlock per lake (axis-aligned box of the polygon)
            var ponds = new List<BeamNGPond>();
            foreach (var lake in context.GetData<ElevationWithLakesData>().Lakes)
            {
                var min = lake.TerrainPolygon.MinPoint;
                var max = lake.TerrainPolygon.MaxPoint;
                var sizeMeters = Math.Max(max.X - min.X, max.Y - min.Y);
                if (sizeMeters < 2f)
                {
                    continue;
                }
                ponds.Add(new BeamNGPond((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, lake.WaterElevation, sizeMeters, 0f));
            }
            scope.WriteLine($"Lakes: {ponds.Count}");

            // Satellite image
            var satMap = context.GetData<RawSatelliteImageData>().Image;

            var writer = new BeamNGLevelWriter(grid, cellSize, config.WorldName, config.WorldName,
                null, null, null, roadInputs, trees, ponds, buildings, satMap, layerMap, buildingMeshes);
            await writer.WriteAsync(targetFile, scope);
        }

        private static bool IsDirt(Road road)
        {
            var name = road.RoadType.ToString().ToLowerInvariant();
            return name.Contains("dirt") || name.Contains("path") || name.Contains("trail") || name.Contains("track");
        }

        private static float EstimateHeight(BuildingTypeId typeId)
        {
            return typeId switch
            {
                BuildingTypeId.Church => 14f,
                BuildingTypeId.HistoricalFort => 10f,
                BuildingTypeId.RadioTower => 25f,
                BuildingTypeId.WaterTower => 20f,
                BuildingTypeId.Lighthouse => 20f,
                BuildingTypeId.WindTurbine => 40f,
                BuildingTypeId.Industrial => 8f,
                BuildingTypeId.Commercial => 8f,
                BuildingTypeId.Military => 6f,
                BuildingTypeId.Agricultural => 6f,
                BuildingTypeId.Hut => 3f,
                BuildingTypeId.Shed => 3f,
                BuildingTypeId.IndividualGarage => 3f,
                BuildingTypeId.BusStopShelter => 3f,
                _ => 7f, // Residential, Retail...
            };
        }

        private static byte[] BuildLayerMap(int size, float cellSize, (List<TerrainPolygon> Polygons, byte Layer)[] layers)
        {
            // Start from grass (0). Each group is rasterized in order; later groups win on overlap.
            // Pixel value stores layer index + 1 (0 = untouched grass); roads are burned later.
            var layerMap = new byte[size * size];
            var options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { Antialias = false } };
            using var image = new Image<L8>(size, size);
            image.Mutate(ctx =>
            {
                foreach (var (polygons, layer) in layers)
                {
                    var color = Color.FromPixel(new L8((byte)(layer + 1)));
                    foreach (var polygon in polygons)
                    {
                        var shell = polygon.Shell.Select(p => new PointF(p.X / cellSize, p.Y / cellSize)).ToArray();
                        if (shell.Length >= 3)
                        {
                            ctx.FillPolygon(options, Brushes.Solid(color), shell);
                        }
                        foreach (var hole in polygon.Holes)
                        {
                            var holePoints = hole.Select(p => new PointF(p.X / cellSize, p.Y / cellSize)).ToArray();
                            if (holePoints.Length >= 3)
                            {
                                // Holes revert to grass (layer 0 -> stored value 1 so it overrides)
                                ctx.FillPolygon(options, Brushes.Solid(Color.FromPixel(new L8(1))), holePoints);
                            }
                        }
                    }
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
            return layerMap;
        }

        private static List<BeamNGForestInstance> SampleForestTrees(List<TerrainPolygon> forestPolygons)
        {
            var totalArea = forestPolygons.Sum(p => Math.Abs(p.Area));
            if (totalArea < 1)
            {
                return new List<BeamNGForestInstance>();
            }
            var density = Math.Min(0.02, MaxTreeInstances * 0.95 / totalArea); // trees per m²
            var result = new List<BeamNGForestInstance>();
            var random = new Random(1234);
            foreach (var polygon in forestPolygons)
            {
                var area = Math.Abs(polygon.Area);
                var count = (int)(area * density);
                if (count == 0)
                {
                    continue;
                }
                var min = polygon.MinPoint;
                var max = polygon.MaxPoint;
                var width = max.X - min.X;
                var height = max.Y - min.Y;
                var placed = 0;
                var attempts = 0;
                var maxAttempts = count * 4;
                while (placed < count && attempts < maxAttempts)
                {
                    attempts++;
                    var point = new TerrainPoint(
                        min.X + (float)(random.NextDouble() * width),
                        min.Y + (float)(random.NextDouble() * height));
                    if (polygon.Contains(point))
                    {
                        result.Add(new BeamNGForestInstance(
                            point.X, point.Y,
                            (float)(random.NextDouble() * Math.PI * 2),
                            0.8f + (float)random.NextDouble() * 0.5f,
                            BeamNGForestKind.Tree));
                        placed++;
                    }
                }
                if (result.Count >= MaxTreeInstances)
                {
                    break;
                }
            }
            return result;
        }
    }
}
