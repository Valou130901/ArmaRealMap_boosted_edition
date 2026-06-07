using System.Text.Json.Serialization;

namespace WeatherStats.Stats
{
    public sealed class MonthWeatherStatsData
    {
        [JsonConstructor]
        public MonthWeatherStatsData(MinMaxAvg humidity, MinMaxAvgStats temperature, MinMaxAvgStats windSpeed, WindDirectionStats windDirection)
        {
            Humidity = humidity;
            Temperature = temperature;
            WindSpeed = windSpeed;
            WindDirection = windDirection;
        }

        [JsonPropertyName("h")]
        public MinMaxAvg Humidity { get; }

        [JsonPropertyName("t")]
        public MinMaxAvgStats Temperature { get; }

        [JsonPropertyName("ws")]
        public MinMaxAvgStats WindSpeed { get; }

        [JsonPropertyName("wd")]
        public WindDirectionStats WindDirection { get; }

        public static MonthWeatherStatsData Average(IEnumerable<MonthWeatherStatsData> enumerable)
        {
            return new MonthWeatherStatsData(
                MinMaxAvg.Average(enumerable.Select(m => m.Humidity)),
                MinMaxAvgStats.Average(enumerable.Select(m => m.Temperature)),
                MinMaxAvgStats.Average(enumerable.Select(m => m.WindSpeed)),
                WindDirectionStats.Average(enumerable.Select(m => m.WindDirection)));
        }

        public static MonthWeatherStatsData From(float[] humidity, float[] temperature, float[] windSpeed, WindDirection8[] windDirection)
        {
            return new MonthWeatherStatsData(
                MinMaxAvg.From(humidity),
                MinMaxAvgStats.From(temperature),
                MinMaxAvgStats.From(windSpeed),
                WindDirectionStats.From(windSpeed, windDirection));
        }

    }
}
