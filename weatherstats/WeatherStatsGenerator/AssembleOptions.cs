using CommandLine;

namespace WeatherStatsGenerator
{
    [Verb("assemble", HelpText = "Takes months based JSON to generate year based JSON.")]
    public class AssembleOptions
    {
        [Value(0, Required = true, HelpText = "Year data, containing months directories")]
        public string? Path { get; set; }
    }
}