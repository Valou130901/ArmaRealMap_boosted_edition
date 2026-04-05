using GameRealisticMap.Arma3.Assets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Arma3
{
    public static class SatMapFromIdMapGenerator
    {
        public static void Generate(string idMapFile, string satMapFile, TerrainMaterialLibrary library, IProgress<double>? progress = null)
        {
            using var idMap = Image.Load<Rgba32>(idMapFile);
            using var satMap = new Image<Rgba32>(idMap.Width, idMap.Height);

            var mapping = new Dictionary<Rgba32, Image<Rgba32>>();

            foreach (var definition in library.Definitions)
            {
                var material = definition.Material;
                var idColor = new Rgba32(material.Id.R, material.Id.G, material.Id.B, 255);
                
                if (!mapping.ContainsKey(idColor))
                {
                    Image<Rgba32> tile;
                    if (material.FakeSatPngImage != null)
                    {
                        tile = Image.Load<Rgba32>(material.FakeSatPngImage);
                    }
                    else
                    {
                        tile = new Image<Rgba32>(1, 1, new Rgba32(128, 128, 128, 255));
                    }
                    mapping.Add(idColor, tile);
                }
            }

            for (int y = 0; y < idMap.Height; y++)
            {
                for (int x = 0; x < idMap.Width; x++)
                {
                    var idPixel = idMap[x, y];
                    
                    if (mapping.TryGetValue(idPixel, out var tile))
                    {
                        satMap[x, y] = tile[x % tile.Width, y % tile.Height];
                    }
                    else
                    {
                        var nearestKey = FindNearestKey(mapping.Keys, idPixel);
                        var nearestTile = mapping[nearestKey];
                        mapping[idPixel] = nearestTile; // Cache it
                        satMap[x, y] = nearestTile[x % nearestTile.Width, y % nearestTile.Height];
                    }
                }

                if (progress != null && y % 500 == 0)
                {
                    progress.Report((double)y / idMap.Height);
                }
            }

            if (progress != null)
            {
                progress.Report(1.0);
            }

            satMap.Save(satMapFile);

            foreach(var tile in mapping.Values)
            {
                tile.Dispose();
            }
        }

        private static Rgba32 FindNearestKey(IEnumerable<Rgba32> keys, Rgba32 pixel)
        {
            Rgba32 nearest = new Rgba32(128, 128, 128);
            int minDistance = int.MaxValue;

            foreach (var key in keys)
            {
                int rDiff = key.R - pixel.R;
                int gDiff = key.G - pixel.G;
                int bDiff = key.B - pixel.B;
                
                int distance = rDiff * rDiff + gDiff * gDiff + bDiff * bDiff;
                
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearest = key;
                }
            }
            return nearest;
        }
    }
}
