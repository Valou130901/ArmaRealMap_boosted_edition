using System.ComponentModel.Composition;
using GameRealisticMap.Studio.Modules.Arma3WorldEditor.Commands;
using Gemini.Framework.Menus;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor
{
    public static class MenuDefinitions
    {
        [Export]
        public static readonly MenuItemGroupDefinition FileImportMenuGroup = new MenuItemGroupDefinition(
            Gemini.Modules.MainMenu.MenuDefinitions.FileMenu, 8);

        [Export]
        public static readonly MenuItemDefinition ImportGameMapMenuItem = new CommandMenuItemDefinition<ImportGameMapCommandDefinition>(
            FileImportMenuGroup, 0);
    }
}
