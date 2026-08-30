using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>One Arma 3 model that has been converted for Arma Reforger.</summary>
    public sealed class ReforgerModelEntry
    {
        /// <summary>Arma 3 model path, as stored in a wrp (for example <c>a3\rocks_f\...\r_x.p3d</c>).</summary>
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>File name of the OBJ inside the library's <c>models</c> folder, if the export succeeded.</summary>
        [JsonPropertyName("obj")]
        public string? Obj { get; set; }

        /// <summary>
        /// Reforger prefab ResourceName once the user has built the prefab in the Workbench.
        /// Filled by harvesting an addon's resource database; null until then.
        /// </summary>
        [JsonPropertyName("prefab")]
        public string? Prefab { get; set; }

        /// <summary>ok, no-visual-lod, not-found, unreadable or failed.</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("vertices")]
        public int Vertices { get; set; }

        [JsonPropertyName("faces")]
        public int Faces { get; set; }

        [JsonIgnore]
        public bool IsConverted => Status == ReforgerModelLibrary.StatusOk;

        [JsonIgnore]
        public bool HasPrefab => !string.IsNullOrEmpty(Prefab);
    }

    /// <summary>
    /// Persistent, map-independent catalogue of Arma 3 models converted for Arma Reforger.
    /// </summary>
    /// <remarks>
    /// The point is to convert each model once for good. A model already in here is never exported
    /// again, whatever map asks for it, and once its Reforger prefab ResourceName is known the
    /// export places it directly instead of listing it as unmapped. That makes the library a
    /// user-owned mapping layer sitting on top of the built-in
    /// <see cref="Assets.ReforgerAssetMapping"/>.
    /// </remarks>
    public sealed class ReforgerModelLibrary
    {
        public const string StatusOk = "ok";
        public const string IndexFileName = "library.json";
        public const string VersionFileName = "library.version";

        /// <summary>
        /// Bumped whenever the port pipeline starts producing something new for every model, so a
        /// library built by an older version is rebuilt instead of being trusted. Entries carry no
        /// record of what was missing, and silently keeping them has already shipped half-empty
        /// exports more than once.
        /// </summary>
        private const int CurrentFormatVersion = 16;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, ReforgerModelEntry> entries;

        private ReforgerModelLibrary(string rootDirectory, Dictionary<string, ReforgerModelEntry> entries)
        {
            RootDirectory = rootDirectory;
            this.entries = entries;
        }

        public string RootDirectory { get; }

        public string ModelsDirectory => Path.Combine(RootDirectory, "models");

        public string TexturesDirectory => Path.Combine(RootDirectory, "textures");

        /// <summary>COLLADA copies of the models, for engines that load dae directly (BeamNG.drive).</summary>
        public string DaeDirectory => Path.Combine(RootDirectory, "dae");

        public string IndexFile => Path.Combine(RootDirectory, IndexFileName);

        public IReadOnlyCollection<ReforgerModelEntry> Entries => entries.Values;

        public int ConvertedCount => entries.Values.Count(e => e.IsConverted);

        public int PrefabCount => entries.Values.Count(e => e.HasPrefab);

        /// <summary>
        /// Default location, shared by every map: a per-user folder, not something inside a pack,
        /// so the work survives deleting an export.
        /// </summary>
        public static string DefaultRootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameRealisticMap", "ReforgerModels");

        public static ReforgerModelLibrary Load(string? rootDirectory = null)
        {
            var root = string.IsNullOrEmpty(rootDirectory) ? DefaultRootDirectory : rootDirectory;
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "models"));
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            Directory.CreateDirectory(Path.Combine(root, "dae"));

            var entries = new Dictionary<string, ReforgerModelEntry>(StringComparer.OrdinalIgnoreCase);
            var index = Path.Combine(root, IndexFileName);
            if (File.Exists(index) && IsCurrentFormat(root))
            {
                try
                {
                    using var stream = File.OpenRead(index);
                    var loaded = JsonSerializer.Deserialize<List<ReforgerModelEntry>>(stream, JsonOptions);
                    if (loaded != null)
                    {
                        foreach (var entry in loaded.Where(e => !string.IsNullOrEmpty(e.Model)))
                        {
                            entries[entry.Model] = entry;
                        }
                    }
                }
                catch (JsonException)
                {
                    // A corrupt index must not lock the user out: the models on disk are re-indexed
                    // as they get converted again
                }
            }
            return new ReforgerModelLibrary(root, entries);
        }

        public void Save()
        {
            using (var stream = File.Create(IndexFile))
            {
                JsonSerializer.Serialize(stream, entries.Values.OrderBy(e => e.Model).ToList(), JsonOptions);
            }
            File.WriteAllText(Path.Combine(RootDirectory, VersionFileName), CurrentFormatVersion.ToString());
        }

        /// <summary>
        /// True when the library on disk was written by a pipeline that produces everything the
        /// current one does. An older library is treated as empty so it gets rebuilt in full.
        /// </summary>
        private static bool IsCurrentFormat(string root)
        {
            var versionFile = Path.Combine(root, VersionFileName);
            return File.Exists(versionFile)
                && int.TryParse(File.ReadAllText(versionFile).Trim(), out var version)
                && version >= CurrentFormatVersion;
        }

        public ReforgerModelEntry? Get(string model)
        {
            return entries.TryGetValue(model, out var entry) ? entry : null;
        }

        /// <summary>
        /// True when the model has already been dealt with and does not need converting again.
        /// A previous failure counts as known: retrying it on every export would waste the time
        /// the library exists to save.
        /// </summary>
        public bool IsKnown(string model)
        {
            if (!entries.TryGetValue(model, out var entry))
            {
                return false;
            }
            if (!entry.IsConverted)
            {
                return true; // a recorded failure, no point retrying automatically
            }
            if (string.IsNullOrEmpty(entry.Obj) || !File.Exists(Path.Combine(ModelsDirectory, entry.Obj)))
            {
                return false; // the obj was deleted by hand, or never written
            }
            // Entries written before the library gained COLLADA output have no dae: converting them
            // again is what fills it in, so they must not count as known.
            return File.Exists(Path.Combine(DaeDirectory, Path.ChangeExtension(entry.Obj, ".dae")));
        }

        /// <summary>Reforger prefab of a model, or null when it has none yet.</summary>
        public string? GetPrefab(string model)
        {
            return entries.TryGetValue(model, out var entry) ? entry.Prefab : null;
        }

        public void Set(ReforgerModelEntry entry)
        {
            entries[entry.Model] = entry;
        }

        /// <summary>
        /// Attaches a Reforger prefab to a model. Returns false when the model is not in the
        /// library, so callers can report names that matched nothing.
        /// </summary>
        public bool SetPrefab(string model, string prefab)
        {
            if (!entries.TryGetValue(model, out var entry))
            {
                return false;
            }
            entry.Prefab = prefab;
            return true;
        }

        /// <summary>
        /// Models converted but still without a prefab: what the user has to build in the Workbench
        /// before the export can place them.
        /// </summary>
        public IEnumerable<ReforgerModelEntry> AwaitingPrefab => entries.Values
            .Where(e => e.IsConverted && !e.HasPrefab)
            .OrderBy(e => e.Model);
    }
}
