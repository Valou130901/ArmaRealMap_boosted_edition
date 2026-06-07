using System.Diagnostics;
using System.Text.Json;
using CommandLine;
using GeoJSON.Text.Feature;
using GeoJSON.Text.Geometry;
using PureHDF.Filters;
using WeatherStats.Databases;
using WeatherStats.Stats;

namespace WeatherStatsGenerator
{
    internal class Program
    {
        static int Main(string[] args)
        {
            try
            {
                return Parser.Default.ParseArguments<DigestOptions, AssembleOptions, AverageOptions, QueryOptions, ExportMaskOptions>(args)
                  .MapResult(
                    (DigestOptions opts) => Digest(opts),
                    (AssembleOptions opts) => Assemble(opts),
                    (AverageOptions opts) => Average(opts),
                    (QueryOptions opts) => Query(opts),
                    (ExportMaskOptions opts) => ExportMask(opts),
                    errs => 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return 2;
            }
        }

        private static int ExportMask(ExportMaskOptions opts)
        {
            var maskRaw = ReadRawMask();
            var polygons = new List<Polygon>();
            for (var latitude = -89.5f; latitude < 90; latitude++)
            {
                var lat = GetRawMaskIndex(latitude);
                if (maskRaw.TryGetValue(lat, out var lonMak))
                {
                    for (var longitude = 0.5f; longitude < 360.5; longitude++)
                    {
                        if (lonMak[GetRawMaskIndex(longitude)] != 0)
                        {
                            polygons.Add(new Polygon(new[]{new LineString(
                                new[] {
                                new Position(latitude - 0.5, longitude - 0.5),
                                new Position(latitude - 0.5, longitude + 0.5),
                                new Position(latitude + 0.5, longitude + 0.5),
                                new Position(latitude + 0.5, longitude - 0.5),
                                new Position(latitude - 0.5, longitude - 0.5)
                                }
                                )}));
                        }
                    }
                }
            }
            File.WriteAllText(opts.Path!, JsonSerializer.Serialize(new FeatureCollection(new List<Feature> { new Feature(new MultiPolygon(polygons)) })));
            return 0;
        }

        private static Dictionary<int, List<int>> ReadRawMask()
        {
            var maskRaw = new Dictionary<int, List<int>>();
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream("WeatherStatsGenerator.landmask.json"))
            {
                maskRaw = JsonSerializer.Deserialize<Dictionary<int, List<int>>>(stream!)!;
            }

            return maskRaw;
        }

        private static int Average(AverageOptions opts)
        {
            Directory.CreateDirectory(opts.Target!);
            var first = opts.Sources!.First();
            var files = Directory.GetFiles(Path.Combine(first!, "assembly"), "*.json").Select(f => Path.GetFileName(f)).ToList();
            Parallel.ForEach(files, file =>
            {
                Console.WriteLine(file);
                var toAverage = opts.Sources!
                    .Select(m => Path.Combine(m, "assembly", file))
                    .Select(f => File.ReadAllText(f))
                    .Select(c => JsonSerializer.Deserialize<List<YearWeatherStatsPoint>>(c)!)
                    .ToList();
                var avgCell = new List<YearWeatherStatsPoint>();
                foreach (var coordinates in toAverage[0])
                {
                    var cellPerYear = toAverage.Select(s => s.First(c => c.Latitude == coordinates.Latitude && c.Longitude == coordinates.Longitude)).ToList();

                    avgCell.Add(new YearWeatherStatsPoint(
                        coordinates.Latitude,
                        coordinates.Longitude,
                        Enumerable.Range(0, 12).Select(m => MonthWeatherStatsData.Average(cellPerYear.Select(y => y.Months[m]))).ToArray()
                        ));

                }
                File.WriteAllText(Path.Combine(opts.Target!, file), JsonSerializer.Serialize(avgCell));
            });
            return 0;
        }

        private static int Query(QueryOptions opts)
        {
            var db = WeatherStatsDatabase.Create(opts.AssemblyPath!);

            var result = db.GetStats(opts.Latitude, opts.Longitude).Result;

            return 0;
        }

        private static int Assemble(AssembleOptions opts)
        {
            var months = Enumerable.Range(1, 12).Select(i => i.ToString("00")).ToList();
            var files = Directory.GetFiles(Path.Combine(opts.Path!, months[0]), "*.json").Select(f => Path.GetFileName(f)).ToList();
            var result = new Dictionary<(int, int), List<YearWeatherStatsPoint>>();
            foreach(var file in files)
            {
                Console.WriteLine(file);
                var toAssemble = months
                    .Select(m => Path.Combine(opts.Path!, m, file))
                    .Select(f => File.ReadAllText(f))
                    .Select(c => JsonSerializer.Deserialize<List<MonthWeatherStatsPoint>>(c)!)
                    .ToList();

                var count = toAssemble[0].Count;

                for(int i = 0; i < count; ++i)
                {
                    var points = toAssemble.Select(r => r[i]).ToList();
                    CheckLanLon(points[0].Latitude, points[0].Longitude, points.Skip(1));
                    var assemblyPoint = new YearWeatherStatsPoint(points[0].Latitude, points[0].Longitude, points.Select(p => p.Data).ToArray());

                    var cellIndex = WeatherStatsDatabase.GetCellIndex(points[0].Latitude, points[0].Longitude);
                    if (!result.TryGetValue(cellIndex, out var cell))
                    {
                        result.Add(cellIndex, cell = new List<YearWeatherStatsPoint>());
                    }
                    cell.Add(assemblyPoint);
                }
            }
            Directory.CreateDirectory(Path.Combine(opts.Path!, "assembly"));
            foreach(var pair in result)
            {
                var name = WeatherStatsDatabase.GetCellFileName(pair.Key);
                Console.WriteLine(name);
                File.WriteAllText(Path.Combine(opts.Path!, "assembly", name), JsonSerializer.Serialize(pair.Value));
            }
            return 0;
        }


        private static void CheckLanLon(float latitude, float longitude, IEnumerable<MonthWeatherStatsPoint> enumerable)
        {
            if (enumerable.Any(p =>p.Latitude != latitude) || enumerable.Any(p => p.Longitude != longitude))
            {
                throw new InvalidOperationException();
            }
        }

        private static int Digest(DigestOptions opts)
        {
            H5Filter.Register(new DeflateISALFilter());
            var fileset = new DataFiles(opts.Path!);
            var mask = GenerateMask(fileset);
            var options = new ParallelOptions();
            options.MaxDegreeOfParallelism = 10;
            var sw1 = Stopwatch.StartNew();
            Parallel.For(0, fileset.LatValues.Length, options, latIndex =>
            {
                var lonMak = mask[latIndex];
                if (lonMak.Length != 0)
                {
                    var worker = new Worker(fileset);
                    var result = worker.Extract((ulong)latIndex, lonMak, sw1);
                    File.WriteAllText(Path.Combine(fileset.BasePath, GetFileName(latIndex, fileset)), JsonSerializer.Serialize(result));
                }
            });
            sw1.Stop();
            return 1;
        }

        private static string GetFileName(int latIndex, DataFiles fileset)
        {
            var value = fileset.LatValues[latIndex];

            return FormattableString.Invariant($"stats_{(value<0?"S":"N")}{MathF.Abs(value):00.00}.json");
        }

        private static bool[][] GenerateMask(DataFiles fileset)
        {
            var maskRaw = ReadRawMask();
            var items = 0;
            var mask = new bool[fileset.LatValues.Length][];
            for (var latIndex = 0; latIndex < fileset.LatValues.Length; ++latIndex)
            {
                var lat = GetRawMaskIndex(fileset.LatValues[latIndex]);
                if (maskRaw.TryGetValue(lat, out var lonMak))
                {
                    mask[latIndex] = Enumerable.Range(0, fileset.LonValues.Length).Select(lonIndex => lonMak[GetRawMaskIndex(fileset.LonValues[lonIndex])] != 0).ToArray();
                    items += mask[latIndex].Count(e => e);
                }
                else
                {
                    mask[latIndex] = new bool[0];
                }
            }
            return mask;
        }

        public static int GetRawMaskIndex(float value)
        {
            return (int)Math.Round(Math.Floor(value));
        }
    }
}