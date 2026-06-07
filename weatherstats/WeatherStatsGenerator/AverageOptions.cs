using CommandLine;

namespace WeatherStatsGenerator
{
    [Verb("average", HelpText = "Takes year data to create multi-year averages.")]
    public class AverageOptions
    {
        [Value(0, Required = true, HelpText = "Target directory")]
        public string? Target { get; set; }

        [Value(1, Min = 1, Max = int.MaxValue, HelpText = "Years datas, containing assembly directory")]
        public IEnumerable<string>? Sources { get; set; }

    }
}
