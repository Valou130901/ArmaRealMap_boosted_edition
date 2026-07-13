using Gemini.Framework.Commands;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.Commands
{
    [CommandDefinition]
    public class ImportGameMapCommandDefinition : CommandDefinition
    {
        public const string CommandName = "ImportGameMap";

        public override string Name
        {
            get { return CommandName; }
        }

        public override string Text
        {
            get { return "Import a map from game or mods..."; }
        }

        public override string ToolTip
        {
            get { return "Extract an existing map (Malden, Stratis, mods...) to edit it and generate a modified version"; }
        }
    }
}
