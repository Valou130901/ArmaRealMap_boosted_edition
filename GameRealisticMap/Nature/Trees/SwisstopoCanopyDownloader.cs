using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using GameRealisticMap.Configuration;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Geometries;
using Pmad.ProgressTracking;

namespace GameRealisticMap.Nature.Trees
{
    /// <summary>
    /// Finds real trees by subtracting the terrain from swisstopo's surface model, then reading the
    /// tops out of what is left.
    /// </summary>
    /// <remarks>
    /// swissSURFACE3D is the height of whatever the aircraft saw first: ground where there is
    /// nothing, roof where there is a building, crown where there is a tree. Take the terrain away
    /// and what remains is the canopy, and every local high point in it is a tree that actually
    /// stands there, at the height it actually has. That replaces scattering trees at random inside
    /// a forest polygon, which is what the pipeline does today.
    /// <para>
    /// Only 0.5 m tiles are published, one square kilometre each: 19 MB compressed, 119 MB and four
    /// million rows once unpacked, about a second to read. They are handled one at a time and thrown
    /// away as soon as their trees are out, so the memory cost stays at one tile whatever the size
    /// of the map.
    /// </para>
    /// </remarks>
    public class SwisstopoCanopyDownloader
    {
        /// <summary>Below this a bump in the canopy is a bush, a wall or noise, not a tree.</summary>
        public const float MinTreeHeight = 3f;

        /// <summary>Above this it is not a tree: a mast, a crane, or a hole in the terrain model.</summary>
        public const float MaxTreeHeight = 45f;

        /// <summary>One found tree, in terrain coordinates.</summary>
        public readonly record struct CanopyTree(float X, float Y, float Height);

        public static async Task<List<CanopyTree>> LoadAsync(
            IProgressScope scope, ITerrainArea area, ElevationGrid terrain)
        {
            var bounds = new LatLngBounds(area);
            var cacheDir = Path.Combine(Path.GetTempPath(), "SwisstopoCanopyCache");
            Directory.CreateDirectory(cacheDir);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "GameRealisticMap/1.0");

            scope.WriteLine("Querying Swisstopo STAC API (swissSURFACE3D)...");
            var urls = await SwisstopoStac.GetAssetHrefsAsync(
                httpClient,
                SwisstopoStac.ItemsUrl("ch.swisstopo.swisssurface3d-raster", bounds),
                name => name.EndsWith(".xyz.zip", StringComparison.OrdinalIgnoreCase))
                .ConfigureAwait(false);

            scope.WriteLine($"Found {urls.Count} surface model tiles ({urls.Count * 19.4 / 1024:0.0} GB to fetch once).");

            var trees = new List<CanopyTree>();
            using var report = scope.CreateInteger("swissSURFACE3D canopy", urls.Count);
            var done = 0;
            foreach (var url in urls)
            {
                var localZipPath = Path.Combine(cacheDir, Path.GetFileName(url));
                if (!File.Exists(localZipPath))
                {
                    var data = await httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(localZipPath, data).ConfigureAwait(false);
                }
                try
                {
                    ExtractTiles(localZipPath, area, terrain, trees);
                }
                catch (Exception ex)
                {
                    scope.WriteLine($"Tile '{Path.GetFileName(url)}' ignored: {ex.Message}");
                }
                done++;
                report.Report(done);
            }

            scope.WriteLine($"Canopy: {trees.Count} trees found in the surface model.");
            return trees;
        }

        private static void ExtractTiles(string zipPath, ITerrainArea area, ElevationGrid terrain, List<CanopyTree> trees)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xyz", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            ReadCanopy(reader, area, terrain, trees);
        }

        /// <summary>
        /// Reads one tile into a canopy raster, then keeps its local maxima.
        /// </summary>
        /// <remarks>
        /// The rows come out in a fixed order, but nothing here relies on that: each row carries its
        /// own easting and northing, so the raster is addressed from the coordinates themselves.
        /// </remarks>
        private static void ReadCanopy(TextReader reader, ITerrainArea area, ElevationGrid terrain, List<CanopyTree> trees)
        {
            const double Step = 0.5;
            const int Side = 2000;

            var canopy = new float[Side * Side];
            var minE = double.MaxValue;
            var minN = double.MaxValue;
            var rows = new List<(double E, double N, float Z)>(Side * Side);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var first = line.Length > 0 ? line[0] : ' ';
                if (!char.IsDigit(first) && first != '-' && first != '﻿')
                {
                    continue; // header
                }
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    continue;
                }
                if (e < minE) minE = e;
                if (n < minN) minN = n;
                rows.Add((e, n, z));
            }
            if (rows.Count == 0)
            {
                return;
            }

            foreach (var (e, n, z) in rows)
            {
                var x = (int)Math.Round((e - minE) / Step);
                var y = (int)Math.Round((n - minN) / Step);
                if (x >= 0 && x < Side && y >= 0 && y < Side)
                {
                    canopy[(y * Side) + x] = z;
                }
            }

            // Surface height becomes height above ground
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    var index = (y * Side) + x;
                    var surface = canopy[index];
                    if (surface == 0f)
                    {
                        canopy[index] = float.MinValue;
                        continue;
                    }
                    canopy[index] = surface - terrain.ElevationAt(
                        ToTerrainPoint(area, minE + (x * Step), minN + (y * Step)));
                }
            }

            FindTops(canopy, Side, Step, minE, minN, area, trees);
        }

        /// <summary>
        /// Swiss grid coordinates to the map's own frame, through WGS84.
        /// </summary>
        private static TerrainPoint ToTerrainPoint(ITerrainArea area, double e, double n)
        {
            ManMade.Buildings.SwissBuildings3dDownloader.Lv95ToWgs84(e, n, out var lat, out var lon);
            return area.LatLngToTerrainPoint(new GeoAPI.Geometries.Coordinate(lon, lat));
        }

        /// <summary>
        /// Keeps every point that is the highest thing within its own crown.
        /// </summary>
        /// <remarks>
        /// The search radius grows with the height, because a tall tree owns a wider crown than a
        /// young one: a quarter of the height, held between 1.5 m and 5 m. A fixed radius either
        /// splits big trees into several stems or swallows whole thickets of small ones.
        /// </remarks>
        private static void FindTops(float[] canopy, int side, double step, double minE, double minN,
            ITerrainArea area, List<CanopyTree> trees)
        {
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var height = canopy[(y * side) + x];
                    if (height < MinTreeHeight || height > MaxTreeHeight)
                    {
                        continue;
                    }

                    var radius = (int)Math.Round(Math.Clamp(height * 0.25f, 1.5f, 5f) / step);
                    var isTop = true;
                    for (var dy = -radius; dy <= radius && isTop; dy++)
                    {
                        var ny = y + dy;
                        if (ny < 0 || ny >= side)
                        {
                            continue;
                        }
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var nx = x + dx;
                            if (nx < 0 || nx >= side || (dx == 0 && dy == 0))
                            {
                                continue;
                            }
                            var other = canopy[(ny * side) + nx];
                            // Strictly greater on one side and greater-or-equal on the other, so a
                            // flat top of equal samples yields exactly one tree rather than none
                            if (other > height || (other == height && (dy < 0 || (dy == 0 && dx < 0))))
                            {
                                isTop = false;
                                break;
                            }
                        }
                    }
                    if (!isTop)
                    {
                        continue;
                    }

                    var point = ToTerrainPoint(area, minE + (x * step), minN + (y * step));
                    if (point.X >= 0 && point.Y >= 0
                        && point.X <= area.SizeInMeters && point.Y <= area.SizeInMeters)
                    {
                        trees.Add(new CanopyTree(point.X, point.Y, height));
                    }
                }
            }
        }
    }
}
