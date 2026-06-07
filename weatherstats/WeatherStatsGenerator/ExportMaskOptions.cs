using CommandLine;

namespace WeatherStatsGenerator
{
    [Verb("export-mask", HelpText = "Export mask to GeoJSON.")]
    internal class ExportMaskOptions
    {
        [Value(0, Required = true)]
        public string? Path { get; set; }
    }
}