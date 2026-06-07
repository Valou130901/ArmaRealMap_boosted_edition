using System.Collections.Concurrent;
using WeatherStats.Stats;

namespace WeatherStats.Databases
{
    public class WeatherStatsDatabase
    {
        private readonly ConcurrentDictionary<string, Task<List<YearWeatherStatsPoint>>> cache = new ConcurrentDictionary<string, Task<List<YearWeatherStatsPoint>>>();
        private readonly IWeatherStatsStorage storage;

        public WeatherStatsDatabase(IWeatherStatsStorage storage)
        {
            this.storage = storage;
        }

        public static WeatherStatsDatabase Create(string location)
        {
            if(location.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            { 
                return new WeatherStatsDatabase(new WeatherStatsHttpStorage(new Uri(location))); 
            }
            if (Directory.Exists(location))
            {
                return new WeatherStatsDatabase(new WeatherStatsFileSystemStorage(location));
            }
            throw new DirectoryNotFoundException($"Location '{location}' was not found.");
        }

        public Task<YearWeatherStatsPoint?> GetStats(double latitude, double longitude)
        {
            if (longitude < 0)
            {
                longitude += 360;
            }
            var pointLatitude = Math.Round(Math.Round(latitude * 4) / 4, 2);
            var pointLongitude = Math.Round(Math.Round(longitude * 4) / 4, 2);
            return GetStatsExact((float)pointLatitude, (float)pointLongitude);
        }

        private async Task<YearWeatherStatsPoint?> GetStatsExact(float pointLatitude, float pointLongitude)
        {
            var name = GetCellFileName(GetCellIndex(pointLatitude, pointLongitude));

            var data = await cache.GetOrAdd(name, storage.Load).ConfigureAwait(false);

            return data.FirstOrDefault(d => d.Latitude == pointLatitude && d.Longitude == pointLongitude);
        }

        public static (int,int) GetCellIndex(float pointLatitude, float pointLongitude)
        {
            var cellLat = (int)Math.Ceiling(Math.Round(pointLatitude, 2));
            var cellLon = (int)Math.Ceiling(Math.Round(pointLongitude, 2));
            cellLat -= cellLat % 2;
            cellLon -= cellLon % 2;
            return (cellLat, cellLon);
        }

        public static string GetCellFileName((int,int) index)
        {
            (var lat, var lon) = index;
            return FormattableString.Invariant($"ERA5AVG_{(lat < 0 ? "S" : "N")}{Math.Abs(lat):00}_{lon:000}.json");
        }
    }
}
