namespace GameRealisticMap.Reforger
{
    /// <summary>Summary of a Reforger export, used to build the README and report unmapped models.</summary>
    public sealed class ReforgerExportStats
    {
        private readonly Dictionary<string, int> layerCounts = new();
        private readonly Dictionary<string, int> unmappedModels = new();

        public IReadOnlyDictionary<string, int> LayerCounts => layerCounts;

        /// <summary>Arma 3 model names with no Reforger prefab mapping, and how many times each was placed.</summary>
        public IReadOnlyDictionary<string, int> UnmappedModels => unmappedModels;

        public int TotalObjects => layerCounts.Values.Sum();

        public void AddLayer(string layer, int count)
        {
            layerCounts[layer] = count;
        }

        public void AddUnmapped(string layer, string modelName)
        {
            unmappedModels.TryGetValue(modelName, out var current);
            unmappedModels[modelName] = current + 1;
        }
    }
}
