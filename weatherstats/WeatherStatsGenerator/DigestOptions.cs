using CommandLine;

namespace WeatherStatsGenerator
{
    [Verb("digest", HelpText = "Transform HC/HDF/NetCDF files into month stats JSON files. Takes 2 hours or more per month.")]
    public class DigestOptions
    {
        [Value(0, Required = true, HelpText = "Month data")]
        public string? Path { get; set; }
    }
}