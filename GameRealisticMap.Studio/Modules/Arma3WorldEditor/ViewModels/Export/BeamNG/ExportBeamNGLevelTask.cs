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

        public ExportBeamNGLevelTask(EditableWrp world, string worldName, HugeImage<Rgb24>? satMap,
            HugeImage<Rgb24>? idMap, List<TerrainMaterialDefinition>? materials, List<EditableArma3Road>? roads,
            ModelInfoLibrary? library, string targetFile)
            : base(targetFile)
        {
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
                var grid = world.ToElevationGrid();
                var cellSize = world.CellSize * world.LandRangeX / world.TerrainRangeX;
                var vegetation = ExtractVegetation(world);
                var ponds = ExtractPonds(world);
                var buildings = ExtractBuildings(world, ui);
                ui.Scope.WriteLine($"Vegetation: {vegetation.Count}, ponds: {ponds.Count}, buildings: {buildings.Count} (from map objects)");
                var roadInputs = roads?
                    .Where(r => !r.IsRemoved && !r.RoadTypeInfos.IsPedestriansOnly && r.Path?.Points != null && r.Path.Points.Count >= 2)
                    .Select(r => new BeamNGRoadInput(r.Path.Points.ToList(), r.RoadTypeInfos.TextureWidth, IsDirtRoad(r.RoadTypeInfos)))
                    .ToList();
                var writer = new BeamNGLevelWriter(grid, cellSize, worldName, worldName, satMap, idMap, materials, roadInputs, vegetation, ponds, buildings);
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

        private static List<BeamNGForestInstance> ExtractVegetation(EditableWrp world)
        {
            var result = new List<BeamNGForestInstance>();
            foreach (var obj in world.GetNonDummyObjects())
            {
                if (string.IsNullOrEmpty(obj.Model))
                {
                    continue;
                }
                var kind = Classify(obj.Model);
                if (kind == null)
                {
                    continue;
                }
                var matrix = obj.Transform.Matrix;
                var yaw = -MathF.Atan2(matrix.M13, matrix.M33);
                var scale = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
                result.Add(new BeamNGForestInstance(matrix.M41, matrix.M43, yaw, scale, kind.Value));
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
