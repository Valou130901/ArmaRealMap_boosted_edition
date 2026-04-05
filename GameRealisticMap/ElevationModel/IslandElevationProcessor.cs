using System.Linq;
using System.Numerics;
using GameRealisticMap.Geometries;
using OsmSharp.Tags;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.ElevationModel
{
    internal class IslandElevationProcessor
    {
        private const float OceanFloorElevation = -20f;
        private const float OceanEdgeTransitionDistance = 100f; // Distance from boundary to ocean floor

        public void Process(ElevationGrid grid, IBuildContext context, IProgressScope scope, List<LakeWithElevation> lakes)
        {
            var boundaryId = context.Options.OsmBoundaryId;
            if (boundaryId == null)
            {
                return;
            }

            var relation = context.OsmSource.Relations.FirstOrDefault(r => r.Id == boundaryId.Value);
            if (relation == null)
            {
                scope.WriteLine($"Island Mode: OSM relation {boundaryId} not found in data.");
                return;
            }

            var maskPolygons = context.OsmSource.Interpret(relation)
                .SelectMany(g => TerrainPolygon.FromGeometry(g, context.Area.LatLngToTerrainPoint))
                .ToList();

            if (maskPolygons.Count == 0)
            {
                scope.WriteLine($"Island Mode: No geometry generated for relation {boundaryId}.");
                return;
            }

            using (var report = scope.CreateSingle("Island Elevation Adjustments"))
            {
                ApplyElevationOffset(grid, maskPolygons, lakes);
                ClampOutsideToOcean(grid, maskPolygons);
            }
        }

        private void ApplyElevationOffset(ElevationGrid grid, List<TerrainPolygon> maskPolygons, List<LakeWithElevation> lakes)
        {
            // Collect elevations inside the mask
            var innerElevations = new List<float>();

            var step = grid.CellSize.X;
            for (var x = 0; x < grid.Size; x++)
            {
                for (var y = 0; y < grid.Size; y++)
                {
                    var point = new TerrainPoint(x * step, y * step);
                    if (maskPolygons.Any(p => p.Contains(point)))
                    {
                        innerElevations.Add(grid[x, y]);
                    }
                }
            }

            if (innerElevations.Count == 0)
            {
                return; // Nothing to offset
            }

            innerElevations.Sort();
            int p2Index = (int)(innerElevations.Count * 0.02);
            if (p2Index >= innerElevations.Count) p2Index = innerElevations.Count - 1;
            
            float p2Elevation = innerElevations[p2Index];

            // We shift the entire grid by -p2Elevation + safetyMargin.
            // A small 0.5m buffer ensures the beaches actually form above 0 (ocean level)
            float safetyMargin = 0.5f;
            float targetZOffset = -p2Elevation + safetyMargin;

            for (var x = 0; x < grid.Size; x++)
            {
                for (var y = 0; y < grid.Size; y++)
                {
                    grid[x, y] += targetZOffset;
                }
            }

            foreach (var lake in lakes)
            {
                lake.WaterElevation += targetZOffset;
                lake.BorderElevation += targetZOffset;
            }
        }

        private void ClampOutsideToOcean(ElevationGrid grid, List<TerrainPolygon> maskPolygons)
        {
            var width = grid.Size;
            var height = grid.Size;

            var image = new Image<L16>(width, height);
            image.Mutate(ctx => 
            {
                ctx.Fill(Color.Black); // 0 means ocean
                foreach (var poly in maskPolygons)
                {
                    PolygonDrawHelper.DrawPolygon(ctx, poly, new SolidBrush(Color.White), l => l.Select(p => new PointF(p.X / grid.CellSize.X, p.Y / grid.CellSize.Y)));
                }

                // Smooth out the transition to ocean logic
                ctx.GaussianBlur(OceanEdgeTransitionDistance / grid.CellSize.X);
            });

            // image ranges from Black (Ocean) to White (Preserve elevation)
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    var weight = image[x, y].PackedValue / 65535f;
                    
                    var origElevation = grid[x, y];
                    
                    if (weight < 1f)
                    {
                        var targetOceanDepth = Math.Min(OceanFloorElevation, origElevation - 5f);
                        grid[x, y] = (origElevation * weight) + (targetOceanDepth * (1f - weight));
                    }
                }
            }
        }
    }
}
