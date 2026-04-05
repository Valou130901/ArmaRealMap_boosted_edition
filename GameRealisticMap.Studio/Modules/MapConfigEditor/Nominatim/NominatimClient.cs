using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GameRealisticMap.Studio.Modules.MapConfigEditor.Nominatim
{
    public class NominatimResult
    {
        [JsonPropertyName("osm_id")]
        public long OsmId { get; set; }

        [JsonPropertyName("osm_type")]
        public string OsmType { get; set; }

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public static class NominatimClient
    {
        private static readonly HttpClient _httpClient;

        static NominatimClient()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "GameRealisticMap.Studio/1.0");
        }

        public static async Task<List<NominatimResult>> SearchAsync(string query)
        {
            var url = $"https://nominatim.openstreetmap.org/search?q={System.Uri.EscapeDataString(query)}&format=json";
            var response = await _httpClient.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<NominatimResult>>(response) ?? new List<NominatimResult>();
        }
    }
}
