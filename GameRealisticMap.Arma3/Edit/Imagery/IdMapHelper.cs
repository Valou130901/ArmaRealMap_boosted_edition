using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BIS.WRP;
using GameRealisticMap.Arma3.IO;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Arma3.Edit.Imagery
{
    public static class IdMapHelper
    {
        /// <summary>
        /// Stable id-map colour for a texture that is not in the material library (imported game
        /// maps). Derived only from the texture path, so the id map and any export listing the
        /// layers (Surface Painter .cfg) compute the exact same colour.
        /// </summary>
        public static Rgb24 GetStableId(string texture)
        {
            var hash = 17;
            foreach (var c in texture.ToLowerInvariant())
            {
                hash = hash * 31 + c;
            }
            return new Rgb24((byte)(hash >> 16), (byte)(hash >> 8), (byte)hash);
        }

        // Indentation is tab in GRM generated rvmat, spaces in rvmat un-binarized from game files
        internal static readonly Regex NormalMatch = new Regex(@"texture=""([^""]*)"";\r?\n[ \t]*texGen=1;", RegexOptions.CultureInvariant);

        internal static readonly Regex TextureMatch = new Regex(@"texture=""([^""]*)"";\r?\n[ \t]*texGen=2;", RegexOptions.CultureInvariant);

        public static Task<List<GroundDetailTexture>> GetUsedTextureList(EditableWrp wrp, IGameFileSystem projectDrive)
        {
            return GetUsedTextureList(GetRvMatList(wrp), projectDrive);
        }

        public static List<string> GetRvMatList(EditableWrp wrp)
        {
            return wrp.MatNames
                            .Where(m => !string.IsNullOrEmpty(m))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }

        public async static Task<List<GroundDetailTexture>> GetUsedTextureList(List<string> rvmat, IGameFileSystem projectDrive)
        {
            var textures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(rvmat, async (rv, ct) =>
            {
                using var file  = projectDrive.OpenFileIfExists(rv);
                if (file != null)
                {
                    var content = await new StreamReader(file).ReadToEndAsync();
                    var colors = TextureMatch.Matches(content).Select(m => m.Groups[1].Value).ToList();
                    var normals = NormalMatch.Matches(content).Select(m => m.Groups[1].Value).ToList();
                    if (colors.Count == normals.Count)
                    {
                        for( var i = 0; i < colors.Count; i++ )
                        {
                            textures.TryAdd(colors[i], normals[i]);
                        }
                    }
                }
            });
            return textures.Select(p => new GroundDetailTexture(p.Key, p.Value)).ToList();
        }
    }
}
