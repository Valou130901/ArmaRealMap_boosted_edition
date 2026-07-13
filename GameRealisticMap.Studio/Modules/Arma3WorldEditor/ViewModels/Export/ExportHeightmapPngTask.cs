using System;
using System.Globalization;
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
    /// Exports the elevation grid as a 16-bit grayscale PNG heightmap, usable by WorldPainter
    /// (Minecraft), Unreal, Unity... A sidecar text file documents the value mapping.
    /// </summary>
    internal class ExportHeightmapPngTask : SingleFileExportBase
    {
        private readonly EditableWrp world;

        public ExportHeightmapPngTask(EditableWrp world, string targetFile)
            : base(targetFile)
        {
            this.world = world;
        }

        public override string Title => "Export heightmap PNG (WorldPainter/Minecraft)";

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
            var range = Math.Max(0.001f, max - min);

            using (var image = new Image<L16>(size, size))
            {
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        // PNG row 0 is north, elevation grid y axis points north
                        image[x, size - 1 - y] = new L16((ushort)((grid[x, y] - min) / range * 65535f));
                    }
                }
                image.SaveAsPng(targetFile);
            }

            var cellSize = world.CellSize * world.LandRangeX / world.TerrainRangeX;
            var seaLevelGray = (0f - min) / range * 65535f;
            File.WriteAllText(Path.ChangeExtension(targetFile, ".readme.txt"), FormattableString.Invariant(
$@"Heightmap export
================
Image size:      {size} x {size} pixels
Pixel size:      {cellSize} m per pixel
Altitude range:  {min:0.##} m (gray 0) to {max:0.##} m (gray 65535)
Sea level (0m):  gray value {seaLevelGray:0}

WorldPainter import (Minecraft):
- File > Import > Height map...
- Scale: {cellSize:0.##} block(s) per pixel for a 1:1 world
- Map '0' to the Minecraft level for {min:0.##} m and '65535' to the level for {max:0.##} m
  (e.g. -60 to {Math.Min(319, -60 + (int)range)} keeps a 1:1 vertical scale when the range fits)
- Water level: set to the Minecraft level corresponding to gray {seaLevelGray:0} (altitude 0 m)
"));

            ui.AddSuccessAction(() => GameRealisticMap.Studio.Toolkit.ShellHelper.OpenUri(Path.GetDirectoryName(targetFile)!), "Open folder");

            return Task.FromResult(true);
        }
    }
}
