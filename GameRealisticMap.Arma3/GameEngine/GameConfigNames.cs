using System.Globalization;
using System.Text.RegularExpressions;

namespace GameRealisticMap.Arma3.GameEngine
{
    /// <summary>A named place declared in the <c>Names</c> class of an Arma 3 world config.</summary>
    public sealed record GameConfigName(string Name, float X, float Y, string Type)
    {
        /// <summary>True for the place types worth showing as a destination on a map.</summary>
        public bool IsSettlement => Type.StartsWith("NameCity", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("NameVillage", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("NameLocal", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("Airport", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("NameMarine", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the place names of an existing Arma 3 map out of its world config.
    /// </summary>
    /// <remarks>
    /// Every Arma world declares its towns in a <c>class Names</c> block, each entry carrying a
    /// display name, a world position and a type. That block is the only authoritative source of
    /// town names for a map that was not generated from OSM data.
    /// </remarks>
    public static class GameConfigNames
    {
        // The body holds array values such as position[]={1,2}, so it has to allow one level of
        // nested braces rather than stopping at the first one.
        private static readonly Regex EntryRegex = new(
            @"class\s+(?<class>\w+)\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex NameRegex = new(
            @"name\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex PositionRegex = new(
            @"position\[\]\s*=\s*\{\s*(?<x>-?[0-9.eE+]+)\s*,\s*(?<y>-?[0-9.eE+]+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex TypeRegex = new(
            @"type\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // #include "kelleysisland.h" or #include <file.hpp>: official maps keep their Names block in
        // a separate header, so a config read as a single file has no places at all
        private static readonly Regex IncludeRegex = new(
            // \r is matched explicitly: in .NET, $ under Multiline matches before \n, leaving the
            // carriage return of a CRLF file unmatched, which is how every real config is written
            @"^[ \t]*#include[ \t]*(?:""(?<path>[^""]+)""|<(?<path>[^>]+)>)[ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>How deep includes are followed before giving up, so a cycle cannot hang the export.</summary>
        private const int MaxIncludeDepth = 8;

        /// <summary>Extracts every place of the <c>Names</c> block of a config, in declaration order.</summary>
        /// <param name="configContent">Raw text of the world config.</param>
        /// <param name="includeResolver">
        /// Returns the text of an included file, or null when it cannot be found. Without one, an
        /// <c>#include</c> is left as-is and the places it holds are lost.
        /// </param>
        public static List<GameConfigName> ReadFromContent(string? configContent, Func<string, string?>? includeResolver = null)
        {
            var result = new List<GameConfigName>();
            if (string.IsNullOrEmpty(configContent))
            {
                return result;
            }

            if (includeResolver != null)
            {
                configContent = ResolveIncludes(configContent, includeResolver, MaxIncludeDepth);
            }

            var body = ExtractNamesBlock(configContent);
            if (body == null)
            {
                return result;
            }

            foreach (Match entry in EntryRegex.Matches(body))
            {
                var inner = entry.Groups["body"].Value;
                var position = PositionRegex.Match(inner);
                if (!position.Success)
                {
                    continue;
                }
                if (!float.TryParse(position.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(position.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    continue;
                }

                var type = TypeRegex.Match(inner) is { Success: true } t ? t.Groups["value"].Value : string.Empty;
                var name = NameRegex.Match(inner) is { Success: true } n ? n.Groups["value"].Value : string.Empty;

                result.Add(new GameConfigName(Prettify(name, entry.Groups["class"].Value), x, y, type));
            }

            return result;
        }

        /// <summary>
        /// Resolver for <see cref="ReadFromContent(string?, Func{string, string?})"/> that reads
        /// includes from disk, then from the project drive.
        /// </summary>
        /// <param name="configDirectory">Folder holding the config, for the usual relative include.</param>
        /// <param name="gameFileSystem">
        /// Used for includes written as a game path (<c>\a3\map_data\names.hpp</c>), which a folder
        /// alone cannot resolve.
        /// </param>
        public static Func<string, string?> CreateIncludeResolver(string? configDirectory, IO.IGameFileSystem? gameFileSystem = null)
        {
            return path =>
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (!string.IsNullOrEmpty(configDirectory))
                {
                    var candidate = Path.Combine(configDirectory, relative.TrimStart(Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        return File.ReadAllText(candidate);
                    }
                }
                if (gameFileSystem != null)
                {
                    using var stream = gameFileSystem.OpenFileIfExists(path.Replace('/', '\\').TrimStart('\\'));
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        return reader.ReadToEnd();
                    }
                }
                return null;
            };
        }

        /// <summary>
        /// Replaces every <c>#include</c> by the text of the included file, recursively.
        /// </summary>
        /// <remarks>
        /// Only what the Names block needs: no macro expansion, no conditional compilation. An
        /// include that cannot be resolved is dropped rather than left in place, so its text never
        /// gets parsed as part of the enclosing class.
        /// </remarks>
        private static string ResolveIncludes(string content, Func<string, string?> resolver, int depth)
        {
            if (depth <= 0 || !content.Contains("#include", StringComparison.Ordinal))
            {
                return content;
            }
            return IncludeRegex.Replace(content, match =>
            {
                string? included;
                try
                {
                    included = resolver(match.Groups["path"].Value);
                }
                catch (IOException)
                {
                    included = null;
                }
                return included == null ? string.Empty : ResolveIncludes(included, resolver, depth - 1);
            });
        }

        /// <summary>
        /// Body of <c>class Names</c>, found by matching braces: the block is nested inside the
        /// world class, so a plain regex would stop at the first closing brace.
        /// </summary>
        private static string? ExtractNamesBlock(string content)
        {
            var match = Regex.Match(content, @"class\s+Names\s*\{", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return null;
            }

            var start = match.Index + match.Length;
            var depth = 1;
            for (var i = start; i < content.Length; i++)
            {
                if (content[i] == '{')
                {
                    depth++;
                }
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return content.Substring(start, i - start);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Display name of a place. Official maps put a stringtable key in <c>name</c>, which is
        /// meaningless outside Arma, so the class name is used instead in that case.
        /// </summary>
        private static string Prettify(string name, string className)
        {
            if (!string.IsNullOrEmpty(name) && !name.StartsWith("$", StringComparison.Ordinal))
            {
                return name;
            }
            var candidate = className;
            if (name.StartsWith("$STR_", StringComparison.OrdinalIgnoreCase))
            {
                // Keys look like $STR_A3_Malden_C_Larche0: the last segment is the readable part,
                // with a disambiguating index stuck on the end
                var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    candidate = parts[^1];
                }
            }
            candidate = candidate.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            candidate = candidate.Replace('_', ' ').Trim();
            return string.IsNullOrEmpty(candidate) ? className : candidate;
        }
    }
}
