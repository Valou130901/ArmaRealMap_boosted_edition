using System.Text.Json;
using WeatherStats.Stats;

namespace WeatherStats.Databases
{
    public sealed class WeatherStatsHttpStorage : IWeatherStatsStorage
    {
        private readonly HttpClient client;

        public WeatherStatsHttpStorage(HttpClient client)
        {
            this.client = client;
            client.DefaultRequestHeaders.Add("User-Agent", "GameRealisticMap/1.0");
        }

        public WeatherStatsHttpStorage(Uri baseAddress)
            : this(new HttpClient() { BaseAddress = baseAddress })
        {

        }

        public async Task<List<YearWeatherStatsPoint>> Load(string path)
        {
            try
            {
                using (var input = await client.GetStreamAsync(path).ConfigureAwait(false))
                {
                    return (await JsonSerializer.DeserializeAsync<List<YearWeatherStatsPoint>>(input).ConfigureAwait(false))!;
                }
            }
            catch(HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new List<YearWeatherStatsPoint>();
            }
        }
    }
}
