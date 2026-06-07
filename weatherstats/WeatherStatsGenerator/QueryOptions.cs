using CommandLine;

namespace WeatherStatsGenerator
{
    [Verb("query", HelpText = "Query a data point from assembly JSON.")]
    internal class QueryOptions
    {
        [Option("assembly", Required = true, HelpText = "Assembly data")]
        public string? AssemblyPath { get; set; }

        [Option("latitude", Required = true)]
        public double Latitude { get; set; }

        [Option("longitude", Required = true)]
        public double Longitude { get; set; }
    }
}