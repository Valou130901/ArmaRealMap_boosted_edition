using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Caliburn.Micro;
using GameRealisticMap.Reforger.Port;
using GameRealisticMap.Studio.Modules.Arma3Data;
using GameRealisticMap.Studio.Modules.Reporting;
using GameRealisticMap.Studio.Toolkit;
using Gemini.Framework.Commands;

namespace GameRealisticMap.Studio.Modules.AssetBrowser.Commands
{
    /// <summary>
    /// Sweeps the project drive and converts every terrain object to OBJ in the shared Reforger
    /// model library, so map exports never have to convert anything again.
    /// </summary>
    internal class BuildReforgerLibraryTask : IProcessTask
    {
        private readonly IArma3DataModule arma3Data;

        public BuildReforgerLibraryTask(IArma3DataModule arma3Data)
        {
            this.arma3Data = arma3Data;
        }

        public string Title => "Build Arma Reforger model library";

        public bool Prompt => true;

        public Task Run(IProgressTaskUI ui)
        {
            var library = ReforgerModelLibrary.Load();
            ui.Scope.WriteLine($"Library: {library.RootDirectory}");
            ui.Scope.WriteLine($"Already converted: {library.ConvertedCount} models, {library.PrefabCount} with a prefab");

            var sweep = new ReforgerModelSweep(arma3Data.ProjectDrive, arma3Data.Library.ReadODOL, library);
            var report = sweep.Run(ui.Scope);

            foreach (var status in report.ByStatus.OrderByDescending(s => s.Value))
            {
                ui.Scope.WriteLine($"  {status.Key}: {status.Value} models");
            }
            BlenderRunner.ConvertLibrary(library, ui.Scope);

            ui.Scope.WriteLine($"Library now holds {library.ConvertedCount} models.");
            ui.Scope.WriteLine("Next, in the Workbench: import the fbx folder, build the prefabs, " +
                "then 'grma3 portlink --addon <your addon>' to link them back.");

            ui.AddSuccessAction(() => ShellHelper.OpenUri(library.RootDirectory), "Open library folder");
            return Task.CompletedTask;
        }
    }

    [CommandHandler]
    public class BuildReforgerLibraryCommandHandler : CommandHandlerBase<BuildReforgerLibraryCommandDefinition>
    {
        public override Task Run(Command command)
        {
            ProgressToolHelper.Start(new BuildReforgerLibraryTask(IoC.Get<IArma3DataModule>()));
            return Task.CompletedTask;
        }
    }
}
