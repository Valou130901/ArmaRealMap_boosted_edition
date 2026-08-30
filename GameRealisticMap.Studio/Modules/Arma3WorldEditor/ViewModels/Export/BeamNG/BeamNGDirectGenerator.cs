using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameRealisticMap;
using GameRealisticMap.Arma3;
using GameRealisticMap.Configuration;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Geometries;
using GameRealisticMap.ManMade;
using GameRealisticMap.ManMade.Buildings;
using GameRealisticMap.ManMade.Roads;
using GameRealisticMap.Nature.Forests;
using GameRealisticMap.Nature.Trees;
using GameRealisticMap.Reforger.Port;
using Pmad.ProgressTracking;
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

            sources = WithSwissImagery(sources, config, scope);

            var catalog = new BuildersCatalog(new DefaultBuildersConfig(), sources);
            var loader = new OsmDataOverPassLoader(scope, sources);
            var osmSource = await loader.Load(terrainArea);
            var context = new BuildContext(catalog, scope, terrainArea, osmSource, config.Imagery);

            var grid = context.GetData<ElevationData>().Elevation;
            var size = grid.Size;
            var cellSize = grid.CellSize.X;

            // Roads
            // Footways and trails are not drivable and would render as thin ribbons: keep vehicle roads only
            var roadInputs = context.GetData<RoadsData>().Roads
                .Where(r => r.Path?.Points != null && r.Path.Points.Count >= 2 && r.RoadTypeInfos.Width >= 3f)
                .Select(r => new BeamNGRoadInput(
                    r.Path.Points.ToList(),
                    (float)r.RoadTypeInfos.Width,
                    IsDirt(r),
                    RoadDrivability(r.RoadType),
                    RoadSpeedLimit(r.RoadType),
                    r.SpecialSegment == WaySpecialSegment.Bridge))
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

            // Real trees where swisstopo covers the map: every crown in the surface model is a tree
            // that stands there, at the height it has. Falls back to scattering inside the forest
            // polygons everywhere else.
            List<BeamNGForestInstance>? trees = null;
            if (config.UseSwisstopoElevation)
            {
                try
                {
                    var canopy = await SwisstopoCanopyDownloader.LoadAsync(scope, terrainArea, grid);
                    canopy = WithoutRooftops(canopy, buildingMeshes, buildings, terrainArea, scope);
                    canopy = WithoutRoads(canopy, roadInputs, scope);
                    if (canopy.Count > 0)
                    {
                        trees = FromCanopy(canopy, scope);
                    }
                }
                catch (Exception ex)
                {
                    scope.WriteLine($"swissSURFACE3D unavailable ({ex.Message}), trees fall back to sampling.");
                }
            }
            if (trees == null)
            {
                trees = SampleForestTrees(forestPolygons);
                scope.WriteLine($"Trees: {trees.Count} sampled in {forestPolygons.Count} forest polygons");
            }

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

            // Walls, fences and hedges along the OSM ways
            var fences = context.GetData<GameRealisticMap.ManMade.Fences.FencesData>().Fences
                .Where(f => f.Path?.Points != null && f.Path.Points.Count >= 2)
                .Select(f => new BeamNGFenceInput(f.Path.Points.ToList(), f.TypeId switch
                {
                    GameRealisticMap.ManMade.Fences.FenceTypeId.Wall => BeamNGFenceKind.Wall,
                    GameRealisticMap.ManMade.Fences.FenceTypeId.Hedge => BeamNGFenceKind.Hedge,
                    _ => BeamNGFenceKind.Fence,
                }))
                .ToList();
            var onRoads = fences.Count;
            fences = fences.Where(f => !CrossesRoad(f, roadInputs)).ToList();
            scope.WriteLine($"Fences: {fences.Count} walls/fences/hedges, {onRoads - fences.Count} dropped for standing on a road");

            // Satellite image
            var satMap = context.GetData<RawSatelliteImageData>().Image;

            // The shared library of converted Arma meshes, so the forest draws real trees instead of
            // the generated billboards. It is filled by porting a wrp; whatever is in it is used.
            var modelLibrary = ReforgerModelLibrary.Load();
            scope.WriteLine($"Model library: {modelLibrary.ConvertedCount} converted Arma meshes available.");

            var places = PlacesOnRoads(context.GetData<GameRealisticMap.ManMade.Places.CitiesData>().Cities, roadInputs, scope);

            var writer = new BeamNGLevelWriter(grid, cellSize, config.WorldName, config.WorldName,
                null, null, null, roadInputs, trees, ponds, buildings, satMap, layerMap, buildingMeshes, fences,
                modelLibraryDirectory: modelLibrary.RootDirectory, places: places);
            await writer.WriteAsync(targetFile, scope);
        }

        /// <summary>
        /// Weight used by the BeamNG AI graph: bigger roads are preferred for traffic and routing.
        /// </summary>
        private static float RoadDrivability(RoadTypeId type) => type switch
        {
            RoadTypeId.TwoLanesMotorway => 1f,
            RoadTypeId.TwoLanesPrimaryRoad => 0.85f,
            RoadTypeId.TwoLanesSecondaryRoad => 0.7f,
            RoadTypeId.TwoLanesConcreteRoad => 0.6f,
            RoadTypeId.SingleLaneConcreteRoad => 0.45f,
            RoadTypeId.SingleLaneDirtRoad => 0.3f,
            RoadTypeId.SingleLaneDirtPath => 0.2f,
            _ => 0.25f,
        };

        private static int RoadSpeedLimit(RoadTypeId type) => type switch
        {
            RoadTypeId.TwoLanesMotorway => 120,
            RoadTypeId.TwoLanesPrimaryRoad => 80,
            RoadTypeId.TwoLanesSecondaryRoad => 80,
            RoadTypeId.TwoLanesConcreteRoad => 50,
            RoadTypeId.SingleLaneConcreteRoad => 50,
            _ => 30,
        };

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

        /// <summary>
        /// Serves the ground texture from swisstopo's own orthophoto on a Swiss map.
        /// </summary>
        /// <remarks>
        /// The default provider is Sentinel-2 cloudless at zoom 15, roughly 3.3 m per pixel at this
        /// latitude and averaged over a season. SWISSIMAGE is an aerial survey: sharper, current,
        /// and the actual colours of the fields being driven past.
        /// <para>
        /// Zoom 16 and no further. The published product goes down to 10 cm, but the level carries
        /// its ground on a 4096 pixel texture, which over a 16.4 km map is 4 m per pixel: anything
        /// finer than zoom 16 is fetched only to be thrown away by the resize.
        /// </para>
        /// </remarks>
        private static ISourceLocations WithSwissImagery(ISourceLocations sources, Arma3MapConfig config, IProgressScope scope)
        {
            if (!config.UseSwisstopoElevation)
            {
                return sources;
            }
            scope.WriteLine("Imagery: SWISSIMAGE (swisstopo) instead of Sentinel-2.");
            return new SwissImagerySources(sources);
        }

        private sealed class SwissImagerySources : ISourceLocations
        {
            private readonly ISourceLocations inner;

            public SwissImagerySources(ISourceLocations inner)
            {
                this.inner = inner;
            }

            // The zoom sits right before the two placeholders, which is where the provider reads it
            public Uri SatelliteImageProvider { get; } = new Uri(
                "https://wmts.geo.admin.ch/1.0.0/ch.swisstopo.swissimage/default/current/3857/16/{x}/{y}.jpeg");

            public Uri MapToolkitSRTM15Plus => inner.MapToolkitSRTM15Plus;

            public Uri MapToolkitSRTM1 => inner.MapToolkitSRTM1;

            public Uri MapToolkitAW3D30 => inner.MapToolkitAW3D30;

            public Uri WeatherStats => inner.WeatherStats;

            public Uri OverpassApiInterpreter => inner.OverpassApiInterpreter;
        }

        /// <summary>
        /// Drops the crowns that stand over a road.
        /// </summary>
        /// <remarks>
        /// The surface model sees a crown, not a trunk, and a tree leaning over a lane puts its
        /// highest point above the tarmac. Planted from that point the trunk comes down in the
        /// middle of the carriageway. The verge is kept: only what falls inside the driving surface
        /// plus a metre goes, so a row of trees along a road survives as a row along the road.
        /// </remarks>
        private static List<SwisstopoCanopyDownloader.CanopyTree> WithoutRoads(
            List<SwisstopoCanopyDownloader.CanopyTree> canopy, List<BeamNGRoadInput> roads, IProgressScope scope)
        {
            const float Margin = 1f;

            var kept = canopy.Where(tree =>
            {
                foreach (var road in roads)
                {
                    var reach = (Math.Max(road.Width, 4f) / 2f) + Margin;
                    var points = road.Points;
                    for (var i = 1; i < points.Count; i++)
                    {
                        var ax = points[i - 1].X;
                        var ay = points[i - 1].Y;
                        var bx = points[i].X - ax;
                        var by = points[i].Y - ay;
                        var length = (bx * bx) + (by * by);
                        var t = length < 0.0001f ? 0f
                            : Math.Clamp((((tree.X - ax) * bx) + ((tree.Y - ay) * by)) / length, 0f, 1f);
                        var dx = tree.X - (ax + (bx * t));
                        var dy = tree.Y - (ay + (by * t));
                        if ((dx * dx) + (dy * dy) <= reach * reach)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }).ToList();

            scope.WriteLine($"Canopy: {canopy.Count - kept.Count} crowns dropped for standing on a road.");
            return kept;
        }

        /// <summary>
        /// True when a wall or hedge stands on the carriageway itself.
        /// </summary>
        /// <remarks>
        /// OSM tags a roundabout's central island, its splitter islands and the kerb lines around a
        /// junction as barriers, and drawing those as walls puts a ring of concrete slabs across the
        /// road and spokes radiating out of it. Anything whose line runs inside a carriageway is
        /// dropped, and a single point on the tarmac is enough. A field wall runs along the outside
        /// of a verge and never touches the carriageway, so it survives; anything that puts one
        /// foot on the road does not, which is what was asked for.
        /// </remarks>
        private static bool CrossesRoad(BeamNGFenceInput fence, List<BeamNGRoadInput> roads)
        {
            foreach (var point in fence.Points)
            {
                foreach (var road in roads)
                {
                    var half = Math.Max(road.Width, 4f) / 2f;
                    var hit = false;
                    var points = road.Points;
                    for (var i = 1; i < points.Count && !hit; i++)
                    {
                        var ax = points[i - 1].X;
                        var ay = points[i - 1].Y;
                        var bx = points[i].X - ax;
                        var by = points[i].Y - ay;
                        var length = (bx * bx) + (by * by);
                        var t = length < 0.0001f ? 0f
                            : Math.Clamp((((point.X - ax) * bx) + ((point.Y - ay) * by)) / length, 0f, 1f);
                        var dx = point.X - (ax + (bx * t));
                        var dy = point.Y - (ay + (by * t));
                        hit = (dx * dx) + (dy * dy) <= half * half;
                    }
                    if (hit)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// One fast travel point per village, put on the nearest road rather than at the centre.
        /// </summary>
        /// <remarks>
        /// BeamNG lists spawn points on the Big Map, so one point per place is both the label and
        /// the destination. The centre of an OSM place is a point in a field or inside a house as
        /// often as not, so it is snapped onto a carriageway and turned to face along it: arriving
        /// by fast travel then puts the car on tarmac, pointing down the road.
        /// </remarks>
        private static List<BeamNGPlace> PlacesOnRoads(
            List<GameRealisticMap.ManMade.Places.City> cities, List<BeamNGRoadInput> roads, IProgressScope scope)
        {
            const float MaxSnap = 600f;

            var result = new List<BeamNGPlace>();
            var snapped = 0;
            foreach (var city in cities.Where(c => !string.IsNullOrEmpty(c.Name)))
            {
                var x = city.Center.X;
                var y = city.Center.Y;
                var yaw = 0f;
                var best = float.MaxValue;

                foreach (var road in roads)
                {
                    var points = road.Points;
                    for (var i = 0; i < points.Count; i++)
                    {
                        var dx = points[i].X - city.Center.X;
                        var dy = points[i].Y - city.Center.Y;
                        var distance = (dx * dx) + (dy * dy);
                        if (distance >= best)
                        {
                            continue;
                        }
                        best = distance;
                        x = points[i].X;
                        y = points[i].Y;
                        var other = i + 1 < points.Count ? points[i + 1] : points[Math.Max(0, i - 1)];
                        yaw = MathF.Atan2(other.Y - points[i].Y, other.X - points[i].X);
                    }
                }

                if (best > MaxSnap * MaxSnap)
                {
                    x = city.Center.X;
                    y = city.Center.Y;
                    yaw = 0f;
                }
                else
                {
                    snapped++;
                }
                result.Add(new BeamNGPlace(city.Name, x, y, yaw));
            }
            scope.WriteLine($"Places: {result.Count} villages, {snapped} put on a road.");
            return result;
        }

        /// <summary>
        /// Drops the crowns that are really rooftops.
        /// </summary>
        /// <remarks>
        /// The surface model records the first thing the aircraft saw, so a building is a solid
        /// block in it exactly like a tree is. Every ridge line and every chimney therefore comes
        /// out of the crown search as a tree, planted on the roof. The footprints are known by this
        /// point, so they are burned into a coarse mask and any crown standing on one is discarded.
        /// <para>
        /// The mask is grown by one cell on purpose: a roof overhang reaches past the wall below it,
        /// and its edge would otherwise keep producing a row of trees around every house.
        /// </para>
        /// </remarks>
        private static List<SwisstopoCanopyDownloader.CanopyTree> WithoutRooftops(
            List<SwisstopoCanopyDownloader.CanopyTree> canopy,
            List<SwissBuildings3dDownloader.BuildingMesh>? meshes,
            List<BeamNGBuildingBox> boxes,
            ITerrainArea area,
            IProgressScope scope)
        {
            const float MaskCell = 2f;

            var side = (int)Math.Ceiling(area.SizeInMeters / MaskCell) + 1;
            var mask = new bool[side * side];

            void Mark(float x, float y)
            {
                var cx = (int)(x / MaskCell);
                var cy = (int)(y / MaskCell);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var mx = cx + dx;
                        var my = cy + dy;
                        if (mx >= 0 && mx < side && my >= 0 && my < side)
                        {
                            mask[(my * side) + mx] = true;
                        }
                    }
                }
            }

            var footprints = 0;
            if (meshes != null)
            {
                foreach (var mesh in meshes)
                {
                    footprints++;
                    foreach (var triangle in mesh.Triangles)
                    {
                        // The whole triangle, not its three corners. A LoD2 roof is a handful of
                        // very large triangles, so marking corners left the middle of every roof
                        // unmarked and trees kept growing straight out of the ridge.
                        var minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
                        var maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
                        var minY = MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y));
                        var maxY = MathF.Max(triangle.A.Y, MathF.Max(triangle.B.Y, triangle.C.Y));
                        for (var y = minY; y <= maxY + MaskCell; y += MaskCell)
                        {
                            for (var x = minX; x <= maxX + MaskCell; x += MaskCell)
                            {
                                Mark(x, y);
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var box in boxes)
                {
                    footprints++;
                    var reach = Math.Max(box.Width, box.Depth) / 2f;
                    for (var y = -reach; y <= reach; y += MaskCell)
                    {
                        for (var x = -reach; x <= reach; x += MaskCell)
                        {
                            Mark(box.X + x, box.Y + y);
                        }
                    }
                }
            }
            if (footprints == 0)
            {
                return canopy;
            }

            var kept = canopy.Where(t =>
            {
                var cx = (int)(t.X / MaskCell);
                var cy = (int)(t.Y / MaskCell);
                if (cx < 0 || cx >= side || cy < 0 || cy >= side)
                {
                    return false;
                }
                return !mask[(cy * side) + cx];
            }).ToList();

            scope.WriteLine($"Canopy: {canopy.Count - kept.Count} crowns dropped as rooftops over {footprints} buildings.");
            return kept;
        }

        /// <summary>
        /// Turns canopy tops into forest instances, keeping the tallest when there are too many.
        /// </summary>
        /// <remarks>
        /// A whole Swiss district holds far more crowns than a level can carry, so the list is cut
        /// by height rather than at random: the big trees are what shapes a wood seen from a road,
        /// and dropping the undergrowth first keeps the canopy line intact. The scale carries the
        /// measured height through, so a 30 m spruce is drawn taller than a 6 m orchard tree instead
        /// of every tree being the same size.
        /// </remarks>
        private static List<BeamNGForestInstance> FromCanopy(
            List<SwisstopoCanopyDownloader.CanopyTree> canopy, IProgressScope scope)
        {
            const float ReferenceHeight = 14f;

            var kept = canopy;
            if (canopy.Count > MaxTreeInstances)
            {
                kept = canopy.OrderByDescending(t => t.Height).Take(MaxTreeInstances).ToList();
                scope.WriteLine($"Trees: {canopy.Count} crowns found, keeping the {MaxTreeInstances} tallest.");
            }
            else
            {
                scope.WriteLine($"Trees: {canopy.Count} real crowns from the surface model.");
            }

            var random = new Random(1234);
            return kept
                .Select(t => new BeamNGForestInstance(
                    t.X, t.Y,
                    (float)(random.NextDouble() * Math.PI * 2),
                    Math.Clamp(t.Height / ReferenceHeight, 0.4f, 2.5f),
                    t.Height >= 8f ? BeamNGForestKind.Tree : BeamNGForestKind.Bush,
                    SpeciesFor(t)))
                .ToList();
        }

        /// <summary>
        /// Central European species that match a crown of this height, drawn from the converted
        /// Arma meshes.
        /// </summary>
        /// <remarks>
        /// The surface model gives a height and nothing else, so the species is chosen from that
        /// height, which is the one thing it does say: conifers hold the tall canopy on the Swiss
        /// plateau, broadleaves the middle, scrub the bottom. Picked from a hash of the position so
        /// the same map always comes out the same, and so neighbouring trees differ.
        /// <para>
        /// A name that is not in the library costs nothing: the writer falls back to its generated
        /// billboard for that species alone.
        /// </para>
        /// </remarks>
        private static string SpeciesFor(SwisstopoCanopyDownloader.CanopyTree tree)
        {
            string[] choices;
            if (tree.Height >= 24f)
            {
                choices = TallConifers;
            }
            else if (tree.Height >= 15f)
            {
                choices = MatureBroadleaves;
            }
            else if (tree.Height >= 8f)
            {
                choices = SmallTrees;
            }
            else
            {
                choices = Scrub;
            }
            var hash = ((int)MathF.Round(tree.X * 4f) * 73856093) ^ ((int)MathF.Round(tree.Y * 4f) * 19349663);
            return choices[Math.Abs(hash) % choices.Length];
        }

        private static readonly string[] TallConifers =
        {
            "t_piceaabies_1f", "t_piceaabies_2f", "t_piceaabies_3f",
            "t_pinussylvestris_2f", "t_pinussylvestris_3f",
        };

        private static readonly string[] MatureBroadleaves =
        {
            "t_fagussylvatica_1f", "t_fagussylvatica_2f", "t_fagussylvatica_3f",
            "t_piceaabies_2s", "t_fraxinusav2s_f", "t_acer2s",
        };

        private static readonly string[] SmallTrees =
        {
            "t_betula_pendula_2s", "t_betula_pendula_3s", "t_fagussylvatica_1s",
            "t_malusdomestica_2s", "t_salix2s", "t_piceaabies_1s",
        };

        private static readonly string[] Scrub =
        {
            "b_corylus_heterophylla_1", "b_prunusspinosa_1s", "b_prunusspinosa_2s",
            "b_sambucusNigra_1s", "b_piceaabies_1f", "b_fagussylvatica_1f",
        };

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
