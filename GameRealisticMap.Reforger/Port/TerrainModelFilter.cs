using System.Text.RegularExpressions;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>
    /// Keeps only the Arma 3 models that can plausibly be placed on a terrain.
    /// </summary>
    /// <remarks>
    /// A full Arma 3 install holds about 18 000 models for 14 GB, but vehicles, characters and
    /// weapons account for most of that volume and never appear in a wrp. Filtering them out leaves
    /// roughly 7 900 models for 1.6 GB, which is what makes a one-shot sweep of the project drive
    /// practical at all.
    /// </remarks>
    public static class TerrainModelFilter
    {
        private static readonly Regex Keep = new(
            @"(structures_f|plants_f|rocks_f|vegetation_f|signs_f|roads_f|walls|fences|furniture|misc_f|props)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex Drop = new(
            @"(characters_f|weapons_f|air_f|armor_f|boat_f|cars_f|soft_f|static_f|supplies_f|ammo|anims_f|dubbing|missions_f|ui_f|editorprev|\\proxies\\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>True when the model is worth converting for terrain use.</summary>
        public static bool IsTerrainModel(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                return false;
            }
            return Keep.IsMatch(modelPath) && !Drop.IsMatch(modelPath);
        }
    }
}
