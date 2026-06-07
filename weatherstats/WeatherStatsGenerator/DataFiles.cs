using PureHDF;
using PureHDF.VOL.Native;

namespace WeatherStatsGenerator
{
    internal sealed class DataFiles
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public const string NorthwardWindFile = "northward_wind_at_10_metres.nc";
        public const string EastwardWindFile = "eastward_wind_at_10_metres.nc";
        public const string AirTemperatureFile = "air_temperature_at_2_metres.nc";
        public const string DewPointTemperatureFile = "dew_point_temperature_at_2_metres.nc";
        public const string PrecipitationFile = "precipitation_amount_1hour_Accumulation.nc";

        private readonly string basePath;

        public DataFiles(string basePath)
        {
            this.basePath = basePath;

            using (var northward = Open(NorthwardWindFile))
            {
                LatValues = northward.Dataset("lat").Read<float[]>();
                LonValues = northward.Dataset("lon").Read<float[]>();
                TimeValues = northward.Dataset("time0").Read<double[]>();
            }
            Check(EastwardWindFile);
            Check(AirTemperatureFile);
            Check(DewPointTemperatureFile);
            Check(PrecipitationFile, "time1");
            StartTime = UnixEpoch.AddSeconds(TimeValues[0]);
            EndTime = UnixEpoch.AddSeconds(TimeValues[TimeValues.Length-1]);
        }

        public string BasePath => basePath;

        private void Check(string fileName, string time = "time0")
        {
            using (var file = Open(fileName))
            {
                Check(LatValues, file.Dataset("lat").Read<float[]>());
                Check(LonValues, file.Dataset("lon").Read<float[]>());
                Check(TimeValues, file.Dataset(time).Read<double[]>());
            }
        }

        private void Check<T>(T[] expected, T[] actual)
        {
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException("Dimensions values mismatch");
            }
        }

        public float[] LatValues { get; }

        public float[] LonValues { get; }

        public double[] TimeValues { get; }

        public DateTime StartTime { get; }

        public DateTime EndTime { get; }

        public NativeFile Open(string name)
        {
            return H5File.OpenRead(Path.Combine(basePath, name));
        }

    }
}
