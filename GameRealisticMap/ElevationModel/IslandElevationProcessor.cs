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
            var step = grid.CellSize.X;

            var image = new Image<L8>(width, height);
            image.Mutate(ctx => 
            {
                ctx.Fill(Color.Black); // 0 means ocean
                foreach (var poly in maskPolygons)
                {
                    var points = poly.Shell.Select(p => new PointF(p.X / step, p.Y / step)).ToArray();
                    ctx.FillPolygon(Color.White, points);
                }
            });

            float[,] distances = new float[width, height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (image[x, y].PackedValue > 0)
                        distances[x, y] = 0; // Inside island
                    else
                        distances[x, y] = float.MaxValue;
                }
            }

            float d1 = 1f;
            float d2 = 1.41421356f;

            // Forward pass
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (distances[x, y] > 0)
                    {
                        float min = distances[x, y];
                        if (x > 0 && distances[x - 1, y] + d1 < min) min = distances[x - 1, y] + d1;
                        if (y > 0 && distances[x, y - 1] + d1 < min) min = distances[x, y - 1] + d1;
                        if (x > 0 && y > 0 && distances[x - 1, y - 1] + d2 < min) min = distances[x - 1, y - 1] + d2;
                        if (x < width - 1 && y > 0 && distances[x + 1, y - 1] + d2 < min) min = distances[x + 1, y - 1] + d2;
                        distances[x, y] = min;
                    }
                }
            }

            // Backward pass
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    if (distances[x, y] > 0)
                    {
                        float min = distances[x, y];
                        if (x < width - 1 && distances[x + 1, y] + d1 < min) min = distances[x + 1, y] + d1;
                        if (y < height - 1 && distances[x, y + 1] + d1 < min) min = distances[x, y + 1] + d1;
                        if (x < width - 1 && y < height - 1 && distances[x + 1, y + 1] + d2 < min) min = distances[x + 1, y + 1] + d2;
                        if (x > 0 && y < height - 1 && distances[x - 1, y + 1] + d2 < min) min = distances[x - 1, y + 1] + d2;
                        distances[x, y] = min;
                    }
                }
            }

            float oceanTransitionDistance = 200f; // Distance over which it drops to ocean floor

            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    float minDistance = distances[x, y] * step;

                    if (minDistance == 0) // Inside island
                    {
                        // Anti-flooding: force land to be at least 0.2m above water
                        if (grid[x, y] < 0.2f)
                        {
                            grid[x, y] = 0.2f;
                        }
                    }
                    else
                    {
                        var origElevation = grid[x, y];
                        float beachBufferDistance = Math.Max(150f, origElevation * 10f); // 10% slope minimum, or 150m
                        if (minDistance <= beachBufferDistance)
                        {
                            // Inside the beach buffer: we want a smooth transition from the edge elevation down to the water level (0m)
                            // Use smoothstep for a more natural looking curve instead of linear
                            float t = minDistance / beachBufferDistance;
                            float weight = 1f - (t * t * (3f - 2f * t)); // Smoothstep(1, 0, t)
                            
                            // Blend towards a flat beach at 0.1m
                            grid[x, y] = Math.Max(0.1f, origElevation * weight);
                        }
                        else if (minDistance <= beachBufferDistance + oceanTransitionDistance)
                        {
                            // Transition from 0.1m to OceanFloorElevation (-20m)
                            float distInTransition = minDistance - beachBufferDistance;
                            float weight = distInTransition / oceanTransitionDistance; // 0 to 1
                            
                            grid[x, y] = (0.1f * (1f - weight)) + (-20f * weight);
                        }
                        else
                        {
                            grid[x, y] = -20f;
                        }
                    }
                }
            }
        }
    }
}
