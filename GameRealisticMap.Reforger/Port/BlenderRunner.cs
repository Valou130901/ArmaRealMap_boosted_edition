using System.Diagnostics;
using Pmad.ProgressTracking;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>
    /// Drives Blender in background mode to turn the library's OBJ files into the FBX the Enfusion
    /// Workbench ingests.
    /// </summary>
    /// <remarks>
    /// FBX is the last format that can be produced outside Bohemia's tools: the conversion to xob
    /// happens when the Workbench imports the FBX, and cannot be done here. Blender is used because
    /// its FBX exporter is the one the official Enfusion Blender Tools build on.
    /// </remarks>
    public static class BlenderRunner
    {
        /// <summary>Finds a blender.exe, or null when none is installed in a known location.</summary>
        public static string? FindBlender()
        {
            var candidates = new List<string>();

            foreach (var programFiles in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            })
            {
                if (string.IsNullOrEmpty(programFiles))
                {
                    continue;
                }
                var foundation = Path.Combine(programFiles, "Blender Foundation");
                if (!Directory.Exists(foundation))
                {
                    continue;
                }
                // Several versions can live side by side: prefer the most recent
                foreach (var directory in Directory.GetDirectories(foundation).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var exe = Path.Combine(directory, "blender.exe");
                    if (File.Exists(exe))
                    {
                        candidates.Add(exe);
                    }
                }
            }

            var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            foreach (var directory in onPath)
            {
                try
                {
                    var exe = Path.Combine(directory, "blender.exe");
                    if (File.Exists(exe))
                    {
                        candidates.Add(exe);
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not break the lookup
                }
            }

            return candidates.FirstOrDefault();
        }

        /// <summary>
        /// Runs the library's convert.py. Models that already have an FBX are skipped by the script
        /// itself, so this is cheap to call after every export.
        /// </summary>
        /// <returns>True when Blender ran and exited cleanly.</returns>
        public static bool ConvertLibrary(ReforgerModelLibrary library, IProgressScope progress, string? blenderPath = null)
        {
            var exe = blenderPath ?? FindBlender();
            if (string.IsNullOrEmpty(exe))
            {
                progress.WriteLine("Blender was not found: the OBJ files are ready but no FBX was produced. " +
                    "Install Blender, or run 'blender --background --python convert.py' in the library folder yourself.");
                return false;
            }

            var script = Path.Combine(library.RootDirectory, "convert.py");
            if (!File.Exists(script))
            {
                progress.WriteLine($"No convert.py in {library.RootDirectory}: nothing to run.");
                return false;
            }

            progress.WriteLine($"Running Blender to build the FBX files ({exe})...");

            using var report = progress.CreateSingle("BlenderConvert");
            var startInfo = new ProcessStartInfo(exe)
            {
                WorkingDirectory = library.RootDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--background");
            startInfo.ArgumentList.Add("--python");
            startInfo.ArgumentList.Add(script);

            try
            {
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    progress.WriteLine("Blender could not be started.");
                    return false;
                }

                // Only the script's own progress lines are worth surfacing: Blender is very chatty
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = process.StandardOutput.ReadLine();
                    if (line != null && (line.StartsWith("converting", StringComparison.Ordinal)
                        || line.StartsWith("done:", StringComparison.Ordinal)
                        || line.StartsWith("FAILED", StringComparison.Ordinal)))
                    {
                        progress.WriteLine("  " + line);
                    }
                }
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    progress.WriteLine($"Blender exited with code {process.ExitCode}.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                progress.WriteLine($"Blender run failed: {ex.Message}");
                return false;
            }

            var fbxDirectory = Path.Combine(library.RootDirectory, "fbx");
            var count = Directory.Exists(fbxDirectory) ? Directory.GetFiles(fbxDirectory, "*.fbx").Length : 0;
            progress.WriteLine($"FBX ready: {count} files in {fbxDirectory}");
            return true;
        }
    }
}
