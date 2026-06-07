using System.Text.Json.Serialization;

namespace WeatherStats.Stats
{
    public sealed class MonthWeatherStatsPoint
    {
        [JsonConstructor]
        public MonthWeatherStatsPoint(float latitude, float longitude, MonthWeatherStatsData data)
        {
            Latitude = latitude;
            Longitude = longitude;
            Data = data;
        }

        [JsonPropertyName("lat")]
        public float Latitude { get; }

        [JsonPropertyName("lon")]
        public float Longitude { get; }

        [JsonPropertyName("d")]
        public MonthWeatherStatsData Data { get; }
    }
}
