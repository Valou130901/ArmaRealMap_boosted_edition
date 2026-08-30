using System;
using Gemini.Framework.Commands;

namespace GameRealisticMap.Studio.Modules.AssetBrowser.Commands
{
    [CommandDefinition]
    public class BuildReforgerLibraryCommandDefinition : CommandDefinition
    {
        public const string CommandName = "Tools.BuildReforgerModelLibrary";

        public override string Name => CommandName;

        public override string Text => "Build Arma Reforger model library";

        public override Uri IconSource => new Uri("pack://application:,,,/GameRealisticMap.Studio;component/Resources/Icons/Objects.png");

        public override string ToolTip =>
            "Convert every terrain object of the project drive to OBJ once, for reuse by any Arma Reforger export";
    }
}
