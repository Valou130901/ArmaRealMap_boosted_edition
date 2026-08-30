using System.Text;

namespace GameRealisticMap.Reforger.Import
{
    /// <summary>Import instructions shipped inside the pack produced by <see cref="WrpReforgerPackWriter"/>.</summary>
    internal static class WrpReforgerReadme
    {
        public static string Create(string worldName, int gridSize, float cellSize, float sizeInMeters,
            float minElevation, float maxElevation, WrpReforgerPackStats stats)
        {
            var sb = new StringBuilder();
            var range = maxElevation - minElevation;

            sb.AppendLine(FormattableString.Invariant($"# {worldName} - Arma 3 to Arma Reforger import pack"));
            sb.AppendLine();
            sb.AppendLine("Produced by Game Realistic Map from an existing Arma 3 world (wrp). Reforger worlds");
            sb.AppendLine("(`.ent`) can only be authored inside the Enfusion Workbench, so this is an import pack,");
            sb.AppendLine("not a ready-to-play mod.");
            sb.AppendLine();

            sb.AppendLine("## Terrain");
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"- Size          : {sizeInMeters:0.#} x {sizeInMeters:0.#} m"));
            sb.AppendLine(FormattableString.Invariant($"- Heightmap     : {gridSize} x {gridSize} vertices"));
            sb.AppendLine(FormattableString.Invariant($"- Cell size     : {cellSize:0.###} m"));
            sb.AppendLine(FormattableString.Invariant($"- Altitude range: {minElevation:0.##} m to {maxElevation:0.##} m ({range:0.##} m span)"));
            sb.AppendLine();
            sb.AppendLine("`heightmap.png` is a 16-bit grayscale image: gray 0 is");
            sb.AppendLine(FormattableString.Invariant($"{minElevation:0.##} m and gray 65535 is {maxElevation:0.##} m, so the import height scale is {range:0.##} m."));
            sb.AppendLine("`elevation.asc` is the same data as a lossless ESRI ASCII grid, with real altitudes in");
            sb.AppendLine("metres and no rescaling - prefer it if the Workbench accepts it, it needs no calibration.");
            sb.AppendLine();

            sb.AppendLine("## Imagery");
            sb.AppendLine();
            sb.AppendLine("- `satmap.png`      : colour satellite image, use as the base colour layer.");
            sb.AppendLine("- `surfacemask.png` : one flat colour per Arma ground material. Reforger has no automatic");
            sb.AppendLine("  mask import, so use it as the reference to lay out your terrain material layers.");
            sb.AppendLine();

            sb.AppendLine("## Objects");
            sb.AppendLine();
            sb.AppendLine("Placements live in `objects/`, one file per family, in the *Import Objects Extended* line");
            sb.AppendLine("format: `[\"{GUID}path.et\"] PosX PosY PosZ Pitch Yaw Roll Scale`.");
            sb.AppendLine("Coordinates are absolute Enfusion world coordinates in metres (Y is altitude ASL), so set");
            sb.AppendLine("`terrainSnapDistance` to -1 unless you deliberately want objects re-snapped to the ground.");
            sb.AppendLine();
            sb.AppendLine("Two ways to place them:");
            sb.AppendLine();
            sb.AppendLine("1. **GRM Workbench plugin** (bundled in `plugin/`): point it at `grm-pack.txt` and it walks");
            sb.AppendLine("   every layer on its own, creating one editor layer per family.");
            sb.AppendLine("2. **Import Objects Extended** (https://github.com/Til-Weimann/import-objects-extended):");
            sb.AppendLine("   set `DataPath` to one `.csv` at a time and click `Place`.");
            sb.AppendLine();
            sb.AppendLine("Lines with no leading `\"{GUID}...\"` have no Reforger prefab yet: assign a prefab pool for");
            sb.AppendLine("that layer before placing it, or extend the mapping and export again.");
            sb.AppendLine();

            sb.AppendLine("### Families");
            sb.AppendLine();
            sb.AppendLine("| Family | Objects | With a prefab | Distinct models |");
            sb.AppendLine("|--------|---------|---------------|-----------------|");
            foreach (var category in stats.Models.Values.Select(m => m.Category).Distinct().OrderBy(c => c))
            {
                var models = stats.ByCategory(category).ToList();
                var total = models.Sum(m => m.Count);
                var mapped = models.Where(m => m.IsMapped).Sum(m => m.Count);
                sb.AppendLine(FormattableString.Invariant(
                    $"| {WrpModelClassifier.GetLayerName(category)} | {total} | {mapped} | {models.Count} |"));
            }
            sb.AppendLine(FormattableString.Invariant(
                $"| **total** | **{stats.TotalObjects}** | **{stats.MappedObjects}** | **{stats.Models.Count}** |"));
            sb.AppendLine();
            if (stats.InvalidObjects > 0)
            {
                sb.AppendLine(FormattableString.Invariant(
                    $"{stats.InvalidObjects} objects were skipped: their wrp transform decomposed to a NaN angle."));
                sb.AppendLine();
            }

            sb.AppendLine("## Model port worklist");
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant(
                $"`models.csv` lists every model and the prefab it resolved to. `port-worklist.csv` lists the"));
            sb.AppendLine(FormattableString.Invariant(
                $"{stats.UnmappedModels.Count()} distinct models that still have no Reforger equivalent, ordered by"));
            sb.AppendLine("how many instances depend on them - convert those first.");
            sb.AppendLine();
            sb.AppendLine("Top of the worklist:");
            sb.AppendLine();
            sb.AppendLine("| Model | Family | Instances |");
            sb.AppendLine("|-------|--------|-----------|");
            foreach (var model in stats.UnmappedModels.Take(40))
            {
                sb.AppendLine(FormattableString.Invariant(
                    $"| {model.Model} | {WrpModelClassifier.GetLayerName(model.Category)} | {model.Count} |"));
            }
            sb.AppendLine();

            sb.AppendLine("## Not converted");
            sb.AppendLine();
            sb.AppendLine("Roads, water bodies, ambient sounds and material layer setup are not part of this pack.");
            sb.AppendLine("Roads have to be rebuilt with the Road Tool inside the World Editor.");
            sb.AppendLine();
            sb.AppendLine("## Licensing");
            sb.AppendLine();
            sb.AppendLine("Terrain shape and object placements are data extracted from an Arma 3 map you own.");
            sb.AppendLine("Redistributing Bohemia Interactive assets outside Arma 3 is governed by their licence -");
            sb.AppendLine("check it before publishing anything built from this pack.");

            return sb.ToString();
        }
    }
}
