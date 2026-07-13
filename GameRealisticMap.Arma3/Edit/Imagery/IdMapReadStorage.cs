using GameRealisticMap.Arma3.Assets;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Arma3.IO;
using Pmad.HugeImages.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.Arma3.Edit.Imagery
{
    internal sealed class IdMapReadStorage : IHugeImageStorageSlot
    {
        private readonly IImageryPartitioner partitioner;
        private readonly IGameFileSystem fileSystem;
        private readonly string path;
        private readonly Dictionary<string, TerrainMaterial> materials;
        private readonly int idMapMultiplier;

        public IdMapReadStorage(IImageryPartitioner partitioner, IGameFileSystem fileSystem, string path, TerrainMaterialLibrary library, IArma3MapConfig config)
        {
            this.partitioner = partitioner;
            this.fileSystem = fileSystem;
            this.path = path;
            idMapMultiplier = config.IdMapMultiplier;
            materials = library.Definitions.Select(d => d.Material).ToDictionary(m => m.GetColorTexturePath(config), m => m, StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {

        }

        public async Task<Image<TPixel>?> LoadImagePart<TPixel>(int partId)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var part = partitioner.GetPartFromId(partId);

            var textures = (await GetTexturesFromRvMat(part)).Select(t => t.Id).ToList();
            while (textures.Count < 6)
            {
                // Game map tiles can use less than 6 textures: pad to keep mask decoding safe
                textures.Add(textures[textures.Count - 1]);
            }

            var imageFileName = $"{path}\\data\\layers\\M_{part.X:000}_{part.Y:000}_lca.png";
            using var streamImage = fileSystem.OpenFileIfExists(imageFileName);
            if (streamImage == null)
            {
                throw new FileNotFoundException($"File '{imageFileName}' was not found.");
            }
            var maskImage = await Image.LoadAsync<Rgba32>(streamImage);

            // Old game maps use tiny solid-color placeholder masks for single-texture tiles:
            // scale to the expected tile size (nearest neighbor, mask colors are discrete)
            var expectedSize = part.Size * idMapMultiplier;
            if (maskImage.Width != expectedSize || maskImage.Height != expectedSize)
            {
                maskImage.Mutate(m => m.Resize(new ResizeOptions
                {
                    Size = new Size(expectedSize, expectedSize),
                    Sampler = KnownResamplers.NearestNeighbor,
                    Mode = ResizeMode.Stretch
                }));
            }

            var finalImage = new Image<TPixel>(maskImage.Width, maskImage.Height);
            var px = new TPixel();
            for (int x = 0; x < maskImage.Width; ++x)
            {
                for (int y = 0; y < maskImage.Height; ++y)
                {
                    px.FromRgb24(GetColor(maskImage[x, y], textures));
                    finalImage[x, y] = px;
                }
            }
            return finalImage;
        }

        private async Task<List<TerrainMaterial>> GetTexturesFromRvMat(ImageryTile part)
        {
            var rvmatFileName = $"{path}\\data\\layers\\P_{part.X:000}-{part.Y:000}.rvmat";
            using var streamRvmat = fileSystem.OpenFileIfExists(rvmatFileName);
            if (streamRvmat == null)
            {
                throw new FileNotFoundException($"File '{rvmatFileName}' was not found.");
            }
            var rvmatContent = await new StreamReader(streamRvmat).ReadToEndAsync();
            var matches = IdMapHelper.TextureMatch.Matches(rvmatContent);
            var textures = matches.Select(m => m.Groups[1].Value)
                .Select(GetMaterial)
                .ToList();
            if (textures.Count == 0)
            {
                throw new ApplicationException($"'{rvmatFileName}' is invalid or corrupted.");
            }
            return textures;
        }

        private TerrainMaterial GetMaterial(string texture)
        {
            lock (materials)
            {
                if (!materials.TryGetValue(texture, out var material))
                {
                    // Texture unknown to the material library (happens with imported game maps):
                    // generate a stable ad-hoc material so the id map can still be reconstructed
                    var hash = 17;
                    foreach (var c in texture.ToLowerInvariant())
                    {
                        hash = hash * 31 + c;
                    }
                    material = new TerrainMaterial(texture, texture, new Rgb24((byte)(hash >> 16), (byte)(hash >> 8), (byte)hash), null);
                    materials.Add(texture, material);
                }
                return material;
            }
        }

        internal static Rgb24 GetColor(Rgba32 rgba32, List<Rgb24> textures)
        {
            if (rgba32.B == 255)
            {
                if (rgba32.A == 0)
                {
                    return textures[5];
                }
                if (rgba32.A == 128)
                {
                    return textures[4];
                }
                return textures[3];
            }
            if (rgba32.G == 255)
            {
                return textures[2];
            }
            if (rgba32.R == 255)
            {
                return textures[1];
            }
            return textures[0];
        }

        public Task SaveImagePart<TPixel>(int partId, Image<TPixel> partImage)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            throw new NotSupportedException();
        }
    }
}
