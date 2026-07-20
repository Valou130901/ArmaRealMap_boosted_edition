using System;
using System.IO;
using System.Threading.Tasks;
using BIS.WRP;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Studio.Modules.Reporting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export
{
    /// <summary>
    /// Exports the elevation grid as a 16-bit grayscale PNG heightmap calibrated for the BeamNG.drive
    /// World Editor terrain importer. A sidecar text file documents the exact import settings.
    /// </summary>
    internal class ExportHeightmapBeamNGTask : SingleFileExportBase
    {
        private readonly EditableWrp world;

        public ExportHeightmapBeamNGTask(EditableWrp world, string targetFile)
            : base(targetFile)
        {
            this.world = world;
        }

        public override string Title => "Export heightmap PNG (BeamNG.drive)";

        protected override Task<bool> Export(IProgressTaskUI ui, string targetFile)
        {
            var grid = world.ToElevationGrid();
            var size = grid.Size;

            var min = float.MaxValue;
            var max = float.MinValue;
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    var value = grid[x, y];
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }
            // BeamNG terrain heights are 0..maxHeight: anchor gray 0 on the lowest point,
            // rounded down to keep whole-meter import settings
            var floor = MathF.Floor(min);
            var range = MathF.Ceiling(max) - floor;
            if (range < 1f) range = 1f;

            using (var image = new Image<L16>(size, size))
            {
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        // PNG row 0 is north, elevation grid y axis points north
                        image[x, size - 1 - y] = new L16((ushort)Math.Clamp((grid[x, y] - floor) / range * 65535f, 0f, 65535f));
                    }
                }
                image.SaveAsPng(targetFile);
            }

            var cellSize = world.CellSize * world.LandRangeX / world.TerrainRangeX;
            var mapSize = cellSize * size;
            var waterHeight = 0f - floor;
            File.WriteAllText(Path.ChangeExtension(targetFile, ".readme.txt"), FormattableString.Invariant(
$@"BeamNG.drive heightmap export
=============================
Image size:      {size} x {size} pixels (16-bit grayscale PNG)
Pixel size:      {cellSize} m per pixel ({mapSize / 1000:0.##} km x {mapSize / 1000:0.##} km)
Altitude range:  {floor} m (gray 0) to {floor + range} m (gray 65535)

Import in the BeamNG.drive World Editor:
1. Create or open a level, press F11 to open the World Editor.
2. Window > Terrain Editor > Import Terrain (or create a new terrain of {size} px).
3. Heightmap: select this PNG.
4. Meters Per Pixel: {cellSize}
5. Max Height: {range}   (the gray range maps linearly to 0..{range} m)
6. After import, if the map has sea/lakes at altitude 0 m, add a WaterPlane and set
   its Z position to {waterHeight} m above the terrain base.

Notes:
- BeamNG supports square power-of-two terrains up to 8192 px; this export is {size} px.
- Roads, buildings and vegetation are NOT exported; only the raw terrain shape.
"));

            ui.AddSuccessAction(() => GameRealisticMap.Studio.Toolkit.ShellHelper.OpenUri(Path.GetDirectoryName(targetFile)!), "Open folder");

            return Task.FromResult(true);
        }
    }
}
