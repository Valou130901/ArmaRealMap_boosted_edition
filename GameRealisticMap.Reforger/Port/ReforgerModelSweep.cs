using BIS.P3D.ODOL;
using GameRealisticMap.Arma3.IO;
using Pmad.ProgressTracking;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>
    /// Converts every terrain-usable Arma 3 model found on the project drive into the shared
    /// <see cref="ReforgerModelLibrary"/>, in one pass.
    /// </summary>
    /// <remarks>
    /// Meant to be run once: afterwards any map export finds its models already converted. Vehicles,
    /// characters and weapons are skipped by <see cref="TerrainModelFilter"/> because they never
    /// appear in a wrp and account for most of the install's volume.
    /// </remarks>
    public sealed class ReforgerModelSweep
    {
        private readonly IGameFileSystem fileSystem;
        private readonly Func<string, ODOL?> readOdol;
        private readonly ReforgerModelLibrary library;

        public ReforgerModelSweep(IGameFileSystem fileSystem, Func<string, ODOL?> readOdol, ReforgerModelLibrary library)
        {
            this.fileSystem = fileSystem;
            this.readOdol = readOdol;
            this.library = library;
        }

        /// <summary>All terrain-usable models the project drive can see, mods included.</summary>
        public List<string> FindTerrainModels(IProgressScope progress)
        {
            using var report = progress.CreateSingle("ScanProjectDrive");
            var models = fileSystem.FindAll("*.p3d")
                .Where(TerrainModelFilter.IsTerrainModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
            progress.WriteLine($"Project drive: {models.Count} terrain models found");
            return models;
        }

        /// <param name="limit">Stop after converting this many new models, or null for all of them.</param>
        public ModelPortReport Run(IProgressScope progress, int? limit = null)
        {
            var models = FindTerrainModels(progress);
            var runner = new ModelPortRunner(readOdol, fileSystem.OpenFileIfExists, library);
            return runner.Port(models, progress, limit);
        }
    }
}
