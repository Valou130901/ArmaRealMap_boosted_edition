namespace GameRealisticMap.Reforger.Import
{
    /// <summary>Per-model accounting of a converted Arma 3 map.</summary>
    public sealed class WrpModelStat
    {
        public WrpModelStat(string model, ReforgerObjectCategory category)
        {
            Model = model;
            Category = category;
        }

        /// <summary>Arma 3 model path, as stored in the wrp.</summary>
        public string Model { get; }

        public ReforgerObjectCategory Category { get; }

        /// <summary>Reforger prefab the mapping resolved, or null when the model has no equivalent yet.</summary>
        public string? Prefab { get; set; }

        public int Count { get; set; }

        public bool IsMapped => !string.IsNullOrEmpty(Prefab);
    }

    /// <summary>
    /// Summary of a wrp to Reforger conversion: what was placed, what still has no prefab, and what
    /// had to be dropped. Drives the README and the model port worklist.
    /// </summary>
    public sealed class WrpReforgerPackStats
    {
        private readonly Dictionary<string, WrpModelStat> models = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, WrpModelStat> Models => models;

        /// <summary>Objects whose wrp transform was degenerate and could not be converted.</summary>
        public int InvalidObjects { get; set; }

        public int TotalObjects => models.Values.Sum(m => m.Count);

        public int MappedObjects => models.Values.Where(m => m.IsMapped).Sum(m => m.Count);

        public int UnmappedObjects => TotalObjects - MappedObjects;

        public void Add(string model, ReforgerObjectCategory category, string? prefab)
        {
            if (!models.TryGetValue(model, out var stat))
            {
                stat = new WrpModelStat(model, category);
                models.Add(model, stat);
            }
            stat.Prefab = prefab;
            stat.Count++;
        }

        public IEnumerable<WrpModelStat> ByCategory(ReforgerObjectCategory category)
        {
            return models.Values.Where(m => m.Category == category);
        }

        public int CountIn(ReforgerObjectCategory category)
        {
            return ByCategory(category).Sum(m => m.Count);
        }

        /// <summary>Distinct models with no Reforger prefab, biggest offenders first.</summary>
        public IEnumerable<WrpModelStat> UnmappedModels => models.Values
            .Where(m => !m.IsMapped)
            .OrderByDescending(m => m.Count);
    }
}
