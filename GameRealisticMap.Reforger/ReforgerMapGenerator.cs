using GameRealisticMap.Arma3;
using GameRealisticMap.Arma3.Assets;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Arma3.Imagery;
using GameRealisticMap.Arma3.IO;
using GameRealisticMap.Arma3.TerrainBuilder;
using GameRealisticMap.Configuration;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.ManMade.Places;
using GameRealisticMap.Osm;
using GameRealisticMap.Reforger.Assets;
using Pmad.Cartography;
using Pmad.Cartography.DataCells.FileFormats;
using Pmad.HugeImages;
using Pmad.HugeImages.Processing;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Reforger
{
    /// <summary>
    /// Generates an import pack for the Arma Reforger / Enfusion World Editor:
    /// a heightmap (ESRI ASCII), surface mask and satellite images, and per-layer entity
    /// placement CSV files for the "Import Objects Extended" Workbench plugin.
    /// </summary>
    /// <remarks>
    /// Arma Reforger worlds (.ent) cannot be written outside the official Enfusion Workbench, so
    /// unlike the Arma 3 generator this produces intermediate files the user imports, exactly like
    /// the Arma 3 Terrain Builder export. Object generation reuses the Arma 3 pipeline; each model
    /// is then translated to a Reforger prefab through <see cref="ReforgerAssetMapping"/>.
    /// </remarks>
    public sealed class ReforgerMapGenerator
    {
        // ~1.8 GB at 24 bpp - matches the Arma 3 Terrain Builder export cap.
        private const int MaxImageSize = 25000;

        private readonly IArma3RegionAssets assets;
        private readonly ProjectDrive projectDrive;
        private readonly ISourceLocations sources;
        private readonly ReforgerAssetMapping mapping;

        public ReforgerMapGenerator(IArma3RegionAssets assets, ProjectDrive projectDrive, ISourceLocations sources, ReforgerAssetMapping? mapping = null)
        {
            this.assets = assets;
            this.projectDrive = projectDrive;
            this.sources = sources;
            this.mapping = mapping ?? ReforgerAssetMapping.LoadDefault();
        }

        public async Task<string?> GenerateReforgerFiles(IProgressScope progress, Arma3MapConfig config, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            var osmSource = await LoadOsmData(progress, config);
            if (progress.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var context = CreateBuildContext(progress, config, osmSource);

            ExportElevation(progress, context, targetDirectory);
            if (progress.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            await ExportImagery(progress, config, context, targetDirectory);
            if (progress.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var stats = await ExportObjects(progress, config, context, targetDirectory);
            context.DisposeHugeImages();
            if (progress.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "README.md"), ReforgerReadme.Create(config, stats));

            return GameConfigGenerator.GetFreindlyName(config, context.GetData<CitiesData>());
        }

        private BuildContext CreateBuildContext(IProgressScope progress, Arma3MapConfig config, IOsmDataSource osmSource)
        {
            var builders = new BuildersCatalog(assets, sources);
            return new BuildContext(builders, progress, config.TerrainArea, osmSource, config.Imagery);
        }

        private async Task<IOsmDataSource> LoadOsmData(IProgressScope progress, Arma3MapConfig config)
        {
            var loader = new OsmDataOverPassLoader(progress, sources);
            return await loader.Load(config.TerrainArea);
        }

        private static void ExportElevation(IProgressScope progress, BuildContext context, string targetDirectory)
        {
            // Reforger terrain origin is (0,0); no easting shift like Terrain Builder.
            var grid = context.GetData<ElevationData>().Elevation.ToDataCell(Coordinates.Zero);
            using var writer = File.CreateText(Path.Combine(targetDirectory, "elevation.asc"));
            using var report = progress.CreatePercent("Elevation.AscFile");
            EsriAsciiHelper.SaveDataCell(writer, grid, "-9999", report);
        }

        private async Task ExportImagery(IProgressScope progress, Arma3MapConfig config, BuildContext context, string targetDirectory)
        {
            var size = config.GetSatMapSize().Width;
            if (size > MaxImageSize)
            {
                progress.WriteLine($"WARNING: imagery is {size}x{size}px, above the {MaxImageSize}px export cap. " +
                    "Surface/satellite images are skipped - use a smaller cell size or export imagery separately.");
                return;
            }

            var source = new ImagerySource(assets.Materials, progress, projectDrive, config, context);
            using (var idMap = await source.CreateIdMap())
            {
                await SaveHugeImage(progress, idMap, Path.Combine(targetDirectory, "surfacemask.png"));
            }
            using (var satMap = await source.CreateSatMap())
            {
                await SaveHugeImage(progress, satMap, Path.Combine(targetDirectory, "satmap.png"));
            }
        }

        private static async Task SaveHugeImage(IProgressScope progress, HugeImage<Rgba32> himage, string filename)
        {
            using var report = progress.CreateSingle(Path.GetFileName(filename));
            var size = himage.Size;
            using var image = new Image<Rgb24>(size.Width, size.Height);
            await image.MutateAsync(async i =>
            {
                await i.DrawHugeImageAsync(himage, new Point(0, 0), new Point(0, 0), size);
            });
            await image.SaveAsPngAsync(filename);
        }

        private async Task<ReforgerExportStats> ExportObjects(IProgressScope progress, Arma3MapConfig config, BuildContext context, string targetDirectory)
        {
            var objectsDirectory = Path.Combine(targetDirectory, "objects");
            Directory.CreateDirectory(objectsDirectory);

            var grid = context.GetData<ElevationData>().Elevation;
            var generators = new Arma3LayerGeneratorCatalog(assets);
            var stats = new ReforgerExportStats();

            using var scope = progress.CreateScope("Objects", generators.Generators.Count);
            foreach (var tb in generators.Generators)
            {
                var name = GetLayerName(tb);
                var entries = (await tb.Generate(config, context, scope)).Where(e => e.IsValid).ToList();
                if (entries.Count == 0)
                {
                    continue;
                }

                var lines = new List<string>(entries.Count);
                foreach (var entry in entries)
                {
                    var prefab = mapping.Resolve(entry.Model.Name);
                    if (prefab == null)
                    {
                        stats.AddUnmapped(name, entry.Model.Name);
                    }
                    lines.Add(ReforgerObject.FromTerrainBuilderObject(entry, grid, prefab).ToCsvLine());
                }

                await File.WriteAllLinesAsync(Path.Combine(objectsDirectory, name + ".csv"), lines);
                stats.AddLayer(name, lines.Count);
            }

            return stats;
        }

        private static string GetLayerName(ITerrainBuilderLayerGenerator tb)
        {
            return tb.GetType().Name.Replace("Generator", "").ToLowerInvariant();
        }
    }
}
