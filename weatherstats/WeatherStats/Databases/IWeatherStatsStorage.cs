using WeatherStats.Stats;

namespace WeatherStats.Databases
{
    public interface IWeatherStatsStorage
    {
        Task<List<YearWeatherStatsPoint>> Load(string path);
    }
}
