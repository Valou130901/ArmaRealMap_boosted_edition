using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GameRealisticMap.Geometries;
using Pmad.ProgressTracking;
using GeoAPI.Geometries;

namespace GameRealisticMap.ElevationModel
{
    public class SwisstopoElevationDownloader
    {
        private readonly List<TileData> _tiles = new List<TileData>();

        private class TileData
        {
            public double MinE { get; set; }
            public double MaxE { get; set; }
            public double MinN { get; set; }
            public double MaxN { get; set; }
            public double Step { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public float[] Z { get; set; } = new float[0];

            public float GetElevation(double e, double n)
            {
                int x = (int)Math.Round((e - MinE) / Step);
                int y = (int)Math.Round((n - MinN) / Step);
                
                if (x < 0) x = 0;
                if (x >= Width) x = Width - 1;
                if (y < 0) y = 0;
                if (y >= Height) y = Height - 1;

                return Z[y * Width + x];
            }
        }

        public static void Wgs84ToLv95(double lat, double lon, out double e, out double n)
        {
            double phi = (lat * 3600 - 169028.66) / 10000;
            double lambda = (lon * 3600 - 26782.5) / 10000;

            e = 2600072.37
                + 211455.93 * lambda
                - 10938.51 * lambda * phi
                - 0.36 * lambda * Math.Pow(phi, 2)
                - 44.54 * Math.Pow(lambda, 3);

            n = 1200147.07
                + 308807.95 * phi
                + 3745.25 * Math.Pow(lambda, 2)
                + 76.63 * Math.Pow(phi, 2)
                - 194.56 * Math.Pow(lambda, 2) * phi
                + 119.79 * Math.Pow(phi, 3);
        }

        public double GetElevation(Coordinate latLong)
        {
            Wgs84ToLv95(latLong.Y, latLong.X, out double e, out double n);

            foreach (var tile in _tiles)
            {
                if (e >= tile.MinE - (tile.Step / 2) && e <= tile.MaxE + (tile.Step / 2) && 
                    n >= tile.MinN - (tile.Step / 2) && n <= tile.MaxN + (tile.Step / 2))
                {
                    return tile.GetElevation(e, n);
                }
            }

            // Fallback to nearest tile to prevent NaNs at edges or missing STAC tiles
            TileData nearestTile = null;
            double minDist = double.MaxValue;
            foreach (var tile in _tiles)
            {
                double centerE = (tile.MinE + tile.MaxE) / 2;
                double centerN = (tile.MinN + tile.MaxN) / 2;
                double dist = (e - centerE) * (e - centerE) + (n - centerN) * (n - centerN);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearestTile = tile;
                }
            }

            if (nearestTile != null)
            {
                return nearestTile.GetElevation(e, n);
            }

            return double.NaN;
        }

        public static async Task<SwisstopoElevationDownloader> LoadAsync(IProgressScope scope, ITerrainArea area)
        {
            var downloader = new SwisstopoElevationDownloader();
            var bounds = new LatLngBounds(area);

            string cacheDir = Path.Combine(Path.GetTempPath(), "SwisstopoXYZCache");
            Directory.CreateDirectory(cacheDir);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "GameRealisticMap/1.0");

            string stacUrl = $"https://data.geo.admin.ch/api/stac/v0.9/collections/ch.swisstopo.swissalti3d/items?bbox={bounds.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)},{bounds.Bottom.ToString(System.Globalization.CultureInfo.InvariantCulture)},{bounds.Right.ToString(System.Globalization.CultureInfo.InvariantCulture)},{bounds.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit=200";
            
            scope.WriteLine("Querying Swisstopo STAC API...");
            var response = await httpClient.GetStringAsync(stacUrl);
            using var json = JsonDocument.Parse(response);
            
            var items = json.RootElement.GetProperty("features");
            var urlsToDownload = new List<string>();

            foreach (var item in items.EnumerateArray())
            {
                var assets = item.GetProperty("assets");
                var assetProps = assets.EnumerateObject();
                foreach(var prop in assetProps)
                {
                    if (prop.Name.Contains("_2_2056_5728.xyz.zip"))
                    {
                        urlsToDownload.Add(prop.Value.GetProperty("href").GetString());
                        break;
                    }
                }
            }

            scope.WriteLine($"Found {urlsToDownload.Count} 2m resolution XYZ tiles.");

            using var report = scope.CreateInteger("Download & Parse Swisstopo", urlsToDownload.Count);
            int done = 0;
            foreach (var url in urlsToDownload)
            {
                var fileName = Path.GetFileName(url);
                var localZipPath = Path.Combine(cacheDir, fileName);

                if (!File.Exists(localZipPath))
                {
                    var data = await httpClient.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(localZipPath, data);
                }

                downloader._tiles.Add(await ParseXyzZipAsync(localZipPath));
                
                done++;
                report.Report(done);
            }

            return downloader;
        }

        private static async Task<TileData> ParseXyzZipAsync(string zipPath)
        {
            var tile = new TileData();
            
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return tile;

            var lines = new List<string>();
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }
            }

            if (lines.Count == 0) return tile;

            double minE = double.MaxValue;
            double maxE = double.MinValue;
            double minN = double.MaxValue;
            double maxN = double.MinValue;

            int dataStart = 0;
            while (dataStart < lines.Count)
            {
                var firstToken = lines[dataStart].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstToken != null && double.TryParse(firstToken, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    break;
                }
                dataStart++;
            }

            if (dataStart >= lines.Count - 1) return tile;

            var parts0 = lines[dataStart].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var parts1 = lines[dataStart + 1].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            tile.Step = Math.Abs(double.Parse(parts1[0], System.Globalization.CultureInfo.InvariantCulture) - double.Parse(parts0[0], System.Globalization.CultureInfo.InvariantCulture));
            if (tile.Step == 0) tile.Step = 2.0;

            for (int i = dataStart; i < lines.Count; i++)
            {
                var line = lines[i];
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    double e = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    double n = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (e < minE) minE = e;
                    if (e > maxE) maxE = e;
                    if (n < minN) minN = n;
                    if (n > maxN) maxN = n;
                }
            }

            tile.MinE = minE;
            tile.MaxE = maxE;
            tile.MinN = minN;
            tile.MaxN = maxN;

            tile.Width = (int)Math.Round((maxE - minE) / tile.Step) + 1;
            tile.Height = (int)Math.Round((maxN - minN) / tile.Step) + 1;
            tile.Z = new float[tile.Width * tile.Height];

            for (int i = dataStart; i < lines.Count; i++)
            {
                var line = lines[i];
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    double e = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    double n = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    float z = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);

                    int x = (int)Math.Round((e - minE) / tile.Step);
                    int y = (int)Math.Round((n - minN) / tile.Step);
                    if (x >= 0 && x < tile.Width && y >= 0 && y < tile.Height)
                    {
                        tile.Z[y * tile.Width + x] = z;
                    }
                }
            }

            return tile;
        }
    }
}
