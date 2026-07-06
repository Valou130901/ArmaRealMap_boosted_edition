using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameRealisticMap.Reforger.Assets
{
    /// <summary>
    /// Translates Arma 3 model names (resolved by the loaded GRM asset config) into
    /// Arma Reforger prefab ResourceNames (<c>{GUID}Prefabs/.../name.et</c>).
    /// </summary>
    /// <remarks>
    /// Resolution order: exact match (case-insensitive on model name) then first keyword rule
    /// whose any token is contained in the model name, then the global fallback.
    /// A <see langword="null"/> result means "no prefab" - the object is still exported, but as a
    /// 7-token line so the user assigns a prefab pool per-layer in the Import Objects Extended tool.
    /// </remarks>
    public sealed class ReforgerAssetMapping
    {
        public sealed class KeywordRule
        {
            [JsonPropertyName("match")]
            public List<string> Match { get; set; } = new();

            [JsonPropertyName("prefab")]
            public string? Prefab { get; set; }
        }

        private sealed class MappingFile
        {
            [JsonPropertyName("exact")]
            public Dictionary<string, string> Exact { get; set; } = new(StringComparer.OrdinalIgnoreCase);

            [JsonPropertyName("keywords")]
            public List<KeywordRule> Keywords { get; set; } = new();

            [JsonPropertyName("fallback")]
            public string? Fallback { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly Dictionary<string, string> exact;
        private readonly List<KeywordRule> keywords;
        private readonly string? fallback;

        private ReforgerAssetMapping(MappingFile file)
        {
            exact = new Dictionary<string, string>(file.Exact, StringComparer.OrdinalIgnoreCase);
            keywords = file.Keywords;
            fallback = file.Fallback;
        }

        /// <summary>Loads the mapping embedded in the assembly.</summary>
        public static ReforgerAssetMapping LoadDefault()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .First(n => n.EndsWith("ReforgerAssetMapping.default.json", StringComparison.OrdinalIgnoreCase));
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            return Load(stream);
        }

        /// <summary>Loads a mapping from a JSON file, falling back to the embedded default if it does not exist.</summary>
        public static ReforgerAssetMapping LoadFromFileOrDefault(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return Load(stream);
            }
            return LoadDefault();
        }

        public static ReforgerAssetMapping Load(Stream stream)
        {
            var file = JsonSerializer.Deserialize<MappingFile>(stream, JsonOptions)
                ?? throw new FormatException("Reforger asset mapping is empty or invalid.");
            return new ReforgerAssetMapping(file);
        }

        /// <summary>
        /// Resolves an Arma 3 model name to a Reforger prefab ResourceName, or <see langword="null"/>
        /// if no rule matches (the object should then be exported without an explicit prefab).
        /// </summary>
        public string? Resolve(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
            {
                return fallback;
            }
            if (exact.TryGetValue(modelName, out var prefab))
            {
                return prefab;
            }
            foreach (var rule in keywords)
            {
                foreach (var token in rule.Match)
                {
                    if (!string.IsNullOrEmpty(token) && modelName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        return rule.Prefab;
                    }
                }
            }
            return fallback;
        }
    }
}
