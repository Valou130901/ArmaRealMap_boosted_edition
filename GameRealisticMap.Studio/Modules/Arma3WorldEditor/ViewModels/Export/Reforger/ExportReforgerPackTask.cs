using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BIS.WRP;
using GameRealisticMap.Arma3.IO;
using GameRealisticMap.Arma3.TerrainBuilder;
using GameRealisticMap.Reforger.Assets;
using GameRealisticMap.Reforger.Import;
using GameRealisticMap.Reforger.Port;
using GameRealisticMap.Studio.Modules.Reporting;
using GameRealisticMap.Studio.Toolkit;
using Pmad.HugeImages;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.Reforger
{
    /// <summary>
    /// Converts the world currently open in the editor into an Arma Reforger import pack: heightmap,
    /// imagery, object placements per family, and the GRM Workbench plugin that reads them.
    /// </summary>
    /// <remarks>
    /// Models with no Reforger prefab are converted into the shared
    /// <see cref="ReforgerModelLibrary"/> rather than into the pack, so each one is converted once
    /// for good and later exports of any map reuse it.
    /// </remarks>
    internal class ExportReforgerPackTask : IProcessTask
    {
        private readonly EditableWrp world;
        private readonly string worldName;
        private readonly string targetDirectory;
        private readonly HugeImage<Rgb24>? satMap;
        private readonly HugeImage<Rgb24>? idMap;
        private readonly ModelInfoLibrary? library;
        private readonly ProjectDrive? projectDrive;

        public ExportReforgerPackTask(EditableWrp world, string worldName, HugeImage<Rgb24>? satMap,
            HugeImage<Rgb24>? idMap, ModelInfoLibrary? library, ProjectDrive? projectDrive, string targetDirectory)
        {
            this.world = world;
            this.worldName = worldName;
            this.satMap = satMap;
            this.idMap = idMap;
            this.library = library;
            this.projectDrive = projectDrive;
            this.targetDirectory = targetDirectory;
        }

        public string Title => "Export Arma Reforger import pack";

        public bool Prompt => true;

        public async Task Run(IProgressTaskUI ui)
        {
            var modelLibrary = ReforgerModelLibrary.Load();
            ui.Scope.WriteLine($"Model library: {modelLibrary.ConvertedCount} models converted, " +
                $"{modelLibrary.PrefabCount} linked to a Reforger prefab ({modelLibrary.RootDirectory})");

            WrpReforgerPackStats stats;
            try
            {
                var mapping = ReforgerAssetMapping.LoadDefault();
                var writer = new WrpReforgerPackWriter(world, worldName, mapping, satMap, idMap, modelLibrary);
                stats = await writer.WriteAsync(targetDirectory, ui.Scope);

                ReforgerWorkbenchPlugin.WriteTo(targetDirectory, worldName);
            }
            finally
            {
                satMap?.Dispose();
                idMap?.Dispose();
            }

            PortMissingModels(ui, stats, modelLibrary);

            ui.Scope.WriteLine($"Pack written to {targetDirectory}");
            ui.AddSuccessAction(() => ShellHelper.OpenUri(targetDirectory), "Open folder");
            ui.AddSuccessAction(() => ShellHelper.OpenUri(Path.Combine(targetDirectory, "README.md")), "Open README");
            ui.AddSuccessAction(() => ShellHelper.OpenUri(modelLibrary.RootDirectory), "Open model library");
        }

        /// <summary>
        /// Converts the models this map needs and nothing resolves yet, then builds their FBX.
        /// </summary>
        /// <remarks>
        /// This is everything that can be automated outside Bohemia's tools. Models already in the
        /// shared library are skipped, so only the first export of a given set of assets is slow.
        /// The remaining hops, FBX to xob and the prefabs, only exist inside the Workbench.
        /// </remarks>
        private void PortMissingModels(IProgressTaskUI ui, WrpReforgerPackStats stats, ReforgerModelLibrary modelLibrary)
        {
            var missing = stats.UnmappedModels.Select(m => m.Model).ToList();
            if (missing.Count == 0)
            {
                return;
            }
            if (library == null || projectDrive == null)
            {
                ui.Scope.WriteLine($"{missing.Count} models have no Reforger prefab, but the project drive is not " +
                    "available: skipping the model conversion. See port-worklist.csv.");
                return;
            }

            var runner = new ModelPortRunner(library.ReadODOL, projectDrive.OpenFileIfExists, modelLibrary);
            var report = runner.Port(missing, ui.Scope);

            foreach (var status in report.ByStatus.OrderByDescending(s => s.Value))
            {
                ui.Scope.WriteLine($"  {status.Key}: {status.Value} models");
            }

            BlenderRunner.ConvertLibrary(modelLibrary, ui.Scope);

            if (modelLibrary.PrefabCount == 0)
            {
                ui.Scope.WriteLine("None of these models has a Reforger prefab yet. In the Workbench: import the fbx " +
                    "folder, build a prefab per model, then run 'grma3 portlink --addon <your addon>' so the next " +
                    "export places them automatically.");
            }
        }
    }
}
