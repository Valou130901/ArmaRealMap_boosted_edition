using System.Text;
using CommandLine;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Arma3.IO;
using GameRealisticMap.Arma3.TerrainBuilder;
using GameRealisticMap.Configuration;
using GameRealisticMap.Reforger;
using GameRealisticMap.Reforger.Assets;
using GameRealisticMap.Reforger.Port;
using GameRealisticMap.Reporting;
using Pmad.ProgressTracking;

namespace GameRealisticMap.Arma3.CommandLine
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            try
            {
                return await Parser.Default.ParseArguments<GenerateObjectLayerOptions, GenerateWrpOptions, GenerateModOptions, GenerateTerrainBuilderOptions, GenerateReforgerOptions, PortModelsOptions, PortSweepOptions, PortLinkOptions>(args)
                  .MapResult(
                    (GenerateObjectLayerOptions opts) => GenerateObjectLayer(opts),
                    (GenerateWrpOptions opts) => GenerateWrp(opts),
                    (GenerateModOptions opts) => GenerateMod(opts),
                    (GenerateTerrainBuilderOptions opts) => GenerateTerrainBuilder(opts),
                    (GenerateReforgerOptions opts) => GenerateReforger(opts),
                    (PortModelsOptions opts) => PortModels(opts),
                    (PortSweepOptions opts) => PortSweep(opts),
                    (PortLinkOptions opts) => PortLink(opts),
                    errs => Task.FromResult(1));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return 2;
            }
        }

        private static async Task<int> GenerateTerrainBuilder(GenerateTerrainBuilderOptions opts)
        {
            using var workspace = await opts.CreateWorkspace();
            var generator = new Arma3TerrainBuilderGenerator(workspace.Assets, workspace.ProjectDrive, workspace.Sources);
            Directory.CreateDirectory(opts.TargetDirectory);
            await generator.GenerateTerrainBuilderFiles(workspace.Progress, workspace.MapConfig, opts.TargetDirectory);
            return 0;
        }

        private static async Task<int> GenerateReforger(GenerateReforgerOptions opts)
        {
            using var workspace = await opts.CreateWorkspace();
            var mapping = ReforgerAssetMapping.LoadFromFileOrDefault(opts.MappingFile);
            var generator = new ReforgerMapGenerator(workspace.Assets, workspace.ProjectDrive, workspace.Sources, mapping);
            Directory.CreateDirectory(opts.TargetDirectory);
            await generator.GenerateReforgerFiles(workspace.Progress, workspace.MapConfig, opts.TargetDirectory);
            return 0;
        }

        /// <summary>
        /// Opens the project drive without needing a map configuration: the model port only has to
        /// read binarized models and textures out of the mounted game and mod files.
        /// </summary>
        private static async Task<(ProjectDrive Drive, ModelInfoLibrary Models)> OpenProjectDrive()
        {
            var workspaceSettings = await WorkspaceSettings.Load();
            var projectDrive = workspaceSettings.CreateProjectDriveAutomatic();
            return (projectDrive, new ModelInfoLibrary(projectDrive));
        }

        /// <summary>
        /// Converts every terrain model of the project drive into the shared model library, so later
        /// exports of any map find them already done.
        /// </summary>
        private static async Task<int> PortSweep(PortSweepOptions opts)
        {
            using var progress = ConsoleProgessHelper.Create();

            var (projectDrive, models) = await OpenProjectDrive();
            var library = ReforgerModelLibrary.Load(opts.LibraryDirectory);
            var sweep = new ReforgerModelSweep(projectDrive, models.ReadODOL, library);

            if (opts.ListOnly)
            {
                var found = sweep.FindTerrainModels(progress);
                var todo = found.Count(m => !library.IsKnown(m));
                Console.WriteLine($"{found.Count} terrain models on the project drive, {todo} not yet converted.");
                return 0;
            }

            var report = sweep.Run(progress, opts.Limit);
            ReportPort(report, library);
            return 0;
        }

        /// <summary>
        /// Converts the models listed in a pack's port-worklist.csv into the shared model library.
        /// </summary>
        private static async Task<int> PortModels(PortModelsOptions opts)
        {
            var worklist = Path.Combine(opts.PackDirectory, "port-worklist.csv");
            if (!File.Exists(worklist))
            {
                Console.WriteLine($"No port-worklist.csv in '{opts.PackDirectory}'. Export the Reforger import pack first.");
                return 1;
            }

            using var progress = ConsoleProgessHelper.Create();

            var (projectDrive, models) = await OpenProjectDrive();
            var library = ReforgerModelLibrary.Load(opts.LibraryDirectory);

            // category;model;count
            var wanted = File.ReadLines(worklist)
                .Skip(1)
                .Select(line => line.Split(';'))
                .Where(parts => parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                .Where(parts => string.IsNullOrEmpty(opts.Category)
                    || string.Equals(parts[0], opts.Category, StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1])
                .ToList();

            if (wanted.Count == 0)
            {
                Console.WriteLine($"Nothing to convert in '{opts.PackDirectory}'"
                    + (string.IsNullOrEmpty(opts.Category) ? "." : $" for family '{opts.Category}'."));
                return 0;
            }

            var runner = new ModelPortRunner(models.ReadODOL, projectDrive.OpenFileIfExists, library);
            var report = runner.Port(wanted, progress, opts.Limit);
            ReportPort(report, library);
            return 0;
        }

        /// <summary>
        /// Reads an addon's resource database and attaches its prefabs to the library models whose
        /// name they match, closing the loop so exports place them without any manual mapping.
        /// </summary>
        private static Task<int> PortLink(PortLinkOptions opts)
        {
            var library = ReforgerModelLibrary.Load(opts.LibraryDirectory);
            var resources = ReforgerResourceDatabase.ReadFromAddon(opts.AddonDirectory);
            if (resources.Count == 0)
            {
                Console.WriteLine($"No {ReforgerResourceDatabase.FileName} found in '{opts.AddonDirectory}', or it is empty.");
                return Task.FromResult(1);
            }

            // Prefabs are matched on file name: the port names an obj after the Arma model, and the
            // user is expected to keep that name for the prefab built from it.
            var prefabs = resources
                .Where(r => r.Path.EndsWith(".et", StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => Path.GetFileNameWithoutExtension(r.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            Console.WriteLine($"{resources.Count} resources in the addon, {prefabs.Count} distinct prefabs.");

            var linked = 0;
            foreach (var entry in library.Entries.Where(e => e.IsConverted).ToList())
            {
                var name = Path.GetFileNameWithoutExtension(entry.Model);
                if (!prefabs.TryGetValue(name, out var prefab))
                {
                    continue;
                }
                if (string.Equals(entry.Prefab, prefab.ResourceName, StringComparison.Ordinal))
                {
                    continue;
                }
                Console.WriteLine($"  {entry.Model} -> {prefab.ResourceName}");
                if (!opts.DryRun)
                {
                    library.SetPrefab(entry.Model, prefab.ResourceName);
                }
                linked++;
            }

            if (opts.DryRun)
            {
                Console.WriteLine($"{linked} models would be linked (dry run, nothing written).");
                return Task.FromResult(0);
            }

            library.Save();
            Console.WriteLine($"{linked} models linked. Library now has {library.PrefabCount} prefabs out of {library.ConvertedCount} converted models.");
            return Task.FromResult(0);
        }

        private static void ReportPort(ModelPortReport report, ReforgerModelLibrary library)
        {
            foreach (var status in report.ByStatus.OrderByDescending(s => s.Value))
            {
                Console.WriteLine($"{status.Key}: {status.Value} models");
            }
            Console.WriteLine($"requested {report.Requested}, already known {report.AlreadyKnown}, converted {report.Converted}, failed {report.Failed}");
            Console.WriteLine($"library: {library.ConvertedCount} models, {library.PrefabCount} with a prefab, at {library.RootDirectory}");
        }

        private static async Task<int> GenerateObjectLayer(GenerateObjectLayerOptions opts)
        {
            using var workspace = await opts.CreateWorkspace();
            var generator = new Arma3TerrainBuilderGenerator(workspace.Assets, workspace.ProjectDrive, workspace.Sources);
            Directory.CreateDirectory(opts.TargetDirectory);
            await generator.GenerateOnlyOneLayer(workspace.Progress, workspace.MapConfig, opts.LayerName, opts.TargetDirectory);
            return 0;
        }

        private static async Task<int> GenerateWrp(GenerateWrpOptions opts)
        {
            using var workspace = await opts.CreateWorkspace();
            var generator = new Arma3MapGenerator(workspace.Assets, workspace.ProjectDrive, new NonePboCompilerFactory(), workspace.Sources);
            await generator.GenerateWrp(workspace.Progress, workspace.MapConfig, !opts.SkipPaa);
            return 0;
        }

        private static async Task<int> GenerateMod(GenerateModOptions opts)
        {
            if ( !OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Mod generation works only on Windows");
            }
            using var workspace = await opts.CreateWorkspace();
            var generator = new Arma3MapGenerator(workspace.Assets, workspace.ProjectDrive, new PboCompilerFactory(workspace.ProjectDrive), workspace.Sources);
            await generator.GenerateMod(workspace.Progress, workspace.MapConfig);
            return 0;
        }
    }
}