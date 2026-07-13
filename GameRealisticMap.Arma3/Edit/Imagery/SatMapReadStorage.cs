using GameRealisticMap.Arma3.IO;
using Pmad.HugeImages.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.Arma3.Edit.Imagery
{
    internal sealed class SatMapReadStorage : IHugeImageStorageSlot
    {
        private readonly IImageryPartitioner partitioner;
        private readonly IGameFileSystem fileSystem;
        private readonly string path;

        public SatMapReadStorage(IImageryPartitioner partitioner, IGameFileSystem fileSystem, string path)
        {
            this.partitioner = partitioner;
            this.fileSystem = fileSystem;
            this.path = path;
        }

        public void Dispose()
        {

        }

        private string GetPartFileName(int partId)
        {
            var part = partitioner.GetPartFromId(partId);
            var fileName = $"{path}\\data\\layers\\S_{part.X:000}_{part.Y:000}_lco.png";
            return fileName;
        }

        public async Task<Image<TPixel>?> LoadImagePart<TPixel>(int partId)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var fileName = GetPartFileName(partId);
            using var stream = fileSystem.OpenFileIfExists(fileName);
            if (stream == null)
            {
                throw new FileNotFoundException($"File '{fileName}' was not found.");
            }
            var image = await Image.LoadAsync<TPixel>(stream);

            // Single-color tiles (open sea...) are stored as tiny placeholder images by the
            // game tools: scale them up to the expected tile size
            var part = partitioner.GetPartFromId(partId);
            var expectedSize = part.ImageBottomRight.X - part.ImageTopLeft.X;
            if (image.Width != expectedSize || image.Height != expectedSize)
            {
                image.Mutate(i => i.Resize(expectedSize, expectedSize, KnownResamplers.NearestNeighbor));
            }
            return image;
        }

        public Task SaveImagePart<TPixel>(int partId, Image<TPixel> partImage)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            throw new NotSupportedException();
        }


    }
}
