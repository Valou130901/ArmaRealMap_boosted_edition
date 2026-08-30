using System.Text;

namespace GameRealisticMap.Reforger.Import
{
    /// <summary>
    /// Emits the Enforce Script Workbench plugin that reads a pack produced by
    /// <see cref="WrpReforgerPackWriter"/> and places its objects through the World Editor API.
    /// </summary>
    /// <remarks>
    /// The plugin is shipped as source because Enforce Script is compiled by the Workbench itself.
    /// It is deliberately written against a narrow slice of the Workbench API (module lookup, entity
    /// actions, entity creation, file reading) and does its own string splitting, so it does not
    /// depend on helper classes that move between tool releases.
    /// </remarks>
    public static class ReforgerWorkbenchPlugin
    {
        public const string PluginFileName = "GrmImportPlugin.c";

        /// <summary>Writes the plugin sources and their install notes under <c>plugin/</c>.</summary>
        public static void WriteTo(string targetDirectory, string worldName)
        {
            var pluginRoot = Path.Combine(targetDirectory, "plugin");
            var scriptsDirectory = Path.Combine(pluginRoot, "scripts", "WorkbenchGame");
            Directory.CreateDirectory(scriptsDirectory);

            File.WriteAllText(Path.Combine(scriptsDirectory, PluginFileName), Source, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(pluginRoot, "README.md"), CreateReadme(worldName), new UTF8Encoding(false));
        }

        private static string CreateReadme(string worldName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# GRM import plugin - {worldName}");
            sb.AppendLine();
            sb.AppendLine("Enfusion Workbench plugin that places the objects of this pack into the world open in the");
            sb.AppendLine("World Editor.");
            sb.AppendLine();
            sb.AppendLine("## Install");
            sb.AppendLine();
            sb.AppendLine("1. Copy `scripts/WorkbenchGame/GrmImportPlugin.c` into your own addon, at the same relative");
            sb.AppendLine("   path (`<YourAddon>/scripts/WorkbenchGame/GrmImportPlugin.c`).");
            sb.AppendLine("2. Restart the Workbench so it compiles the plugin. Compile errors show up in the Script");
            sb.AppendLine("   Editor output; the plugin targets a narrow API slice but Enfusion does move, so fix any");
            sb.AppendLine("   signature drift there.");
            sb.AppendLine("3. The plugin appears in the World Editor under `Plugins` as **GRM: import Arma 3 pack**.");
            sb.AppendLine();
            sb.AppendLine("## Use");
            sb.AppendLine();
            sb.AppendLine("1. Open the target world in the World Editor and select the entity layer you want the");
            sb.AppendLine("   objects created in. The plugin creates everything in the current layer.");
            sb.AppendLine("2. Run the plugin. Set `m_PackFile` to the absolute path of `grm-pack.txt` in this pack.");
            sb.AppendLine("3. Set `m_Family` to a single family name (`tree`, `building`, ...) to place just that one,");
            sb.AppendLine("   or leave it empty to place every family listed in the manifest.");
            sb.AppendLine("4. `m_DryRun` is on by default: it parses the files and reports counts without creating");
            sb.AppendLine("   anything. Always worth one pass first on a big map.");
            sb.AppendLine();
            sb.AppendLine("Placements with no prefab (7-value lines) are counted and skipped: they have no Reforger");
            sb.AppendLine("model yet. Fill them in through the mapping and export again, or place them from a prefab");
            sb.AppendLine("pool with the Import Objects Extended tool.");
            sb.AppendLine();
            sb.AppendLine("## Coordinates");
            sb.AppendLine();
            sb.AppendLine("Positions are absolute world metres, taken verbatim from the Arma 3 map: X east, Y altitude");
            sb.AppendLine("above sea level, Z north. Angles are degrees, passed to the editor as (yaw, pitch, roll).");
            sb.AppendLine("If the whole map comes in mirrored in heading, turn on `m_InvertYaw` and re-run.");
            return sb.ToString();
        }

        /// <summary>The Enforce Script source of the plugin.</summary>
        private const string Source = @"//------------------------------------------------------------------------------------------------
// Game Realistic Map - Arma 3 to Arma Reforger import plugin
//
// Reads a 'grm-pack.txt' manifest produced by Game Realistic Map and creates the object placements
// it references, in the world currently open in the World Editor.
//
// Placement line format (one per line, 'Import Objects Extended' compatible):
//   <quote>{GUID}Prefabs/Path/Name.et<quote> PosX PosY PosZ Pitch Yaw Roll Scale
//   PosX PosY PosZ Pitch Yaw Roll Scale                  <- no prefab yet, counted and skipped
//------------------------------------------------------------------------------------------------
[WorkbenchPluginAttribute(name: ""GRM: import Arma 3 pack"", description: ""Place the objects of a Game Realistic Map import pack"", wbModules: { ""WorldEditor"" })]
class GrmImportPlugin : WorkbenchPlugin
{
	// A double quote, kept in one place so the parsing code stays readable
	private const string QUOTE = ""\"""";

	[Attribute(defvalue: """", desc: ""Absolute path to grm-pack.txt"")]
	string m_PackFile;

	[Attribute(defvalue: """", desc: ""Single family to place: tree, bush, rock, building, wall, fence, infrastructure, water, other. Empty places every family."")]
	string m_Family;

	[Attribute(defvalue: ""1"", desc: ""Parse and report only, create nothing"")]
	bool m_DryRun;

	[Attribute(defvalue: ""0"", desc: ""Negate every heading, if the map comes in mirrored"")]
	bool m_InvertYaw;

	private int m_Created;
	private int m_SkippedNoPrefab;
	private int m_SkippedBadLine;

	//------------------------------------------------------------------------------------------------
	override void Run()
	{
		if (!Workbench.ScriptDialog(""GRM: import Arma 3 pack"", ""Place the objects of a Game Realistic Map import pack"", this))
			return;

		if (m_PackFile == """")
		{
			Print(""GRM: no pack file set."", LogLevel.ERROR);
			return;
		}

		WorldEditor worldEditor = Workbench.GetModule(WorldEditor);
		if (!worldEditor)
		{
			Print(""GRM: the World Editor module is not available."", LogLevel.ERROR);
			return;
		}

		WorldEditorAPI api = worldEditor.GetApi();
		if (!api)
		{
			Print(""GRM: could not get the World Editor API."", LogLevel.ERROR);
			return;
		}

		array<string> layerNames = new array<string>();
		array<string> layerFiles = new array<string>();
		if (!ReadManifest(m_PackFile, layerNames, layerFiles))
			return;

		string packDirectory = DirectoryOf(m_PackFile);

		m_Created = 0;
		m_SkippedNoPrefab = 0;
		m_SkippedBadLine = 0;

		if (!m_DryRun)
			api.BeginEntityAction(""GRM import"");

		for (int i = 0; i < layerNames.Count(); i++)
		{
			if (m_Family != """" && m_Family != layerNames[i])
				continue;

			string file = packDirectory + layerFiles[i];
			Print(""GRM: placing family '"" + layerNames[i] + ""' from "" + file);
			PlaceFile(api, file);
		}

		if (!m_DryRun)
			api.EndEntityAction();

		Print(""GRM: done. created="" + m_Created.ToString()
			+ "" skipped_no_prefab="" + m_SkippedNoPrefab.ToString()
			+ "" skipped_bad_line="" + m_SkippedBadLine.ToString());
	}

	//------------------------------------------------------------------------------------------------
	// Reads the 'layer=<name>|<relative path>|<count>' entries of the manifest.
	private bool ReadManifest(string path, array<string> names, array<string> files)
	{
		FileHandle handle = FileIO.OpenFile(path, FileMode.READ);
		if (!handle)
		{
			Print(""GRM: cannot open "" + path, LogLevel.ERROR);
			return false;
		}

		string line;
		while (handle.ReadLine(line) >= 0)
		{
			line = line.Trim();
			if (line.Length() < 7)
				continue;
			if (line.Substring(0, 6) != ""layer="")
				continue;

			string value = line.Substring(6, line.Length() - 6);
			int firstBar = IndexOfChar(value, ""|"", 0);
			if (firstBar < 0)
				continue;

			string name = value.Substring(0, firstBar);
			int secondBar = IndexOfChar(value, ""|"", firstBar + 1);
			if (secondBar < 0)
				secondBar = value.Length();

			names.Insert(name);
			files.Insert(value.Substring(firstBar + 1, secondBar - firstBar - 1));
		}
		handle.Close();

		if (names.Count() == 0)
			Print(""GRM: no layer entry found in "" + path, LogLevel.WARNING);

		return true;
	}

	//------------------------------------------------------------------------------------------------
	private void PlaceFile(WorldEditorAPI api, string path)
	{
		FileHandle handle = FileIO.OpenFile(path, FileMode.READ);
		if (!handle)
		{
			Print(""GRM: cannot open "" + path, LogLevel.ERROR);
			return;
		}

		int layerId = api.GetCurrentEntityLayerId();
		string line;
		while (handle.ReadLine(line) >= 0)
		{
			PlaceLine(api, layerId, line);
		}
		handle.Close();
	}

	//------------------------------------------------------------------------------------------------
	private void PlaceLine(WorldEditorAPI api, int layerId, string line)
	{
		line = line.Trim();
		if (line == """")
			return;

		string head = line.Substring(0, 1);
		if (head == ""#"")
			return;

		string prefab = """";
		string rest = line;

		// A leading quoted token is the prefab resource name
		if (head == QUOTE)
		{
			int closing = IndexOfChar(line, QUOTE, 1);
			if (closing < 1)
			{
				m_SkippedBadLine++;
				return;
			}
			prefab = line.Substring(1, closing - 1);
			rest = line.Substring(closing + 1, line.Length() - closing - 1);
		}

		if (prefab == """")
		{
			// No Reforger model for this Arma 3 object yet
			m_SkippedNoPrefab++;
			return;
		}

		array<string> values = new array<string>();
		Tokenize(rest, values);
		if (values.Count() < 7)
		{
			m_SkippedBadLine++;
			return;
		}

		vector position;
		position[0] = values[0].ToFloat();
		position[1] = values[1].ToFloat();
		position[2] = values[2].ToFloat();

		float pitch = values[3].ToFloat();
		float yaw = values[4].ToFloat();
		float roll = values[5].ToFloat();
		if (m_InvertYaw)
			yaw = -yaw;

		// Enfusion orders an angles vector as yaw, pitch, roll
		vector angles;
		angles[0] = yaw;
		angles[1] = pitch;
		angles[2] = roll;

		if (m_DryRun)
		{
			m_Created++;
			return;
		}

		IEntitySource created = api.CreateEntity(prefab, """", layerId, null, position, angles);
		if (!created)
		{
			m_SkippedBadLine++;
			return;
		}

		m_Created++;
	}

	//------------------------------------------------------------------------------------------------
	// Splits on runs of spaces, tabs and carriage returns. Written by hand so the plugin does not
	// depend on any particular string helper being present.
	private void Tokenize(string value, array<string> tokens)
	{
		int length = value.Length();
		int start = -1;
		for (int i = 0; i < length; i++)
		{
			string c = value.Substring(i, 1);
			if (c == "" "" || c == ""\t"" || c == ""\r"")
			{
				if (start >= 0)
				{
					tokens.Insert(value.Substring(start, i - start));
					start = -1;
				}
			}
			else if (start < 0)
			{
				start = i;
			}
		}
		if (start >= 0)
			tokens.Insert(value.Substring(start, length - start));
	}

	//------------------------------------------------------------------------------------------------
	// Index of the first occurrence of a single character at or after 'from', or -1.
	private int IndexOfChar(string value, string character, int from)
	{
		int length = value.Length();
		for (int i = from; i < length; i++)
		{
			if (value.Substring(i, 1) == character)
				return i;
		}
		return -1;
	}

	//------------------------------------------------------------------------------------------------
	// Everything up to and including the last separator of a path.
	private string DirectoryOf(string path)
	{
		int cut = -1;
		int length = path.Length();
		for (int i = 0; i < length; i++)
		{
			string c = path.Substring(i, 1);
			if (c == ""/"" || c == ""\\"")
				cut = i;
		}
		if (cut < 0)
			return """";
		return path.Substring(0, cut + 1);
	}
}
";
    }
}
