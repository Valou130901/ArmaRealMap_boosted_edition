using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BIS.WRP;
using GameRealisticMap.Arma3.Assets;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Arma3.GameEngine.Roads;
using GameRealisticMap.Arma3.TerrainBuilder;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Reforger.Import;
using GameRealisticMap.Reforger.Port;
using GameRealisticMap.Studio.Modules.Reporting;
using Pmad.HugeImages;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.BeamNG
{
    /// <summary>
    /// Generates a playable BeamNG.drive level zip: terrain, satellite base texture, per-surface
    /// physics (from the id map), roads as DecalRoad, vegetation as Forest instances, water and spawn.
    /// </summary>
    internal class ExportBeamNGLevelTask : SingleFileExportBase
    {
        private readonly EditableWrp world;
        private readonly string worldName;
        private readonly HugeImage<Rgb24>? satMap;
        private readonly HugeImage<Rgb24>? idMap;
        private readonly List<TerrainMaterialDefinition>? materials;
        private readonly List<EditableArma3Road>? roads;
        private readonly ModelInfoLibrary? library;
        private readonly GameRealisticMap.Arma3.IO.ProjectDrive? projectDrive;
        private readonly string? configContent;
        private readonly string? configDirectory;

        public ExportBeamNGLevelTask(EditableWrp world, string worldName, HugeImage<Rgb24>? satMap,
            HugeImage<Rgb24>? idMap, List<TerrainMaterialDefinition>? materials, List<EditableArma3Road>? roads,
            ModelInfoLibrary? library, string targetFile, GameRealisticMap.Arma3.IO.ProjectDrive? projectDrive = null,
            string? configContent = null, string? configDirectory = null)
            : base(targetFile)
        {
            this.projectDrive = projectDrive;
            this.configContent = configContent;
            this.configDirectory = configDirectory;
            this.world = world;
            this.worldName = worldName;
            this.satMap = satMap;
            this.idMap = idMap;
            this.materials = materials;
            this.roads = roads;
            this.library = library;
        }

        public override string Title => "Export BeamNG.drive level";

        protected override async Task<bool> Export(IProgressTaskUI ui, string targetFile)
        {
            try
            {
                var sourceGrid = world.ToElevationGrid();
                var sourceCellSize = world.CellSize * world.LandRangeX / world.TerrainRangeX;
                var grid = Upsample(sourceGrid, ui);
                var cellSize = sourceCellSize * sourceGrid.Size / grid.Size;
                var vegetation = ExtractVegetation(world);
                var ponds = ExtractPonds(world);
                var buildings = ExtractBuildings(world, ui);
                var modelInstances = ExtractModelInstances(world);
                // Forest species get their real mesh too, so their models must be in the library
                var modelLibrary = PortModels(
                    modelInstances.Select(i => i.Model)
                        .Concat(vegetation.Select(v => v.Model).Where(m => !string.IsNullOrEmpty(m))!)
                        .ToList(),
                    ui);
                ui.Scope.WriteLine($"Vegetation: {vegetation.Count}, ponds: {ponds.Count}, buildings: {buildings.Count} (from map objects)");
                var roadInputs = roads?
                    .Where(r => !r.IsRemoved && !r.RoadTypeInfos.IsPedestriansOnly && r.Path?.Points != null && r.Path.Points.Count >= 2)
                    .Select(r => new BeamNGRoadInput(r.Path.Points.ToList(), r.RoadTypeInfos.TextureWidth, IsDirtRoad(r.RoadTypeInfos)))
                    .ToList();
                MarkBridgeRoads(roadInputs, modelInstances, ui);
                modelInstances = FilterRoadSurfaces(modelInstances, roadInputs, ui);
                var places = ExtractPlaces(roadInputs, ui);
                var writer = new BeamNGLevelWriter(grid, cellSize, worldName, worldName, satMap, idMap, materials,
                    roadInputs, vegetation, ponds, buildings,
                    modelInstances: modelInstances, modelLibraryDirectory: modelLibrary?.RootDirectory,
                    places: places, sourceGrid: sourceGrid);
                await writer.WriteAsync(targetFile, ui.Scope);
            }
            finally
            {
                satMap?.Dispose();
                idMap?.Dispose();
            }

            ui.AddSuccessAction(() => GameRealisticMap.Studio.Toolkit.ShellHelper.OpenUri(Path.GetDirectoryName(targetFile)!), "Open folder");
            return true;
        }

        /// <summary>
        /// Town names of the source map, snapped onto the nearest road so spawning there puts the
        /// player on tarmac rather than in a field or inside a wall.
        /// </summary>
        /// <remarks>
        /// BeamNG lists spawn points on the Big Map, so one point per town doubles as the map label
        /// and as the fast travel destination.
        /// </remarks>
        private List<BeamNGPlace> ExtractPlaces(List<BeamNGRoadInput>? roadInputs, IProgressTaskUI ui)
        {
            // Official maps (Kelleys Island, Malden) keep their Names block in an included header
            var resolver = GameConfigNames.CreateIncludeResolver(configDirectory, projectDrive);
            var names = GameConfigNames.ReadFromContent(configContent, resolver).Where(n => n.IsSettlement).ToList();
            if (names.Count == 0)
            {
                ui.Scope.WriteLine("Places: none, the map config declares no Names block");
                return new List<BeamNGPlace>();
            }

            // Farthest a town centre may be from a road before spawning on the road stops making sense
            const float MaxSnapDistance = 400f;

            var result = new List<BeamNGPlace>(names.Count);
            var snapped = 0;
            foreach (var place in names)
            {
                var x = place.X;
                var y = place.Y;
                var yaw = 0f;
                var best = float.MaxValue;

                if (roadInputs != null)
                {
                    foreach (var road in roadInputs)
                    {
                        var points = road.Points;
                        for (var i = 0; i < points.Count; i++)
                        {
                            var dx = points[i].X - place.X;
                            var dy = points[i].Y - place.Y;
                            var distance = (dx * dx) + (dy * dy);
                            if (distance >= best)
                            {
                                continue;
                            }
                            best = distance;
                            x = points[i].X;
                            y = points[i].Y;
                            // Face along the road, using the neighbouring point
                            var other = i + 1 < points.Count ? points[i + 1] : points[Math.Max(0, i - 1)];
                            yaw = MathF.Atan2(other.Y - points[i].Y, other.X - points[i].X);
                        }
                    }
                }

                if (best > MaxSnapDistance * MaxSnapDistance)
                {
                    x = place.X;
                    y = place.Y;
                    yaw = 0f;
                }
                else
                {
                    snapped++;
                }
                result.Add(new BeamNGPlace(place.Name, x, y, yaw));
            }

            ui.Scope.WriteLine($"Places: {result.Count} named from the map config, {snapped} snapped onto a road");
            return result;
        }

        /// <summary>
        /// Families placed as individual TSStatic objects, one scene object each, editable in the
        /// World Editor.
        /// </summary>
        /// <remarks>
        /// Buildings only, and for a hard reason. A TSStatic is a full scene object: it is culled,
        /// zoned and submitted on its own every frame, which is why BeamNG's own levels hold a few
        /// thousand of them. Letting walls, fences, kerbs and power lines in as well put 58 000 in
        /// Malden and the frame rate collapsed. Everything else goes through <see cref="ClutterCategories"/>
        /// into the Forest system, which batches by cell and swallows hundreds of thousands.
        /// </remarks>
        private static readonly HashSet<ReforgerObjectCategory> MeshCategories = new()
        {
            ReforgerObjectCategory.Building,
            // Back as scene objects: a Forest item only ever carries a heading. Every rotation
            // matrix BeamNG ships in a forest file is a plain yaw, and feeding it pitch and roll
            // left the guard rails leaning at angles Arma never gave them. A TSStatic takes the
            // whole transform. The object count that first drove them into the Forest turned out
            // not to be the cost anyway; the missing LOD hierarchy was.
            ReforgerObjectCategory.Wall,
            ReforgerObjectCategory.Fence,
            ReforgerObjectCategory.Infrastructure,
            ReforgerObjectCategory.Other,
        };

        /// <summary>
        /// Families drawn by the Forest system alongside the vegetation: numerous, repetitive and
        /// not worth an individually editable object each.
        /// </summary>
        private static readonly HashSet<ReforgerObjectCategory> ClutterCategories = new()
        {
            // Water is left out on purpose: pond models already become WaterBlocks further down,
            // and placing them twice would put a solid mesh inside every pond.
        };

        /// <summary>How far from a bridge a road may pass and still be carried by it.</summary>
        private const float BridgeReach = 35f;

        /// <summary>A carriageway piece this close to a spline is one the DecalRoad already covers.</summary>
        private const float RoadCoverageReach = 12f;

        /// <summary>Share of carriageway pieces that must be covered before the models are dropped.</summary>
        private const float RoadCoverageRequired = 0.6f;

        /// <summary>
        /// Drops Arma's own carriageway pieces, but only on maps whose road splines actually cover
        /// them.
        /// </summary>
        /// <remarks>
        /// Terrains are built two ways. Some carry a full spline network, and there Arma's road
        /// meshes merely duplicate the DecalRoad built from the same centreline, leaving stray slabs
        /// of tarmac beside the carriageway. Others lay their roads as objects and give the splines
        /// nothing to work from; dropping the meshes there deletes the road itself, and the guard
        /// rails, which keep their own coordinates, end up lining a road that is no longer drawn.
        /// Measured on Kelleys Island, only 26% of crash barriers sat within ten metres of a spline.
        /// So the map decides, not a constant: the pieces go only when the splines are demonstrably
        /// already there.
        /// </remarks>
        private static List<BeamNGModelInstance> FilterRoadSurfaces(List<BeamNGModelInstance> instances,
            List<BeamNGRoadInput>? roadInputs, IProgressTaskUI ui)
        {
            var surfaces = instances.Where(i => IsRoadSurface(i.Model)).ToList();
            if (surfaces.Count == 0)
            {
                return instances;
            }

            var points = roadInputs?.SelectMany(r => r.Points).ToList();
            var covered = 0;
            if (points != null && points.Count > 0)
            {
                // Every eleventh piece is enough to tell a covered network from an absent one, and
                // keeps this from becoming millions of distance tests on a large terrain.
                var sampled = 0;
                for (var index = 0; index < surfaces.Count; index += 11)
                {
                    var surface = surfaces[index];
                    sampled++;
                    foreach (var point in points)
                    {
                        var dx = point.X - surface.X;
                        var dy = point.Y - surface.Y;
                        if ((dx * dx) + (dy * dy) < RoadCoverageReach * RoadCoverageReach)
                        {
                            covered++;
                            break;
                        }
                    }
                }
                if (sampled > 0 && (float)covered / sampled >= RoadCoverageRequired)
                {
                    ui.Scope.WriteLine(FormattableString.Invariant(
                        $"Roads: spline network covers {100f * covered / sampled:0}% of Arma's {surfaces.Count} carriageway pieces, dropping them for the DecalRoads"));
                    return instances.Where(i => !IsRoadSurface(i.Model)).ToList();
                }
                ui.Scope.WriteLine(FormattableString.Invariant(
                    $"Roads: spline network covers only {100f * covered / sampled:0}% of Arma's {surfaces.Count} carriageway pieces, keeping them so the map has roads"));
                return instances;
            }

            ui.Scope.WriteLine($"Roads: no spline network, keeping Arma's {surfaces.Count} carriageway pieces");
            return instances;
        }

        /// <summary>
        /// True for Arma's own road surface pieces, which this export does not place.
        /// </summary>

        /// <summary>
        /// Flags the roads that run over a bridge, so their surface decal is projected onto the deck
        /// rather than onto the terrain.
        /// </summary>
        /// <remarks>
        /// A DecalRoad normally drapes over the heightmap alone. Where an Arma bridge spans a gap
        /// that leaves the carriageway painted on the ground under the bridge, with the deck standing
        /// clear above it as a hump to be climbed. Torque projects onto scene objects too once
        /// overObjects is set, and that flag is what joins the two together.
        /// </remarks>
        private static void MarkBridgeRoads(List<BeamNGRoadInput>? roadInputs,
            List<BeamNGModelInstance> modelInstances, IProgressTaskUI ui)
        {
            if (roadInputs == null || roadInputs.Count == 0)
            {
                return;
            }
            var bridges = modelInstances
                .Where(i => i.Model.Contains("bridge", StringComparison.OrdinalIgnoreCase))
                .Select(i => (i.X, i.Y))
                .ToList();
            if (bridges.Count == 0)
            {
                return;
            }

            var marked = 0;
            for (var index = 0; index < roadInputs.Count; index++)
            {
                var road = roadInputs[index];
                if (road.ProjectOverObjects || !road.Points.Any(p => bridges.Any(b =>
                        ((b.X - p.X) * (b.X - p.X)) + ((b.Y - p.Y) * (b.Y - p.Y)) < BridgeReach * BridgeReach)))
                {
                    continue;
                }
                roadInputs[index] = road with { ProjectOverObjects = true };
                marked++;
            }
            ui.Scope.WriteLine($"Bridges: {bridges.Count} placed, {marked} road segments projected onto their decks");
        }

        /// <remarks>
        /// A road ended up drawn three times over: Arma's road meshes, the DecalRoad built from the
        /// same centreline, and the satellite image that already has the road painted into it. The
        /// three disagree on width and shade, which is what produced stray slabs of tarmac lying
        /// beside the carriageway. The DecalRoad is the one that carries drivability, so it wins.
        /// </remarks>
        /// <summary>
        /// Name fragments of the carriageway pieces Arma lays along a road, the only ones a
        /// DecalRoad genuinely duplicates.
        /// </summary>
        /// <remarks>
        /// Kerbs, pavements, painted markings and the airfield surfaces are deliberately absent:
        /// none of them competes with a DecalRoad. They are raised or painted detail that the
        /// generated roads do not provide, and cutting them stripped nearly three thousand pieces
        /// out of the towns and left the airport as bare grass.
        /// </remarks>
        private static readonly string[] RoadSurfaceNames =
        {
            "road", "asphalt",
        };

        /// <summary>
        /// Pieces that are never placed, whatever the map. These are not carriageway: they are
        /// obstacles Arma lays alongside one.
        /// </summary>
        /// <remarks>
        /// Kerbs and pavements are raised strips a soldier steps over without thinking; in a car
        /// they line the road with a continuous low wall. Concrete and tyre barriers are worse:
        /// Arma rings race tracks and compounds with them by the thousand, 3959 on Kelleys Island
        /// against 74 on Malden, and beside a carriageway they wall it off entirely.
        /// Unconditional, unlike <see cref="RoadSurfaceNames"/>: a map can depend on Arma's
        /// carriageway meshes for its road, never on its kerbs.
        /// </remarks>
        private static readonly string[] BlockingNames =
        {
            "pavement", "sidewalk", "curb", "kerb", "cncbarrier", "tyrebarrier",
        };

        private static bool IsBlocking(string model)
        {
            var file = Path.GetFileNameWithoutExtension(model).ToLowerInvariant();
            return BlockingNames.Any(keyword => file.Contains(keyword, StringComparison.Ordinal));
        }

        private static bool IsRoadSurface(string model)
        {
            var name = model.Replace('/', '\\').ToLowerInvariant();
            if (name.Contains(@"\roads_f\") || name.Contains(@"\roads\"))
            {
                return true;
            }
            var file = Path.GetFileNameWithoutExtension(name);
            return RoadSurfaceNames.Any(keyword => file.Contains(keyword, StringComparison.Ordinal));
        }

        /// <summary>
        /// Map objects as real model placements rather than boxes. Uses the same family classifier
        /// as the Arma Reforger export so both pipelines agree on what is what.
        /// </summary>
        private static List<BeamNGModelInstance> ExtractModelInstances(EditableWrp world)
        {
            var result = new List<BeamNGModelInstance>();
            var categories = new Dictionary<string, ReforgerObjectCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in world.GetNonDummyObjects())
            {
                var model = obj.Model;
                if (string.IsNullOrEmpty(model))
                {
                    continue;
                }
                if (!categories.TryGetValue(model, out var category))
                {
                    category = WrpModelClassifier.Classify(model);
                    categories.Add(model, category);
                }
                if (!MeshCategories.Contains(category) || IsBlocking(model))
                {
                    continue;
                }
                var matrix = obj.Transform.Matrix;
                var scale = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
                // Arma Y is altitude, Z is north; BeamNG wants X east, Y north, Z up
                result.Add(new BeamNGModelInstance(model, matrix.M41, matrix.M43, matrix.M42,
                    ToBeamNGRotation(matrix, scale), scale));
            }
            return result;
        }

        /// <summary>Terrain resolution to aim for. BeamNG handles this comfortably at map scale.</summary>
        private const int TargetGridSize = 4096;

        /// <summary>
        /// Resamples the elevation grid to a finer resolution, bilinearly.
        /// </summary>
        /// <remarks>
        /// Arma terrains are built for walking: Malden is 12.8 km over 1024 cells, so 12.5 m each.
        /// A driving game needs far more. The surface layer map is painted at the same resolution as
        /// the heightmap, so at 12.5 m the asphalt band can never follow a 7 m road, and grip
        /// alternates between road and grass as you drive. Resampling fixes the road surface and
        /// smooths the visible faceting at the same time.
        /// </remarks>
        private static ElevationGrid Upsample(ElevationGrid source, IProgressTaskUI ui)
        {
            var size = source.Size;
            if (size >= TargetGridSize)
            {
                return source;
            }

            var factor = TargetGridSize / size;
            var targetSize = size * factor;
            var target = new ElevationGrid(targetSize, source.CellSize.X / factor);

            using var report = ui.Scope.CreateSingle("Terrain.Upsample");
            var last = size - 1;
            for (var y = 0; y < targetSize; y++)
            {
                var sy = (float)y / factor;
                var y0 = Math.Min((int)sy, last);
                var y1 = Math.Min(y0 + 1, last);
                var fy = sy - y0;
                for (var x = 0; x < targetSize; x++)
                {
                    var sx = (float)x / factor;
                    var x0 = Math.Min((int)sx, last);
                    var x1 = Math.Min(x0 + 1, last);
                    var fx = sx - x0;

                    var top = (source[x0, y0] * (1f - fx)) + (source[x1, y0] * fx);
                    var bottom = (source[x0, y1] * (1f - fx)) + (source[x1, y1] * fx);
                    target[x, y] = (top * (1f - fy)) + (bottom * fy);
                }
            }

            ui.Scope.WriteLine(FormattableString.Invariant(
                $"Terrain: resampled {size} x {size} at {source.CellSize.X:0.###} m to {targetSize} x {targetSize} at {target.CellSize.X:0.###} m"));
            return target;
        }

        /// <summary>
        /// Converts an Arma object transform into the 3x3 row major orientation TSStatic expects.
        /// </summary>
        /// <remarks>
        /// Reconstructing the rotation from a heading alone flattens everything Arma tilted, which
        /// is why houses on slopes came out at the wrong angle. The basis vectors are taken straight
        /// from the matrix instead, with the same Y/Z swap the meshes get, and normalised so the
        /// object's scale is not applied twice.
        /// </remarks>
        private static float[] ToBeamNGRotation(Matrix4x4 matrix, float scale)
        {
            var inverse = scale > 0.0001f ? 1f / scale : 1f;

            // Arma rows are the X, Y (up) and Z (north) axes; BeamNG wants X, Y (north), Z (up),
            // and each vector has its own Y and Z components swapped for the same reason.
            var right = new Vector3(matrix.M11, matrix.M13, matrix.M12) * inverse;
            var forward = new Vector3(matrix.M31, matrix.M33, matrix.M32) * inverse;
            var up = new Vector3(matrix.M21, matrix.M23, matrix.M22) * inverse;

            return new[]
            {
                right.X, right.Y, right.Z,
                forward.X, forward.Y, forward.Z,
                up.X, up.Y, up.Z
            };
        }

        /// <summary>
        /// Makes sure every building model of the map exists in the shared model library, converting
        /// the ones that do not. Returns null when the project drive is unavailable.
        /// </summary>
        private ReforgerModelLibrary? PortModels(List<string> models, IProgressTaskUI ui)
        {
            if (models.Count == 0 || library == null || projectDrive == null)
            {
                return null;
            }
            var modelLibrary = ReforgerModelLibrary.Load();
            var runner = new ModelPortRunner(library.ReadODOL, projectDrive.OpenFileIfExists, modelLibrary);
            runner.Port(models.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ui.Scope);
            return modelLibrary;
        }

        /// <summary>
        /// Everything the Forest system draws: the vegetation, plus the built clutter that is far
        /// too numerous to give a scene object each.
        /// </summary>
        private static List<BeamNGForestInstance> ExtractVegetation(EditableWrp world)
        {
            var result = new List<BeamNGForestInstance>();
            var categories = new Dictionary<string, ReforgerObjectCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in world.GetNonDummyObjects())
            {
                if (string.IsNullOrEmpty(obj.Model))
                {
                    continue;
                }
                var matrix = obj.Transform.Matrix;
                var scale = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();

                var kind = Classify(obj.Model);
                if (kind != null)
                {
                    var yaw = -MathF.Atan2(matrix.M13, matrix.M33);
                    // Every instance keeps the whole transform Arma gave it, altitude included.
                    // Only rocks used to, and dropping it for trees and bushes was what sank them:
                    // an ODOL model is autocentred, so its origin sits halfway up the mesh, and
                    // standing that origin on the terrain buries the lower half of the plant.
                    // Measured on Malden, the forest sat 0.01 m above the ground to the item --
                    // exactly snapped, exactly wrong. Arma's own altitude already accounts for
                    // where the model is anchored.
                    result.Add(new BeamNGForestInstance(matrix.M41, matrix.M43, yaw, scale, kind.Value, obj.Model,
                        ToBeamNGRotation(matrix, scale), matrix.M42));
                    continue;
                }

                if (!categories.TryGetValue(obj.Model, out var category))
                {
                    category = WrpModelClassifier.Classify(obj.Model);
                    categories.Add(obj.Model, category);
                }
                if (!ClutterCategories.Contains(category) || IsRoadSurface(obj.Model))
                {
                    continue;
                }
                // Walls and kerbs follow the ground Arma put them on, and lean with it, so they keep
                // their own altitude and their full orientation rather than a heading on the terrain.
                result.Add(new BeamNGForestInstance(matrix.M41, matrix.M43, 0f, scale,
                    BeamNGForestKind.Clutter, obj.Model, ToBeamNGRotation(matrix, scale), matrix.M42));
            }
            return result;
        }

        private static bool IsDirtRoad(EditableArma3RoadTypeInfos infos)
        {
            var reference = (infos.Texture + ";" + infos.Material).ToLowerInvariant();
            return reference.Contains("dirt") || reference.Contains("path") || reference.Contains("track")
                || reference.Contains("gravel") || reference.Contains("mud") || reference.Contains("trail");
        }

        private static List<BeamNGPond> ExtractPonds(EditableWrp world)
        {
            var result = new List<BeamNGPond>();
            foreach (var obj in world.GetNonDummyObjects())
            {
                var model = obj.Model;
                if (string.IsNullOrEmpty(model) || !model.Contains("pond", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var digits = new string(Path.GetFileNameWithoutExtension(model).Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                if (!float.TryParse(digits, out var pondSize) || pondSize < 1f || pondSize > 500f)
                {
                    continue;
                }
                var matrix = obj.Transform.Matrix;
                var yaw = -MathF.Atan2(matrix.M13, matrix.M33);
                result.Add(new BeamNGPond(matrix.M41, matrix.M43, matrix.M42, pondSize, yaw));
            }
            return result;
        }

        private static readonly string[] BuildingKeywords =
        {
            "house", "building", "budova", "dum", "kostel", "church", "chapel", "barn", "stodola",
            "garage", "garaz", "shed", "hangar", "cottage", "chalet", "hotel", "shop", "store",
            "mill", "factory", "tovarna", "school", "skola", "station", "farm", "hospital", "office",
        };

        private static readonly string[] BuildingExclusions =
        {
            "wall", "fence", "lamp", "pylon", "pole", "sign", "bridge", "ruin", "wreck", "prop",
        };

        private List<BeamNGBuildingBox> ExtractBuildings(EditableWrp world, IProgressTaskUI ui)
        {
            var result = new List<BeamNGBuildingBox>();
            if (library == null)
            {
                return result;
            }
            var cache = new Dictionary<string, (float ox, float oz, float w, float d, float h)?>(StringComparer.OrdinalIgnoreCase);
            var unknownModels = 0;
            foreach (var obj in world.GetNonDummyObjects())
            {
                var model = obj.Model;
                if (string.IsNullOrEmpty(model))
                {
                    continue;
                }
                var name = model.ToLowerInvariant();
                if (!BuildingKeywords.Any(name.Contains) || BuildingExclusions.Any(name.Contains))
                {
                    continue;
                }
                if (!cache.TryGetValue(model, out var box))
                {
                    box = null;
                    try
                    {
                        var info = library.ReadModelInfoOnly(model);
                        if (info != null)
                        {
                            var width = info.BboxMax.X - info.BboxMin.X;
                            var depth = info.BboxMax.Z - info.BboxMin.Z;
                            var height = info.BboxMax.Y - info.BboxMin.Y;
                            // Keep plausible building volumes only (filters small props matched by keywords)
                            if (width >= 3f && depth >= 3f && height >= 2.5f && width <= 120f && depth <= 120f)
                            {
                                box = ((info.BboxMin.X + info.BboxMax.X) / 2f, (info.BboxMin.Z + info.BboxMax.Z) / 2f, width, depth, height);
                            }
                        }
                    }
                    catch
                    {
                        unknownModels++;
                    }
                    cache[model] = box;
                }
                if (box == null)
                {
                    continue;
                }
                var (ox, oz, w, d, h) = box.Value;
                var matrix = obj.Transform.Matrix;
                var yaw = -MathF.Atan2(matrix.M13, matrix.M33);
                var c = MathF.Cos(yaw);
                var s = MathF.Sin(yaw);
                var cx = matrix.M41 + (ox * c - oz * s);
                var cy = matrix.M43 + (ox * s + oz * c);
                result.Add(new BeamNGBuildingBox(cx, cy, yaw, w, d, h));
            }
            if (unknownModels > 0)
            {
                ui.Scope.WriteLine($"Buildings: {unknownModels} models could not be read (mod not loaded?)");
            }
            return result;
        }

        private static BeamNGForestKind? Classify(string model)
        {
            var name = model.ToLowerInvariant();
            if (name.Contains("clutter") || name.Contains("grass"))
            {
                return null;
            }
            if (name.Contains("bush") || name.Contains("\\b_"))
            {
                return BeamNGForestKind.Bush;
            }
            if (name.Contains("tree") || name.Contains("\\t_"))
            {
                return BeamNGForestKind.Tree;
            }
            if (name.Contains("rock") || name.Contains("stone") || name.Contains("boulder"))
            {
                return BeamNGForestKind.Rock;
            }
            return null;
        }
    }
}
