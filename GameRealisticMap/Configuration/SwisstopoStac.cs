using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameRealisticMap.Configuration
{
    /// <summary>
    /// Reads the swisstopo STAC catalogue, following its pagination.
    /// </summary>
    /// <remarks>
    /// The catalogue answers at most one hundred items per request, whatever limit is asked for,
    /// and hands back a "next" link for the rest. Ignoring that link is not a small loss: over a
    /// 16.4 km map the elevation collection holds 612 tiles and a single request returns 100 of
    /// them. The downloader then falls back to the nearest tile it does have, so five sixths of the
    /// map quietly gets invented ground instead of failing outright.
    /// </remarks>
    public static class SwisstopoStac
    {
        public const string ApiRoot = "https://data.geo.admin.ch/api/stac/v0.9";

        public static string ItemsUrl(string collection, LatLngBounds bounds, int limit = 100)
        {
            return FormattableString.Invariant(
                $"{ApiRoot}/collections/{collection}/items?bbox={bounds.Left},{bounds.Bottom},{bounds.Right},{bounds.Top}&limit={limit}");
        }

        /// <summary>
        /// Every asset href of every item matching <paramref name="isWanted"/>, across all pages.
        /// </summary>
        /// <param name="maxPages">
        /// A guard against a catalogue that keeps handing out next links. Enough for the whole of
        /// Switzerland at the tile sizes these collections use.
        /// </param>
        public static async Task<List<string>> GetAssetHrefsAsync(
            HttpClient client, string url, Func<string, bool> isWanted, int maxPages = 200)
        {
            var hrefs = new List<string>();
            var next = url;
            var pages = 0;

            while (!string.IsNullOrEmpty(next) && pages < maxPages)
            {
                var response = await client.GetStringAsync(next).ConfigureAwait(false);
                pages++;
                using var json = JsonDocument.Parse(response);

                if (json.RootElement.TryGetProperty("features", out var features))
                {
                    foreach (var item in features.EnumerateArray())
                    {
                        if (!item.TryGetProperty("assets", out var assets))
                        {
                            continue;
                        }
                        foreach (var asset in assets.EnumerateObject())
                        {
                            if (isWanted(asset.Name) && asset.Value.TryGetProperty("href", out var href))
                            {
                                var value = href.GetString();
                                if (!string.IsNullOrEmpty(value))
                                {
                                    hrefs.Add(value);
                                }
                                break;
                            }
                        }
                    }
                }

                next = null;
                if (json.RootElement.TryGetProperty("links", out var links))
                {
                    foreach (var link in links.EnumerateArray())
                    {
                        if (link.TryGetProperty("rel", out var rel)
                            && rel.GetString() == "next"
                            && link.TryGetProperty("href", out var target))
                        {
                            next = target.GetString();
                            break;
                        }
                    }
                }
            }

            return hrefs;
        }
    }
}
