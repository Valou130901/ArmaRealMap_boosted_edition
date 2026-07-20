using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using GameRealisticMap.ElevationModel;
using GeoAPI.Geometries;
using Pmad.ProgressTracking;

namespace GameRealisticMap.ManMade.Buildings
{
    /// <summary>
    /// Downloads swissBUILDINGS3D 2.0 (real LoD2 building meshes with roofs, swisstopo open data)
    /// for a terrain area and returns triangles in terrain coordinates (Z = absolute altitude).
    /// Only works for maps located in Switzerland.
    /// </summary>
    public class SwissBuildings3dDownloader
    {
        public record struct MeshTriangle(Vector3 A, Vector3 B, Vector3 C);

        /// <summary>
        /// One building: in the DXF each building is a separate POLYLINE polyface mesh, so they
        /// stay individually addressable (one editable object per building in the target engine).
        /// </summary>
        public sealed class BuildingMesh
        {
            public List<MeshTriangle> Triangles { get; } = new List<MeshTriangle>();
        }

        public static void Lv95ToWgs84(double e, double n, out double lat, out double lon)
        {
            var y = (e - 2600000) / 1000000;
            var x = (n - 1200000) / 1000000;
            var lambda = 2.6779094 + 4.728982 * y + 0.791484 * y * x + 0.1306 * y * x * x - 0.0436 * y * y * y;
            var phi = 16.9023892 + 3.238272 * x - 0.270978 * y * y - 0.002528 * x * x - 0.0447 * y * y * x - 0.0140 * x * x * x;
            lon = lambda * 100 / 36;
            lat = phi * 100 / 36;
        }

        public static async Task<List<BuildingMesh>> LoadAsync(IProgressScope scope, ITerrainArea area)
        {
            var bounds = new LatLngBounds(area);
            var cacheDir = Path.Combine(Path.GetTempPath(), "SwisstopoBuildingsCache");
            Directory.CreateDirectory(cacheDir);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "GameRealisticMap/1.0");

            var stacUrl = FormattableString.Invariant(
                $"https://data.geo.admin.ch/api/stac/v0.9/collections/ch.swisstopo.swissbuildings3d_2/items?bbox={bounds.Left},{bounds.Bottom},{bounds.Right},{bounds.Top}&limit=200");

            scope.WriteLine("Querying Swisstopo STAC API (swissBUILDINGS3D 2.0)...");
            var urls = new List<string>();
            var response = await httpClient.GetStringAsync(stacUrl).ConfigureAwait(false);
            using (var json = JsonDocument.Parse(response))
            {
                foreach (var item in json.RootElement.GetProperty("features").EnumerateArray())
                {
                    foreach (var asset in item.GetProperty("assets").EnumerateObject())
                    {
                        if (asset.Name.EndsWith(".dxf.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            var href = asset.Value.GetProperty("href").GetString();
                            if (!string.IsNullOrEmpty(href))
                            {
                                urls.Add(href);
                            }
                            break;
                        }
                    }
                }
            }
            scope.WriteLine($"Found {urls.Count} building tiles.");

            var buildings = new List<BuildingMesh>();
            using var report = scope.CreateInteger("swissBUILDINGS3D", urls.Count);
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
                    await ParseDxfZip(localZipPath, area, buildings).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    scope.WriteLine($"Tile '{Path.GetFileName(url)}' ignored: {ex.Message}");
                }
                done++;
                report.Report(done);
            }
            scope.WriteLine($"swissBUILDINGS3D: {buildings.Count} buildings ({buildings.Sum(b => b.Triangles.Count)} triangles) within map bounds.");
            return buildings;
        }

        private static async Task ParseDxfZip(string zipPath, ITerrainArea area, List<BuildingMesh> buildings)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                await ParseDxf(reader, area, buildings).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Minimal DXF reader: extracts 3DFACE entities and POLYLINE polyface meshes
        /// (swissBUILDINGS3D 2.0 stores buildings as polyface meshes: POLYLINE followed by
        /// VERTEX records — flag 192 = mesh vertex, flag 128 = face with 1-based indices in 71..74).
        /// </summary>
        private static async Task ParseDxf(StreamReader reader, ITerrainArea area, List<BuildingMesh> buildings)
        {
            // Triangles of the POLYLINE currently being read (one polyface mesh = one building)
            var triangles = new List<MeshTriangle>();
            var size = area.SizeInMeters;
            var corners = new double[4][]; // e, n, alt per corner (3DFACE)
            for (var i = 0; i < 4; i++)
            {
                corners[i] = new double[3];
            }

            var meshVertices = new List<double[]>(); // polyface mesh vertices
            var meshFaces = new List<int[]>(); // polyface faces, 1-based indices
            var currentVertex = new double[3];
            var currentFace = new int[4];
            var vertexFlags = 0;

            string? codeLine;
            var entity = "";
            var cornerMask = 0;

            void CloseEntity()
            {
                switch (entity)
                {
                    case "3DFACE":
                        EmitFace(area, size, corners, cornerMask, triangles);
                        break;
                    case "VERTEX":
                        if ((vertexFlags & 128) != 0 && (vertexFlags & 64) == 0)
                        {
                            meshFaces.Add((int[])currentFace.Clone());
                        }
                        else
                        {
                            meshVertices.Add((double[])currentVertex.Clone());
                        }
                        break;
                    case "SEQEND":
                    case "POLYLINE" when meshFaces.Count > 0:
                        break;
                }
            }

            void FlushMesh()
            {
                foreach (var face in meshFaces)
                {
                    EmitMeshFace(area, size, meshVertices, face, triangles);
                }
                meshVertices.Clear();
                meshFaces.Clear();
                if (triangles.Count > 0)
                {
                    var building = new BuildingMesh();
                    building.Triangles.AddRange(triangles);
                    buildings.Add(building);
                    triangles.Clear();
                }
            }

            while ((codeLine = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                var valueLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (valueLine == null)
                {
                    break;
                }
                if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    continue;
                }
                if (code == 0)
                {
                    CloseEntity();
                    var next = valueLine.Trim().ToUpperInvariant();
                    if (next == "POLYLINE" || (next != "VERTEX" && next != "SEQEND" && meshFaces.Count > 0))
                    {
                        FlushMesh();
                    }
                    entity = next;
                    cornerMask = 0;
                    vertexFlags = 0;
                    Array.Clear(currentFace);
                    continue;
                }
                switch (entity)
                {
                    case "3DFACE" when code >= 10 && code <= 33:
                        {
                            var corner = code % 10;
                            var axis = (code / 10) - 1;
                            if (corner < 4 && axis >= 0 && axis < 3
                                && double.TryParse(valueLine.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                            {
                                corners[corner][axis] = value;
                                if (axis == 0)
                                {
                                    cornerMask |= 1 << corner;
                                }
                            }
                            break;
                        }
                    case "VERTEX" when code == 10 || code == 20 || code == 30:
                        if (double.TryParse(valueLine.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var coordinate))
                        {
                            currentVertex[(code / 10) - 1] = coordinate;
                        }
                        break;
                    case "VERTEX" when code == 70:
                        int.TryParse(valueLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out vertexFlags);
                        break;
                    case "VERTEX" when code >= 71 && code <= 74:
                        if (int.TryParse(valueLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                        {
                            currentFace[code - 71] = Math.Abs(index); // negative = invisible edge
                        }
                        break;
                }
            }
            CloseEntity();
            FlushMesh();
        }

        private static void EmitMeshFace(ITerrainArea area, float size, List<double[]> vertices, int[] face, List<MeshTriangle> triangles)
        {
            var count = face[3] != 0 ? 4 : (face[2] != 0 ? 3 : 0);
            if (count < 3)
            {
                return;
            }
            var points = new Vector3[4];
            for (var i = 0; i < count; i++)
            {
                var index = face[i] - 1; // 1-based
                if (index < 0 || index >= vertices.Count)
                {
                    return;
                }
                var vertex = vertices[index];
                Lv95ToWgs84(vertex[0], vertex[1], out var lat, out var lon);
                var terrainPoint = area.LatLngToTerrainPoint(new Coordinate(lon, lat));
                points[i] = new Vector3(terrainPoint.X, terrainPoint.Y, (float)vertex[2]);
            }
            var anyInside = false;
            for (var i = 0; i < count; i++)
            {
                if (points[i].X >= 0 && points[i].X <= size && points[i].Y >= 0 && points[i].Y <= size)
                {
                    anyInside = true;
                    break;
                }
            }
            if (!anyInside)
            {
                return;
            }
            triangles.Add(new MeshTriangle(points[0], points[1], points[2]));
            if (count == 4 && Vector3.DistanceSquared(points[2], points[3]) > 0.0001f)
            {
                triangles.Add(new MeshTriangle(points[0], points[2], points[3]));
            }
        }

        private static void EmitFace(ITerrainArea area, float size, double[][] corners, int cornerMask, List<MeshTriangle> triangles)
        {
            if ((cornerMask & 0b0111) != 0b0111)
            {
                return; // Not even a triangle
            }
            var points = new Vector3[4];
            var count = (cornerMask & 0b1000) != 0 ? 4 : 3;
            for (var i = 0; i < count; i++)
            {
                Lv95ToWgs84(corners[i][0], corners[i][1], out var lat, out var lon);
                var terrainPoint = area.LatLngToTerrainPoint(new Coordinate(lon, lat));
                points[i] = new Vector3(terrainPoint.X, terrainPoint.Y, (float)corners[i][2]);
            }
            // Skip faces fully outside the map
            var anyInside = false;
            for (var i = 0; i < count; i++)
            {
                if (points[i].X >= 0 && points[i].X <= size && points[i].Y >= 0 && points[i].Y <= size)
                {
                    anyInside = true;
                    break;
                }
            }
            if (!anyInside)
            {
                return;
            }
            triangles.Add(new MeshTriangle(points[0], points[1], points[2]));
            if (count == 4 && Vector3.DistanceSquared(points[2], points[3]) > 0.0001f)
            {
                triangles.Add(new MeshTriangle(points[0], points[2], points[3]));
            }
        }
    }
}
