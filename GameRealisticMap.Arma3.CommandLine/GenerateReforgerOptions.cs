using CommandLine;

namespace GameRealisticMap.Arma3.CommandLine
{
    [Verb("genreforger", HelpText = "Generate an Arma Reforger / Enfusion World Editor import pack")]
    internal class GenerateReforgerOptions : MapOptionsBase
    {
        [Option('t', "target", Required = true, HelpText = "Target directory")]
        public string TargetDirectory { get; set; } = string.Empty;

        [Option('m', "mapping", Required = false, HelpText = "Optional Reforger asset mapping JSON file (defaults to the built-in mapping)")]
        public string? MappingFile { get; set; }
    }
}
