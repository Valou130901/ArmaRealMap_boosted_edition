using System.Text.Json;
using WeatherStats.Stats;

namespace WeatherStats.Databases
{
    public sealed class WeatherStatsFileSystemStorage : IWeatherStatsStorage
    {
        private readonly string basePath;

        public WeatherStatsFileSystemStorage(string basePath)
        {
            this.basePath = basePath;
        }

        public async Task<List<YearWeatherStatsPoint>> Load(string path)
        {
            var file = Path.Combine(basePath, path);
            if (File.Exists(file))
            {
                using (var input = File.OpenRead(file))
                {
                    return (await JsonSerializer.DeserializeAsync<List<YearWeatherStatsPoint>>(input).ConfigureAwait(false))!;
                }
            }
            return new List<YearWeatherStatsPoint>();
        }
    }
}
