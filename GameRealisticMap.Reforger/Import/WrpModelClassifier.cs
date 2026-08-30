namespace GameRealisticMap.Reforger.Import
{
    /// <summary>
    /// Sorts Arma 3 model paths into <see cref="ReforgerObjectCategory"/> families.
    /// </summary>
    /// <remarks>
    /// Classification is driven by the path first (Bohemia ships models in family folders such as
    /// <c>a3\plants_f\Tree</c> or <c>a3\structures_f\Walls</c>, which is far more reliable than the
    /// file name) and falls back to name keywords for community content that does not follow the
    /// convention. Rules are ordered from most to least specific; the first match wins.
    /// </remarks>
    public static class WrpModelClassifier
    {
        private sealed record Rule(ReforgerObjectCategory Category, string[] PathParts, string[] Keywords);

        /// <summary>
        /// Checked before everything else, on path and name at once. These are the cases where the
        /// folder a model ships in genuinely lies about what it is, so the generic rules below get
        /// them wrong: Bohemia files vineyard fencing under industrial agriculture, and pavements
        /// under civilian structures.
        /// </summary>
        private static readonly Rule[] Overrides =
        {
            new(ReforgerObjectCategory.Fence, Array.Empty<string>(),
                new[] { "vineyardfence", "vineyard_fence" }),

            new(ReforgerObjectCategory.Infrastructure,
                new[] { @"\pavements\" },
                new[] { "pavement", "sidewalk" }),
        };

        // Checked in order: a model matching several families is assigned the first one listed.
        private static readonly Rule[] Rules =
        {
            new(ReforgerObjectCategory.Clutter,
                new[] { @"\clutter\", @"\plants_f\clutter" },
                new[] { "clutter", "grass_", "_grass", "weed", "seaweed" }),

            // Only genuine water surfaces. Note that a3\rocks_f\water holds rocks that sit in the
            // sea, not water: those are left to the rock rule below.
            new(ReforgerObjectCategory.Water,
                new[] { @"\water_f\" },
                new[] { "pond", "waterhole", "fountain" }),

            new(ReforgerObjectCategory.Rock,
                new[] { @"\rocks_f\", @"\rocks\", @"\stones\", @"\limestone\" },
                new[] { "rock", "stone", "boulder", "kamen", "cliff" }),

            new(ReforgerObjectCategory.Tree,
                new[] { @"\plants_f\tree", @"\tree\", @"\trees\" },
                new[] { @"\t_", "tree", "strom", "palm", "picea", "pinus", "quercus", "olea", "cupressus" }),

            new(ReforgerObjectCategory.Bush,
                new[] { @"\plants_f\bush", @"\bush\", @"\bushes\", @"\plants_f\plant" },
                new[] { @"\b_", "bush", "ker_", "shrub", "hedge" }),

            new(ReforgerObjectCategory.Wall,
                new[] { @"\walls\", @"\wall_" },
                new[] { "wall", "zed_", "hedgehog", "barrier", "concrete_block", "sandbag" }),

            new(ReforgerObjectCategory.Fence,
                new[] { @"\fences\", @"\fence_" },
                new[] { "fence", "plot_", "railing", "wired", "wire_", "gate", "chainlink" }),

            new(ReforgerObjectCategory.Infrastructure,
                new[] { @"\infrastructure\", @"\signs_f\", @"\furniture\", @"\roads_f\", @"\bridges\" },
                new[] { "lamp", "light_", "pole", "pylon", "powerline", "power_line", "sign", "bench",
                        "antenna", "mast", "hydrant", "bin_", "trash", "bus_stop", "billboard", "crane",
                        "pier", "dock", "bridge", "stairs", "fuelstation", "watertower" }),

            new(ReforgerObjectCategory.Building,
                new[] { @"\households\", @"\structures_f", @"\buildings\", @"\ruins\", @"\industrial\",
                        @"\military\", @"\civ\", @"\commercial\", @"\cultural\", @"\research\" },
                new[] { "house", "building", "budova", "dum_", "kostel", "church", "chapel", "barn",
                        "stodola", "garage", "garaz", "shed", "hangar", "cottage", "chalet", "hotel",
                        "shop", "store", "mill", "factory", "tovarna", "school", "skola", "station",
                        "farm", "hospital", "office", "castle", "tower", "silo", "warehouse", "hut",
                        "cabin", "villa", "block_", "shelter", "bunker" }),
        };

        /// <summary>
        /// Classifies a model by its Arma 3 path (for example <c>a3\plants_f\Tree\t_ficus_f.p3d</c>).
        /// </summary>
        public static ReforgerObjectCategory Classify(string? modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                return ReforgerObjectCategory.Other;
            }

            var path = modelPath.Replace('/', '\\').ToLowerInvariant();
            if (!path.StartsWith("\\", StringComparison.Ordinal))
            {
                // Make the leading folder matchable by the same "\segment\" patterns as the rest
                path = "\\" + path;
            }

            foreach (var rule in Overrides)
            {
                if (Matches(path, rule.PathParts) || Matches(path, rule.Keywords))
                {
                    return rule.Category;
                }
            }

            foreach (var rule in Rules)
            {
                foreach (var part in rule.PathParts)
                {
                    if (path.Contains(part, StringComparison.Ordinal))
                    {
                        return rule.Category;
                    }
                }
            }

            foreach (var rule in Rules)
            {
                foreach (var keyword in rule.Keywords)
                {
                    if (path.Contains(keyword, StringComparison.Ordinal))
                    {
                        return rule.Category;
                    }
                }
            }

            return ReforgerObjectCategory.Other;
        }

        private static bool Matches(string path, string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (path.Contains(token, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>File name (without extension) used for the export file of a family.</summary>
        public static string GetLayerName(ReforgerObjectCategory category)
        {
            return category.ToString().ToLowerInvariant();
        }
    }
}
