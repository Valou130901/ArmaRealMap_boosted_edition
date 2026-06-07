using System.Diagnostics;
using System.Numerics;
using PureHDF;
using PureHDF.Selections;
using WeatherStats;
using WeatherStats.Stats;

namespace WeatherStatsGenerator
{
    internal class Worker
    {
        internal static long processed = 0;
        private readonly DataFiles files;

        private readonly int timeCountAsInteger;

        // Buffers to limit memory allocation needs at each data point
        private readonly ulong timeCount;
        private readonly float[] windSpeed;
        private readonly WindDirection8[] windAngle;
        private readonly float[] tempC;
        private readonly float[] humidity;

        public Worker(DataFiles files)
        {
            this.files = files;

            this.timeCountAsInteger = (int)files.TimeValues.Length;
            this.timeCount = (ulong)files.TimeValues.Length;
            this.windSpeed = new float[timeCountAsInteger];
            this.windAngle = new WindDirection8[timeCountAsInteger];
            this.tempC = new float[timeCountAsInteger];
            this.humidity = new float[timeCountAsInteger];
        }

        public List<MonthWeatherStatsPoint> Extract(ulong latIndex, bool[] mask, Stopwatch globalStopwatch)
        {
            using var northward = files.Open(DataFiles.NorthwardWindFile);
            using var eastward = files.Open(DataFiles.EastwardWindFile);
            using var temperature = files.Open(DataFiles.AirTemperatureFile);
            using var dewpoint = files.Open(DataFiles.DewPointTemperatureFile);

            Console.WriteLine($"{globalStopwatch.Elapsed}: Start reading {latIndex}");
            var readStopwatch = Stopwatch.StartNew();
            var rawDy = northward.Dataset("northward_wind_at_10_metres").Read<float[]>(CreateSelectionLat(latIndex));
            var rawDx = eastward.Dataset("eastward_wind_at_10_metres").Read<float[]>(CreateSelectionLat(latIndex));
            var rawTemp = temperature.Dataset("air_temperature_at_2_metres").Read<float[]>(CreateSelectionLat(latIndex));
            var rawDew = dewpoint.Dataset("dew_point_temperature_at_2_metres").Read<float[]>(CreateSelectionLat(latIndex));
            readStopwatch.Stop();
            Console.WriteLine($"{globalStopwatch.Elapsed}: Done read {latIndex} in {readStopwatch.ElapsedMilliseconds} msec");

            var maxLonIndex = (ulong)files.LonValues.Length;
            var results = new List<MonthWeatherStatsPoint>();

            for (ulong lonIndex = 0; lonIndex < maxLonIndex; ++lonIndex)
            {
                if (!mask[lonIndex])
                {
                    continue;
                }

                var indices = Enumerable.Range(0, timeCountAsInteger).Select(i => (i * files.LonValues.Length) + (int)lonIndex).ToArray();

                var dy = indices.Select(i => rawDy[i]).ToArray();
                var dx = indices.Select(i => rawDx[i]).ToArray();
                var temp = indices.Select(i => rawTemp[i]).ToArray();
                var dew = indices.Select(i => rawDew[i]).ToArray();

                results.Add(Compute(latIndex, lonIndex, dy, dx, temp, dew));

                Interlocked.Increment(ref processed);
            }
            if (globalStopwatch.ElapsedMilliseconds > 0)
            {
                var n = Interlocked.Read(ref processed);
                Console.WriteLine($"{globalStopwatch.Elapsed}: {n * 1000 / globalStopwatch.ElapsedMilliseconds} samples/second -- {n}");
            }
            return results;
        }

        private MonthWeatherStatsPoint Compute(ulong latIndex, ulong lonIndex, float[] dy, float[] dx, float[] temp, float[] dew)
        {
            for (int t = 0; t < timeCountAsInteger; ++t)
            {
                var wind = new Vector2(dx[t], dy[t]);
                windSpeed[t] = wind.Length();
                windAngle[t] = VariableConvert.GetWindDirection8(wind);
                tempC[t] = VariableConvert.KelvinToCelcius(temp[t]);
                humidity[t] = VariableConvert.RelativeHumidity(VariableConvert.KelvinToCelcius(dew[t]), VariableConvert.KelvinToCelcius(temp[t]));
            }

            return new MonthWeatherStatsPoint(files.LatValues[latIndex], files.LonValues[lonIndex], MonthWeatherStatsData.From(humidity, tempC, windSpeed, windAngle));
        }

        public List<MonthWeatherStatsPoint> ExtractSlow(ulong latIndex, bool[] mask, Stopwatch globalStopwatch)
        {
            using var northward = files.Open(DataFiles.NorthwardWindFile);
            using var eastward = files.Open(DataFiles.EastwardWindFile);
            using var temperature = files.Open(DataFiles.AirTemperatureFile);
            using var dewpoint = files.Open(DataFiles.DewPointTemperatureFile);

            var northwardDataset = northward.Dataset("northward_wind_at_10_metres");
            var eastwardDataset = eastward.Dataset("eastward_wind_at_10_metres");
            var temperatureDataset = temperature.Dataset("air_temperature_at_2_metres");
            var dewpointDataset = dewpoint.Dataset("dew_point_temperature_at_2_metres");

            var maxLonIndex = (ulong)files.LonValues.Length;

            var results = new List<MonthWeatherStatsPoint>();
            for (ulong lonIndex = 0; lonIndex < maxLonIndex; ++lonIndex)
            {
                if (!mask[lonIndex])
                {
                    continue;
                }

                var dy = northwardDataset.Read<float[]>(CreateSelectionPoint(latIndex, lonIndex));
                var dx = eastwardDataset.Read<float[]>(CreateSelectionPoint(latIndex, lonIndex));
                var temp = temperatureDataset.Read<float[]>(CreateSelectionPoint(latIndex, lonIndex));
                var dew = dewpointDataset.Read<float[]>(CreateSelectionPoint(latIndex, lonIndex));

                results.Add(Compute(latIndex, lonIndex, dy, dx, temp, dew));

                var n = Interlocked.Increment(ref processed);
                if (n % 100 == 0)
                {
                    Console.WriteLine($"{globalStopwatch.Elapsed}: {n * 1000 / globalStopwatch.ElapsedMilliseconds} samples/second -- {n}");
                }
            }
            return results;
        }

        private DelegateSelection CreateSelectionPoint(ulong latIndex, ulong lonIndex)
        {
            return new DelegateSelection(
                totalElementCount: timeCount, 
                _ => Enumerable.Range(0, timeCountAsInteger).Select(t => new Step(Coordinates: new ulong[] { (ulong)t, latIndex, lonIndex }, ElementCount: 1)));
        }

        private DelegateSelection CreateSelectionLat(ulong latIndex)
        {
            return new DelegateSelection(
                totalElementCount: timeCount * (ulong)files.LonValues.Length, 
                _ => Enumerable.Range(0, timeCountAsInteger).Select(t => new Step(Coordinates: new ulong[] { (ulong)t, latIndex, 0 }, ElementCount: (ulong)files.LonValues.Length)));
        }
    }
}
