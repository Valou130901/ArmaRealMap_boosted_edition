using System.Linq;
using GameRealisticMap.Geometries;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.ElevationModel
{
    internal class IslandElevationProcessor
    {
        private const float OceanFloorElevation = -50f;
        private const float OceanFullDepthDistance = 500f; // Distance from boundary at which ocean floor depth is reached
        private const float MinLandElevation = 0.2f; // Anti-flooding: land inside the boundary stays above this

        private bool[,]? insideMask;

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
                BuildInsideMask(grid, maskPolygons);
                ApplyElevationOffset(grid, lakes);
                ClampOutsideToOcean(grid);
            }
        }

        /// <summary>
        /// Rasterize the boundary polygons once: point-in-polygon tests per grid cell are far too
        /// slow against OSM boundaries (thousands of vertices).
        /// </summary>
        private void BuildInsideMask(ElevationGrid grid, List<TerrainPolygon> maskPolygons)
        {
            var width = grid.Size;
            var height = grid.Size;
            var step = grid.CellSize.X;

            using var image = new Image<L8>(width, height);
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.Black); // 0 means ocean
                foreach (var poly in maskPolygons)
                {
                    // +0.5: align pixel centers with grid points (grid[x,y] is at x*step, y*step)
                    var points = poly.Shell.Select(p => new PointF(p.X / step + 0.5f, p.Y / step + 0.5f)).ToArray();
                    ctx.FillPolygon(Color.White, points);
                }
            });

            var mask = new bool[width, height];
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    mask[x, y] = image[x, y].PackedValue > 0;
                }
            });
            insideMask = mask;
        }

        private void ApplyElevationOffset(ElevationGrid grid, List<LakeWithElevation> lakes)
        {
            var mask = insideMask!;

            // Collect elevations inside the mask
            var innerElevations = new List<float>();
            for (var x = 0; x < grid.Size; x++)
            {
                for (var y = 0; y < grid.Size; y++)
                {
                    if (mask[x, y])
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

            Parallel.For(0, grid.Size, x =>
            {
                for (var y = 0; y < grid.Size; y++)
                {
                    grid[x, y] += targetZOffset;
                }
            });

            foreach (var lake in lakes)
            {
                lake.WaterElevation += targetZOffset;
                lake.BorderElevation += targetZOffset;
            }
        }

        /// <summary>
        /// Re-apply the anti-flooding clamp after the constraint solver ran: roads smoothing and
        /// watercourses (river beds are forced below their initial elevation) can push cells of
        /// the island below the ocean level.
        /// </summary>
        public void EnforceLandAboveOcean(ElevationGrid grid)
        {
            if (insideMask == null)
            {
                return; // Island mode inactive
            }
            var mask = insideMask;
            Parallel.For(0, grid.Size, x =>
            {
                for (var y = 0; y < grid.Size; y++)
                {
                    if (mask[x, y] && grid[x, y] < MinLandElevation)
                    {
                        grid[x, y] = MinLandElevation;
                    }
                }
            });
        }

        private void ClampOutsideToOcean(ElevationGrid grid)
        {
            var mask = insideMask!;
            var width = grid.Size;
            var height = grid.Size;
            var step = grid.CellSize.X;

            float[,] distances = new float[width, height];
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    distances[x, y] = mask[x, y] ? 0f : float.MaxValue;
                }
            });

            float d1 = 1f;
            float d2 = 1.41421356f;

            // Forward pass (sequential: chamfer distance transform propagates between rows)
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

            Parallel.For(0, width, x =>
            {
                for (var y = 0; y < height; y++)
                {
                    float minDistance = distances[x, y] * step;

                    if (minDistance == 0) // Inside island
                    {
                        // Anti-flooding: force land to be at least 0.2m above water
                        if (grid[x, y] < MinLandElevation)
                        {
                            grid[x, y] = MinLandElevation;
                        }
                    }
                    else
                    {
                        // Base coast elevation: original terrain clamped to shore level. Without
                        // the clamp, valleys located outside the boundary (below the global offset
                        // reference) carve deep trenches right along the coast.
                        float baseElevation = Math.Max(grid[x, y], MinLandElevation);

                        // Seabed profile from boundary distance: gentle slope near the shore,
                        // reaching the ocean floor at OceanFullDepthDistance (smoothstep curve)
                        float td = Math.Min(minDistance / OceanFullDepthDistance, 1f);
                        float seabed = OceanFloorElevation * (td * td * (3f - 2f * td));

                        // Blend coast elevation into the seabed profile. The ramp length scales
                        // with the coast elevation (capped at ~12% slope) so high coasts descend
                        // as cliffs while low coasts become beaches right at the boundary.
                        float rampDistance = Math.Clamp(baseElevation * 8f, 30f, 300f);
                        float t = Math.Min(minDistance / rampDistance, 1f);
                        float weight = t * t * (3f - 2f * t); // Smoothstep(0, 1, t)

                        grid[x, y] = (baseElevation * (1f - weight)) + (seabed * weight);
                    }
                }
            });
        }
    }
}
