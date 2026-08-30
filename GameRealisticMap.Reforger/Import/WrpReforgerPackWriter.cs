using System.Globalization;
using System.Text;
using BIS.WRP;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Reforger.Assets;
using GameRealisticMap.Reforger.Port;
using Pmad.Cartography;
using Pmad.Cartography.DataCells.FileFormats;
using Pmad.HugeImages;
using Pmad.HugeImages.Processing;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Reforger.Import
{
    /// <summary>
    /// Converts an existing Arma 3 world (wrp) into an import pack for the Enfusion World Editor:
    /// terrain heightmap, imagery, and object placements grouped by family.
    /// </summary>
    /// <remarks>
    /// Reforger worlds (.ent) can only be authored inside the Workbench, so this produces the
    /// intermediate files the Workbench consumes, not a ready-to-play mod. The placement files use
    /// the "Import Objects Extended" line format, which the bundled GRM Workbench plugin also reads.
    /// </remarks>
    public sealed class WrpReforgerPackWriter
    {
        // ~1.8 GB at 24 bpp - same cap as the Arma 3 Terrain Builder and OSM Reforger exports.
        private const int MaxImageSize = 25000;

        private readonly EditableWrp world;
        private readonly string worldName;
        private readonly ReforgerAssetMapping mapping;
        private readonly HugeImage<Rgb24>? satMap;
        private readonly HugeImage<Rgb24>? idMap;
        private readonly ReforgerModelLibrary? library;

        /// <param name="library">
        /// Optional shared model library. Models the user has already ported and linked to a prefab
        /// resolve through it, so a model only stays unmapped until it has been ported once.
        /// </param>
        public WrpReforgerPackWriter(EditableWrp world, string worldName, ReforgerAssetMapping mapping,
            HugeImage<Rgb24>? satMap = null, HugeImage<Rgb24>? idMap = null, ReforgerModelLibrary? library = null)
        {
            this.world = world;
            this.worldName = worldName;
            this.mapping = mapping;
            this.satMap = satMap;
            this.idMap = idMap;
            this.library = library;
        }

        public async Task<WrpReforgerPackStats> WriteAsync(string targetDirectory, IProgressScope progress)
        {
            Directory.CreateDirectory(targetDirectory);

            var grid = world.ToElevationGrid();
            var gridSize = grid.Size;
            var cellSize = world.CellSize * world.LandRangeX / world.TerrainRangeX;
            var sizeInMeters = cellSize * gridSize;

            progress.WriteLine(FormattableString.Invariant(
                $"{worldName}: {sizeInMeters:0.#} x {sizeInMeters:0.#} m, heightmap {gridSize} x {gridSize} at {cellSize:0.###} m/cell"));
            if ((gridSize & (gridSize - 1)) != 0)
            {
                progress.WriteLine($"WARNING: heightmap is {gridSize} cells, not a power of two. " +
                    "Enfusion terrains want a power-of-two face count, so the Workbench will resample it.");
            }

            WriteElevationAsc(progress, grid, targetDirectory);
            var (minElevation, maxElevation) = WriteHeightmapPng(progress, grid, targetDirectory);

            await WriteImagery(progress, targetDirectory);

            var stats = await WriteObjects(progress, targetDirectory);

            WriteModelInventory(stats, targetDirectory);
            WriteManifest(stats, targetDirectory, gridSize, cellSize, sizeInMeters, minElevation, maxElevation);
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "README.md"),
                WrpReforgerReadme.Create(worldName, gridSize, cellSize, sizeInMeters, minElevation, maxElevation, stats));

            return stats;
        }

        private void WriteElevationAsc(IProgressScope progress, GameRealisticMap.ElevationModel.ElevationGrid grid, string targetDirectory)
        {
            // Reforger terrain origin is (0,0), no easting shift like Terrain Builder
            using var writer = File.CreateText(Path.Combine(targetDirectory, "elevation.asc"));
            using var report = progress.CreatePercent("Elevation.AscFile");
            EsriAsciiHelper.SaveDataCell(writer, grid.ToDataCell(Coordinates.Zero), "-9999", report);
        }

        /// <summary>
        /// Writes the heightmap as a 16-bit grayscale PNG, the format the Enfusion terrain importer
        /// takes. Returns the altitude range the gray ramp was calibrated on.
        /// </summary>
        private static (float Min, float Max) WriteHeightmapPng(IProgressScope progress,
            GameRealisticMap.ElevationModel.ElevationGrid grid, string targetDirectory)
        {
            using var report = progress.CreateSingle("Elevation.HeightmapPng");

            var size = grid.Size;
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    var value = grid[x, y];
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            // Anchor the ramp on whole metres so the import settings stay round numbers
            var floor = MathF.Floor(min);
            var ceiling = MathF.Ceiling(max);
            var range = ceiling - floor;
            if (range < 1f)
            {
                range = 1f;
            }

            using var image = new Image<L16>(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // PNG row 0 is north, the elevation grid y axis points north
                    image[x, size - 1 - y] = new L16((ushort)Math.Clamp((grid[x, y] - floor) / range * 65535f, 0f, 65535f));
                }
            }
            image.SaveAsPng(Path.Combine(targetDirectory, "heightmap.png"));

            return (floor, floor + range);
        }

        private async Task WriteImagery(IProgressScope progress, string targetDirectory)
        {
            if (satMap != null)
            {
                await SaveHugeImage(progress, satMap, Path.Combine(targetDirectory, "satmap.png"));
            }
            if (idMap != null)
            {
                await SaveHugeImage(progress, idMap, Path.Combine(targetDirectory, "surfacemask.png"));
            }
        }

        private static async Task SaveHugeImage(IProgressScope progress, HugeImage<Rgb24> source, string filename)
        {
            var size = source.Size;
            if (size.Width > MaxImageSize || size.Height > MaxImageSize)
            {
                progress.WriteLine($"WARNING: {Path.GetFileName(filename)} would be {size.Width}x{size.Height}px, " +
                    $"above the {MaxImageSize}px export cap: skipped.");
                return;
            }
            using var report = progress.CreateSingle(Path.GetFileName(filename));
            using var image = new Image<Rgb24>(size.Width, size.Height);
            await image.MutateAsync(async i =>
            {
                await i.DrawHugeImageAsync(source, new Point(0, 0), new Point(0, 0), size);
            });
            await image.SaveAsPngAsync(filename);
        }

        private async Task<WrpReforgerPackStats> WriteObjects(IProgressScope progress, string targetDirectory)
        {
            var objectsDirectory = Path.Combine(targetDirectory, "objects");
            Directory.CreateDirectory(objectsDirectory);

            // Drop placement files of a previous export: a family that no longer has any object
            // would otherwise leave a stale file behind, which the import would happily replay
            foreach (var stale in Directory.GetFiles(objectsDirectory, "*.csv"))
            {
                File.Delete(stale);
            }

            var stats = new WrpReforgerPackStats();
            var lines = new Dictionary<ReforgerObjectCategory, List<string>>();
            // The mapping lookup is the expensive part and models repeat heavily across a map
            var prefabCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var categoryCache = new Dictionary<string, ReforgerObjectCategory>(StringComparer.OrdinalIgnoreCase);

            using (var report = progress.CreateSingle("Objects"))
            {
                foreach (var obj in world.GetNonDummyObjects())
                {
                    var model = obj.Model;
                    if (string.IsNullOrEmpty(model))
                    {
                        continue;
                    }

                    if (!categoryCache.TryGetValue(model, out var category))
                    {
                        category = WrpModelClassifier.Classify(model);
                        categoryCache.Add(model, category);
                    }
                    if (category == ReforgerObjectCategory.Clutter)
                    {
                        continue; // Ground clutter is generated by the Reforger terrain materials, never placed
                    }

                    if (!prefabCache.TryGetValue(model, out var prefab))
                    {
                        // The built-in mapping wins; the user's own ported models fill the gaps
                        prefab = mapping.Resolve(Path.GetFileNameWithoutExtension(model))
                            ?? library?.GetPrefab(model);
                        prefabCache.Add(model, prefab);
                    }

                    var placement = ReforgerObject.FromWrpMatrix(obj.Transform.Matrix, prefab, model);
                    if (!placement.IsValid)
                    {
                        stats.InvalidObjects++;
                        continue;
                    }

                    if (!lines.TryGetValue(category, out var list))
                    {
                        list = new List<string>();
                        lines.Add(category, list);
                    }
                    list.Add(placement.ToCsvLine());
                    stats.Add(model, category, prefab);
                }
            }

            foreach (var pair in lines)
            {
                var name = WrpModelClassifier.GetLayerName(pair.Key);
                await File.WriteAllLinesAsync(Path.Combine(objectsDirectory, name + ".csv"), pair.Value);
            }

            progress.WriteLine(FormattableString.Invariant(
                $"Objects: {stats.TotalObjects} placed ({stats.MappedObjects} with a prefab, {stats.UnmappedObjects} without), {stats.Models.Count} distinct models, {stats.InvalidObjects} skipped as invalid"));

            return stats;
        }

        /// <summary>
        /// Full model inventory plus the port worklist: the distinct models with no Reforger prefab,
        /// which is exactly what the p3d to xob pipeline has to produce.
        /// </summary>
        private static void WriteModelInventory(WrpReforgerPackStats stats, string targetDirectory)
        {
            var inventory = new StringBuilder();
            inventory.AppendLine("category;model;count;prefab");
            foreach (var model in stats.Models.Values.OrderBy(m => m.Category).ThenByDescending(m => m.Count))
            {
                inventory.AppendLine(FormattableString.Invariant(
                    $"{WrpModelClassifier.GetLayerName(model.Category)};{model.Model};{model.Count};{model.Prefab}"));
            }
            File.WriteAllText(Path.Combine(targetDirectory, "models.csv"), inventory.ToString());

            var worklist = new StringBuilder();
            worklist.AppendLine("category;model;count");
            foreach (var model in stats.UnmappedModels)
            {
                worklist.AppendLine(FormattableString.Invariant(
                    $"{WrpModelClassifier.GetLayerName(model.Category)};{model.Model};{model.Count}"));
            }
            File.WriteAllText(Path.Combine(targetDirectory, "port-worklist.csv"), worklist.ToString());
        }

        /// <summary>
        /// Machine-readable pack description for the GRM Workbench plugin. Deliberately a flat
        /// key=value text file: Enforce Script has no dependable JSON reader in plugin context.
        /// </summary>
        private void WriteManifest(WrpReforgerPackStats stats, string targetDirectory, int gridSize,
            float cellSize, float sizeInMeters, float minElevation, float maxElevation)
        {
            var manifest = new StringBuilder();
            manifest.AppendLine("# Game Realistic Map - Arma 3 to Arma Reforger import pack");
            manifest.AppendLine("formatVersion=1");
            manifest.AppendLine(FormattableString.Invariant($"world={worldName}"));
            manifest.AppendLine(FormattableString.Invariant($"sizeInMeters={sizeInMeters:0.###}"));
            manifest.AppendLine(FormattableString.Invariant($"gridSize={gridSize}"));
            manifest.AppendLine(FormattableString.Invariant($"cellSize={cellSize:0.#####}"));
            manifest.AppendLine(FormattableString.Invariant($"minElevation={minElevation:0.###}"));
            manifest.AppendLine(FormattableString.Invariant($"maxElevation={maxElevation:0.###}"));
            manifest.AppendLine("heightmapAsc=elevation.asc");
            manifest.AppendLine("heightmapPng=heightmap.png");
            if (satMap != null)
            {
                manifest.AppendLine("satmap=satmap.png");
            }
            if (idMap != null)
            {
                manifest.AppendLine("surfacemask=surfacemask.png");
            }
            foreach (var category in stats.Models.Values.Select(m => m.Category).Distinct().OrderBy(c => c))
            {
                var name = WrpModelClassifier.GetLayerName(category);
                manifest.AppendLine(FormattableString.Invariant($"layer={name}|objects/{name}.csv|{stats.CountIn(category)}"));
            }
            File.WriteAllText(Path.Combine(targetDirectory, "grm-pack.txt"), manifest.ToString());
        }
    }
}
