using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using GameRealisticMap.Arma3;

namespace GameRealisticMap.Studio.Modules.Arma3Data.Services
{
    [Export(typeof(IArma3ModsService))]
    internal class Arma3ModsService : IArma3ModsService
    {
        public static string[] CreatorDlcIds = new string[] { "1175380", "1042220" };

        public Arma3ModsService()
        {
            CreatorDlc = [
                new ModInfo("CDLC: Spearhead 1944", Path.Combine(Arma3ToolsHelper.GetArma3Path(), "SPE"), "1175380", "ww2", "https://drive.google.com/uc?export=download&id=19RTlnWngHzzYrVc1BFm6DqM3SnK44V1u"),
                new ModInfo("CDLC: Global Mobilization", Path.Combine(Arma3ToolsHelper.GetArma3Path(), "gm"), "1042220", "gm", "http://global-mobilization.com/samples/gm_terrain_substitutes.zip"),
                ];
        }

        public IReadOnlyList<ModInfo> CreatorDlc { get; }

        public ModInfo? GetMod(string steamId)
        {
            var cdlc = CreatorDlc.FirstOrDefault(d => d.SteamId == steamId);
            if (cdlc != null && Directory.Exists(cdlc.Path))
            {
                return cdlc;
            }
            var wk = Arma3ToolsHelper.GetArma3WorkshopPath();
            if (!string.IsNullOrEmpty(wk))
            {
                var d = Path.Combine(wk, steamId); 
                if ( Directory.Exists(d) )
                {
                    return new ModInfo(GetModName(d), Path.Combine(d, "addons"), Path.GetFileName(d));
                }
            }
            return null;
        }

        public List<ModInfo> GetModsList()
        {
            var cdlc = CreatorDlc.Where(d => Directory.Exists(d.Path));

            var mods = new List<ModInfo>();

            var wk = Arma3ToolsHelper.GetArma3WorkshopPath();
            if (!string.IsNullOrEmpty(wk))
            {
                mods.AddRange(Directory.GetDirectories(wk)
                    .Where(d => Directory.Exists(Path.Combine(d, "addons")))
                    .Select(d => new ModInfo(GetModName(d), Path.Combine(d, "addons"), Path.GetFileName(d))));
            }

            // Local mods: "@" prefixed folders in the game directory
            var a3 = Arma3ToolsHelper.GetArma3Path();
            if (!string.IsNullOrEmpty(a3) && Directory.Exists(a3))
            {
                mods.AddRange(Directory.GetDirectories(a3, "@*")
                    .Where(d => Directory.Exists(Path.Combine(d, "addons")))
                    .Select(d => new ModInfo(GetModName(d) + " (local)", Path.Combine(d, "addons"))));
            }

            return cdlc.Concat(mods.OrderBy(d => d.Name)).ToList();
        }

        private string GetModName(string path)
        {
            var config = Path.Combine(path, "meta.cpp");
            if (File.Exists(config))
            {
                var nameLine = File.ReadAllLines(config).FirstOrDefault(l => l.StartsWith("name ="));
                if (nameLine != null)
                {
                    return nameLine.Substring(6).Trim(' ', '"', ';');
                }
            }
            config = Path.Combine(path, "mod.cpp");
            if (File.Exists(config))
            {
                var nameLine = File.ReadAllLines(config).FirstOrDefault(l => l.StartsWith("name ="));
                if (nameLine != null)
                {
                    return nameLine.Substring(6).Trim(' ', '"', ';');
                }
            }
            return Path.GetFileName(path);
        }
    }
}
