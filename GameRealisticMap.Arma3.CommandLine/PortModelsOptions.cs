using CommandLine;

namespace GameRealisticMap.Arma3.CommandLine
{
    internal class LibraryOptionsBase
    {
        [Option("library", Required = false, HelpText = "Model library directory (defaults to the shared per-user library)")]
        public string? LibraryDirectory { get; set; }
    }

    [Verb("portsweep", HelpText = "Convert every terrain model of the project drive into the shared Arma Reforger model library")]
    internal class PortSweepOptions : LibraryOptionsBase
    {
        [Option('l', "limit", Required = false, HelpText = "Stop after converting this many new models. Omit to convert all of them.")]
        public int? Limit { get; set; }

        [Option("list", Required = false, HelpText = "Only list what would be converted, convert nothing")]
        public bool ListOnly { get; set; }
    }

    [Verb("portmodels", HelpText = "Convert the models an import pack still misses into the shared Arma Reforger model library")]
    internal class PortModelsOptions : LibraryOptionsBase
    {
        [Option('p', "pack", Required = true, HelpText = "Import pack directory, the one holding port-worklist.csv")]
        public string PackDirectory { get; set; } = string.Empty;

        [Option('c', "category", Required = false, HelpText = "Only this family: building, tree, bush, rock, wall, fence, infrastructure, water, other")]
        public string? Category { get; set; }

        [Option('l', "limit", Required = false, HelpText = "Stop after converting this many new models. Omit to convert all of them.")]
        public int? Limit { get; set; }
    }

    [Verb("portlink", HelpText = "Harvest prefab ResourceNames from an Enfusion addon back into the model library")]
    internal class PortLinkOptions : LibraryOptionsBase
    {
        [Option('a', "addon", Required = true, HelpText = "Addon folder holding resourceDatabase.rdb")]
        public string AddonDirectory { get; set; } = string.Empty;

        [Option("dry-run", Required = false, HelpText = "Report the matches without writing them")]
        public bool DryRun { get; set; }
    }
}
