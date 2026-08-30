using System.Text;
using BIS.P3D.ODOL;
using BIS.PAA;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>What one port run did.</summary>
    public sealed class ModelPortReport
    {
        public int AlreadyKnown { get; set; }

        public int Converted { get; set; }

        public int Failed { get; set; }

        public int Requested { get; set; }

        public Dictionary<string, int> ByStatus { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fills a <see cref="ReforgerModelLibrary"/> with Arma 3 models converted to OBJ plus PNG
    /// textures, the interchange the Enfusion Blender Tools turn into xob.
    /// </summary>
    /// <remarks>
    /// Every model is converted once and only once: anything already recorded in the library is
    /// skipped, including previous failures, so repeated exports cost nothing after the first.
    /// </remarks>
    public sealed class ModelPortRunner
    {
        private readonly Func<string, ODOL?> readOdol;
        private readonly Func<string, Stream?> openTexture;
        private readonly ReforgerModelLibrary library;
        private readonly Dictionary<string, string?> textureCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> ddsCache = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="readOdol">Reads a binarized model by its Arma path, or returns null.</param>
        /// <param name="openTexture">Opens an Arma texture (paa) by path, or returns null.</param>
        public ModelPortRunner(Func<string, ODOL?> readOdol, Func<string, Stream?> openTexture, ReforgerModelLibrary library)
        {
            this.readOdol = readOdol;
            this.openTexture = openTexture;
            this.library = library;
        }

        /// <summary>
        /// Converts the models that the library does not already know about.
        /// </summary>
        /// <param name="limit">Stop after converting this many new models, or null for all of them.</param>
        public ModelPortReport Port(IEnumerable<string> models, IProgressScope progress, int? limit = null)
        {
            var todo = new List<string>();
            var report = new ModelPortReport();

            foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                report.Requested++;
                if (library.IsKnown(model))
                {
                    report.AlreadyKnown++;
                    continue;
                }
                todo.Add(model);
            }

            if (limit != null && todo.Count > limit.Value)
            {
                todo = todo.Take(limit.Value).ToList();
            }

            if (todo.Count == 0)
            {
                progress.WriteLine($"Model library: nothing to convert, {report.AlreadyKnown} models already known");
                return report;
            }

            progress.WriteLine($"Model library: converting {todo.Count} new models ({report.AlreadyKnown} already known)");

            using (var tracker = progress.CreateInteger("PortModels", todo.Count))
            {
                foreach (var model in todo)
                {
                    var entry = PortOne(model, progress);
                    library.Set(entry);

                    report.ByStatus.TryGetValue(entry.Status, out var count);
                    report.ByStatus[entry.Status] = count + 1;
                    if (entry.IsConverted)
                    {
                        report.Converted++;
                    }
                    else
                    {
                        report.Failed++;
                    }
                    tracker.ReportOneDone();
                }
            }

            library.Save();
            WriteHelperFiles();

            progress.WriteLine($"Model library: {report.Converted} converted, {report.Failed} failed, " +
                $"{library.ConvertedCount} models total in {library.RootDirectory}");

            return report;
        }

        private ReforgerModelEntry PortOne(string model, IProgressScope progress)
        {
            var entry = new ReforgerModelEntry { Model = model };

            ODOL? odol;
            try
            {
                odol = readOdol(model);
            }
            catch (Exception ex)
            {
                progress.WriteLine($"{model}: could not be read ({ex.Message})");
                entry.Status = "unreadable";
                return entry;
            }

            if (odol == null)
            {
                // Almost always a mod that is not mounted on the project drive
                entry.Status = "not-found";
                return entry;
            }

            var name = Path.GetFileNameWithoutExtension(model);
            P3dExportResult? export;
            try
            {
                export = P3dObjExporter.Export(odol, name, library.ModelsDirectory, ResolveTexture);
            }
            catch (Exception ex)
            {
                progress.WriteLine($"{model}: export failed ({ex.Message})");
                entry.Status = "failed";
                return entry;
            }

            if (export == null)
            {
                entry.Status = "no-visual-lod";
                return entry;
            }

            // A COLLADA copy alongside the OBJ, for engines that load dae directly. Its textures are
            // referenced by bare name because the dae ends up next to them once packed into a level.
            try
            {
                P3dColladaExporter.Export(odol, name, library.DaeDirectory, ResolveTextureDds);
            }
            catch (Exception ex)
            {
                progress.WriteLine($"{model}: dae export failed ({ex.Message})");
            }

            entry.Status = ReforgerModelLibrary.StatusOk;
            entry.Obj = Path.GetFileName(export.ObjFile);
            entry.Vertices = export.VertexCount;
            entry.Faces = export.FaceCount;
            return entry;
        }

        /// <summary>
        /// Converts an Arma texture to a PNG in the library, returning the path the mtl should use.
        /// Cached because textures are shared heavily between models.
        /// </summary>
        private string? ResolveTexture(string texturePath)
        {
            if (textureCache.TryGetValue(texturePath, out var cached))
            {
                return cached;
            }

            string? relative = null;
            try
            {
                var name = P3dObjExporter.SanitizeName(Path.GetFileNameWithoutExtension(texturePath)) + ".png";
                var target = Path.Combine(library.TexturesDirectory, name);
                if (File.Exists(target))
                {
                    relative = "../textures/" + name;
                }
                else
                {
                    using var source = openTexture(texturePath);
                    if (source != null)
                    {
                        using var memory = new MemoryStream();
                        source.CopyTo(memory);
                        memory.Position = 0;
                        PaaToPng(memory, target);
                        relative = "../textures/" + name;
                    }
                }
            }
            catch
            {
                // A texture that will not convert must not sink the whole model
                relative = null;
            }

            textureCache[texturePath] = relative;
            return relative;
        }

        /// <summary>
        /// Converts an Arma texture to a dds next to the models and returns its bare file name, the
        /// form a COLLADA document references. Falls back to the PNG copy for the handful of
        /// textures that are not block compressed.
        /// </summary>
        private string? ResolveTextureDds(string texturePath)
        {
            if (ddsCache.TryGetValue(texturePath, out var cached))
            {
                return cached;
            }

            string? name = null;
            try
            {
                // dds, repackaged straight from the paa. The mip chain Arma already built comes
                // across untouched, which png cannot carry, and the block compression survives all
                // the way into video memory. A png copy is a third larger, has to be decoded rather
                // than rewrapped, and leaves the engine to generate mips at load time.
                var candidate = P3dObjExporter.SanitizeName(Path.GetFileNameWithoutExtension(texturePath)) + ".dds";
                var target = Path.Combine(library.TexturesDirectory, candidate);
                if (File.Exists(target))
                {
                    name = candidate;
                }
                else
                {
                    using var source = openTexture(texturePath);
                    if (source != null)
                    {
                        using var memory = new MemoryStream();
                        source.CopyTo(memory);
                        if (PaaToDdsConverter.TryConvert(memory, target))
                        {
                            name = candidate;
                        }
                        else
                        {
                            // Not block compressed: the png copy is the only usable form
                            name = Path.GetFileName(ResolveTexture(texturePath));
                        }
                    }
                }
            }
            catch
            {
                name = null;
            }

            ddsCache[texturePath] = name;
            return name;
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

        private void WriteHelperFiles()
        {
            File.WriteAllText(Path.Combine(library.RootDirectory, "convert.py"), BlenderBatchScript, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(library.RootDirectory, "README.md"), CreateReadme(), new UTF8Encoding(false));

            // Worklist of what still has to be built as a prefab in the Workbench
            var pending = new StringBuilder();
            pending.AppendLine("model;obj;vertices;faces;prefab");
            foreach (var entry in library.AwaitingPrefab)
            {
                pending.AppendLine(FormattableString.Invariant(
                    $"{entry.Model};{entry.Obj};{entry.Vertices};{entry.Faces};"));
            }
            File.WriteAllText(Path.Combine(library.RootDirectory, "awaiting-prefab.csv"), pending.ToString(), new UTF8Encoding(false));
        }

        private string CreateReadme()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Arma 3 to Arma Reforger model library");
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant(
                $"{library.ConvertedCount} models converted, {library.PrefabCount} of them already linked to a Reforger prefab."));
            sb.AppendLine();
            sb.AppendLine("This folder is shared by every map. A model is converted once and reused for good, so");
            sb.AppendLine("exports after the first cost nothing for models already here.");
            sb.AppendLine();
            sb.AppendLine("- `models/`   : one `.obj` plus `.mtl` per model, most detailed visual LOD.");
            sb.AppendLine("- `textures/` : the referenced Arma textures as PNG.");
            sb.AppendLine("- `library.json` : the index, including the Reforger prefab of each model once known.");
            sb.AppendLine("- `awaiting-prefab.csv` : converted models that still have no Reforger prefab.");
            sb.AppendLine();
            sb.AppendLine("## 1. OBJ to FBX");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("blender --background --python convert.py");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Writes an `fbx/` folder here. Already converted files are skipped, so re-running after a");
            sb.AppendLine("sweep only handles the new models.");
            sb.AppendLine();
            sb.AppendLine("## 2. Into Reforger");
            sb.AppendLine();
            sb.AppendLine("Import `fbx/` and `textures/` into your addon: the Workbench turns each FBX into `.xob`");
            sb.AppendLine("on import. Build a prefab (`.et`) per model.");
            sb.AppendLine();
            sb.AppendLine("## 3. Close the loop");
            sb.AppendLine();
            sb.AppendLine("Harvest the prefab ResourceNames back into this library so exports place the models");
            sb.AppendLine("directly instead of listing them as unmapped:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("grma3 portlink --addon \"<path to your addon folder>\"");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Licensing");
            sb.AppendLine();
            sb.AppendLine("These are Bohemia Interactive assets extracted from your own Arma 3 install. Redistributing");
            sb.AppendLine("them outside Arma 3 is governed by their licence: check it before publishing anything.");
            return sb.ToString();
        }

        /// <summary>Blender batch that turns every OBJ of the library into an FBX.</summary>
        private const string BlenderBatchScript = @"# Game Realistic Map - OBJ to FBX batch for the Arma 3 model library.
# Run with:  blender --background --python convert.py
import bpy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, 'models')
OUTPUT = os.path.join(HERE, 'fbx')


def clear_scene():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def import_obj(path):
    # Blender 4.x renamed the operator; keep working on 3.x too
    if hasattr(bpy.ops.wm, 'obj_import'):
        bpy.ops.wm.obj_import(filepath=path)
    else:
        bpy.ops.import_scene.obj(filepath=path)


def main():
    if not os.path.isdir(MODELS):
        print('no models folder at ' + MODELS)
        sys.exit(1)

    os.makedirs(OUTPUT, exist_ok=True)
    names = sorted(n for n in os.listdir(MODELS) if n.lower().endswith('.obj'))
    print('converting %d models' % len(names))

    done = 0
    skipped = 0
    for name in names:
        target = os.path.join(OUTPUT, os.path.splitext(name)[0] + '.fbx')
        if os.path.exists(target):
            skipped += 1
            continue
        clear_scene()
        try:
            import_obj(os.path.join(MODELS, name))
            bpy.ops.export_scene.fbx(
                filepath=target,
                use_selection=False,
                apply_unit_scale=True,
                axis_forward='-Z',
                axis_up='Y',
                path_mode='COPY',
                embed_textures=False)
            done += 1
        except Exception as error:
            print('FAILED %s: %s' % (name, error))

    print('done: %d written, %d already present, in %s' % (done, skipped, OUTPUT))


main()
";
    }
}
