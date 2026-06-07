using System.Text.Json.Serialization;

namespace WeatherStats.Stats
{
    public sealed class YearWeatherStatsPoint
    {
        [JsonConstructor]
        public YearWeatherStatsPoint(float latitude, float longitude, MonthWeatherStatsData[] months)
        {
            Latitude = latitude;
            Longitude = longitude;
            Months = months;
        }

        [JsonPropertyName("lat")]
        public float Latitude { get; }

        [JsonPropertyName("lon")]
        public float Longitude { get; }

        [JsonPropertyName("m")]
        public MonthWeatherStatsData[] Months { get; }
    }
}
