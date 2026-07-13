using System.Linq;
using GameRealisticMap.Geometries;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
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

            // Use the Clipper-cleaned ocean polygons instead of the raw OSM boundary geometry:
            // raw administrative boundaries can self-intersect, which creates un-filled bands
            // (even-odd rule) carved to the ocean floor in the middle of the island.
            var oceanData = context.GetData<Nature.Ocean.OceanData>();
            if (!oceanData.IsIsland || oceanData.Polygons.Count == 0)
            {
                scope.WriteLine($"Island Mode: no ocean geometry for boundary {boundaryId}.");
                return;
            }

            using (var report = scope.CreateSingle("Island Elevation Adjustments"))
            {
                BuildInsideMask(grid, oceanData.Polygons);
                ApplyElevationOffset(grid, lakes);
                ClampOutsideToOcean(grid);
            }
        }

        /// <summary>
        /// Rasterize the ocean polygons once: point-in-polygon tests per grid cell are far too
        /// slow against OSM boundaries (thousands of vertices).
        /// </summary>
        private void BuildInsideMask(ElevationGrid grid, List<TerrainPolygon> oceanPolygons)
        {
            var width = grid.Size;
            var height = grid.Size;
            var step = grid.CellSize.X;

            var drawingOptions = new DrawingOptions
            {
                ShapeOptions = new ShapeOptions { IntersectionRule = IntersectionRule.NonZero }
            };

            using var image = new Image<L8>(width, height);
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.White); // land by default, ocean polygons carve it out
                foreach (var poly in oceanPolygons)
                {
                    // +0.5: align pixel centers with grid points (grid[x,y] is at x*step, y*step)
                    var points = poly.Shell.Select(p => new PointF(p.X / step + 0.5f, p.Y / step + 0.5f)).ToArray();
                    ctx.FillPolygon(drawingOptions, Brushes.Solid(Color.Black), points);
                    foreach (var hole in poly.Holes)
                    {
                        var holePoints = hole.Select(p => new PointF(p.X / step + 0.5f, p.Y / step + 0.5f)).ToArray();
                        ctx.FillPolygon(drawingOptions, Brushes.Solid(Color.White), holePoints);
                    }
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

            RemoveOceanPocketsInsideIsland(mask, width, height);

            insideMask = mask;
        }

        /// <summary>
        /// Safety net: any "ocean" area not connected to the map border is a rasterization or
        /// geometry artifact — turn it back into land. Guarantees the island can never contain
        /// holes carved to the ocean floor, whatever the OSM boundary geometry looks like.
        /// </summary>
        private static void RemoveOceanPocketsInsideIsland(bool[,] mask, int width, int height)
        {
            var reachable = new bool[width, height];
            var stack = new Stack<int>();

            void Visit(int x, int y)
            {
                if (!mask[x, y] && !reachable[x, y])
                {
                    reachable[x, y] = true;
                    stack.Push(y * width + x);
                }
            }

            for (int x = 0; x < width; x++) { Visit(x, 0); Visit(x, height - 1); }
            for (int y = 0; y < height; y++) { Visit(0, y); Visit(width - 1, y); }

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int x = index % width;
                int y = index / width;
                if (x > 0) Visit(x - 1, y);
                if (x < width - 1) Visit(x + 1, y);
                if (y > 0) Visit(x, y - 1);
                if (y < height - 1) Visit(x, y + 1);
            }

            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    if (!mask[x, y] && !reachable[x, y])
                    {
                        mask[x, y] = true; // enclosed "ocean" pocket: actually land
                    }
                }
            });
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
            // Reference = the lowest terrain inside the boundary (0.1th percentile to ignore
            // occasional bad DEM pixels): nothing inside ends up below sea level, so the island
            // terrain is purely translated, never deformed. High boundary edges simply become
            // cliffs or long coastal slopes.
            int refIndex = (int)(innerElevations.Count * 0.001);
            if (refIndex >= innerElevations.Count) refIndex = innerElevations.Count - 1;

            float refElevation = innerElevations[refIndex];

            // We shift the entire grid by -refElevation + safetyMargin.
            // A small 0.5m buffer ensures the lowest terrain stays above 0 (ocean level)
            float safetyMargin = 0.5f;
            float targetZOffset = -refElevation + safetyMargin;

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

        /// <summary>
        /// Separable box blur (approximates a Gaussian with multiple passes), in place.
        /// </summary>
        private static void BoxBlur(float[,] data, int width, int height, int radius, int passes)
        {
            var temp = new float[width, height];
            for (int pass = 0; pass < passes; pass++)
            {
                // Horizontal
                Parallel.For(0, height, y =>
                {
                    float sum = 0;
                    int count = 0;
                    for (int x = 0; x <= radius && x < width; x++) { sum += data[x, y]; count++; }
                    for (int x = 0; x < width; x++)
                    {
                        temp[x, y] = sum / count;
                        int add = x + radius + 1;
                        if (add < width) { sum += data[add, y]; count++; }
                        int remove = x - radius;
                        if (remove >= 0) { sum -= data[remove, y]; count--; }
                    }
                });
                // Vertical
                Parallel.For(0, width, x =>
                {
                    float sum = 0;
                    int count = 0;
                    for (int y = 0; y <= radius && y < height; y++) { sum += temp[x, y]; count++; }
                    for (int y = 0; y < height; y++)
                    {
                        data[x, y] = sum / count;
                        int add = y + radius + 1;
                        if (add < height) { sum += temp[x, add]; count++; }
                        int remove = y - radius;
                        if (remove >= 0) { sum -= temp[x, remove]; count--; }
                    }
                });
            }
        }

        private void ClampOutsideToOcean(ElevationGrid grid)
        {
            var mask = insideMask!;
            var width = grid.Size;
            var height = grid.Size;
            var step = grid.CellSize.X;

            // Chamfer distance transform that also propagates the elevation of the nearest
            // island cell (feature transform). The coast outside the boundary is built ONLY
            // from that edge elevation: using the raw outside terrain creates walls (when the
            // outside terrain is higher than the island edge, e.g. boundary along a valley)
            // or trenches (when it is far lower).
            float[,] distances = new float[width, height];
            float[,] edgeElevation = new float[width, height];
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    if (mask[x, y])
                    {
                        distances[x, y] = 0f;
                        edgeElevation[x, y] = Math.Max(grid[x, y], MinLandElevation);
                    }
                    else
                    {
                        distances[x, y] = float.MaxValue;
                    }
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
                        float edge = edgeElevation[x, y];
                        if (x > 0 && distances[x - 1, y] + d1 < min) { min = distances[x - 1, y] + d1; edge = edgeElevation[x - 1, y]; }
                        if (y > 0 && distances[x, y - 1] + d1 < min) { min = distances[x, y - 1] + d1; edge = edgeElevation[x, y - 1]; }
                        if (x > 0 && y > 0 && distances[x - 1, y - 1] + d2 < min) { min = distances[x - 1, y - 1] + d2; edge = edgeElevation[x - 1, y - 1]; }
                        if (x < width - 1 && y > 0 && distances[x + 1, y - 1] + d2 < min) { min = distances[x + 1, y - 1] + d2; edge = edgeElevation[x + 1, y - 1]; }
                        distances[x, y] = min;
                        edgeElevation[x, y] = edge;
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
                        float edge = edgeElevation[x, y];
                        if (x < width - 1 && distances[x + 1, y] + d1 < min) { min = distances[x + 1, y] + d1; edge = edgeElevation[x + 1, y]; }
                        if (y < height - 1 && distances[x, y + 1] + d1 < min) { min = distances[x, y + 1] + d1; edge = edgeElevation[x, y + 1]; }
                        if (x < width - 1 && y < height - 1 && distances[x + 1, y + 1] + d2 < min) { min = distances[x + 1, y + 1] + d2; edge = edgeElevation[x + 1, y + 1]; }
                        if (x > 0 && y < height - 1 && distances[x - 1, y + 1] + d2 < min) { min = distances[x - 1, y + 1] + d2; edge = edgeElevation[x - 1, y + 1]; }
                        distances[x, y] = min;
                        edgeElevation[x, y] = edge;
                    }
                }
            }

            // The feature transform propagates the elevation of the NEAREST coast point: with a
            // hilly boundary (hills/valleys alternating along the edge), neighbouring pixels of
            // the buffer can inherit very different elevations, creating vertical seams radiating
            // from the coast. Smooth the edge elevation field with a wide blur, and blend from
            // the exact local value (continuity at the shoreline) to the smoothed one offshore.
            var smoothedEdge = (float[,])edgeElevation.Clone();
            BoxBlur(smoothedEdge, width, height, Math.Clamp((int)(150f / step), 4, 256), 2);

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
                        // Elevation of the nearest island cell: exact near the shoreline, smoothed
                        // field further out to avoid seams between coast "watersheds"
                        float mixT = Math.Min(minDistance / 150f, 1f);
                        float baseElevation = edgeElevation[x, y] + (smoothedEdge[x, y] - edgeElevation[x, y]) * mixT;

                        // Seabed profile from boundary distance: gentle slope near the shore,
                        // reaching the ocean floor at OceanFullDepthDistance (smoothstep curve)
                        float td = Math.Min(minDistance / OceanFullDepthDistance, 1f);
                        float seabed = OceanFloorElevation * (td * td * (3f - 2f * td));

                        // Blend the island edge elevation into the seabed profile. The ramp length
                        // scales with the edge elevation (~12% slope) so high boundaries become
                        // long coastal slopes and low boundaries become beaches right at the edge.
                        float rampDistance = Math.Clamp(baseElevation * 8f, 30f, 2000f);
                        float t = Math.Min(minDistance / rampDistance, 1f);
                        float weight = t * t * (3f - 2f * t); // Smoothstep(0, 1, t)

                        grid[x, y] = (baseElevation * (1f - weight)) + (seabed * weight);
                    }
                }
            });
        }
    }
}
