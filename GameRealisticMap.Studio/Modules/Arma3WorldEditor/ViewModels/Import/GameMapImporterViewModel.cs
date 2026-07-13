using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BIS.Core.Config;
using BIS.Core.Streams;
using BIS.PAA;
using BIS.PBO;
using BIS.WRP;
using Caliburn.Micro;
using GameRealisticMap.Arma3;
using GameRealisticMap.Arma3.Assets;
using GameRealisticMap.Arma3.Edit.Imagery;
using GameRealisticMap.Arma3.IO;
using Pmad.HugeImages;
using Pmad.HugeImages.Storage;
using GameRealisticMap.Studio.Modules.Arma3Data;
using GameRealisticMap.Studio.Toolkit;
using Gemini.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Import
{
    internal class GameMapItem : PropertyChangedBase
    {
        public GameMapItem(string pboPath, string pboPrefix, string wrpFileName, int size, bool isProjectDrive = false)
        {
            PboPath = pboPath;
            PboPrefix = pboPrefix;
            WrpFileName = wrpFileName;
            Size = size;
            IsProjectDrive = isProjectDrive;
        }

        public string PboPath { get; }

        public string PboPrefix { get; }

        public string WrpFileName { get; }

        public int Size { get; }

        /// <summary>
        /// The map is already unpacked on the project drive (P:) as loose files, not inside a PBO
        /// </summary>
        public bool IsProjectDrive { get; }

        public string Source => IsProjectDrive ? "P: drive" : "PBO";

        public string WorldName => Path.GetFileNameWithoutExtension(WrpFileName);

        public double SizeMB => Size / 1024.0 / 1024.0;

        public string DisplayName => FormattableString.Invariant($"{WorldName}  ({PboPrefix}, {SizeMB:0.0} MB)");
    }

    /// <summary>
    /// Imports an existing map (game or mod) into the project drive to edit it: extracts the
    /// wrp (binarized OPRW supported) and its config.bin, then opens the world editor.
    /// </summary>
    internal class GameMapImporterViewModel : WindowBase
    {
        private readonly IArma3DataModule arma3Data;
        private List<GameMapItem> items = new List<GameMapItem>();
        private GameMapItem? selectedItem;
        private bool isScanning;
        private string status = string.Empty;
        private string newPboPrefix = string.Empty;
        private string newWorldName = string.Empty;

        public GameMapImporterViewModel()
        {
            arma3Data = IoC.Get<IArma3DataModule>();
        }

        public List<GameMapItem> Items
        {
            get { return items; }
            set { items = value; NotifyOfPropertyChange(); }
        }

        public GameMapItem? SelectedItem
        {
            get { return selectedItem; }
            set { selectedItem = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(CanImport)); }
        }

        public bool IsScanning
        {
            get { return isScanning; }
            set { isScanning = value; NotifyOfPropertyChange(); }
        }

        public string Status
        {
            get { return status; }
            set { status = value; NotifyOfPropertyChange(); }
        }

        public bool CanImport => SelectedItem != null;

        /// <summary>
        /// Optional: import as a custom version under this PBO prefix instead of overriding the
        /// original map (e.g. "myname\malden_custom")
        /// </summary>
        public string NewPboPrefix
        {
            get { return newPboPrefix; }
            set { newPboPrefix = value; NotifyOfPropertyChange(); }
        }

        /// <summary>
        /// Optional: world name of the custom version (defaults to the original name)
        /// </summary>
        public string NewWorldName
        {
            get { return newWorldName; }
            set { newWorldName = value; NotifyOfPropertyChange(); }
        }

        protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            await base.OnInitializeAsync(cancellationToken);
            IsScanning = true;
            Status = "Scanning game and mods PBO files...";
            _ = Task.Run(() => Scan());
        }

        private List<string> GetScanRoots()
        {
            var roots = new List<string>();
            var arma3Path = Arma3ToolsHelper.GetArma3Path();
            if (!string.IsNullOrEmpty(arma3Path))
            {
                roots.Add(arma3Path);
            }
            roots.AddRange(arma3Data.ActiveMods);
            // Layers of a map can be shipped by another mod: include the whole workshop content
            var workshop = Arma3ToolsHelper.GetArma3WorkshopPath();
            if (!string.IsNullOrEmpty(workshop) && !roots.Any(r => string.Equals(r, workshop, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(workshop);
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A valid Arma world name: starts with a letter, only letters/digits/underscores
        private static readonly System.Text.RegularExpressions.Regex ValidWorldName =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z0-9_]*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Filters out the decoy/junk wrp entries that protected (obfuscated) PBOs inject in their
        /// header: they have a zero (or absurdly small) size and/or a garbage world name.
        /// </summary>
        private static bool IsRealMapEntry(string? prefix, IPBOFileEntry entry)
        {
            // A real map wrp is never empty; obfuscation decoys point to no data
            if (entry.Size <= 0)
            {
                return false;
            }
            var worldName = Path.GetFileNameWithoutExtension(entry.FileName);
            if (!ValidWorldName.IsMatch(worldName))
            {
                return false;
            }
            return true;
        }

        private void Scan()
        {
            var result = new List<GameMapItem>();

            // Loose wrp files already unpacked on the project drive (e.g. P:\KelleysIsland\kelleysisland.wrp).
            // Listed first: on duplicate prefix+name they win over the PBO version, as they may
            // contain local edits.
            ScanProjectDrive(result);

            var roots = GetScanRoots();

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                foreach (var pboPath in Directory.EnumerateFiles(root, "*.pbo", SearchOption.AllDirectories))
                {
                    try
                    {
                        var pbo = new PBO(pboPath, false);
                        foreach (var entry in pbo.Files)
                        {
                            if (entry.FileName.EndsWith(".wrp", StringComparison.OrdinalIgnoreCase)
                                && IsRealMapEntry(pbo.Prefix, entry))
                            {
                                result.Add(new GameMapItem(pboPath, pbo.Prefix, entry.FileName, entry.Size));
                            }
                        }
                    }
                    catch
                    {
                        // Unreadable or malformed PBO: ignore
                    }
                }
            }

            // The workshop root can overlap with active mods paths: remove duplicates
            result = result
                .GroupBy(i => i.PboPrefix + "|" + i.WrpFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            result.Sort((a, b) => string.Compare(a.WorldName, b.WorldName, StringComparison.OrdinalIgnoreCase));

            OnUIThread(() =>
            {
                Items = result;
                IsScanning = false;
                Status = FormattableString.Invariant($"{result.Count} maps found.");
            });
        }

        private void ScanProjectDrive(List<GameMapItem> result)
        {
            var projectDrive = arma3Data.ProjectDrive;
            try
            {
                // Physical enumeration only: ProjectDrive.FindAll would also scan every game
                // and mod PBO through the secondary source, which takes minutes
                var mountRoot = projectDrive.MountPath;
                if (!mountRoot.EndsWith("\\", StringComparison.Ordinal) && !mountRoot.EndsWith("/", StringComparison.Ordinal))
                {
                    mountRoot += Path.DirectorySeparatorChar;
                }
                if (!Directory.Exists(mountRoot))
                {
                    return;
                }
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive
                };
                foreach (var fullPath in Directory.EnumerateFiles(mountRoot, "*.wrp", options))
                {
                    var gamePath = fullPath.Substring(mountRoot.Length).TrimStart('\\', '/').Replace('/', '\\');
                    if (gamePath.StartsWith("temp\\", StringComparison.OrdinalIgnoreCase)
                        || gamePath.StartsWith("grm-temp\\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var prefix = Path.GetDirectoryName(gamePath)?.Replace('/', '\\');
                    if (string.IsNullOrEmpty(prefix))
                    {
                        continue; // A wrp at the drive root has no usable PBO prefix
                    }
                    var size = (int)Math.Min(new FileInfo(fullPath).Length, int.MaxValue);
                    result.Add(new GameMapItem(fullPath, prefix, Path.GetFileName(gamePath), size, isProjectDrive: true));
                }
            }
            catch
            {
                // Project drive not mounted or unreadable: ignore
            }
        }

        public Task ImportItem(GameMapItem item)
        {
            SelectedItem = item;
            return Import();
        }

        public async Task Import()
        {
            var item = SelectedItem;
            if (item == null)
            {
                return;
            }

            var projectDrive = arma3Data.ProjectDrive;
            var targetWrpGamePath = item.PboPrefix + "\\" + item.WrpFileName;

            if (item.IsProjectDrive)
            {
                // The map source is already unpacked on the project drive: nothing to extract,
                // only prepare the layers for the imagery tooling (paa to png, rvmat to text)
                Status = "Preparing map...";
                await Task.Run(() =>
                {
                    var layerFolders = GetLayerFolders(projectDrive.GetFullPath(targetWrpGamePath), item);

                    ImportLayers(projectDrive, item, layerFolders);
                });
            }
            else
            {
                var pbo = new PBO(item.PboPath, false);

                var wrpEntry = pbo.Files.FirstOrDefault(f => string.Equals(f.FileName, item.WrpFileName, StringComparison.OrdinalIgnoreCase));
                if (wrpEntry == null)
                {
                    return;
                }

                Status = "Extracting map...";
                await Task.Run(() =>
                {
                    // Extract the whole map PBO: wrp, config, roads shapefiles, map data...
                    var hasConfigCpp = projectDrive.FileExists(item.PboPrefix + "\\config.cpp");
                    foreach (var entry in pbo.Files)
                    {
                        if (hasConfigCpp && string.Equals(entry.FileName, "config.bin", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // A text config already exists on the project drive, keep it authoritative
                        }
                        try
                        {
                            ExtractEntry(projectDrive, entry, item.PboPrefix + "\\" + entry.FileName);
                        }
                        catch
                        {
                            // Non-critical file: ignore
                        }
                    }

                    // The wrp knows exactly where its layers are (rvmat paths): use that to find
                    // them, even when they are shipped by another PBO or another mod
                    var layerFolders = GetLayerFolders(projectDrive.GetFullPath(targetWrpGamePath), item);

                    ImportLayers(projectDrive, item, layerFolders);
                });
            }

            // Optional: create a renamed custom version (own PBO prefix and world name), so the
            // mod does not override the original map
            var customPrefix = (NewPboPrefix ?? string.Empty).Trim().Trim('\\');
            if (!string.IsNullOrEmpty(customPrefix) && !string.Equals(customPrefix, item.PboPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var customWorld = string.IsNullOrWhiteSpace(NewWorldName) ? item.WorldName : NewWorldName.Trim();
                Status = "Creating custom version...";
                targetWrpGamePath = await Task.Run(() => CreateCustomVersion(projectDrive, item, targetWrpGamePath, customPrefix, customWorld));
            }

            // Recreate the editable source images (assembled satmap and idmap) from the tiles,
            // like a map generated by GRM would have
            Status = "Recreating satmap and idmap source images...";
            await Task.Run(() => RecreateSourceImages(projectDrive, targetWrpGamePath));

            await TryCloseAsync(true);

            await EditorHelper.OpenDefaultEditor(projectDrive.GetFullPath(targetWrpGamePath));
        }

        private static async Task<string> CreateCustomVersion(ProjectDrive projectDrive, GameMapItem item, string sourceWrpGamePath, string customPrefix, string customWorld)
        {
            var task = IoC.Get<Modules.Reporting.IProgressTool>().StartTask("Custom version");
            try
            {
                var world = StreamHelper.Read<AnyWrp>(projectDrive.GetFullPath(sourceWrpGamePath)).GetEditableWrp();

                var worker = new GameRealisticMap.Arma3.Edit.WrpRenameWorker(task.Scope, projectDrive, item.PboPrefix, customPrefix);

                // Rewrite material paths and copy the layer files to the new prefix
                await worker.RenameAndCopyMaterials(world);

                // Rewrite the game config: world class name, worldName, all referenced paths
                var configText = ReadOriginalConfigText(projectDrive, item);
                if (configText != null)
                {
                    var config = GameRealisticMap.Arma3.GameEngine.GameConfigTextData.ReadFromContent(configText, item.WorldName);
                    await worker.UpdateConfig(config, customWorld);
                }

                await worker.CopyReferencedFiles();

                var newWrpGamePath = customPrefix + "\\" + customWorld + ".wrp";
                projectDrive.CreateDirectory(customPrefix);
                StreamHelper.Write(world, projectDrive.GetFullPath(newWrpGamePath));

                GenerateTileRvmats(projectDrive.GetFullPath(customPrefix + "\\data\\layers"));

                return newWrpGamePath;
            }
            catch (Exception ex)
            {
                task.Scope.Failed(ex);
                return sourceWrpGamePath;
            }
            finally
            {
                task.Done();
            }
        }

        /// <summary>
        /// Rebuild the full satmap and idmap source images from the imported tiles, next to the
        /// wrp file (worldname-satmap.png / worldname-idmap.png), so the map has editable sources
        /// like a GRM generated map.
        /// </summary>
        private static async Task RecreateSourceImages(ProjectDrive projectDrive, string wrpGamePath)
        {
            var task = IoC.Get<Modules.Reporting.IProgressTool>().StartTask("Source images");
            try
            {
                var wrpFullPath = projectDrive.GetFullPath(wrpGamePath);
                var world = StreamHelper.Read<AnyWrp>(wrpFullPath).GetEditableWrp();
                var sizeInMeters = world.LandRangeX * world.CellSize;
                var pboPrefix = Path.GetDirectoryName(wrpGamePath)!.Replace('/', '\\');

                var infos = ExistingImageryInfos.TryCreate(projectDrive, pboPrefix, sizeInMeters);
                if (infos == null)
                {
                    return; // Layers incomplete: no source images can be rebuilt
                }

                // Assembled images can be very large
                SixLabors.ImageSharp.Configuration.Default.MemoryAllocator = SixLabors.ImageSharp.Memory.MemoryAllocator.Create(
                    new SixLabors.ImageSharp.Memory.MemoryAllocatorOptions()
                    {
                        MaximumPoolSizeMegabytes = 32_768,
                        AllocationLimitMegabytes = 16_384
                    });

                var baseName = Path.Combine(Path.GetDirectoryName(wrpFullPath)!, Path.GetFileNameWithoutExtension(wrpFullPath));

                using (task.Scope.CreateSingle("Satmap"))
                {
                    using var satMap = infos.GetSatMap(projectDrive);
                    await satMap.SaveUniqueAsync(baseName + "-satmap.png");
                }

                using (task.Scope.CreateSingle("Idmap"))
                {
                    // Unknown textures get stable ad-hoc colors, so the exported idmap stays
                    // consistent with the in-editor view and reimport
                    using var idMap = infos.GetIdMap(projectDrive, new TerrainMaterialLibrary());
                    await idMap.SaveUniqueAsync(baseName + "-idmap.png");
                }
            }
            catch (Exception ex)
            {
                task.Scope.Failed(ex);
            }
            finally
            {
                task.Done();
            }
        }

        private static string? ReadOriginalConfigText(ProjectDrive projectDrive, GameMapItem item)
        {
            var configCpp = projectDrive.GetFullPath(item.PboPrefix + "\\config.cpp");
            if (File.Exists(configCpp))
            {
                return File.ReadAllText(configCpp);
            }
            var configBin = projectDrive.GetFullPath(item.PboPrefix + "\\config.bin");
            if (File.Exists(configBin))
            {
                return StreamHelper.Read<ParamFile>(configBin).ToString();
            }
            return null;
        }

        private static void ExtractEntry(ProjectDrive projectDrive, IPBOFileEntry entry, string targetGamePath)
        {
            var fullPath = projectDrive.GetFullPath(targetGamePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using (var source = entry.OpenRead())
            {
                using (var target = File.Create(fullPath))
                {
                    source.CopyTo(target);
                }
            }
        }

        /// <summary>
        /// Extract the imagery layers (id map mask tiles, satmap tiles, rvmat) so the world editor
        /// can rebuild the id map and regenerate the satmap. PAA tiles are converted to PNG,
        /// binarized rvmat are converted back to text.
        /// </summary>
        /// <summary>
        /// Folders containing the imagery layers, deduced from the rvmat paths of the wrp itself
        /// (works whatever the PBO or the mod that ships them), plus the conventional location.
        /// </summary>
        private static List<string> GetLayerFolders(string wrpFullPath, GameMapItem item)
        {
            var folders = new List<string>
            {
                item.PboPrefix.TrimEnd('\\') + "\\data\\layers"
            };
            try
            {
                var matNames = StreamHelper.Read<AnyWrp>(wrpFullPath).MatNames;
                foreach (var matName in matNames)
                {
                    if (!string.IsNullOrEmpty(matName))
                    {
                        var folder = Path.GetDirectoryName(matName);
                        if (!string.IsNullOrEmpty(folder) && !folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                        {
                            folders.Add(folder);
                        }
                    }
                }
            }
            catch
            {
                // Fall back to the conventional location only
            }
            return folders;
        }

        private void ImportLayers(ProjectDrive projectDrive, GameMapItem item, List<string> layerFolders)
        {
            var done = 0;
            var failed = 0;

            // Layer files already unpacked on the project drive (map source in loose files):
            // convert them in place before looking into PBOs
            foreach (var folder in layerFolders)
            {
                ProcessLooseLayerFolder(projectDrive, folder, ref done, ref failed);
            }

            foreach (var root in GetScanRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                foreach (var pboPath in Directory.EnumerateFiles(root, "*.pbo", SearchOption.AllDirectories))
                {
                    IEnumerable<IPBOFileEntry> entries;
                    string prefix;
                    try
                    {
                        var pbo = new PBO(pboPath, false);
                        prefix = (pbo.Prefix ?? string.Empty).TrimEnd('\\');
                        entries = pbo.Files;
                    }
                    catch
                    {
                        continue; // Unreadable or malformed PBO: ignore
                    }
                    // The PBO may contain layer files if its prefix is under a layer folder, or
                    // if a layer folder is under its prefix
                    var prefixSlash = prefix + "\\";
                    if (!layerFolders.Any(f => (f + "\\").StartsWith(prefixSlash, StringComparison.OrdinalIgnoreCase)
                                            || prefixSlash.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // Not related to the imported map
                    }
                    foreach (var entry in entries)
                    {
                        var fullGamePath = prefix + "\\" + entry.FileName;
                        if (!layerFolders.Any(f => fullGamePath.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        if (item.IsProjectDrive && LayerTargetExists(projectDrive, fullGamePath))
                        {
                            continue; // Never overwrite the loose files of an unpacked map source
                        }
                        try
                        {
                            ExtractLayerEntry(projectDrive, entry, fullGamePath);
                            done++;
                        }
                        catch
                        {
                            failed++;
                        }
                        if ((done + failed) % 50 == 0)
                        {
                            var progressText = FormattableString.Invariant($"Extracting layers... {done + failed} files");
                            OnUIThread(() => Status = progressText);
                        }
                    }
                }
            }

            var finalText = FormattableString.Invariant($"Layers: {done} files extracted, {failed} errors.");
            OnUIThread(() => Status = finalText);

            foreach (var folder in layerFolders)
            {
                GenerateTileRvmats(projectDrive.GetFullPath(folder));
            }
        }

        /// <summary>
        /// Game maps have one rvmat per texture combination and per tile (P_000-000_L02_L04.rvmat).
        /// The imagery tooling expects a single P_000-000.rvmat per tile: generate it from the
        /// variant with the most ground textures.
        /// </summary>
        private static void GenerateTileRvmats(string layersDirectory)
        {
            if (!Directory.Exists(layersDirectory))
            {
                return;
            }
            var tileRegex = new System.Text.RegularExpressions.Regex(@"^p_(\d{3})-(\d{3})(_.+)?\.rvmat$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var colorTextureRegex = new System.Text.RegularExpressions.Regex(@"texture=""([^""]*)"";\r?\n[ \t]*texGen=2;",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            var byTile = Directory.GetFiles(layersDirectory, "*.rvmat")
                .Select(path => new { Path = path, Match = tileRegex.Match(Path.GetFileName(path)) })
                .Where(f => f.Match.Success)
                .GroupBy(f => f.Match.Groups[1].Value + "-" + f.Match.Groups[2].Value);

            foreach (var tile in byTile)
            {
                var plainName = System.IO.Path.Combine(layersDirectory, $"P_{tile.Key}.rvmat");
                if (tile.Any(f => string.IsNullOrEmpty(f.Match.Groups[3].Value)))
                {
                    continue; // A plain tile rvmat already exists
                }
                // Pick the variant with the most ground color textures
                var best = tile
                    .Select(f => new { f.Path, Content = File.ReadAllText(f.Path) })
                    .OrderByDescending(f => colorTextureRegex.Matches(f.Content).Count)
                    .First();
                File.WriteAllText(plainName, best.Content);
            }
        }

        private static void ExtractLayerEntry(ProjectDrive projectDrive, IPBOFileEntry entry, string fullGamePath)
        {
            var extension = Path.GetExtension(entry.FileName).ToLowerInvariant();
            if (extension == ".paa")
            {
                // Convert to PNG: the imagery edit tooling works with PNG tiles
                var targetPath = projectDrive.GetFullPath(GetLayerPngPath(fullGamePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using var memory = new MemoryStream();
                using (var source = entry.OpenRead())
                {
                    source.CopyTo(memory);
                }
                PaaToPng(memory, targetPath);
            }
            else if (extension == ".rvmat")
            {
                // Binarized rvmat (raP) must be converted back to text for the imagery tooling
                var targetPath = projectDrive.GetFullPath(fullGamePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using var memory = new MemoryStream();
                using (var source = entry.OpenRead())
                {
                    source.CopyTo(memory);
                }
                memory.Position = 0;
                if (IsBinarizedConfig(memory.GetBuffer(), memory.Length))
                {
                    var text = StreamHelper.Read<ParamFile>(memory).ToString();
                    File.WriteAllText(targetPath, text);
                }
                else
                {
                    File.WriteAllBytes(targetPath, memory.ToArray());
                }
            }
            else
            {
                ExtractEntry(projectDrive, entry, fullGamePath);
            }
        }

        /// <summary>
        /// True if the target of a layer entry extraction is already present on the project drive
        /// (for paa entries the target is the converted png)
        /// </summary>
        private static bool LayerTargetExists(ProjectDrive projectDrive, string fullGamePath)
        {
            if (string.Equals(Path.GetExtension(fullGamePath), ".paa", StringComparison.OrdinalIgnoreCase))
            {
                return projectDrive.FileExists(GetLayerPngPath(fullGamePath))
                    || projectDrive.FileExists(fullGamePath);
            }
            return projectDrive.FileExists(fullGamePath);
        }

        // Old Terrain Builder projects name the id map mask tiles M_xxx_yyy_lco, the imagery
        // tooling expects M_xxx_yyy_lca
        private static readonly System.Text.RegularExpressions.Regex OldMaskTileName = new System.Text.RegularExpressions.Regex(
            @"^m_(\d+)_(\d+)_lco$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// PNG path for a layer tile, renaming old convention mask tiles (M_xxx_yyy_lco to
        /// M_xxx_yyy_lca). Works with game paths and physical paths.
        /// </summary>
        private static string GetLayerPngPath(string tilePath)
        {
            var name = Path.GetFileNameWithoutExtension(tilePath);
            var match = OldMaskTileName.Match(name);
            if (match.Success)
            {
                name = $"M_{match.Groups[1].Value}_{match.Groups[2].Value}_lca";
            }
            var directory = Path.GetDirectoryName(tilePath);
            return string.IsNullOrEmpty(directory) ? name + ".png" : directory + Path.DirectorySeparatorChar + name + ".png";
        }

        /// <summary>
        /// Prepare the layer files of a map source already unpacked on the project drive:
        /// converts paa tiles to png and binarized rvmat back to text, in place.
        /// </summary>
        private static void ProcessLooseLayerFolder(ProjectDrive projectDrive, string layerFolderGamePath, ref int done, ref int failed)
        {
            var fullFolderPath = projectDrive.GetFullPath(layerFolderGamePath);
            if (!Directory.Exists(fullFolderPath))
            {
                return;
            }
            foreach (var filePath in Directory.GetFiles(fullFolderPath))
            {
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                try
                {
                    if (extension == ".paa")
                    {
                        var pngPath = GetLayerPngPath(filePath);
                        if (!File.Exists(pngPath))
                        {
                            using var memory = new MemoryStream(File.ReadAllBytes(filePath));
                            PaaToPng(memory, pngPath);
                            done++;
                        }
                    }
                    else if (extension == ".png")
                    {
                        // Rename mask tiles converted with the old name by a previous import
                        var expectedPath = GetLayerPngPath(filePath);
                        if (!string.Equals(expectedPath, filePath, StringComparison.OrdinalIgnoreCase) && !File.Exists(expectedPath))
                        {
                            File.Move(filePath, expectedPath);
                            done++;
                        }
                    }
                    else if (extension == ".rvmat")
                    {
                        var bytes = File.ReadAllBytes(filePath);
                        if (IsBinarizedConfig(bytes, bytes.Length))
                        {
                            using var memory = new MemoryStream(bytes);
                            var text = StreamHelper.Read<ParamFile>(memory).ToString();
                            File.WriteAllText(filePath, text);
                            done++;
                        }
                    }
                }
                catch
                {
                    failed++;
                }
            }
        }

        private static void PaaToPng(MemoryStream memory, string targetPath)
        {
            memory.Position = 0;
            var paa = new PAA(memory);
            var mipmap = paa.Mipmaps.OrderByDescending(m => m.Width).First();
            var pixels = PAA.GetARGB32PixelData(paa, memory, mipmap);
            using var image = Image.LoadPixelData<Bgra32>(pixels, mipmap.Width, mipmap.Height);
            image.SaveAsPng(targetPath);
        }

        private static bool IsBinarizedConfig(byte[] bytes, long length)
        {
            return length > 4 && bytes[0] == 0 && bytes[1] == (byte)'r' && bytes[2] == (byte)'a' && bytes[3] == (byte)'P';
        }

        public Task Cancel() => TryCloseAsync(false);
    }
}
