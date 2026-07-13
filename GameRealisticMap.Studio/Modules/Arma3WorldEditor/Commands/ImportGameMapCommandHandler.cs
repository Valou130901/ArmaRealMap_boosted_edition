using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Caliburn.Micro;
using GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Import;
using Gemini.Framework.Commands;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.Commands
{
    [CommandHandler]
    public class ImportGameMapCommandHandler : CommandHandlerBase<ImportGameMapCommandDefinition>
    {
        private readonly IWindowManager _windowManager;

        [ImportingConstructor]
        public ImportGameMapCommandHandler(IWindowManager windowManager)
        {
            _windowManager = windowManager;
        }

        public override Task Run(Command command)
        {
            return _windowManager.ShowDialogAsync(new GameMapImporterViewModel());
        }
    }
}
