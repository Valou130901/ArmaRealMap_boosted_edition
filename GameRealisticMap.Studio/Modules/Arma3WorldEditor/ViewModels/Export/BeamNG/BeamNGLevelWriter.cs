using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameRealisticMap.Arma3.Assets;
using GameRealisticMap.Arma3.GameEngine.Roads;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Geometries;
using Pmad.HugeImages;
using Pmad.HugeImages.Processing;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.BeamNG
{
    internal enum BeamNGForestKind { Tree, Bush, Rock, Clutter }

    /// <param name="Model">
    /// Arma model the instance came from. When the shared library holds a converted mesh for it, the
    /// forest draws the real model instead of a generated billboard.
    /// </param>
    /// <param name="Rotation">
    /// Full 3x3 orientation, row major, for instances that need pitch and roll. Null falls back to
    /// <paramref name="YawRad"/>, which is all a tree ever needs.
    /// </param>
    /// <param name="Z">
    /// Altitude Arma gave the instance. Null falls back to sitting the origin on the terrain, which
    /// buries any autocentred model up to its middle and is never what is wanted.
    /// </param>
    internal record struct BeamNGForestInstance(float X, float Y, float YawRad, float Scale, BeamNGForestKind Kind,
        string? Model = null, float[]? Rotation = null, float? Z = null);

    internal record struct BeamNGPond(float X, float Y, float SurfaceZ, float Size, float YawRad);

    internal record struct BeamNGBuildingBox(float X, float Y, float YawRad, float Width, float Depth, float Height);

    /// <summary>
    /// One placement of a real Arma 3 model, converted to COLLADA in the shared model library.
    /// Coordinates are BeamNG world space: X east, Y north, Z up.
    /// </summary>
    /// <param name="Rotation">
    /// Full 3x3 orientation, row major, as TSStatic expects it. Carrying the whole matrix rather
    /// than a heading keeps the pitch and roll Arma gives objects that sit on a slope.
    /// </param>
    internal record struct BeamNGModelInstance(string Model, float X, float Y, float Z, float[] Rotation, float Scale);

    /// <summary>Shape of a forest species: the mesh it draws and how it behaves in the wind.</summary>
    internal record struct BeamNGForestShape(string Name, string ShapePath, float Radius, float Mass, float WindScale, string Annotation);

    /// <summary>
    /// A named place of the map. Becomes a spawn point, which BeamNG lists on the Big Map both as a
    /// label and as a destination the player can spawn at.
    /// </summary>
    internal record struct BeamNGPlace(string Name, float X, float Y, float YawRad);

    internal record BeamNGRoadInput(List<TerrainPoint> Points, float Width, bool IsDirt,
        float Drivability = 0.5f, int SpeedLimit = 50, bool IsBridge = false,
        bool ProjectOverObjects = false);

    internal enum BeamNGFenceKind { Wall, Fence, Hedge }

    internal record BeamNGFenceInput(List<TerrainPoint> Points, BeamNGFenceKind Kind);

    /// <summary>
    /// Writes a playable BeamNG.drive level zip from an elevation grid, satellite/id imagery,
    /// the road network and the vegetation objects. Level structure and .ter binary format
    /// (version 9) follow BeamNG official levels (validated by the mapng project).
    /// </summary>
    internal class BeamNGLevelWriter
    {
        private readonly ElevationGrid grid;

        /// <summary>Arma's own elevation grid, before resampling, or null when it was not supplied.</summary>
        private readonly ElevationGrid? sourceGrid;
        private readonly float cellSize;
        private readonly string levelName;
        private readonly string levelTitle;
        private readonly HugeImage<Rgb24>? satMap;
        private readonly HugeImage<Rgba32>? satMapRgba;
        private readonly HugeImage<Rgb24>? idMap;
        private readonly List<TerrainMaterialDefinition>? materials;
        private readonly List<BeamNGRoadInput>? roads;
        private readonly List<BeamNGForestInstance>? vegetation;
        private readonly List<BeamNGPond>? ponds;
        private readonly List<BeamNGBuildingBox>? buildings;
        private readonly List<BeamNGModelInstance>? modelInstances;
        private readonly string? modelLibraryDirectory;

        // Shapes copied out of the shared model library, shared by the building placements and the
        // forest species so a mesh and its textures land in the level exactly once.
        private readonly HashSet<string> copiedShapes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (float X, float Y, float Z)> shapeHeads = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> copiedShapeTextures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> armaMaterials = new();
        private readonly List<BeamNGPlace>? places;
        private readonly byte[]? presetLayerMap;
        private readonly List<BeamNGFenceInput>? fences;
        private readonly List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>? buildingMeshes;
        // Buildings are merged per zone: one object per zone keeps the editor responsive while
        // still letting a district be edited area by area
        private const float BuildingZoneSize = 500f;
        private const int MaxBuildingZones = 4_000;

        private const int BaseTextureSize = 4096;
        private const int PreviewSize = 512;
        private const int HeightmapPngMaxSize = 2048;
        private const int MaxForestInstancesPerType = 500_000;
        /// <summary>
        /// Ceiling on the whole forest, all species together. Arma packs vegetation far denser than
        /// a driving game needs: Malden alone carries over half a million bushes and trees, and each
        /// one is a real mesh here rather than a billboard.
        /// </summary>
        private const int MaxForestInstancesTotal = 300_000;
        private const int MaxDecalRoadNodes = 150;
        private const float MinRoadNodeSpacing = 4f;

        /// <summary>
        /// Length of the approach ramp at each end of a bridge. The deck sits at its own altitude
        /// and the ground does not rise to meet it, so without a ramp the carriageway steps up onto
        /// the bridge and back down off it.
        /// </summary>
        private const float BridgeRampLength = 18f;
        // Arma road widths are the texture width, which reads narrow in a driving game, so they get
        // a modest widening. 1.7 made every lane look like a motorway once real buildings framed it.
        private const float RoadWidthFactor = 1.25f;
        // Light smoothing of the elevation grid: removes the faceted look of the terrain mesh
        // without flattening the real relief
        private const float TerrainSmoothing = 0.55f;
        // Pixel size of every generated detail/macro terrain texture. All textures bound to the
        // same TerrainMaterialTextureSet slot must share these dimensions.
        private const int DetailTextureSize = 512;
        // Road corridors are levelled into the heightmap: the road itself is a decal draped over the
        // terrain, so any cell-scale noise left under it is felt as a bump while driving.
        private const float RoadProfileWindow = 60f;  // Length of the longitudinal average, in meters
        private const int RoadProfilePasses = 3;
        private const float RoadShoulder = 10f;       // Blend band outside the road, in meters
        // A corridor only two cells wide still ripples at the grid frequency under the wheels:
        // level a band wide enough to hold several fully flat cells whatever the road width
        private const float RoadMinHalfWidth = 4f;
        /// <summary>How far a carriageway decal floats above the terrain it is draped on.</summary>
        private const float RoadSurfaceLift = 0.1f;
        private const int RoadCorridorSmoothPasses = 2;

        // Terrain layers: physics (groundmodel) varies per painted surface, visuals stay the satellite image
        private static readonly (string Name, string GroundModel)[] TerrainLayers =
        {
            ("grm_grass", "GRASS"),
            ("grm_asphalt", "ASPHALT"),
            ("grm_dirt", "DIRT"),
            ("grm_gravel", "GRAVEL"),
            ("grm_sand", "SAND"),
            ("grm_rock", "ROCK"),
            ("grm_mud", "MUD"),
        };

        public BeamNGLevelWriter(ElevationGrid grid, float cellSize, string levelName, string levelTitle,
            HugeImage<Rgb24>? satMap, HugeImage<Rgb24>? idMap, List<TerrainMaterialDefinition>? materials,
            List<BeamNGRoadInput>? roads, List<BeamNGForestInstance>? vegetation,
            List<BeamNGPond>? ponds = null, List<BeamNGBuildingBox>? buildings = null,
            HugeImage<Rgba32>? satMapRgba = null, byte[]? presetLayerMap = null,
            List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>? buildingMeshes = null,
            List<BeamNGFenceInput>? fences = null,
            List<BeamNGModelInstance>? modelInstances = null, string? modelLibraryDirectory = null,
            List<BeamNGPlace>? places = null, ElevationGrid? sourceGrid = null)
        {
            this.sourceGrid = sourceGrid;
            this.places = places;
            this.buildingMeshes = buildingMeshes;
            this.fences = fences;
            this.modelInstances = modelInstances;
            this.modelLibraryDirectory = modelLibraryDirectory;
            this.grid = grid;
            this.cellSize = cellSize;
            this.levelName = Sanitize(levelName);
            this.levelTitle = levelTitle;
            this.satMap = satMap;
            this.satMapRgba = satMapRgba;
            this.idMap = idMap;
            this.materials = materials;
            this.roads = roads;
            this.vegetation = vegetation;
            this.ponds = ponds;
            this.buildings = buildings;
            this.presetLayerMap = presetLayerMap;
        }

        private float[]? smoothedHeights;
        private int smoothedSize;

        /// <summary>
        /// Blend the elevation grid with a 3x3 average. At 2-4 m per cell the raw grid renders as
        /// visible triangular facets; a light blend keeps the real relief but softens the mesh.
        /// Used for the terrain and for object placement alike, so nothing floats or sinks.
        /// </summary>
        private void BuildSmoothedHeights(int size)
        {
            smoothedSize = size;
            var source = new float[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    source[y * size + x] = grid[x, y];
                }
            }
            var result = new float[size * size];
            Parallel.For(0, size, y =>
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = 0f;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var sy = y + dy;
                        if (sy < 0 || sy >= size) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var sx = x + dx;
                            if (sx < 0 || sx >= size) continue;
                            sum += source[sy * size + sx];
                            count++;
                        }
                    }
                    var average = sum / count;
                    var original = source[y * size + x];
                    result[y * size + x] = original + (average - original) * TerrainSmoothing;
                }
            });
            smoothedHeights = result;
        }

        /// <summary>
        /// Levels the terrain along every road: the elevation profile is averaged along the
        /// centerline, then written back over the road width with a shoulder that blends into the
        /// surrounding relief. Without this the decal road follows every cell of the raw grid and
        /// the car is thrown around. Bridges are skipped, they carry their own deck.
        /// </summary>
        /// <summary>Passes of relaxation over the corridor target. Enough to heal a junction,
        /// few enough that a road keeps its own gradient.</summary>
        private const int CorridorRelaxPasses = 6;

        /// <summary>
        /// Largest height difference the relaxation will average across. A junction disagrees by a
        /// few tens of centimetres; two roads merely passing each other at different levels
        /// disagree by metres, and averaging those settles the corridor between them and leaves a
        /// cliff where it meets untouched ground.
        /// </summary>
        private const float CorridorRelaxLimit = 2f;

        /// <summary>
        /// Averages the corridor target height with its neighbours, staying inside the corridor and
        /// only between cells that nearly agree already.
        /// </summary>
        /// <remarks>
        /// Nearest-road-wins gives every cell the height of whichever centreline is closest, so
        /// where two segments meet the target jumps from one road's profile to the other's across a
        /// single cell. Measured on Malden: road_133 climbed 1.9 m over its first 4.5 m, a 43%
        /// grade, while every other node of the same road sat between 2% and 7%. That step is the
        /// bump felt driving through a junction.
        /// </remarks>
        private static void RelaxCorridorTarget(float[] target, float[] blend, int size)
        {
            var buffer = new float[target.Length];
            for (var pass = 0; pass < CorridorRelaxPasses; pass++)
            {
                Array.Copy(target, buffer, target.Length);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var index = (y * size) + x;
                        if (blend[index] <= 0f)
                        {
                            continue;
                        }
                        var sum = 0f;
                        var count = 0;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var ny = y + dy;
                            if (ny < 0 || ny >= size)
                            {
                                continue;
                            }
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var nx = x + dx;
                                if (nx < 0 || nx >= size)
                                {
                                    continue;
                                }
                                var neighbour = (ny * size) + nx;
                                if (blend[neighbour] > 0f
                                    && MathF.Abs(buffer[neighbour] - buffer[index]) < CorridorRelaxLimit)
                                {
                                    sum += buffer[neighbour];
                                    count++;
                                }
                            }
                        }
                        if (count > 0)
                        {
                            target[index] = sum / count;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Radius within which two road ends are taken to be the same junction. A real crossroads
        /// has width: measured on Malden, road_106, road_133 and road_134 meet over 22 m of ground.
        /// </summary>
        private const float JunctionReach = 24f;

        /// <summary>
        /// Distance over which a profile is bent back to its junction height. Matched to
        /// <see cref="RoadProfileWindow"/>, because that is the length over which the profile was
        /// smoothed and therefore the length across which the two roads drifted apart. At 25 m a
        /// 4 m disagreement still averaged 16% of grade; over 60 m it is under 7%.
        /// </summary>
        private const float JunctionBlend = 40f;

        /// <summary>
        /// One agreed altitude per junction, keyed by a coarse grid cell.
        /// </summary>
        /// <remarks>
        /// Each road smooths its own profile over sixty metres of its own length, so two segments
        /// meeting end to end arrive at their shared point with different answers. Nearest-road-wins
        /// then hands the cell to one of them and the other stops in mid air. Measured on Malden:
        /// road_133 and road_134 meet 4.3 m apart and disagree by 4.1 m, which the corridor turns
        /// into a 43% ramp over the first few metres of road_133 while the rest of it sits between
        /// 2% and 7%. Averaging afterwards cannot fix this; the two profiles have to start from the
        /// same number.
        /// </remarks>
        private readonly record struct Junction(float X, float Y, float Height);

        /// <summary>
        /// One agreed altitude per junction, grouped by distance between road ends.
        /// </summary>
        /// <remarks>
        /// Each road smooths its own profile over sixty metres of its own length, so segments
        /// meeting end to end arrive at their shared point with different answers. Nearest-road-wins
        /// then hands each cell to one of them and the others stop in mid air. Measured on Malden:
        /// road_106, road_133 and road_134 meet across 22 m and disagreed by 3.2 m, which the
        /// corridor turned into a 40% step over the last 4.5 m of road_106 while the rest of it sat
        /// at 1-2%. Averaging the corridor afterwards cannot repair that; the profiles have to start
        /// from the same number.
        /// <para>
        /// Grouped by real distance rather than by rounding onto a grid: a grid cell splits two ends
        /// a metre apart that happen to straddle a boundary, and merges nothing that is genuinely
        /// far. That was the first attempt and it left road_106 out of its own junction.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Pulls the ends of the roads that meet at a junction onto the junction itself, and pushes
        /// each of them just far enough across it to reach the far edge of the widest road there.
        /// </summary>
        /// <remarks>
        /// Arma's road segments stop wherever their own polyline ended, not on a shared point:
        /// measured on Malden, the three ends at road_80 / road_160 / road_162 stand 14 m apart, and
        /// the median junction spreads 3.7 m. A decal road is a ribbon, so ends that far apart leave
        /// the terrain showing between them, which is the tan wedge that appears in the middle of
        /// every junction where three roads meet.
        /// <para>
        /// The overhang is half the width of the widest <em>other</em> road of the junction, never
        /// more. That is exactly the distance from the centre line of the road being crossed to its
        /// kerb, so a side road reaches the far edge of the road it joins and stops there. Adding a
        /// fixed overhang instead would leave a tongue of tarmac sticking out into the field on
        /// every T junction.
        /// </para>
        /// </remarks>
        private void StitchJunctions(IProgressScope scope)
        {
            if (roads == null || roads.Count == 0)
            {
                return;
            }

            var ends = new List<(int Road, bool FromStart, float X, float Y, float Width)>();
            for (var i = 0; i < roads.Count; i++)
            {
                var road = roads[i];
                if (road.Points == null || road.Points.Count < 2)
                {
                    continue;
                }
                ends.Add((i, true, road.Points[0].X, road.Points[0].Y, road.Width));
                ends.Add((i, false, road.Points[^1].X, road.Points[^1].Y, road.Width));
            }

            var moved = 0;
            var junctions = 0;
            foreach (var group in ClusterEnds(ends.Select(e => (e.X, e.Y)).ToList(),
                                              ends.Select(e => e.Road).ToList()))
            {
                if (group.Count < 2)
                {
                    continue; // a dead end, nothing to stitch it to
                }
                junctions++;

                var centreX = group.Average(k => ends[k].X);
                var centreY = group.Average(k => ends[k].Y);
                foreach (var k in group)
                {
                    var overhang = group.Where(o => ends[o].Road != ends[k].Road)
                        .Select(o => DrawnWidth(ends[o].Width, roads[ends[o].Road].IsDirt) / 2f)
                        .DefaultIfEmpty(0f)
                        .Max();
                    if (MoveEndTo(ends[k].Road, ends[k].FromStart, centreX, centreY, overhang))
                    {
                        moved++;
                    }
                }
            }
            scope.WriteLine($"Junctions: {moved} road ends stitched onto {junctions} junctions");
        }

        /// <summary>
        /// Groups road ends into junctions, merging on the distance between running centres.
        /// </summary>
        /// <remarks>
        /// Not a single pass that claims everything near the first end it meets. At the junction of
        /// road_8, road_9 and road_10 on Malden the outer two ends stand 24.3 m apart while both sit
        /// within reach of the middle one, so whether the three came out as one junction or two
        /// depended on which end the pass happened to start from -- and it started from the wrong
        /// one, which left that crossing three quarters bare ground. Merging clusters by their
        /// centres instead makes the answer independent of the order the roads are listed in.
        /// <para>
        /// The radius cap stops a village high street from chaining end to end into a single
        /// hundred-metre "junction" that would drag every road in it onto one point.
        /// </para>
        /// <para>
        /// A road never joins itself. road_50 on Malden is 9.7 m long from one node to the next, so
        /// both of its ends fell inside the same reach: they were declared a junction with
        /// themselves and each was dragged towards the other's midpoint, folding the road in half.
        /// Any road shorter than the reach would have gone the same way.
        /// </para>
        /// </remarks>
        private static List<List<int>> ClusterEnds(List<(float X, float Y)> ends, List<int> roadOfEnd)
        {
            const float MaxJunctionRadius = 18f;

            // Two ends belong together when they are within reach of EACH OTHER, not when the
            // running centres are. Comparing centres was too strict and it showed: at the crossing
            // of road_40, road_1037 and road_1399 on Romont the third end is 22.9 m from the first
            // and 27.2 m from the second, so once the first two had merged their centre had drifted
            // out of range and the third was left out of its own junction, baring 98% of it.
            var parent = Enumerable.Range(0, ends.Count).ToArray();

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            for (var i = 0; i < ends.Count; i++)
            {
                for (var j = i + 1; j < ends.Count; j++)
                {
                    var dx = ends[j].X - ends[i].X;
                    var dy = ends[j].Y - ends[i].Y;
                    if ((dx * dx) + (dy * dy) >= JunctionReach * JunctionReach)
                    {
                        continue;
                    }
                    var a = Find(i);
                    var b = Find(j);
                    if (a != b)
                    {
                        parent[a] = b;
                    }
                }
            }

            var byRoot = new Dictionary<int, List<int>>();
            for (var i = 0; i < ends.Count; i++)
            {
                var root = Find(i);
                if (!byRoot.TryGetValue(root, out var list))
                {
                    byRoot[root] = list = new List<int>();
                }
                list.Add(i);
            }

            var result = new List<List<int>>();
            foreach (var group in byRoot.Values)
            {
                if (group.Count < 2)
                {
                    result.Add(group);
                    continue;
                }
                var cx = group.Average(k => ends[k].X);
                var cy = group.Average(k => ends[k].Y);

                // A road that brings both its ends to the same crossing keeps only the nearer one.
                // Abandoning the whole group instead, which is what this did first, left 45 of
                // Romont's 548 junctions untouched: at (2162, -1668) two dirt roads each loop back
                // within 24 m of themselves, so one duplicated road was enough to bare the crossing
                // completely. Evicting the far end leaves a perfectly good junction behind.
                var kept = group
                    .GroupBy(k => roadOfEnd[k])
                    .Select(g => g.OrderBy(k => Distance(ends[k], cx, cy)).First())
                    .ToList();
                var evicted = group.Where(k => !kept.Contains(k)).ToList();

                // A chain of ends running down a village street can link up into something far too
                // big to be one crossing. That one is left alone entirely: leaving the ends where
                // Arma put them beats dragging them somewhere invented.
                if (kept.Count < 2
                    || kept.Max(k => Distance(ends[k], cx, cy)) > MaxJunctionRadius)
                {
                    result.AddRange(group.Select(k => new List<int> { k }));
                    continue;
                }

                result.Add(kept);
                result.AddRange(evicted.Select(k => new List<int> { k }));
            }
            return result;
        }

        private static float Distance((float X, float Y) point, float x, float y)
        {
            var dx = point.X - x;
            var dy = point.Y - y;
            return MathF.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>
        /// Width the carriageway is actually drawn at, which is not the width Arma declares: the
        /// export widens roads and enforces a drivable minimum. The overhang has to match the ribbon
        /// on screen, not the number in the config.
        /// </summary>
        private static float DrawnWidth(float armaWidth, bool isDirt)
        {
            return Math.Max(isDirt ? 4f : 6.5f, armaWidth * RoadWidthFactor);
        }

        /// <summary>
        /// Moves one end of a road onto a junction, carried <paramref name="overhang"/> metres past
        /// it along the direction the road was already heading. Returns false when the road is too
        /// short or too degenerate to give a direction.
        /// </summary>
        private bool MoveEndTo(int roadIndex, bool fromStart, float centreX, float centreY, float overhang)
        {
            var points = roads![roadIndex].Points;
            var endIndex = fromStart ? 0 : points.Count - 1;
            var neighbour = fromStart ? points[1] : points[^2];
            var original = points[endIndex];

            var dx = centreX - neighbour.X;
            var dy = centreY - neighbour.Y;
            var length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (length < 1f)
            {
                // The junction sits on top of the second node: extending from here would point the
                // road in an arbitrary direction, so leave it where it is.
                return false;
            }

            // The junction has to lie ahead of the road, not behind it. Where it lay behind, the tip
            // was dragged back over the road's own second segment and the ribbon folded flat on
            // itself: measured on Romont, ten roads doubled back through 155 to 180 degrees at their
            // very first or last node.
            var awayX = original.X - neighbour.X;
            var awayY = original.Y - neighbour.Y;
            if ((awayX * dx) + (awayY * dy) <= 0f)
            {
                return false;
            }

            // The overhang must not carry the tip back past the node it is measured from, or the
            // last segment doubles back and the ribbon folds over itself
            var reach = Math.Min(overhang, Math.Max(0f, length - 1f));
            var newX = centreX + (dx / length * reach);
            var newY = centreY + (dy / length * reach);

            // A tip that lands right on top of its own neighbour leaves a stub of a segment, which
            // is what put a 0.59 m last leg on road_296. The road already reaches the junction
            // without it, so the node goes rather than the distance.
            var leg = MathF.Sqrt(((newX - neighbour.X) * (newX - neighbour.X))
                                 + ((newY - neighbour.Y) * (newY - neighbour.Y)));
            if (leg < 2f)
            {
                if (points.Count <= 2)
                {
                    return false;
                }
                points.RemoveAt(endIndex);
                return true;
            }

            points[endIndex] = new TerrainPoint(newX, newY);
            return true;
        }

        /// <summary>
        /// How much further a corridor reaches sideways as it approaches a junction, so the wedge
        /// between two roads meeting at an angle ends up inside one corridor or the other.
        /// </summary>
        private static float JunctionExtraReach(TerrainPoint point, List<Junction> junctions)
        {
            const float Range = 25f;
            const float Extra = 10f;

            var best = float.MaxValue;
            foreach (var junction in junctions)
            {
                var dx = junction.X - point.X;
                var dy = junction.Y - point.Y;
                var distance = (dx * dx) + (dy * dy);
                if (distance < best)
                {
                    best = distance;
                }
            }
            if (best > Range * Range)
            {
                return 0f;
            }
            return Extra * (1f - (MathF.Sqrt(best) / Range));
        }

        /// <summary>
        /// Junction heights taken from the roads' own smoothed profiles.
        /// </summary>
        /// <remarks>
        /// The height a junction settles on has to be one the roads meeting there can actually
        /// reach without a jolt, and that means it comes from their profiles, not from the ground
        /// underneath. <see cref="BuildJunctions"/> reads the raw terrain and is still what the
        /// bridge pass wants, but for the corridor it produced a target the smoothing never passes
        /// through, so the road climbed onto it over its last few metres.
        /// </remarks>
        private List<Junction> JunctionsFromProfiles(
            List<(BeamNGRoadInput Road, List<TerrainPoint> Samples, float[] Profile)> sampled)
        {
            var ends = new List<(float X, float Y, float Z)>();
            foreach (var (_, samples, profile) in sampled)
            {
                ends.Add((samples[0].X, samples[0].Y, profile[0]));
                ends.Add((samples[^1].X, samples[^1].Y, profile[^1]));
            }

            var result = new List<Junction>();
            var taken = new bool[ends.Count];
            for (var i = 0; i < ends.Count; i++)
            {
                if (taken[i])
                {
                    continue;
                }
                var group = new List<int> { i };
                taken[i] = true;
                for (var j = i + 1; j < ends.Count; j++)
                {
                    if (taken[j])
                    {
                        continue;
                    }
                    var dx = ends[j].X - ends[i].X;
                    var dy = ends[j].Y - ends[i].Y;
                    if ((dx * dx) + (dy * dy) < JunctionReach * JunctionReach)
                    {
                        group.Add(j);
                        taken[j] = true;
                    }
                }
                if (group.Count > 1)
                {
                    result.Add(new Junction(
                        group.Average(k => ends[k].X),
                        group.Average(k => ends[k].Y),
                        group.Average(k => ends[k].Z)));
                }
            }
            return result;
        }

        private List<Junction> BuildJunctions()
        {
            var ends = new List<(float X, float Y, float Z)>();
            if (roads != null)
            {
                foreach (var road in roads)
                {
                    if (road.Points == null || road.Points.Count < 2)
                    {
                        continue;
                    }
                    foreach (var point in new[] { road.Points[0], road.Points[^1] })
                    {
                        ends.Add((point.X, point.Y, ElevationAt(point.X, point.Y)));
                    }
                }
            }

            var result = new List<Junction>();
            var taken = new bool[ends.Count];
            for (var i = 0; i < ends.Count; i++)
            {
                if (taken[i])
                {
                    continue;
                }
                var group = new List<int> { i };
                taken[i] = true;
                for (var j = i + 1; j < ends.Count; j++)
                {
                    if (taken[j])
                    {
                        continue;
                    }
                    var dx = ends[j].X - ends[i].X;
                    var dy = ends[j].Y - ends[i].Y;
                    if ((dx * dx) + (dy * dy) < JunctionReach * JunctionReach)
                    {
                        group.Add(j);
                        taken[j] = true;
                    }
                }
                // A lone end is a dead end, not a junction: nothing to agree with.
                if (group.Count > 1)
                {
                    result.Add(new Junction(
                        group.Average(k => ends[k].X),
                        group.Average(k => ends[k].Y),
                        group.Average(k => ends[k].Z)));
                }
            }
            return result;
        }

        /// <summary>
        /// Bends one end of a profile back to the height its junction agreed on, fading the
        /// correction out over <see cref="JunctionBlend"/> so the road keeps its own gradient a
        /// short way in.
        /// </summary>
        private static void PinToJunction(float[] profile, List<TerrainPoint> samples,
            List<Junction> junctions, float step, bool fromStart)
        {
            var end = fromStart ? 0 : samples.Count - 1;
            var x = samples[end].X;
            var y = samples[end].Y;

            var best = float.MaxValue;
            var agreed = 0f;
            foreach (var junction in junctions)
            {
                var dx = junction.X - x;
                var dy = junction.Y - y;
                var distance = (dx * dx) + (dy * dy);
                if (distance < best)
                {
                    best = distance;
                    agreed = junction.Height;
                }
            }
            if (best > JunctionReach * JunctionReach)
            {
                return;
            }

            var delta = agreed - profile[end];
            if (MathF.Abs(delta) < 0.01f)
            {
                return;
            }
            var span = Math.Min(samples.Count, Math.Max(1, (int)(JunctionBlend / step)));
            for (var i = 0; i < span; i++)
            {
                var index = fromStart ? i : samples.Count - 1 - i;
                profile[index] += delta * (1f - ((float)i / span));
            }
        }

        private void FlattenRoadCorridors(int size)
        {
            if (roads == null || roads.Count == 0 || smoothedHeights == null)
            {
                return;
            }
            // Nearest centerline sample wins: averaging the samples around a cell blurs a curve
            // into a saddle and puts back the bumps this is supposed to remove. Where two roads
            // meet that rule alone leaves a step, because each road smoothed its own profile along
            // its own length and the two do not agree at the crossing; the target field is relaxed
            // afterwards to heal exactly that.
            var bestDistance = new float[size * size];
            var target = new float[size * size];
            var blend = new float[size * size];
            Array.Fill(bestDistance, float.MaxValue);
            var step = Math.Max(0.5f, cellSize * 0.5f);

            // Every profile is smoothed before any junction height is worked out, and the junction
            // then takes the average of the profiles that meet there rather than of the raw ground.
            // Taking it from the ground was the bump: each road is laid on its own smoothed profile,
            // so pinning its end to a height the smoothing never produced makes the corridor jump at
            // the very last metres. Measured on Romont before this: 358 humps over 60 cm, 82% of
            // them within 25 m of a road end, the worst 2.54 m over forty metres of road.
            var sampled = new List<(BeamNGRoadInput Road, List<TerrainPoint> Samples, float[] Profile)>();
            foreach (var road in roads)
            {
                if (road.IsBridge || road.Points == null || road.Points.Count < 2)
                {
                    continue;
                }
                var samples = ResamplePath(road.Points, step);
                if (samples.Count < 2)
                {
                    continue;
                }
                var profile = new float[samples.Count];
                for (var i = 0; i < samples.Count; i++)
                {
                    profile[i] = ElevationAt(samples[i].X, samples[i].Y);
                }
                SmoothProfile(profile, Math.Max(1, (int)(RoadProfileWindow / step / 2f)));
                sampled.Add((road, samples, profile));
            }

            var junctions = JunctionsFromProfiles(sampled);

            foreach (var (road, samples, profile) in sampled)
            {
                PinToJunction(profile, samples, junctions, step, fromStart: true);
                PinToJunction(profile, samples, junctions, step, fromStart: false);

                var halfWidth = Math.Max(RoadMinHalfWidth, road.Width * RoadWidthFactor / 2f);
                var baseReach = halfWidth + RoadShoulder;
                for (var i = 0; i < samples.Count; i++)
                {
                    var height = profile[i];
                    // Widened near a crossing, because the wedge between two corridors meeting at an
                    // angle lies outside the reach of both and keeps the raw hillside, standing up
                    // as a mound where the wheels cross. Widening rather than flattening on purpose:
                    // levelling that area to one height was tried and it cut a step into the road
                    // where the pad met a slope, a 24% spike beside the junction at (1204, 1416).
                    // Reaching further keeps the nearest-centre-line rule and its own gradient.
                    var reach = baseReach + JunctionExtraReach(samples[i], junctions);
                    var gx = samples[i].X / cellSize;
                    var gy = samples[i].Y / cellSize;
                    var cells = (int)MathF.Ceiling(reach / cellSize);
                    var minX = Math.Max(0, (int)gx - cells);
                    var maxX = Math.Min(size - 1, (int)gx + cells + 1);
                    var minY = Math.Max(0, (int)gy - cells);
                    var maxY = Math.Min(size - 1, (int)gy + cells + 1);
                    for (var y = minY; y <= maxY; y++)
                    {
                        for (var x = minX; x <= maxX; x++)
                        {
                            var dx = (x - gx) * cellSize;
                            var dy = (y - gy) * cellSize;
                            var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                            if (distance >= reach)
                            {
                                continue;
                            }
                            var weight = distance <= halfWidth ? 1f : 1f - ((distance - halfWidth) / RoadShoulder);
                            var index = (y * size) + x;
                            // Past the road's own shoulder the widening may only cut, never fill.
                            // It exists to take down the wedge of raw hillside left standing between
                            // two corridors; allowed to raise as well it builds the road's
                            // embankment outwards into a mound sitting across the crossing, which is
                            // exactly the hump that kept coming back at the junctions.
                            if (distance > baseReach && height > smoothedHeights[index])
                            {
                                continue;
                            }
                            if (distance < bestDistance[index])
                            {
                                bestDistance[index] = distance;
                                target[index] = height;
                            }
                            if (weight > blend[index])
                            {
                                blend[index] = weight;
                            }
                        }
                    }
                }
            }

            RelaxCorridorTarget(target, blend, size);

            for (var index = 0; index < smoothedHeights.Length; index++)
            {
                if (blend[index] > 0f)
                {
                    smoothedHeights[index] += (target[index] - smoothedHeights[index]) * blend[index];
                }
            }

            SmoothCorridors(size, blend);
            CarveUnderBridges(size);
        }

        /// <summary>
        /// A bridge deck is a straight chord between its two ends. Where the ground rises above that
        /// chord the slab comes out of the hillside, which is what produced the concrete walls across
        /// the landscape. Cut the ground back down to the deck there, and only there: a real gap
        /// under the deck is left untouched so the bridge still spans it.
        /// Runs after the road corridors so both read the same levelled ends.
        /// </summary>
        private void CarveUnderBridges(int size)
        {
            if (roads == null || smoothedHeights == null)
            {
                return;
            }
            const float clearance = 0.3f; // Keep the slab visibly above the ground it grazes
            var step = Math.Max(0.5f, cellSize * 0.5f);
            foreach (var road in roads)
            {
                if (!road.IsBridge || road.Points == null || road.Points.Count < 2)
                {
                    continue;
                }
                var samples = ResamplePath(road.Points, step);
                if (samples.Count < 2)
                {
                    continue;
                }
                // Same chord as FlattenBridgeDeck: linear between both ends, by travelled distance
                var startZ = ElevationAt(samples[0].X, samples[0].Y);
                var endZ = ElevationAt(samples[^1].X, samples[^1].Y);
                var distances = new float[samples.Count];
                var total = 0f;
                for (var i = 1; i < samples.Count; i++)
                {
                    total += (samples[i].Vector - samples[i - 1].Vector).Length();
                    distances[i] = total;
                }
                if (total <= 0f)
                {
                    continue;
                }

                var halfWidth = Math.Max(RoadMinHalfWidth, road.Width * RoadWidthFactor / 2f);
                var reach = halfWidth + RoadShoulder;
                for (var i = 0; i < samples.Count; i++)
                {
                    var deck = startZ + ((endZ - startZ) * (distances[i] / total));
                    var gx = samples[i].X / cellSize;
                    var gy = samples[i].Y / cellSize;
                    var cells = (int)MathF.Ceiling(reach / cellSize);
                    var minX = Math.Max(0, (int)gx - cells);
                    var maxX = Math.Min(size - 1, (int)gx + cells + 1);
                    var minY = Math.Max(0, (int)gy - cells);
                    var maxY = Math.Min(size - 1, (int)gy + cells + 1);
                    for (var y = minY; y <= maxY; y++)
                    {
                        for (var x = minX; x <= maxX; x++)
                        {
                            var dx = (x - gx) * cellSize;
                            var dy = (y - gy) * cellSize;
                            var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                            if (distance >= reach)
                            {
                                continue;
                            }
                            // Full cut under the deck, fading out over the shoulder so the cut edge
                            // is a slope and not a trench wall
                            var weight = distance <= halfWidth ? 1f : 1f - ((distance - halfWidth) / RoadShoulder);
                            var index = (y * size) + x;

                            var ceiling = deck - (clearance * weight);
                            if (smoothedHeights[index] > ceiling)
                            {
                                smoothedHeights[index] += (ceiling - smoothedHeights[index]) * weight;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Averages the levelled cells with their neighbours, so the seams left where two roads
        /// meet or where the shoulder joins the relief do not read as a step under the wheels.
        /// Cells outside a corridor are read but never written: the surrounding relief is kept.
        /// </summary>
        private void SmoothCorridors(int size, float[] blend)
        {
            if (smoothedHeights == null)
            {
                return;
            }
            var buffer = new float[smoothedHeights.Length];
            for (var pass = 0; pass < RoadCorridorSmoothPasses; pass++)
            {
                Array.Copy(smoothedHeights, buffer, buffer.Length);
                Parallel.For(0, size, y =>
                {
                    for (var x = 0; x < size; x++)
                    {
                        var index = (y * size) + x;
                        if (blend[index] <= 0f)
                        {
                            continue;
                        }
                        var sum = 0f;
                        var count = 0;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var sy = y + dy;
                            if (sy < 0 || sy >= size) continue;
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var sx = x + dx;
                                if (sx < 0 || sx >= size) continue;
                                sum += buffer[(sy * size) + sx];
                                count++;
                            }
                        }
                        smoothedHeights[index] = sum / count;
                    }
                });
            }
        }

        /// <summary>Walks a path and returns points evenly spaced by <paramref name="step"/> meters.</summary>
        private static List<TerrainPoint> ResamplePath(List<TerrainPoint> points, float step)
        {
            var result = new List<TerrainPoint> { points[0] };
            var carry = 0f;
            for (var i = 1; i < points.Count; i++)
            {
                var from = points[i - 1];
                var to = points[i];
                var length = (to.Vector - from.Vector).Length();
                if (length <= 0f)
                {
                    continue;
                }
                var direction = (to.Vector - from.Vector) / length;
                for (var distance = step - carry; distance < length; distance += step)
                {
                    result.Add(new TerrainPoint(from.Vector + (direction * distance)));
                }
                carry = (carry + length) % step;
            }
            result.Add(points[points.Count - 1]);
            return result;
        }

        /// <summary>In-place moving average over a profile, repeated to approximate a gaussian.</summary>
        private static void SmoothProfile(float[] profile, int radius)
        {
            var buffer = new float[profile.Length];
            for (var pass = 0; pass < RoadProfilePasses; pass++)
            {
                for (var i = 0; i < profile.Length; i++)
                {
                    var sum = 0f;
                    var count = 0;
                    var from = Math.Max(0, i - radius);
                    var to = Math.Min(profile.Length - 1, i + radius);
                    for (var j = from; j <= to; j++)
                    {
                        sum += profile[j];
                        count++;
                    }
                    buffer[i] = sum / count;
                }
                Array.Copy(buffer, profile, profile.Length);
            }
        }

        /// <summary>
        /// Gives the ground under every placed bridge back the shape Arma gave it.
        /// </summary>
        /// <remarks>
        /// A bridge only reads as a bridge if there is a gap under it. The corridor pass levels the
        /// ground along the carriageway, and where that carriageway runs over a bridge it fills in
        /// the very ravine the bridge spans: measured on Malden, the terrain across all eight decks
        /// came out flat to within a metre, so every one of them sat in the dirt with its ramps
        /// buried. Restoring the source elevation under the deck reopens the gap without touching
        /// the road, which keeps its levelled corridor either side.
        /// <para>
        /// Only ever lowers. Where Arma's own ground was higher than the levelled corridor, the
        /// corridor is the one that has to win, or the road would climb back into the hill it was
        /// cut through.
        /// </para>
        /// </remarks>
        private void CarveBridgeGaps(IProgressScope scope)
        {
            if (smoothedHeights == null || sourceGrid == null || modelInstances == null
                || string.IsNullOrEmpty(modelLibraryDirectory))
            {
                return;
            }

            // Enough room either side of the deck for the banks, without eating the approach roads
            const float SideMargin = 2f;
            const float Feather = 6f;

            var daeDirectory = Path.Combine(modelLibraryDirectory, "dae");
            var bounds = new Dictionary<string, (float HalfLength, float HalfWidth)>(StringComparer.OrdinalIgnoreCase);
            var carved = 0;
            var lowered = 0;

            // Every reading is taken before the first cell is touched. A deck has to keep the height
            // of the levelled corridor it carries, and the whole point of this pass is to drop the
            // ground away from under it: reading afterwards would sink each bridge into the ravine
            // it just opened, and two bridges close together would drag each other down in turn.
            foreach (var instance in modelInstances)
            {
                if (!instance.Model.Contains("bridge", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var key = GroundKey(instance.X, instance.Y);
                var deckName = SanitizeModelName(instance.Model);
                var deckFile = Path.Combine(daeDirectory, deckName + ".dae");
                if (File.Exists(deckFile) && TryDeckTarget(instance, File.ReadAllText(deckFile), out var target))
                {
                    bridgeTargetZ[key] = target;
                }
                else
                {
                    bridgeLift[key] = LiftForBridge(instance.X, instance.Y);
                }
            }

            foreach (var instance in modelInstances)
            {
                if (!instance.Model.Contains("bridge", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var name = SanitizeModelName(instance.Model);
                if (!bounds.TryGetValue(name, out var box))
                {
                    var file = Path.Combine(daeDirectory, name + ".dae");
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    box = ShapeFootprint(File.ReadAllText(file));
                    bounds[name] = box;
                }
                if (box.HalfLength < 2f || box.HalfWidth < 1f)
                {
                    continue;
                }
                carved++;
                lowered += CarveOne(instance, box.HalfLength * instance.Scale,
                    (box.HalfWidth * instance.Scale) + SideMargin, Feather);
            }

            if (carved > 0)
            {
                scope.WriteLine($"Bridges: {lowered} terrain cells reopened under {carved} decks");
            }
        }

        /// <summary>
        /// Pulls the terrain under one deck back down to the source elevation, fading out over
        /// <paramref name="feather"/> metres so the banks meet the untouched ground smoothly.
        /// </summary>
        private int CarveOne(BeamNGModelInstance instance, float halfLength, float halfWidth, float feather)
        {
            var rotation = instance.Rotation;
            // Row 0 is the model's own +X in world, which is the axis a deck runs along
            var alongX = rotation.Length >= 9 ? rotation[0] : 1f;
            var alongY = rotation.Length >= 9 ? rotation[1] : 0f;
            var length = MathF.Sqrt((alongX * alongX) + (alongY * alongY));
            if (length < 0.001f)
            {
                return 0;
            }
            alongX /= length;
            alongY /= length;

            var reach = halfLength + halfWidth + feather;
            var minX = Math.Max(0, (int)((instance.X - reach) / cellSize));
            var maxX = Math.Min(smoothedSize - 1, (int)((instance.X + reach) / cellSize) + 1);
            var minY = Math.Max(0, (int)((instance.Y - reach) / cellSize));
            var maxY = Math.Min(smoothedSize - 1, (int)((instance.Y + reach) / cellSize) + 1);

            var count = 0;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var worldX = x * cellSize;
                    var worldY = y * cellSize;
                    var dx = worldX - instance.X;
                    var dy = worldY - instance.Y;
                    var along = MathF.Abs((dx * alongX) + (dy * alongY));
                    var across = MathF.Abs((dx * -alongY) + (dy * alongX));
                    if (along > halfLength + feather || across > halfWidth + feather)
                    {
                        continue;
                    }

                    var weight = MathF.Min(
                        Falloff(along, halfLength, feather),
                        Falloff(across, halfWidth, feather));
                    if (weight <= 0f)
                    {
                        continue;
                    }

                    var index = (y * smoothedSize) + x;
                    var source = sourceGrid!.ElevationAt(new TerrainPoint(worldX, worldY));
                    if (source >= smoothedHeights![index])
                    {
                        continue; // the corridor is already the lower of the two, leave it alone
                    }
                    smoothedHeights[index] += (source - smoothedHeights[index]) * weight;
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// How far each bridge has to move to stay with its road, recorded before any gap was cut.
        /// </summary>
        private readonly Dictionary<(int X, int Y), float> bridgeLift = new();

        private static (int X, int Y) GroundKey(float x, float y)
        {
            return ((int)MathF.Round(x), (int)MathF.Round(y));
        }

        /// <summary>
        /// Height change a bridge has to follow: the one its own carriageway went through when the
        /// corridor was levelled.
        /// </summary>
        /// <remarks>
        /// Measured under the deck's own position. Following the nearest road instead was tried and
        /// is worse: for an overpass the nearest road is the one passing underneath, whose corridor
        /// moved by something else entirely, and the decks went from 1.1 m below their carriageway
        /// to 3.9 m below it.
        /// </remarks>
        private float LiftForBridge(float x, float y)
        {
            return ElevationAt(x, y) - sourceGrid!.ElevationAt(new TerrainPoint(x, y));
        }

        private float BridgeLift(float x, float y, float fallbackGround, float armaGround)
        {
            return bridgeLift.TryGetValue(GroundKey(x, y), out var lift)
                ? lift
                : fallbackGround - armaGround;
        }

        /// <summary>Absolute altitude a bridge needs so its deck meets the road it carries.</summary>
        private readonly Dictionary<(int X, int Y), float> bridgeTargetZ = new();

        /// <summary>
        /// Works out where a bridge has to sit for its deck to come out level with its carriageway.
        /// </summary>
        /// <remarks>
        /// Everything else about a bridge follows from two numbers neither of which is its position.
        /// <para>
        /// The first is how far the driving surface sits above the model's origin, and it is not
        /// zero: an ODOL model is autocentred, so <c>bridge_highway_f</c> runs from -4.29 m to
        /// +4.26 m about its anchor and the deck is up at +3.2 m. Placing the origin at road height,
        /// which is what every rule tried before this one did, leaves the deck three metres in the
        /// air with the road running underneath it. Measured across Malden's seven bridges the deck
        /// stood 1.85 m to 5.62 m above its own carriageway. The median height of the collision mesh
        /// estimates that offset to within 0.2 m of a proper area weighted answer, and costs nothing.
        /// </para>
        /// <para>
        /// The second is which way the deck runs, and it is the model's own Y, not its X:
        /// <c>bridge_highway_f</c> measures 20.8 m across and 44.3 m along. Looking down the wrong
        /// axis is what made five of eight bridges report that they carried no road at all.
        /// </para>
        /// </remarks>
        private bool TryDeckTarget(BeamNGModelInstance instance, string dae, out float target)
        {
            const float RoadSearch = 50f;
            const float AlignDegrees = 25f;

            target = 0f;
            var deck = DeckOffset(dae);
            if (deck <= 0f)
            {
                return false;
            }

            var alongY = LongAxisIsY(dae);
            var rotation = instance.Rotation;
            var axisX = 1f;
            var axisY = 0f;
            if (rotation.Length >= 9)
            {
                axisX = alongY ? rotation[3] : rotation[0];
                axisY = alongY ? rotation[4] : rotation[1];
            }
            var axisLength = MathF.Sqrt((axisX * axisX) + (axisY * axisY));
            if (axisLength < 0.001f)
            {
                return false;
            }
            axisX /= axisLength;
            axisY /= axisLength;

            var best = float.MaxValue;
            var bestZ = 0f;
            if (roads != null)
            {
                foreach (var road in roads)
                {
                    var points = road.Points;
                    if (points == null || points.Count < 2)
                    {
                        continue;
                    }
                    for (var i = 1; i < points.Count; i++)
                    {
                        var ax = points[i].X - points[i - 1].X;
                        var ay = points[i].Y - points[i - 1].Y;
                        var run = MathF.Sqrt((ax * ax) + (ay * ay));
                        if (run < 0.5f)
                        {
                            continue;
                        }
                        // A deck has no front, so the road may run either way along it
                        var cos = MathF.Abs(((ax / run) * axisX) + ((ay / run) * axisY));
                        if (cos < MathF.Cos(AlignDegrees * MathF.PI / 180f))
                        {
                            continue;
                        }
                        var mx = (points[i].X + points[i - 1].X) / 2f;
                        var my = (points[i].Y + points[i - 1].Y) / 2f;
                        var distance = ((mx - instance.X) * (mx - instance.X)) + ((my - instance.Y) * (my - instance.Y));
                        if (distance < best)
                        {
                            best = distance;
                            bestZ = ElevationAt(mx, my) + RoadSurfaceLift;
                        }
                    }
                }
            }
            if (best > RoadSearch * RoadSearch)
            {
                return false;
            }

            target = bestZ - (deck * instance.Scale);
            return true;
        }

        /// <summary>
        /// Height of the driving surface above a shape's origin, taken as the median height of its
        /// collision mesh.
        /// </summary>
        private static float DeckOffset(string dae)
        {
            var match = Regex.Match(dae, @"Colmesh[^""]*-pos-array""\s+count=""\d+"">(?<values>[^<]*)<",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return 0f;
            }
            var heights = new List<float>();
            var index = 0;
            foreach (var token in match.Groups["values"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (index % 3 == 2 && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    heights.Add(value);
                }
                index++;
            }
            if (heights.Count == 0)
            {
                return 0f;
            }
            heights.Sort();
            return heights[heights.Count / 2];
        }

        /// <summary>True when the shape is longer along its own Y than along its X.</summary>
        private static bool LongAxisIsY(string dae)
        {
            var match = PositionArrayRegex.Match(dae);
            if (!match.Success)
            {
                return false;
            }
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            var index = 0;
            foreach (var token in match.Groups["values"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (index % 3 != 2
                    && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    if (index % 3 == 0)
                    {
                        minX = MathF.Min(minX, value);
                        maxX = MathF.Max(maxX, value);
                    }
                    else
                    {
                        minY = MathF.Min(minY, value);
                        maxY = MathF.Max(maxY, value);
                    }
                }
                index++;
            }
            return (maxY - minY) > (maxX - minX);
        }

        private static float Falloff(float distance, float half, float feather)
        {
            if (distance <= half)
            {
                return 1f;
            }
            return MathF.Max(0f, 1f - ((distance - half) / feather));
        }

        /// <summary>
        /// Half length and half width of a ported shape on the ground, from the finest detail level.
        /// </summary>
        /// <remarks>
        /// The port writes vertices as (X, arma Z, arma Y): the first two are the ground plane, the
        /// third is height. A deck runs along its own X.
        /// </remarks>
        private static (float HalfLength, float HalfWidth) ShapeFootprint(string dae)
        {
            var array = PositionArrayRegex.Match(dae);
            if (!array.Success)
            {
                return (0f, 0f);
            }
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            var index = 0;
            foreach (var token in array.Groups["values"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (index % 3 != 2
                    && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    if (index % 3 == 0)
                    {
                        minX = MathF.Min(minX, value);
                        maxX = MathF.Max(maxX, value);
                    }
                    else
                    {
                        minY = MathF.Min(minY, value);
                        maxY = MathF.Max(maxY, value);
                    }
                }
                index++;
            }
            if (minX > maxX || minY > maxY)
            {
                return (0f, 0f);
            }
            return ((maxX - minX) / 2f, (maxY - minY) / 2f);
        }

        private float Height(int x, int y)
        {
            if (smoothedHeights == null)
            {
                return grid[x, y];
            }
            return smoothedHeights[Math.Clamp(y, 0, smoothedSize - 1) * smoothedSize + Math.Clamp(x, 0, smoothedSize - 1)];
        }

        /// <summary>
        /// Bilinear sample of the smoothed elevation, in terrain meters.
        /// </summary>
        /// <summary>
        /// Altitude for an object Arma placed at <paramref name="armaZ"/>, moved onto this level's
        /// terrain.
        /// </summary>
        /// <remarks>
        /// The exported terrain is not Arma's: it is resampled four times finer and then smoothed,
        /// so its surface sits a little above or below the original almost everywhere. Placing
        /// objects at their raw Arma altitude leaves guard rails hanging in the air and rocks
        /// perched on the surface instead of bedded into it. What has to be preserved is each
        /// object's height above the ground, not its height above sea level.
        /// </remarks>
        /// <summary>
        /// How far above or below Arma's own ground an object may sit and still be treated as
        /// resting on it. Beyond this it is a structure at an absolute height, not a prop.
        /// </summary>
        private const float GroundedZLimit = 3f;

        private float GroundedZ(float terrainX, float terrainY, float armaZ, bool followGround = false)
        {
            if (sourceGrid == null)
            {
                return armaZ;
            }
            var armaGround = sourceGrid.ElevationAt(new TerrainPoint(terrainX, terrainY));
            if (followGround)
            {
                // Bridges. Neither of the two rules below fits them: their own altitude leaves them
                // hanging over a corridor the road flattening has moved, and dropping them onto the
                // ground flattens an overpass onto the road it is supposed to cross, which is what
                // put bridge_highway_f face down on the tarmac with its ramps buried in the field.
                // What has to survive is the clearance Arma gave them: a deck standing six metres
                // above the valley floor keeps standing six metres above ours. Where the deck could
                // be paired with its own carriageway the answer is exact and is used directly;
                // otherwise fall back to following the ground it stood on.
                if (bridgeTargetZ.TryGetValue(GroundKey(terrainX, terrainY), out var target))
                {
                    return target;
                }
                return armaZ + BridgeLift(terrainX, terrainY, ElevationAt(terrainX, terrainY), armaGround);
            }
            if (MathF.Abs(armaZ - armaGround) > GroundedZLimit)
            {
                // Too far off the ground to be resting on it. A bridge spans a gap and a gantry
                // stands over the carriageway: both are engineered to an absolute height, and
                // re-hanging them that far above a corridor the road flattening has already raised
                // lifts them twice over. Their own altitude is the right one.
                return armaZ;
            }
            return armaZ + (ElevationAt(terrainX, terrainY) - armaGround);
        }

        private float ElevationAt(float terrainX, float terrainY)
        {
            if (smoothedHeights == null)
            {
                return grid.ElevationAt(new TerrainPoint(terrainX, terrainY));
            }
            var gx = terrainX / cellSize;
            var gy = terrainY / cellSize;
            var x0 = (int)MathF.Floor(gx);
            var y0 = (int)MathF.Floor(gy);
            var tx = gx - x0;
            var ty = gy - y0;
            var h00 = Height(x0, y0);
            var h10 = Height(x0 + 1, y0);
            var h01 = Height(x0, y0 + 1);
            var h11 = Height(x0 + 1, y0 + 1);
            var top = h00 + (h10 - h00) * tx;
            var bottom = h01 + (h11 - h01) * tx;
            return top + (bottom - top) * ty;
        }

        private static string Sanitize(string name)
        {
            var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars).Trim('_');
        }

        public async Task WriteAsync(string targetZipFile, IProgressScope scope)
        {
            var size = grid.Size;
            if ((size & (size - 1)) != 0 || size > 8192)
            {
                throw new ApplicationException($"BeamNG terrains must be a power-of-two size up to 8192 (map grid is {size}).");
            }

            using (scope.CreateSingle("Smoothing elevation"))
            {
                BuildSmoothedHeights(size);
            }

            // Before the corridor pass, so the levelled ground follows the stitched ends
            using (scope.CreateSingle("Stitching road junctions"))
            {
                StitchJunctions(scope);
            }

            // Must run before anything reads ElevationAt: roads, objects and the .ter heightmap all
            // have to agree on the levelled surface
            using (scope.CreateSingle("Levelling road corridors"))
            {
                FlattenRoadCorridors(size);
            }

            using (scope.CreateSingle("Opening bridge gaps"))
            {
                CarveBridgeGaps(scope);
            }

            var min = float.MaxValue;
            var max = float.MinValue;
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    var v = Height(x, y);
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            var floor = MathF.Floor(min);
            var range = Math.Max(1f, MathF.Ceiling(max) - floor);
            var worldSize = size * cellSize;
            var half = worldSize / 2f;

            var basePath = $"levels/{levelName}";

            byte[]? layerMap = null;
            if (presetLayerMap != null || (idMap != null && materials != null && materials.Count > 0) || (roads != null && roads.Count > 0))
            {
                using (scope.CreateSingle("Surface layer map"))
                {
                    layerMap = presetLayerMap ?? await BuildLayerMap(size).ConfigureAwait(false) ?? new byte[size * size];
                    // GRM id maps do not paint asphalt under the roads (roads are separate objects in
                    // Arma): burn the road network into the layer map so roads get road physics
                    BurnRoadsIntoLayerMap(layerMap, size);
                }
            }
            var useLayers = layerMap != null;
            var materialNames = useLayers ? TerrainLayers.Select(l => l.Name).ToArray() : new[] { "DefaultMaterial" };

            var forestByType = BuildForestPlacements(floor, half, scope);
            var decalRoads = BuildDecalRoads(floor, half);
            AssignDecalPriorities(decalRoads);

            using var zipStream = File.Create(targetZipFile);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);

            var directories = new List<string>
            {
                "levels/", $"{basePath}/", $"{basePath}/art/", $"{basePath}/art/terrains/",
                $"{basePath}/main/", $"{basePath}/main/MissionGroup/",
                $"{basePath}/main/MissionGroup/Level_objects/", $"{basePath}/main/MissionGroup/Level_objects/Other/",
                $"{basePath}/main/MissionGroup/PlayerDropPoints/", $"{basePath}/main/MissionGroup/Water/"
            };
            if (decalRoads.Count > 0)
            {
                directories.Add($"{basePath}/main/MissionGroup/Decal_Roads/");
                directories.Add($"{basePath}/art/roads/");
            }
            if (forestByType.Count > 0 || useLayers)
            {
                directories.Add($"{basePath}/main/MissionGroup/Level_objects/vegetation/");
            }
            if (forestByType.Count > 0)
            {
                directories.Add($"{basePath}/art/forest/");
                directories.Add($"{basePath}/forest/");
                directories.Add($"{basePath}/art/shapes/");
                directories.Add($"{basePath}/art/shapes/trees/");
            }
            if (forestByType.Count > 0 || (buildings != null && buildings.Count > 0) || (buildingMeshes != null && buildingMeshes.Count > 0))
            {
                directories.Add($"{basePath}/art/shapes/");
            }
            if (buildingMeshes != null && buildingMeshes.Count > 0)
            {
                directories.Add($"{basePath}/art/shapes/buildings/");
            }
            if (HasBuildingsGroup)
            {
                directories.Add($"{basePath}/main/MissionGroup/Buildings/");
            }
            if (modelInstances != null && modelInstances.Count > 0 && !string.IsNullOrEmpty(modelLibraryDirectory))
            {
                directories.Add($"{basePath}/art/shapes/");
                directories.Add($"{basePath}/art/shapes/arma/");
            }
            // Several branches ask for the same folder, and a zip with duplicate entries upsets
            // some readers
            foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                zip.CreateEntry(dir);
            }

            using (scope.CreateSingle("Terrain (.ter)"))
            {
                WriteTer(zip, $"{basePath}/theTerrain.ter", size, floor, range, layerMap, materialNames);
            }

            using (scope.CreateSingle("Heightmap preview"))
            {
                WriteHeightmapPng(zip, $"{basePath}/theTerrain.terrainheightmap.png", size, floor, range);
            }

            var baseTexSize = (satMap != null || satMapRgba != null) ? BaseTextureSize : 1024;
            Image<Rgba32>? baseTexture = null;
            try
            {
                if (satMap != null || satMapRgba != null)
                {
                    using (scope.CreateSingle("Base texture (satellite)"))
                    {
                        baseTexture = satMap != null
                            ? await DownscaleHugeImage(satMap, BaseTextureSize, false).ConfigureAwait(false)
                            : await DownscaleHugeImage(satMapRgba!, BaseTextureSize, false).ConfigureAwait(false);
                    }
                }
                WriteBaseTextureAndPreview(zip, basePath, baseTexture, size, floor, range);
            }
            finally
            {
                baseTexture?.Dispose();
            }

            using (scope.CreateSingle("Level definition"))
            {
                WriteLevelDefinition(zip, basePath, size, floor, range, worldSize, half, materialNames, useLayers, baseTexSize, decalRoads, forestByType, scope);
            }
        }

        private void WriteLevelDefinition(ZipArchive zip, string basePath, int size, float floor, float range,
            float worldSize, float half, string[] materialNames, bool useLayers, int baseTexSize,
            List<Dictionary<string, object>> decalRoads, Dictionary<string, List<BeamNGForestInstance>> forestByType, IProgressScope scope)
        {
            WriteJson(zip, $"{basePath}/info.json", new Dictionary<string, object>
            {
                ["authors"] = "GameRealisticMap",
                ["defaultSpawnPointName"] = "spawn_default",
                ["description"] = $"Generated by GameRealisticMap from {levelTitle}",
                ["previews"] = new[] { "preview.png" },
                ["size"] = new[] { size, size },
                ["spawnPoints"] = BuildSpawnPointList(),
                ["title"] = levelTitle,
            });

            WriteText(zip, $"{basePath}/mainLevel.lua", "-- Auto-generated by GameRealisticMap\nlocal M = {}\nreturn M\n");

            WriteJson(zip, $"{basePath}/theTerrain.terrain.json", new Dictionary<string, object>
            {
                ["binaryFormat"] = "version(char), size(unsigned int), heightMap(heightMapSize * heightMapItemSize), layerMap(layerMapSize * layerMapItemSize), layerTextureMap(layerMapSize * layerMapItemSize), materialNames",
                ["datafile"] = $"/levels/{levelName}/theTerrain.ter",
                ["heightMapItemSize"] = 2,
                ["heightMapSize"] = size * size,
                ["heightmapImage"] = $"/levels/{levelName}/theTerrain.terrainheightmap.png",
                ["layerMapItemSize"] = 1,
                ["layerMapSize"] = size * size,
                ["materials"] = materialNames,
                ["size"] = size,
                ["version"] = 9,
            });

            // Terrain materials: full PBR (satellite base + official detail/macro textures) when the
            // layer map is available, single stretched satellite otherwise
            var terrainMaterials = new Dictionary<string, object>();
            var textureSetName = "";
            var satellitePath = $"/levels/{levelName}/art/terrains/terrain.png";
            string TerrainFile(string f) => $"/levels/{levelName}/art/terrains/{f}";
            if (useLayers)
            {
                textureSetName = $"{levelName}TerrainMaterialTextureSet";
                terrainMaterials[textureSetName] = new Dictionary<string, object>
                {
                    ["name"] = textureSetName,
                    ["class"] = "TerrainMaterialTextureSet",
                    ["persistentId"] = Guid.NewGuid().ToString(),
                    // Pixel dimensions of each texture array: every texture bound to a slot must
                    // match these exactly, otherwise the array fails to build and the terrain
                    // renders black
                    ["baseTexSize"] = new[] { baseTexSize, baseTexSize },
                    ["detailTexSize"] = new[] { DetailTextureSize, DetailTextureSize },
                    ["macroTexSize"] = new[] { DetailTextureSize, DetailTextureSize },
                };
                var templates = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(LoadTerrainMaterialsJson())!;
                // Every layer is built from the SAME template. The official templates are not
                // interchangeable: some carry legacy non-PBR fields (macroMap, detailSize,
                // diffuseSize...) and miss the PBR distance attenuations, which makes those layers
                // render pure white or black. "Grass" is the one with the complete PBR field set;
                // the visual difference between surfaces comes from the generated textures anyway.
                var reference = templates["Grass"];
                var mapping = new (string Layer, string GroundModel, string Annotation, Rgb24 Color, int Noise, int TileMeters)[]
                {
                    ("grm_grass", "GRASS", "GRASS", new Rgb24(96, 112, 62), 26, 3),
                    ("grm_asphalt", "ASPHALT", "ASPHALT", new Rgb24(64, 64, 68), 12, 4),
                    ("grm_dirt", "DIRT", "NATURE", new Rgb24(118, 95, 68), 24, 3),
                    ("grm_gravel", "GRAVEL", "NATURE", new Rgb24(132, 126, 116), 34, 2),
                    ("grm_sand", "SAND", "SAND", new Rgb24(196, 180, 148), 16, 3),
                    ("grm_rock", "ROCK", "ROCK", new Rgb24(124, 120, 114), 30, 4),
                    ("grm_mud", "MUD", "NATURE", new Rgb24(82, 68, 52), 20, 3),
                };
                foreach (var (layer, groundModel, annotation, color, noise, tileMeters) in mapping)
                {
                    // Repoint every texture slot to files generated inside this level: textures
                    // owned by other levels are not mounted when a generated level runs.
                    var def = reference.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                    def["name"] = layer;
                    def["internalName"] = layer;
                    def["persistentId"] = Guid.NewGuid().ToString();
                    def["groundmodelName"] = groundModel;
                    def["annotation"] = annotation;

                    WriteDetailTextures(zip, basePath, layer, color, noise);

                    foreach (var key in def.Keys.Where(IsTextureSlot).ToList())
                    {
                        def[key] = ResolveTextureSlot(key, layer, satellitePath, TerrainFile);
                    }
                    // *TexSize on a material is the ground distance covered by one tile, in meters
                    // (the pixel dimensions live in the TerrainMaterialTextureSet)
                    def["baseColorBaseTexSize"] = worldSize;
                    def["aoBaseTexSize"] = worldSize;
                    def["normalBaseTexSize"] = worldSize;
                    def["roughnessBaseTexSize"] = worldSize;
                    def["heightBaseTexSize"] = worldSize;
                    def["baseColorDetailTexSize"] = tileMeters;
                    def["normalDetailTexSize"] = tileMeters;
                    def["roughnessDetailTexSize"] = tileMeters;
                    def["heightDetailTexSize"] = tileMeters;
                    def["aoDetailTexSize"] = tileMeters;
                    def["diffuseSize"] = worldSize;
                    terrainMaterials[layer] = def;
                }
                // Shared neutral base slots (AO white, flat normal, mid roughness). They sit in the
                // same texture array as the satellite image, so they must share its pixel size.
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_ao.png", baseTexSize, new Rgb24(255, 255, 255));
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_nm.png", baseTexSize, new Rgb24(128, 128, 255));
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_r.png", baseTexSize, new Rgb24(180, 180, 180));
                WriteUniformPng(zip, $"{basePath}/art/terrains/shared_ao_detail.png", DetailTextureSize, new Rgb24(255, 255, 255));

                // Grass billboards: texture here, material in the same main.materials.json
                WriteGrassCoverTexture(zip, basePath);
                terrainMaterials["grm_grass_cover"] = GrassCoverMaterial();
            }
            else
            {
                terrainMaterials["DefaultMaterial"] = new Dictionary<string, object>
                {
                    ["class"] = "TerrainMaterial",
                    ["internalName"] = "DefaultMaterial",
                    ["diffuseMap"] = $"levels/{levelName}/art/terrains/terrain.png",
                    ["diffuseSize"] = worldSize,
                    ["groundmodelName"] = "GROUNDMODEL_ASPHALT1",
                };
            }
            WriteJson(zip, $"{basePath}/art/terrains/main.materials.json", terrainMaterials);

            WriteNdJson(zip, $"{basePath}/main/items.level.json",
                Item("SimGroup", "MissionGroup", null));

            var missionGroupItems = new List<Dictionary<string, object>>
            {
                Item("SimGroup", "PlayerDropPoints", "MissionGroup"),
                Item("SimGroup", "Level_objects", "MissionGroup"),
                Item("SimGroup", "Water", "MissionGroup"),
            };
            if (decalRoads.Count > 0)
            {
                missionGroupItems.Add(Item("SimGroup", "Decal_Roads", "MissionGroup"));
            }
            // Both the swissBUILDINGS3D shapes and the ported Arma models land in this group: it has
            // to be declared for either, or its items file is never loaded.
            if (HasBuildingsGroup)
            {
                missionGroupItems.Add(Item("SimGroup", "Buildings", "MissionGroup"));
            }
            // Roads are listed too: they carry the street lamps, which land in the same group
            if ((fences != null && fences.Count > 0) || bridgeSpans.Count > 0 || (roads != null && roads.Count > 0))
            {
                missionGroupItems.Add(Item("SimGroup", "Furniture", "MissionGroup"));
                zip.CreateEntry($"{basePath}/main/MissionGroup/Furniture/");
                zip.CreateEntry($"{basePath}/art/shapes/furniture/");
            }
            // Night lights of the map's own lamp posts, filled in by WriteModelInstances. A group
            // whose parents are not declared is never loaded, so the whole nesting goes in now,
            // before anything knows how many lights there will be.
            if (HasStreetLampModels)
            {
                missionGroupItems.Add(Item("SimGroup", "nightlights", "MissionGroup"));
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/nightlights/items.level.json",
                    Item("SimGroup", "lightemitters", "nightlights"));
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/nightlights/lightemitters/items.level.json",
                    Item("SimGroup", "grm_streetlights", "lightemitters"));
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/items.level.json", missionGroupItems.ToArray());

            var levelInfo = Item("LevelInfo", "theLevelInfo", "Level_objects");
            levelInfo["canvasClearColor"] = new[] { 0, 0, 0, 1 };
            levelInfo["fogAtmosphereHeight"] = 1000;
            levelInfo["fogDensity"] = 0.0001;
            levelInfo["fogDensityOffset"] = 0;
            levelInfo["globalEnviromentMap"] = "BNG_Sky_02_cubemap";
            levelInfo["gravity"] = -9.81;
            levelInfo["nearClip"] = 0.1;
            levelInfo["visibleDistance"] = 4000;

            var tod = Item("TimeOfDay", "tod", "Level_objects");
            tod["startTime"] = 0.15;

            var sky = Item("ScatterSky", "sunsky", "Level_objects");
            sky["ambientScaleGradientFile"] = "art/sky_gradients/default/gradient_ambient.png";
            sky["colorizeGradientFile"] = "art/sky_gradients/default/gradient_colorize.png";
            sky["enableFogFallBack"] = false;
            sky["fogScaleGradientFile"] = "art/sky_gradients/default/gradient_fog.png";
            sky["shadowDistance"] = 1500;
            sky["skyBrightness"] = 40;
            sky["sunScaleGradientFile"] = "art/sky_gradients/default/gradient_sunscale.png";
            sky["texSize"] = 2048;

            var levelObjects = new List<Dictionary<string, object>> { levelInfo, tod, sky, Item("SimGroup", "Other", "Level_objects") };
            if (forestByType.Count > 0 || useLayers)
            {
                levelObjects.Add(Item("SimGroup", "vegetation", "Level_objects"));
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/Level_objects/items.level.json", levelObjects.ToArray());

            var terrainBlock = Item("TerrainBlock", "theTerrain", "Other");
            terrainBlock["position"] = new object[] { -half, -half, 0 };
            terrainBlock["squareSize"] = cellSize;
            terrainBlock["maxHeight"] = range;
            terrainBlock["baseTexSize"] = size;
            terrainBlock["terrainFile"] = $"/levels/{levelName}/theTerrain.ter";
            terrainBlock["materialTextureSet"] = textureSetName;
            terrainBlock["minimapImage"] = "";

            var otherItems = new List<Dictionary<string, object>> { terrainBlock };
            var buildingCount = 0;
            var buildingItems = new List<Dictionary<string, object>>();
            if (buildingMeshes != null && buildingMeshes.Count > 0)
            {
                // Real swissBUILDINGS3D meshes (roof shapes included), merged per zone so the
                // editor stays usable: one object per BuildingZoneSize square
                var zones = new Dictionary<(int, int), List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>>();
                foreach (var mesh in buildingMeshes)
                {
                    if (mesh.Triangles.Count == 0)
                    {
                        continue;
                    }
                    var first = mesh.Triangles[0].A;
                    var key = ((int)MathF.Floor(first.X / BuildingZoneSize), (int)MathF.Floor(first.Y / BuildingZoneSize));
                    if (!zones.TryGetValue(key, out var list))
                    {
                        zones.Add(key, list = new List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.BuildingMesh>());
                    }
                    list.Add(mesh);
                }
                var zoneIndex = 0;
                foreach (var zone in zones.OrderBy(z => z.Key.Item2).ThenBy(z => z.Key.Item1).Take(MaxBuildingZones))
                {
                    zoneIndex++;
                    var triangles = zone.Value.SelectMany(m => m.Triangles).ToList();
                    // Geometry relative to the zone centre so the engine can cull it properly
                    var cx = (zone.Key.Item1 + 0.5f) * BuildingZoneSize;
                    var cy = (zone.Key.Item2 + 0.5f) * BuildingZoneSize;
                    var cz = triangles.Min(t => MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)));
                    var shapeFile = $"art/shapes/buildings/zone_{zoneIndex:0000}.dae";
                    WriteText(zip, $"{basePath}/{shapeFile}", BuildTexturedBuildingCollada(triangles, cx, cy, cz));

                    var item = Item("TSStatic", $"buildings_zone_{zoneIndex:0000}", "Buildings");
                    item["position"] = new object[] { MathF.Round(cx - half, 3), MathF.Round(cy - half, 3), MathF.Round(cz - floor, 3) };
                    item["shapeName"] = $"levels/{levelName}/{shapeFile}";
                    item["collisionType"] = "Visible Mesh";
                    item["decalType"] = "Visible Mesh";
                    item["prebuildCollisionData"] = 0;
                    item["useInstanceRenderData"] = true;
                    buildingItems.Add(item);
                }
                buildingCount = buildingMeshes.Count;
                scope.WriteLine($"Buildings: {buildingCount} swissBUILDINGS3D buildings in {buildingItems.Count} zone objects ({BuildingZoneSize} m zones)");
            }
            else if (modelInstances != null && modelInstances.Count > 0 && !string.IsNullOrEmpty(modelLibraryDirectory))
            {
                buildingCount = WriteModelInstances(zip, basePath, buildingItems, floor, half, scope);
            }
            else if (buildings != null && buildings.Count > 0)
            {
                var kept = buildings;
                if (kept.Count > 12000)
                {
                    var stride = (double)kept.Count / 12000;
                    var thinned = new List<BeamNGBuildingBox>(12000);
                    for (double i = 0; i < kept.Count && thinned.Count < 12000; i += stride)
                    {
                        thinned.Add(kept[(int)i]);
                    }
                    kept = thinned;
                }
                buildingCount = kept.Count;
                WriteText(zip, $"{basePath}/art/shapes/buildings.dae", BuildBuildingsCollada(kept, floor, half));
                var buildingsStatic = Item("TSStatic", "buildings", "Other");
                buildingsStatic["position"] = new object[] { 0, 0, 0 };
                buildingsStatic["shapeName"] = $"levels/{levelName}/art/shapes/buildings.dae";
                buildingsStatic["collisionType"] = "Visible Mesh";
                buildingsStatic["decalType"] = "Visible Mesh";
                buildingsStatic["prebuildCollisionData"] = 0;
                buildingsStatic["useInstanceRenderData"] = true;
                otherItems.Add(buildingsStatic);
                scope.WriteLine($"Buildings: {buildingCount} collision boxes");
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/Level_objects/Other/items.level.json", otherItems.ToArray());

            if (buildingItems.Count > 0)
            {
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Buildings/items.level.json", buildingItems.ToArray());
            }

            var fenceItems = WriteFences(zip, basePath, floor, half, scope);
            // No generated street lamps. The map brings its own along the roads Arma lit, and a
            // second, invented set stood a plain grey pole beside every real one. The lamps that
            // come with the map get their night light in WriteModelInstances.
            for (var index = 0; index < bridgeSpans.Count; index++)
            {
                // One object per span, centered on its own geometry so it can be picked and moved
                // in the editor. Bridge decks are already in level coordinates (built from decal
                // road nodes), so the position needs no half-world shift.
                var span = bridgeSpans[index];
                var cx = span.Average(t => (t.A.X + t.B.X + t.C.X) / 3f);
                var cy = span.Average(t => (t.A.Y + t.B.Y + t.C.Y) / 3f);
                var cz = span.Min(t => MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)));
                var shapeFile = $"art/shapes/furniture/grm_bridge_{index:0000}.dae";
                WriteText(zip, $"{basePath}/{shapeFile}", BuildFurnitureCollada(span, cx, cy, cz, "grm_bridge"));

                var bridge = Item("TSStatic", $"grm_bridge_{index:0000}", "Furniture");
                bridge["position"] = new object[] { MathF.Round(cx, 3), MathF.Round(cy, 3), MathF.Round(cz, 3) };
                bridge["shapeName"] = $"levels/{levelName}/{shapeFile}";
                bridge["collisionType"] = "Visible Mesh";
                bridge["decalType"] = "Visible Mesh";
                bridge["useInstanceRenderData"] = true;
                fenceItems.Add(bridge);
            }
            if (bridgeSpans.Count > 0)
            {
                scope.WriteLine($"Bridges: {bridgeSpans.Count} movable spans with deck and parapets");
            }

            // Arma's own decks, one object per span, laid end to end along the crossing
            for (var index = 0; index < armaBridgeSpans.Count; index++)
            {
                var span = armaBridgeSpans[index];
                if (!TryCopyLibraryShape(zip, basePath, span.Model))
                {
                    continue;
                }
                var item = Item("TSStatic", $"arma_bridge_{index:0000}", "Furniture");
                item["position"] = new object[]
                {
                    MathF.Round(span.X, 3), MathF.Round(span.Y, 3), MathF.Round(span.Z, 3)
                };
                // The deck runs along the model's own Y, so the heading turns Y onto the road
                var cos = MathF.Cos(span.Heading);
                var sin = MathF.Sin(span.Heading);
                item["rotationMatrix"] = new object[]
                {
                    MathF.Round(sin, 6), MathF.Round(-cos, 6), 0f,
                    MathF.Round(cos, 6), MathF.Round(sin, 6), 0f,
                    0f, 0f, 1f,
                };
                item["shapeName"] = $"levels/{levelName}/art/shapes/arma/{span.Model}.dae";
                item["collisionType"] = "Collision Mesh";
                item["decalType"] = "Collision Mesh";
                item["useInstanceRenderData"] = true;
                fenceItems.Add(item);
            }
            if (armaBridgeSpans.Count > 0)
            {
                scope.WriteLine($"Bridges: {armaBridgeSpans.Count} Arma spans laid along the crossings");
            }
            if (fenceItems.Count > 0)
            {
                // Materials of every furniture shape (fences and bridges share the folder)
                WriteJson(zip, $"{basePath}/art/shapes/furniture/main.materials.json", new Dictionary<string, object>
                {
                    ["grm_wall"] = FlatMaterial("grm_wall", 0.66, 0.64, 0.60),
                    ["grm_fence"] = FlatMaterial("grm_fence", 0.42, 0.33, 0.24),
                    ["grm_hedge"] = FlatMaterial("grm_hedge", 0.25, 0.38, 0.18),
                    ["grm_bridge"] = FlatMaterial("grm_bridge", 0.58, 0.57, 0.55),
                });
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Furniture/items.level.json", fenceItems.ToArray());
            }

            if (decalRoads.Count > 0)
            {
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Decal_Roads/items.level.json", decalRoads.ToArray());
                WriteRoadMaterials(zip, basePath);
                scope.WriteLine($"Roads: {decalRoads.Count} DecalRoad segments (self-contained materials)");
            }

            if (forestByType.Count > 0 || (buildings != null && buildings.Count > 0) || (buildingMeshes != null && buildingMeshes.Count > 0))
            {
                // Materials of the official tree/rock shapes: they are level-scoped in the game files,
                // so they must be re-declared inside this level or the shapes render orange.
                // The buildings material is appended to the same file.
                var shapeMaterials = JsonSerializer.Deserialize<Dictionary<string, object>>(LoadShapeMaterialsJson())!;
                if (buildingMeshes != null && buildingMeshes.Count > 0)
                {
                    WriteFacadePng(zip, $"{basePath}/art/shapes/buildings/facade.png");
                    WriteRoofPng(zip, $"{basePath}/art/shapes/buildings/roof.png");
                    shapeMaterials["grm_wall_face"] = BuildingMaterial("grm_wall_face",
                        $"levels/{levelName}/art/shapes/buildings/facade.png");
                    shapeMaterials["grm_roof"] = BuildingMaterial("grm_roof",
                        $"levels/{levelName}/art/shapes/buildings/roof.png");
                }
                shapeMaterials["grm_building"] = new Dictionary<string, object>
                {
                    ["name"] = "grm_building",
                    ["mapTo"] = "grm_building",
                    ["class"] = "Material",
                    ["persistentId"] = Guid.NewGuid().ToString(),
                    ["Stages"] = new object[]
                    {
                        new Dictionary<string, object> { ["diffuseColor"] = new[] { 0.62, 0.6, 0.56, 1 }, ["specularPower"] = 1 },
                        new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                    },
                    ["materialTag0"] = "beamng",
                };
                WriteJson(zip, $"{basePath}/art/shapes/main.materials.json", shapeMaterials);
            }

            if (forestByType.Count > 0 || useLayers)
            {
                var vegetationItems = new List<Dictionary<string, object>>();
                if (forestByType.Count > 0)
                {
                    vegetationItems.Add(ForestObject());
                }
                if (useLayers)
                {
                    // Volumetric grass tufts on the grass layer: the single biggest close-up gain
                    vegetationItems.Add(GroundCoverObject(worldSize, grid[grid.Size / 2, grid.Size / 2] - floor));
                }
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/Level_objects/vegetation/items.level.json",
                    vegetationItems.ToArray());
            }
            if (forestByType.Count > 0)
            {
                // Species whose Arma mesh is in the library draw the real model; the rest keep the
                // generated billboard.
                var realShapes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var type in forestByType.Keys)
                {
                    if (TryCopyLibraryShape(zip, basePath, type))
                    {
                        realShapes[type] = $"levels/{levelName}/art/shapes/arma/{type}.dae";
                    }
                }
                WriteJson(zip, $"{basePath}/art/forest/managedItemData.json", ManagedForestItemData(forestByType, realShapes));
                WriteTreeAssets(zip, basePath, forestByType.Keys.Where(t => !realShapes.ContainsKey(t)).ToList());
                if (realShapes.Count > 0)
                {
                    scope.WriteLine($"Forest: {realShapes.Count} of {forestByType.Count} species use their real Arma mesh");
                }
            }
            if (armaMaterials.Count > 0)
            {
                // Written once, after both the placed models and the forest species have registered
                // whatever they needed
                WriteJson(zip, $"{basePath}/art/shapes/arma/main.materials.json", armaMaterials);
            }

            // Outside the block above, and it matters. These files used to be written only when at
            // least one Arma material had been registered, which is true of a map exported from a
            // wrp and false of one generated straight from real-world data: Romont counted 214 764
            // trees into its own report and shipped an empty forest folder, because no Arma model
            // meant no Arma material meant the whole branch was skipped.
            foreach (var (type, instances) in forestByType)
            {
                var sb = new StringBuilder();
                foreach (var instance in instances)
                {
                    sb.Append(SerializeForestInstance(instance, type, floor, half));
                    sb.Append('\n');
                }
                WriteText(zip, $"{basePath}/forest/{type}.forest4.json", sb.ToString());
                scope.WriteLine($"Forest: {instances.Count} x {type}");
            }

            // Sea plane when the map contains ocean (altitude 0 above the lowest point),
            // plus one WaterBlock per lake pond tile of the Arma map
            var waterItems = new List<Dictionary<string, object>>();
            if (floor < -0.5f)
            {
                waterItems.Add(SeaWaterPlane(-floor));
            }
            if (ponds != null && ponds.Count > 0)
            {
                var pondIndex = 0;
                foreach (var pond in ponds.Take(2000))
                {
                    waterItems.Add(PondWaterBlock(pond, floor, half, ++pondIndex));
                }
                scope.WriteLine($"Water: {Math.Min(ponds.Count, 2000)} lake WaterBlocks");
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/Water/items.level.json", waterItems.ToArray());

            var center = grid.Size / 2;
            var spawnZ = Height(center, center) - floor + 3f;
            var spawn = Item("SpawnSphere", "spawn_default", "PlayerDropPoints");
            spawn["dataBlock"] = "SpawnSphereMarker";
            spawn["position"] = new object[] { 0, 0, spawnZ };
            spawn["rotationMatrix"] = new[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            spawn["radius"] = 5;

            var dropPoints = new List<Dictionary<string, object>> { spawn };
            foreach (var place in NamedPlaces())
            {
                var c = MathF.Cos(place.Place.YawRad);
                var s = MathF.Sin(place.Place.YawRad);
                var item = Item("SpawnSphere", place.ObjectName, "PlayerDropPoints");
                item["dataBlock"] = "SpawnSphereMarker";
                item["position"] = new object[]
                {
                    MathF.Round(place.Place.X - half, 3),
                    MathF.Round(place.Place.Y - half, 3),
                    MathF.Round(HeightAtWorld(place.Place.X, place.Place.Y) - floor + 1f, 3)
                };
                item["rotationMatrix"] = new object[]
                {
                    MathF.Round(c, 6), MathF.Round(s, 6), 0,
                    MathF.Round(-s, 6), MathF.Round(c, 6), 0,
                    0, 0, 1
                };
                item["radius"] = 5;
                dropPoints.Add(item);
            }
            WriteNdJson(zip, $"{basePath}/main/MissionGroup/PlayerDropPoints/items.level.json", dropPoints.ToArray());
            if (dropPoints.Count > 1)
            {
                scope.WriteLine($"Places: {dropPoints.Count - 1} named spawn points (Big Map labels and fast travel)");
            }

            WriteText(zip, $"{basePath}/export_report.txt", FormattableString.Invariant(
$@"BeamNG.drive level generated by GameRealisticMap
================================================
Level name:     {levelName}
Terrain:        {grid.Size} x {grid.Size} @ {cellSize} m ({worldSize / 1000:0.##} km x {worldSize / 1000:0.##} km)
Altitude range: {floor} m .. {floor + range} m (TerrainBlock maxHeight = {range})
Sea level:      {(floor < -0.5f ? FormattableString.Invariant($"z = {-floor} (WaterPlane included)") : "no ocean in this map")}
Surface layers: {(useLayers ? string.Join(", ", materialNames) : "single material (no id map available)")}
Roads:          {decalRoads.Count} DecalRoad segments, self-contained materials, AI drivability set (traffic + GPS)
Grass:          volumetric GroundCover on the grass and field layers
Fences:         {fences?.Count ?? 0} OSM walls / fences / hedges
Forest:         {forestByType.Sum(kv => kv.Value.Count)} instances ({string.Join(", ", forestByType.Select(kv => $"{kv.Value.Count} {kv.Key}"))})
Lakes:          {Math.Min(ponds?.Count ?? 0, 2000)} WaterBlocks
Buildings:      {buildingCount} objects{DescribeBuildingSource()}

Install: copy this zip into Documents\BeamNG.drive\<version>\mods\
The level then appears in Freeroam as '{levelTitle}'.
"));
        }

        // ── Forest ─────────────────────────────────────────────────────────────

        // Self-contained species generated into the level (Swiss-looking: spruce, beech, bush)
        private static readonly Dictionary<BeamNGForestKind, string> ForestTypeNames = new()
        {
            [BeamNGForestKind.Tree] = "grm_spruce",
            [BeamNGForestKind.Bush] = "grm_bush",
            [BeamNGForestKind.Rock] = "grm_rock_small",
        };

        private Dictionary<string, List<BeamNGForestInstance>> BuildForestPlacements(float floor, float half, IProgressScope scope)
        {
            var result = new Dictionary<string, List<BeamNGForestInstance>>();
            if (vegetation == null || vegetation.Count == 0)
            {
                return result;
            }
            // Clutter is exempt from the budget: a wall with a third of its sections missing reads as
            // broken, whereas a slightly thinner wood reads as a wood.
            var plants = vegetation.Count(v => v.Kind != BeamNGForestKind.Clutter);
            var share = plants > MaxForestInstancesTotal
                ? (double)MaxForestInstancesTotal / plants
                : 1d;
            if (share < 1d)
            {
                scope.WriteLine($"Forest: {plants} plants thinned to {share:P0} to stay drawable");
            }

            // One species per Arma model when the library can supply its mesh, so the forest draws
            // real trees instead of a single generic billboard for everything.
            foreach (var group in vegetation.GroupBy(ForestTypeOf).Where(g => g.Key != null))
            {
                var list = group.ToList();
                var budget = list[0].Kind == BeamNGForestKind.Clutter
                    ? MaxForestInstancesPerType
                    : Math.Min(MaxForestInstancesPerType, Math.Max(1, (int)(list.Count * share)));
                if (list.Count > budget)
                {
                    // Deterministic thinning to stay within engine-friendly instance counts
                    var stride = (double)list.Count / budget;
                    var thinned = new List<BeamNGForestInstance>(budget);
                    for (double i = 0; i < list.Count && thinned.Count < budget; i += stride)
                    {
                        thinned.Add(list[(int)i]);
                    }
                    list = thinned;
                }
                result[group.Key!] = list;
            }
            return result;
        }

        /// <summary>
        /// Species name of a forest instance: its own model when the library holds a converted mesh,
        /// otherwise the generic generated species for its kind. Clutter has no generic stand-in, so
        /// a wall or a pylon the library cannot supply is simply left out rather than drawn as a
        /// bush; null means drop the instance.
        /// </summary>
        private string? ForestTypeOf(BeamNGForestInstance instance)
        {
            if (!string.IsNullOrEmpty(instance.Model) && !string.IsNullOrEmpty(modelLibraryDirectory))
            {
                var name = SanitizeModelName(instance.Model);
                if (File.Exists(Path.Combine(modelLibraryDirectory, "dae", name + ".dae")))
                {
                    return name;
                }
            }
            return ForestTypeNames.TryGetValue(instance.Kind, out var generic) ? generic : null;
        }

        private string SerializeForestInstance(BeamNGForestInstance instance, string type, float floor, float half)
        {
            var z = (instance.Z.HasValue
                ? GroundedZ(instance.X, instance.Y, instance.Z.Value)
                : ElevationAt(instance.X, instance.Y) + FootLift(type) * Math.Clamp(instance.Scale, 0.4f, 2.5f)) - floor;
            object[] rotation;
            if (instance.Rotation != null)
            {
                rotation = instance.Rotation.Select(v => (object)MathF.Round(v, 6)).ToArray();
            }
            else
            {
                var c = MathF.Cos(instance.YawRad);
                var s = MathF.Sin(instance.YawRad);
                rotation = new object[] { MathF.Round(c, 6), MathF.Round(s, 6), 0, MathF.Round(-s, 6), MathF.Round(c, 6), 0, 0, 0, 1 };
            }
            var scale = Math.Clamp(instance.Scale, 0.4f, 2.5f);
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ctxid"] = 0,
                ["pos"] = new object[] { MathF.Round(instance.X - half, 3), MathF.Round(instance.Y - half, 3), MathF.Round(z, 3) },
                ["rotationMatrix"] = rotation,
                ["scale"] = MathF.Round(scale, 6),
                ["type"] = type,
            });
        }

        private Dictionary<string, object> ManagedForestItemData(
            Dictionary<string, List<BeamNGForestInstance>> forestByType,
            Dictionary<string, string>? realShapes = null)
        {
            Dictionary<string, object> Item(string name, float radius, float mass, float windScale, string annotation)
            {
                var shape = realShapes != null && realShapes.TryGetValue(name, out var real)
                    ? real
                    : $"levels/{levelName}/art/shapes/trees/{name}.dae";
                return new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["internalName"] = name,
                    ["class"] = "TSForestItemData",
                    ["annotation"] = annotation,
                    ["branchAmp"] = 0.03,
                    ["detailAmp"] = 0.2,
                    ["detailFreq"] = 4,
                    ["mass"] = mass,
                    ["radius"] = radius,
                    ["rigidity"] = 17,
                    ["shapeFile"] = shape,
                    ["trunkBendScale"] = 0.08,
                    ["windScale"] = windScale,
                };
            }

            var result = new Dictionary<string, object>();
            foreach (var (type, instances) in forestByType)
            {
                // The three generated species keep their tuned values; real Arma species get
                // sensible defaults based on what they are.
                result[type] = type switch
                {
                    "grm_spruce" => Item(type, 0.7f, 20f, 0.35f, "NATURE"),
                    "grm_bush" => Item(type, 0.5f, 1f, 0.5f, "NATURE"),
                    "grm_rock_small" => Item(type, 0.3f, 40f, 0f, "ROCK"),
                    // Walls, pylons and kerbs are built, not grown: they must not sway, and they
                    // have to be heavy enough that a car loses rather than moves them.
                    _ when instances[0].Kind == BeamNGForestKind.Clutter => Item(type, 0.6f, 5000f, 0f, "BUILDING"),
                    _ when IsRockName(type) => Item(type, 0.6f, 200f, 0f, "ROCK"),
                    _ when IsBushName(type) => Item(type, 0.5f, 1f, 0.5f, "NATURE"),
                    _ => Item(type, 0.7f, 20f, 0.35f, "NATURE"),
                };
            }
            return result;
        }

        /// <summary>
        /// Generate the vegetation shapes used by the forest: crossed billboard planes with a
        /// painted texture. Self-contained, so forests render on any install.
        /// </summary>
        private void WriteTreeAssets(ZipArchive zip, string basePath, IEnumerable<string> usedTypes)
        {
            var materials = new Dictionary<string, object>();
            foreach (var type in usedTypes)
            {
                float height, width;
                switch (type)
                {
                    case "grm_bush": height = 1.6f; width = 2.0f; break;
                    case "grm_rock_small": height = 0.8f; width = 1.4f; break;
                    default: height = 18f; width = 6.5f; break; // spruce
                }
                WriteVegetationTexture(zip, $"{basePath}/art/shapes/trees/{type}.png", type);
                WriteText(zip, $"{basePath}/art/shapes/trees/{type}.dae", BuildCrossPlaneCollada(type, width, height));
                materials[type] = new Dictionary<string, object>
                {
                    ["name"] = type,
                    ["mapTo"] = type,
                    ["class"] = "Material",
                    ["persistentId"] = Guid.NewGuid().ToString(),
                    ["Stages"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["colorMap"] = $"levels/{levelName}/art/shapes/trees/{type}.png",
                            ["specularPower"] = 1,
                        },
                        new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                    },
                    ["alphaRef"] = 80,
                    ["alphaTest"] = true,
                    ["doubleSided"] = true,
                    ["translucentBlendOp"] = "None",
                    ["materialTag0"] = "beamng",
                    ["materialTag1"] = "vegetation",
                };
            }
            WriteJson(zip, $"{basePath}/art/shapes/trees/main.materials.json", materials);
        }

        /// <summary>
        /// Two vertical quads crossed at 90 degrees, textured with the billboard.
        /// </summary>
        private static string BuildCrossPlaneCollada(string name, float width, float height)
        {
            var hw = width / 2f;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var positions = new List<float>();
            var normals = new List<float>();
            var uvs = new List<float>();
            var indices = new StringBuilder();
            var vertex = 0;

            void Quad(bool alongX)
            {
                var quadPositions = alongX
                    ? new[] { -hw, 0f, 0f, hw, 0f, 0f, hw, 0f, height, -hw, 0f, height }
                    : new[] { 0f, -hw, 0f, 0f, hw, 0f, 0f, hw, height, 0f, -hw, height };
                positions.AddRange(quadPositions);
                var normal = alongX ? new[] { 0f, 1f, 0f } : new[] { 1f, 0f, 0f };
                normals.AddRange(normal);
                uvs.AddRange(new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f });
                var n = vertex / 4; // one normal per quad
                indices.Append($"{vertex} {n} {vertex} {vertex + 1} {n} {vertex + 1} {vertex + 2} {n} {vertex + 2} ");
                indices.Append($"{vertex} {n} {vertex} {vertex + 2} {n} {vertex + 2} {vertex + 3} {n} {vertex + 3} ");
                vertex += 4;
            }
            Quad(true);
            Quad(false);

            string Floats(IEnumerable<float> values) => string.Join(" ", values.Select(v => v.ToString("0.###", culture)));

            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<COLLADA xmlns=""http://www.collada.org/2005/11/COLLADASchema"" version=""1.4.1"">
 <asset><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name=""meter"" meter=""1""/><up_axis>Z_UP</up_axis></asset>
 <library_effects>
  <effect id=""{name}-effect""><profile_COMMON>
   <newparam sid=""{name}-surface""><surface type=""2D""><init_from>{name}-image</init_from></surface></newparam>
   <newparam sid=""{name}-sampler""><sampler2D><source>{name}-surface</source></sampler2D></newparam>
   <technique sid=""common""><lambert><diffuse><texture texture=""{name}-sampler"" texcoord=""UVSET0""/></diffuse></lambert></technique>
  </profile_COMMON></effect>
 </library_effects>
 <library_images><image id=""{name}-image""><init_from>{name}.png</init_from></image></library_images>
 <library_materials><material id=""{name}-material"" name=""{name}""><instance_effect url=""#{name}-effect""/></material></library_materials>
 <library_geometries>
  <geometry id=""{name}-mesh"" name=""{name}"">
   <mesh>
    <source id=""{name}-pos"">
     <float_array id=""{name}-pos-array"" count=""{positions.Count}"">{Floats(positions)}</float_array>
     <technique_common><accessor source=""#{name}-pos-array"" count=""{positions.Count / 3}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <source id=""{name}-nrm"">
     <float_array id=""{name}-nrm-array"" count=""{normals.Count}"">{Floats(normals)}</float_array>
     <technique_common><accessor source=""#{name}-nrm-array"" count=""{normals.Count / 3}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <source id=""{name}-uv"">
     <float_array id=""{name}-uv-array"" count=""{uvs.Count}"">{Floats(uvs)}</float_array>
     <technique_common><accessor source=""#{name}-uv-array"" count=""{uvs.Count / 2}"" stride=""2""><param name=""S"" type=""float""/><param name=""T"" type=""float""/></accessor></technique_common>
    </source>
    <vertices id=""{name}-verts""><input semantic=""POSITION"" source=""#{name}-pos""/></vertices>
    <triangles material=""{name}"" count=""4"">
     <input semantic=""VERTEX"" source=""#{name}-verts"" offset=""0""/>
     <input semantic=""NORMAL"" source=""#{name}-nrm"" offset=""1""/>
     <input semantic=""TEXCOORD"" source=""#{name}-uv"" offset=""2"" set=""0""/>
     <p>{indices}</p>
    </triangles>
   </mesh>
  </geometry>
 </library_geometries>
 <library_visual_scenes><visual_scene id=""Scene"" name=""Scene"">
  <node id=""{name}"" name=""{name}"" type=""NODE"">
   <instance_geometry url=""#{name}-mesh""><bind_material><technique_common>
    <instance_material symbol=""{name}"" target=""#{name}-material""><bind_vertex_input semantic=""UVSET0"" input_semantic=""TEXCOORD"" input_set=""0""/></instance_material>
   </technique_common></bind_material></instance_geometry>
  </node>
 </visual_scene></library_visual_scenes>
 <scene><instance_visual_scene url=""#Scene""/></scene>
</COLLADA>";
        }

        /// <summary>
        /// Paint a vegetation billboard: spruce silhouette, leafy bush or rock, on transparency.
        /// </summary>
        private static void WriteVegetationTexture(ZipArchive zip, string entryName, string type)
        {
            const int size = 256;
            using var image = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 0));
            var random = new Random(type.GetHashCode());

            void Blob(float cx, float cy, float radius, Rgba32 color, float ragged)
            {
                var r2 = radius * radius;
                for (var y = (int)(cy - radius); y <= cy + radius; y++)
                {
                    for (var x = (int)(cx - radius); x <= cx + radius; x++)
                    {
                        if (x < 0 || x >= size || y < 0 || y >= size) continue;
                        var dx = x - cx;
                        var dy = y - cy;
                        var d2 = dx * dx + dy * dy;
                        if (d2 > r2) continue;
                        // Ragged edge so the silhouette does not look like a perfect disc
                        if (d2 > r2 * 0.55f && random.NextDouble() < ragged) continue;
                        var shade = 0.72f + 0.28f * (1f - d2 / r2) + (float)random.NextDouble() * 0.12f;
                        image[x, y] = new Rgba32(
                            (byte)Math.Clamp(color.R * shade, 0, 255),
                            (byte)Math.Clamp(color.G * shade, 0, 255),
                            (byte)Math.Clamp(color.B * shade, 0, 255), 255);
                    }
                }
            }

            void Trunk(float width, float top, Rgba32 color)
            {
                for (var y = (int)top; y < size; y++)
                {
                    var w = width * (0.6f + 0.4f * (y - top) / (size - top));
                    for (var x = (int)(size / 2f - w); x <= size / 2f + w; x++)
                    {
                        if (x < 0 || x >= size) continue;
                        image[x, y] = color;
                    }
                }
            }

            if (type == "grm_rock_small")
            {
                Blob(size / 2f, size * 0.68f, size * 0.32f, new Rgba32(124, 120, 112, 255), 0.35f);
                Blob(size * 0.36f, size * 0.8f, size * 0.2f, new Rgba32(110, 106, 100, 255), 0.35f);
            }
            else if (type == "grm_bush")
            {
                var green = new Rgba32(74, 108, 52, 255);
                Blob(size / 2f, size * 0.62f, size * 0.34f, green, 0.4f);
                Blob(size * 0.34f, size * 0.74f, size * 0.24f, new Rgba32(88, 122, 60, 255), 0.45f);
                Blob(size * 0.68f, size * 0.72f, size * 0.24f, new Rgba32(66, 98, 46, 255), 0.45f);
            }
            else
            {
                // Spruce: stacked tiers narrowing towards the top, dark alpine green
                Trunk(size * 0.028f, size * 0.72f, new Rgba32(78, 58, 40, 255));
                var tiers = 7;
                for (var i = 0; i < tiers; i++)
                {
                    var t = (float)i / (tiers - 1);
                    var cy = size * (0.14f + t * 0.68f);
                    var radius = size * (0.075f + t * 0.2f);
                    var shade = 1f - t * 0.18f;
                    var color = new Rgba32(
                        (byte)(52 * shade + 10), (byte)(84 * shade + 12), (byte)(46 * shade + 8), 255);
                    Blob(size / 2f, cy, radius, color, 0.5f);
                }
            }
            SavePng(zip, entryName, image);
        }

        /// <summary>
        /// One Collada mesh with a rotated box per building footprint: visible grey mass and
        /// collision at the same time (TSStatic collisionType "Visible Mesh").
        /// </summary>
        private string BuildBuildingsCollada(List<BeamNGBuildingBox> boxes, float floor, float half)
        {
            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var indices = new StringBuilder();
            var vertexCount = 0;
            var normalCount = 0;
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            void AddFace((float x, float y, float z)[] corners, (float x, float y, float z) normal)
            {
                foreach (var (x, y, z) in corners)
                {
                    positions.Append(x.ToString("0.###", culture)).Append(' ')
                             .Append(y.ToString("0.###", culture)).Append(' ')
                             .Append(z.ToString("0.###", culture)).Append(' ');
                }
                normals.Append(normal.x.ToString("0.###", culture)).Append(' ')
                       .Append(normal.y.ToString("0.###", culture)).Append(' ')
                       .Append(normal.z.ToString("0.###", culture)).Append(' ');
                // Two triangles per quad, each vertex paired with the face normal
                int v0 = vertexCount, n0 = normalCount;
                indices.Append($"{v0} {n0} {v0 + 1} {n0} {v0 + 2} {n0} {v0} {n0} {v0 + 2} {n0} {v0 + 3} {n0} ");
                vertexCount += 4;
                normalCount += 1;
            }

            foreach (var box in boxes)
            {
                var cx = box.X - half;
                var cy = box.Y - half;
                var groundZ = ElevationAt(box.X, box.Y) - floor;
                var z0 = groundZ - 0.5f; // sink slightly into the terrain
                var z1 = groundZ + Math.Clamp(box.Height, 2.5f, 40f);
                var hw = Math.Clamp(box.Width, 1.5f, 100f) / 2f;
                var hd = Math.Clamp(box.Depth, 1.5f, 100f) / 2f;
                var c = MathF.Cos(box.YawRad);
                var s = MathF.Sin(box.YawRad);

                (float x, float y, float z) P(float lx, float ly, float z) => (cx + lx * c - ly * s, cy + lx * s + ly * c, z);
                (float x, float y, float z) N(float lx, float ly) => (lx * c - ly * s, lx * s + ly * c, 0);

                // Bottom corners a,b,c,d counter-clockwise
                var a0 = P(-hw, -hd, z0); var b0 = P(hw, -hd, z0); var c0 = P(hw, hd, z0); var d0 = P(-hw, hd, z0);
                var a1 = P(-hw, -hd, z1); var b1 = P(hw, -hd, z1); var c1 = P(hw, hd, z1); var d1 = P(-hw, hd, z1);

                AddFace(new[] { a1, b1, c1, d1 }, (0, 0, 1)); // roof
                AddFace(new[] { a0, b0, b1, a1 }, N(0, -1)); // south wall
                AddFace(new[] { b0, c0, c1, b1 }, N(1, 0)); // east wall
                AddFace(new[] { c0, d0, d1, c1 }, N(0, 1)); // north wall
                AddFace(new[] { d0, a0, a1, d1 }, N(-1, 0)); // west wall
            }

            return ColladaDocument(positions.ToString(), normals.ToString(), indices.ToString(), vertexCount, normalCount, vertexCount / 4 * 2);
        }

        /// <summary>
        /// swissBUILDINGS3D buildings of one zone as a single Collada shape. Vertices are relative
        /// to the zone centre so the TSStatic can be culled, moved or deleted per area.
        /// </summary>
        /// <summary>
        /// One building as a textured shape: walls and roof told apart, each with its own material
        /// and its own unwrap.
        /// </summary>
        /// <remarks>
        /// swissBUILDINGS3D carries no texture and no texture coordinate at all -- it is a solid
        /// with a roof shape and nothing else -- so a single flat colour over the whole thing is
        /// what made every building read as a grey brick. Both are recoverable from the geometry.
        /// <para>
        /// A face is roof or wall by the tilt of its own normal. Walls are unwrapped by how far
        /// along the wall a corner sits and how high it is, both in metres, so a window is the same
        /// size on a hangar as on a cottage. Roofs are unwrapped by their footprint, which keeps the
        /// tiles running the same way over a whole roof plane instead of stretching along a slope.
        /// </para>
        /// </remarks>
        private string BuildTexturedBuildingCollada(
            List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle> meshTriangles,
            float cx, float cy, float cz)
        {
            // Metres of wall covered by one tile of the facade texture. A storey is about 3 m, and
            // the texture holds one row of windows, so the rows land at storey height.
            const float FacadeTile = 3f;
            const float RoofTile = 4f;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var uvs = new StringBuilder();
            var wallIndices = new StringBuilder();
            var roofIndices = new StringBuilder();
            var vertexCount = 0;
            var normalCount = 0;
            var wallTriangles = 0;
            var roofTriangles = 0;

            foreach (var triangle in meshTriangles)
            {
                var a = new System.Numerics.Vector3(triangle.A.X - cx, triangle.A.Y - cy, triangle.A.Z - cz);
                var b = new System.Numerics.Vector3(triangle.B.X - cx, triangle.B.Y - cy, triangle.B.Z - cz);
                var c = new System.Numerics.Vector3(triangle.C.X - cx, triangle.C.Y - cy, triangle.C.Z - cz);
                var normal = System.Numerics.Vector3.Cross(b - a, c - a);
                var length = normal.Length();
                if (length < 0.0001f)
                {
                    continue;
                }
                normal /= length;

                // Half way between flat and upright: anything flatter than 45 degrees is roof, and
                // that is what tells a shallow roof plane from a gable wall.
                var isRoof = MathF.Abs(normal.Z) > 0.707f;

                foreach (var vertex in new[] { a, b, c })
                {
                    positions.Append(vertex.X.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Y.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Z.ToString("0.###", culture)).Append(' ');

                    float u, v;
                    if (isRoof)
                    {
                        u = vertex.X / RoofTile;
                        v = vertex.Y / RoofTile;
                    }
                    else
                    {
                        // Along the wall, measured on the ground plane in the direction the face
                        // runs, so the unwrap does not shear on a gable
                        var alongX = -normal.Y;
                        var alongY = normal.X;
                        var alongLength = MathF.Sqrt((alongX * alongX) + (alongY * alongY));
                        if (alongLength < 0.0001f)
                        {
                            alongX = 1f;
                            alongY = 0f;
                            alongLength = 1f;
                        }
                        u = (((vertex.X * alongX) + (vertex.Y * alongY)) / alongLength) / FacadeTile;
                        v = vertex.Z / FacadeTile;
                    }
                    uvs.Append(u.ToString("0.####", culture)).Append(' ')
                       .Append(v.ToString("0.####", culture)).Append(' ');
                }

                normals.Append(normal.X.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Y.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Z.ToString("0.###", culture)).Append(' ');

                var target = isRoof ? roofIndices : wallIndices;
                for (var k = 0; k < 3; k++)
                {
                    target.Append(vertexCount + k).Append(' ')
                          .Append(normalCount).Append(' ')
                          .Append(vertexCount + k).Append(' ');
                }
                if (isRoof)
                {
                    roofTriangles++;
                }
                else
                {
                    wallTriangles++;
                }
                vertexCount += 3;
                normalCount += 1;
            }

            return TexturedBuildingDocument(positions.ToString(), normals.ToString(), uvs.ToString(),
                wallIndices.ToString(), roofIndices.ToString(),
                vertexCount, normalCount, wallTriangles, roofTriangles);
        }

        private string BuildBuildingsColladaFromTriangles(List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle> meshTriangles, float cx, float cy, float cz)
        {
            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var indices = new StringBuilder();
            var vertexCount = 0;
            var normalCount = 0;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var triangle in meshTriangles)
            {
                var a = new System.Numerics.Vector3(triangle.A.X - cx, triangle.A.Y - cy, triangle.A.Z - cz);
                var b = new System.Numerics.Vector3(triangle.B.X - cx, triangle.B.Y - cy, triangle.B.Z - cz);
                var c = new System.Numerics.Vector3(triangle.C.X - cx, triangle.C.Y - cy, triangle.C.Z - cz);
                var normal = System.Numerics.Vector3.Cross(b - a, c - a);
                var length = normal.Length();
                if (length < 0.0001f)
                {
                    continue;
                }
                normal /= length;
                foreach (var vertex in new[] { a, b, c })
                {
                    positions.Append(vertex.X.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Y.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Z.ToString("0.###", culture)).Append(' ');
                }
                normals.Append(normal.X.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Y.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Z.ToString("0.###", culture)).Append(' ');
                indices.Append($"{vertexCount} {normalCount} {vertexCount + 1} {normalCount} {vertexCount + 2} {normalCount} ");
                vertexCount += 3;
                normalCount += 1;
            }
            return ColladaDocument(positions.ToString(), normals.ToString(), indices.ToString(), vertexCount, normalCount, vertexCount / 3);
        }

        /// <summary>
        /// Places real Arma 3 models: each distinct model is copied once from the shared model
        /// library as a COLLADA shape with its textures, then instanced with a TSStatic per
        /// placement.
        /// </summary>
        /// <returns>Number of placements written.</returns>
        private int WriteModelInstances(ZipArchive zip, string basePath, List<Dictionary<string, object>> buildingItems,
            float floor, float half, IProgressScope scope)
        {
            var daeDirectory = Path.Combine(modelLibraryDirectory!, "dae");
            var textureDirectory = Path.Combine(modelLibraryDirectory!, "textures");
            var shapeFolder = $"{basePath}/art/shapes/arma";

            var copiedTextures = copiedShapeTextures;
            var materials = armaMaterials;
            var placed = 0;
            var missing = 0;
            var lampLights = new List<Dictionary<string, object>>();

            foreach (var group in modelInstances!.GroupBy(i => i.Model, StringComparer.OrdinalIgnoreCase))
            {
                var name = SanitizeModelName(group.Key);
                var source = Path.Combine(daeDirectory, name + ".dae");
                if (!File.Exists(source))
                {
                    missing += group.Count();
                    continue;
                }

                var isLamp = IsLitStreetLamp(group.Key);

                if (copiedShapes.Add(name))
                {
                    var dae = File.ReadAllText(source);
                    WriteText(zip, $"{shapeFolder}/{name}.dae", dae);
                    if (isLamp)
                    {
                        shapeHeads[name] = ShapeHead(dae);
                    }

                    // Faces with no texture, and Arma's procedural flat colours, still declare a
                    // material symbol. Without a definition BeamNG paints them with its NO MATERIAL
                    // placeholder, which is what turned whole buildings red.
                    foreach (var (symbol, colour) in ExtractColourMaterials(dae))
                    {
                        if (!materials.ContainsKey(symbol))
                        {
                            materials[symbol] = ColourMaterial(symbol, colour);
                        }
                    }

                    // The dae references its textures by bare file name, so they must sit next to it
                    foreach (var texture in ExtractTextureNames(dae))
                    {
                        if (!copiedTextures.Add(texture))
                        {
                            continue;
                        }
                        var texturePath = Path.Combine(textureDirectory, texture);
                        if (!File.Exists(texturePath))
                        {
                            continue;
                        }
                        var entry = zip.CreateEntry($"{shapeFolder}/{texture}", CompressionLevel.Fastest);
                        using var entryStream = entry.Open();
                        using var file = File.OpenRead(texturePath);
                        file.CopyTo(entryStream);

                        // One definition of an Arma material, shared with TryCopyLibraryShape. This
                        // used to be a second copy built inline, and it silently won: every fix
                        // made to TextureMaterial went into a method the placed models never called.
                        materials[Path.GetFileNameWithoutExtension(texture)] = TextureMaterial(texture);
                    }
                }

                foreach (var instance in group)
                {
                    var levelX = instance.X - half;
                    var levelY = instance.Y - half;
                    var levelZ = GroundedZ(instance.X, instance.Y, instance.Z,
                        instance.Model.Contains("bridge", StringComparison.OrdinalIgnoreCase)) - floor;

                    var item = Item("TSStatic", $"arma_{name}_{placed:00000}", "Buildings");
                    item["position"] = new object[]
                    {
                        MathF.Round(levelX, 3),
                        MathF.Round(levelY, 3),
                        MathF.Round(levelZ, 3)
                    };
                    item["rotationMatrix"] = instance.Rotation.Select(v => (object)MathF.Round(v, 6)).ToArray();
                    // TSStatic takes a three component scale: a bare number is rejected and the
                    // object never gets instantiated, silently.
                    if (MathF.Abs(instance.Scale - 1f) > 0.001f)
                    {
                        var scale = MathF.Round(instance.Scale, 6);
                        item["scale"] = new object[] { scale, scale, scale };
                    }
                    item["shapeName"] = $"levels/{levelName}/art/shapes/arma/{name}.dae";
                    // Ported shapes carry a Colmesh node built from Arma's own geometry LOD.
                    // "Visible Mesh" would collide against the finest visual mesh instead, which at
                    // this object count is what turns a map into a slideshow.
                    item["collisionType"] = "Collision Mesh";
                    item["decalType"] = "Collision Mesh";
                    item["useInstanceRenderData"] = true;
                    buildingItems.Add(item);

                    if (isLamp)
                    {
                        AddLampLight(lampLights, instance, name, source, levelX, levelY, levelZ);
                    }
                    placed++;
                }
            }

            scope.WriteLine($"Buildings: {placed} real Arma models placed ({copiedShapes.Count} distinct shapes, {copiedTextures.Count} textures)"
                + (missing > 0 ? $", {missing} placements skipped, model not in the library" : string.Empty));

            if (lampLights.Count > 0)
            {
                // Same folder path vanilla uses, because the group nesting is what the level format
                // stores: MissionGroup/nightlights/lightemitters/<group>
                WriteNdJson(zip, $"{basePath}/main/MissionGroup/nightlights/lightemitters/grm_streetlights/items.level.json",
                    lampLights.ToArray());
                scope.WriteLine($"Street lamps: {lampLights.Count} night lights on the map's own Arma lamp posts");
            }

            return placed;
        }

        /// <summary>
        /// Named places paired with the unique object name their spawn point uses. Names are made
        /// unique because BeamNG keys spawn points by object name and Arma maps do reuse a label.
        /// </summary>
        private IEnumerable<(BeamNGPlace Place, string ObjectName)> NamedPlaces()
        {
            if (places == null)
            {
                yield break;
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var place in places)
            {
                var slug = Sanitize(place.Name);
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }
                var objectName = "spawn_" + slug;
                var suffix = 2;
                while (!used.Add(objectName))
                {
                    objectName = FormattableString.Invariant($"spawn_{slug}_{suffix++}");
                }
                yield return (place, objectName);
            }
        }

        /// <summary>Spawn point list of info.json: the default one, then every named place.</summary>
        private object[] BuildSpawnPointList()
        {
            var result = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "Default",
                    ["objectname"] = "spawn_default",
                    ["preview"] = "preview.png",
                    ["translationId"] = "Default Spawnpoint",
                }
            };
            foreach (var place in NamedPlaces())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["name"] = place.Place.Name,
                    ["objectname"] = place.ObjectName,
                    ["preview"] = "preview.png",
                    ["translationId"] = place.Place.Name,
                });
            }
            return result.ToArray();
        }

        /// <summary>Terrain height at a world position in metres, before the level floor offset.</summary>
        private float HeightAtWorld(float x, float y)
        {
            var gx = Math.Clamp((int)MathF.Round(x / cellSize), 0, grid.Size - 1);
            var gy = Math.Clamp((int)MathF.Round(y / cellSize), 0, grid.Size - 1);
            return Height(gx, gy);
        }

        /// <summary>
        /// Copies a shape of the shared model library into the level, with its textures and
        /// materials, unless it is already there. Returns false when the library has no such mesh.
        /// </summary>
        private readonly Dictionary<string, float> footLifts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How far a library shape has to be raised for its foot to rest on the ground.
        /// </summary>
        /// <remarks>
        /// An ODOL model is autocentred, so a spruce runs from about -9 m to +9 m around its anchor.
        /// A placement that comes from a wrp carries an altitude that already accounts for that, but
        /// one worked out here does not: standing the origin on the terrain buries the whole lower
        /// half, which is why the canopy trees came out sunk into the ground and up to their middles
        /// inside the houses next to them. Returns the depth of the model below its own origin.
        /// <para>
        /// Zero for a generated billboard, which is built with its foot at the origin already.
        /// </para>
        /// </remarks>
        private float FootLift(string type)
        {
            if (footLifts.TryGetValue(type, out var lift))
            {
                return lift;
            }
            lift = 0f;
            if (!string.IsNullOrEmpty(modelLibraryDirectory))
            {
                var source = Path.Combine(modelLibraryDirectory, "dae", type + ".dae");
                if (File.Exists(source))
                {
                    var match = PositionArrayRegex.Match(File.ReadAllText(source));
                    if (match.Success)
                    {
                        var heights = new List<float>();
                        var index = 0;
                        foreach (var token in match.Groups["values"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (index % 3 == 2
                                && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                            {
                                heights.Add(value);
                            }
                            index++;
                        }
                        if (heights.Count > 0)
                        {
                            heights.Sort();
                            // The fifth percentile, not the lowest vertex. Arma models its trees
                            // with roots reaching below the ground they stand on: lifting by the
                            // very lowest point stands the whole root ball on the surface, which is
                            // what left the big spruces perched on their roots in the open. Taking a
                            // low percentile instead buries the roots and rests the trunk on the
                            // ground, which is where Arma itself puts them.
                            var foot = heights[Math.Min(heights.Count - 1, heights.Count / 20)];
                            if (foot < 0f)
                            {
                                lift = -foot;
                            }
                        }
                    }
                }
            }
            footLifts[type] = lift;
            return lift;
        }

        private bool TryCopyLibraryShape(ZipArchive zip, string basePath, string name)
        {
            if (string.IsNullOrEmpty(modelLibraryDirectory))
            {
                return false;
            }
            var source = Path.Combine(modelLibraryDirectory, "dae", name + ".dae");
            if (!File.Exists(source))
            {
                return false;
            }
            if (!copiedShapes.Add(name))
            {
                return true; // already in the level
            }

            var shapeFolder = $"{basePath}/art/shapes/arma";
            var textureDirectory = Path.Combine(modelLibraryDirectory, "textures");
            var dae = File.ReadAllText(source);
            WriteText(zip, $"{shapeFolder}/{name}.dae", dae);

            foreach (var (symbol, colour) in ExtractColourMaterials(dae))
            {
                if (!armaMaterials.ContainsKey(symbol))
                {
                    armaMaterials[symbol] = ColourMaterial(symbol, colour);
                }
            }

            foreach (var texture in ExtractTextureNames(dae))
            {
                if (!copiedShapeTextures.Add(texture))
                {
                    continue;
                }
                var texturePath = Path.Combine(textureDirectory, texture);
                if (!File.Exists(texturePath))
                {
                    continue;
                }
                var entry = zip.CreateEntry($"{shapeFolder}/{texture}", CompressionLevel.Fastest);
                using (var entryStream = entry.Open())
                using (var file = File.OpenRead(texturePath))
                {
                    file.CopyTo(entryStream);
                }
                armaMaterials[StripColourSuffix(Path.GetFileNameWithoutExtension(texture))] = TextureMaterial(texture);
            }
            return true;
        }

        /// <summary>Material bound to one converted Arma texture.</summary>
        /// <summary>
        /// True when the exported texture carries a real alpha channel, read from the dds header:
        /// DXT3 and DXT5 store one, DXT1 does not.
        /// </summary>
        private bool TextureHasAlphaChannel(string texture)
        {
            if (string.IsNullOrEmpty(modelLibraryDirectory))
            {
                return false;
            }
            try
            {
                var path = Path.Combine(modelLibraryDirectory, "textures", texture);
                if (!File.Exists(path))
                {
                    return false;
                }
                var header = new byte[88];
                using (var file = File.OpenRead(path))
                {
                    if (file.Read(header, 0, header.Length) < header.Length)
                    {
                        return false;
                    }
                }
                if (texture.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    // IHDR colour type, byte 25: 4 is grey with alpha, 6 is truecolour with alpha
                    return header[25] == 4 || header[25] == 6;
                }
                // dds: DXT3 and DXT5 carry an alpha block, DXT1 does not
                var fourCc = System.Text.Encoding.ASCII.GetString(header, 84, 4);
                return fourCc == "DXT3" || fourCc == "DXT5";
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static string StripColourSuffix(string name)
        {
            return name.EndsWith(".color", StringComparison.OrdinalIgnoreCase)
                ? name[..^6]
                : name;
        }

        private Dictionary<string, object> TextureMaterial(string texture)
        {
            // The .color suffix is a colour space marker on the file, not part of the material
            // name: the COLLADA still binds the symbol without it.
            var material = StripColourSuffix(Path.GetFileNameWithoutExtension(texture));

            // Arma names many alpha textures *_ca, but by no means all of them: foliage ships as
            // *_co with the cutout in the alpha channel, and trusting the suffix alone left every
            // leaf card drawn as a solid rectangle. The file itself is the authority.
            var hasAlpha = material.EndsWith("_ca", StringComparison.OrdinalIgnoreCase)
                || TextureHasAlphaChannel(texture);

            var result = new Dictionary<string, object>
            {
                ["name"] = material,
                ["mapTo"] = material,
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                // Legacy stage, deliberately. The 1.5 PBR path does not sample a dds through
                // baseColorMap: surfaces came out lit but pure white. Vanilla's PBR materials are
                // almost all .color.png, and that suffix on a dds breaks the loader outright, so
                // dds and PBR cannot be combined. This path reads the texture, and the darkness it
                // was blamed for turned out to be the inverted normals instead.
                ["Stages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["colorMap"] = $"levels/{levelName}/art/shapes/arma/{texture}",
                        ["specularPower"] = 8,
                        ["useAnisotropic"] = true
                    },
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>()
                },
                ["materialTag0"] = "beamng"
            };
            if (hasAlpha)
            {
                result["alphaRef"] = 80;
                result["alphaTest"] = true;
                result["doubleSided"] = true;
                result["translucentBlendOp"] = "None";
            }
            return result;
        }

        private static bool IsRockName(string name)
        {
            return name.Contains("rock", StringComparison.OrdinalIgnoreCase)
                || name.Contains("stone", StringComparison.OrdinalIgnoreCase)
                || name.Contains("limestone", StringComparison.OrdinalIgnoreCase)
                || name.Contains("boulder", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBushName(string name)
        {
            return name.StartsWith("b_", StringComparison.OrdinalIgnoreCase)
                || name.Contains("bush", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Arma lamp posts that are meant to be lit, so a light can be hung under their head.
        /// </summary>
        /// <remarks>
        /// Only the posts: the map is also full of runway edge lights, navigation lights and flush
        /// markers, and giving each of those a real light would put thousands of them on the map for
        /// a glow the player never drives under. Arma ships an unlit twin of every post
        /// (<c>lampstreet_off_f</c>), which must stay dark.
        /// </remarks>
        private static bool IsLitStreetLamp(string? model)
        {
            if (model == null)
            {
                return false;
            }
            var name = Path.GetFileNameWithoutExtension(model);
            return name.Contains("lamp", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("_off", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("lamphouse", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Hangs a night light under the head of one lamp post.
        /// </summary>
        /// <remarks>
        /// Copied from what BeamNG's own levels do: a spot light aimed straight down, disabled in
        /// the file and flagged <c>nightLight</c>, which is the flag the engine flips at dusk.
        /// Shadows are left off on purpose. Vanilla turns them on, but it places a few thousand
        /// hand-authored lights, and a shadow-casting light per Arma lamp post is exactly the kind
        /// of per-object cost that already cost this export its frame rate once.
        /// </remarks>
        private void AddLampLight(List<Dictionary<string, object>> lights, BeamNGModelInstance instance,
            string shapeName, string shapeFile, float x, float y, float z)
        {
            if (!shapeHeads.TryGetValue(shapeName, out var head))
            {
                head = ShapeHead(File.ReadAllText(shapeFile));
                shapeHeads[shapeName] = head;
            }
            if (head.Z < 2f)
            {
                return; // not a post: nothing to hang a light under
            }

            // Just under the head, so the lamp housing itself is not lit from inside
            var localX = head.X * instance.Scale;
            var localY = head.Y * instance.Scale;
            var localZ = (head.Z - 0.35f) * instance.Scale;
            var rotation = instance.Rotation;
            float offsetX, offsetY, offsetZ;
            if (rotation.Length >= 9)
            {
                offsetX = (rotation[0] * localX) + (rotation[1] * localY) + (rotation[2] * localZ);
                offsetY = (rotation[3] * localX) + (rotation[4] * localY) + (rotation[5] * localZ);
                offsetZ = (rotation[6] * localX) + (rotation[7] * localY) + (rotation[8] * localZ);
            }
            else
            {
                offsetX = localX;
                offsetY = localY;
                offsetZ = localZ;
            }

            var light = Item("SpotLight", $"grm_lamp_light_{lights.Count:00000}", "grm_streetlights");
            light["position"] = new object[]
            {
                MathF.Round(x + offsetX, 3),
                MathF.Round(y + offsetY, 3),
                MathF.Round(z + offsetZ, 3)
            };
            // Straight down, the same matrix every vanilla street light carries
            light["rotationMatrix"] = new object[] { 1f, 0f, 0f, 0f, 0f, -1f, 0f, 1f, 0f };
            light["color"] = new object[] { 1f, 0.87f, 0.68f, 1f };
            light["innerAngle"] = 25;
            light["outerAngle"] = 150;
            // Vanilla's own street lights carry a range of 45 m. Scaling it with the post keeps a
            // 3.5 m harbour lamp from lighting a whole square, without cutting the pool of a real
            // street lamp down to a spot right at its foot.
            light["range"] = MathF.Round(Math.Clamp(localZ * 3.5f, 20f, 45f), 1);
            light["castShadows"] = false;
            light["isEnabled"] = false;
            light["nightLight"] = "1";
            light["useColorTemperature"] = "false";
            lights.Add(light);
        }

        /// <summary>
        /// Where the head of a ported lamp post sits, in the model's own coordinates.
        /// </summary>
        /// <remarks>
        /// Z is not the height of the object: an ODOL model is autocentred, so a lamp post runs from
        /// -6.6 m to +6.5 m around the point the wrp places it at, and it is that upper half the
        /// light hangs from. X and Y matter just as much, because a street lamp's arm reaches out
        /// over the carriageway: measured on <c>lampstreet_f</c> the head sits 0.88 m to the side of
        /// the mast, so a light placed on the mast axis lights the pavement and leaves the road dark.
        /// Taken as the centroid of everything within a metre of the top of the finest detail level;
        /// the coarser levels and the billboard are skipped, the billboard being a flat card whose
        /// corners would drag the centroid sideways.
        /// <para>
        /// The port writes vertices as (X, arma Z, arma Y), so the third float of each triple is the
        /// vertical one.
        /// </para>
        /// </remarks>
        private static (float X, float Y, float Z) ShapeHead(string dae)
        {
            var array = PositionArrayRegex.Match(dae);
            if (!array.Success)
            {
                return (0f, 0f, 0f);
            }

            var values = new List<float>();
            foreach (var token in array.Groups["values"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                values.Add(float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f);
            }

            var top = float.MinValue;
            for (var i = 2; i < values.Count; i += 3)
            {
                if (values[i] > top)
                {
                    top = values[i];
                }
            }
            if (top == float.MinValue)
            {
                return (0f, 0f, 0f);
            }

            float sumX = 0f, sumY = 0f, sumZ = 0f;
            var count = 0;
            for (var i = 2; i < values.Count; i += 3)
            {
                if (values[i] <= top - 1f)
                {
                    continue;
                }
                sumX += values[i - 2];
                sumY += values[i - 1];
                sumZ += values[i];
                count++;
            }
            return count == 0 ? (0f, 0f, top) : (sumX / count, sumY / count, top);
        }

        private static readonly Regex PositionArrayRegex = new(
            @"-pos-array""\s+count=""\d+"">(?<values>[^<]*)<",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>True when the map places lamp posts that will get a night light.</summary>
        private bool HasStreetLampModels => modelInstances != null
            && !string.IsNullOrEmpty(modelLibraryDirectory)
            && modelInstances.Any(i => IsLitStreetLamp(i.Model));

        /// <summary>True when anything will be written into the Buildings mission group.</summary>
        private bool HasBuildingsGroup =>
            (buildingMeshes != null && buildingMeshes.Count > 0)
            || (modelInstances != null && modelInstances.Count > 0 && !string.IsNullOrEmpty(modelLibraryDirectory));

        /// <summary>How the buildings of this level were produced, for the export report.</summary>
        private string DescribeBuildingSource()
        {
            if (buildingMeshes != null && buildingMeshes.Count > 0)
            {
                return " (individual swissBUILDINGS3D shapes, editable one by one in MissionGroup/Buildings)";
            }
            if (modelInstances != null && modelInstances.Count > 0 && !string.IsNullOrEmpty(modelLibraryDirectory))
            {
                return " (real Arma 3 models from the shared model library, one TSStatic each)";
            }
            return " (merged OSM footprint boxes)";
        }

        /// <summary>
        /// Material symbols of a COLLADA document that carry a plain colour rather than a texture,
        /// with the colour the effect declares. Covers untextured faces and the procedural colours
        /// Arma writes as <c>#(argb,8,8,3)color(r,g,b,a)</c>.
        /// </summary>
        private static IEnumerable<(string Symbol, float[] Colour)> ExtractColourMaterials(string dae)
        {
            var textured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(dae, @"<effect id=""([^""]+)-effect""><profile_COMMON>\s*<newparam"))
            {
                textured.Add(match.Groups[1].Value);
            }

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(dae, @"<triangles material=""([^""]+)"""))
            {
                var symbol = match.Groups[1].Value;
                if (textured.Contains(symbol))
                {
                    continue;
                }
                yield return (symbol, ReadEffectColour(dae, symbol));
            }
        }

        /// <summary>Diffuse colour an effect declares, or a neutral grey when it has none.</summary>
        private static float[] ReadEffectColour(string dae, string symbol)
        {
            var match = System.Text.RegularExpressions.Regex.Match(dae,
                @"<effect id=""" + System.Text.RegularExpressions.Regex.Escape(symbol) + @"-effect"".*?<color>([^<]+)</color>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success)
            {
                var parts = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g)
                    && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b))
                {
                    return new[] { r, g, b, 1f };
                }
            }
            return new[] { 0.62f, 0.6f, 0.56f, 1f };
        }

        private Dictionary<string, object> ColourMaterial(string symbol, float[] colour)
        {
            return new Dictionary<string, object>
            {
                ["name"] = symbol,
                ["mapTo"] = symbol,
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                // Legacy stage, same reason as TextureMaterial.
                ["Stages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["diffuseColor"] = new object[] { colour[0], colour[1], colour[2], colour[3] },
                        ["specularPower"] = 8
                    },
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>()
                },
                ["materialTag0"] = "beamng"
            };
        }

        /// <summary>Texture file names a COLLADA document loads, read from its init_from elements.</summary>
        private static IEnumerable<string> ExtractTextureNames(string dae)
        {
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(dae, @"<init_from>([^<]+\.(?:dds|png))</init_from>"))
            {
                yield return match.Groups[1].Value;
            }
        }

        /// <summary>Library file name of a model, matching what the port pipeline wrote.</summary>
        private static string SanitizeModelName(string model)
        {
            var name = Path.GetFileNameWithoutExtension(model);
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString();
        }

        private static string ColladaDocument(string positions, string normals, string indices, int vertexCount, int normalCount, int triangleCount, string material = "grm_building")
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<COLLADA xmlns=""http://www.collada.org/2005/11/COLLADASchema"" version=""1.4.1"">
 <asset><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name=""meter"" meter=""1""/><up_axis>Z_UP</up_axis></asset>
 <library_effects>
  <effect id=""{material}-effect""><profile_COMMON><technique sid=""common""><lambert><diffuse><color>0.62 0.6 0.56 1</color></diffuse></lambert></technique></profile_COMMON></effect>
 </library_effects>
 <library_materials>
  <material id=""{material}-material"" name=""{material}""><instance_effect url=""#{material}-effect""/></material>
 </library_materials>
 <library_geometries>
  <geometry id=""buildings-mesh"" name=""buildings"">
   <mesh>
    <source id=""buildings-pos"">
     <float_array id=""buildings-pos-array"" count=""{vertexCount * 3}"">{positions}</float_array>
     <technique_common><accessor source=""#buildings-pos-array"" count=""{vertexCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <source id=""buildings-nrm"">
     <float_array id=""buildings-nrm-array"" count=""{normalCount * 3}"">{normals}</float_array>
     <technique_common><accessor source=""#buildings-nrm-array"" count=""{normalCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <vertices id=""buildings-verts""><input semantic=""POSITION"" source=""#buildings-pos""/></vertices>
    <triangles material=""{material}"" count=""{triangleCount}"">
     <input semantic=""VERTEX"" source=""#buildings-verts"" offset=""0""/>
     <input semantic=""NORMAL"" source=""#buildings-nrm"" offset=""1""/>
     <p>{indices}</p>
    </triangles>
   </mesh>
  </geometry>
 </library_geometries>
 <library_visual_scenes>
  <visual_scene id=""Scene"" name=""Scene"">
   <node id=""buildings"" name=""buildings"" type=""NODE"">
    <instance_geometry url=""#buildings-mesh"">
     <bind_material><technique_common><instance_material symbol=""{material}"" target=""#{material}-material""/></technique_common></bind_material>
    </instance_geometry>
   </node>
  </visual_scene>
 </library_visual_scenes>
 <scene><instance_visual_scene url=""#Scene""/></scene>
</COLLADA>";
        }

        /// <summary>
        /// COLLADA for a building carrying texture coordinates and two materials, one for the walls
        /// and one for the roof.
        /// </summary>
        private static string TexturedBuildingDocument(string positions, string normals, string uvs,
            string wallIndices, string roofIndices, int vertexCount, int normalCount,
            int wallTriangles, int roofTriangles)
        {
            var groups = new StringBuilder();
            if (wallTriangles > 0)
            {
                groups.Append($@"
    <triangles material=""grm_wall_face"" count=""{wallTriangles}"">
     <input semantic=""VERTEX"" source=""#buildings-verts"" offset=""0""/>
     <input semantic=""NORMAL"" source=""#buildings-nrm"" offset=""1""/>
     <input semantic=""TEXCOORD"" source=""#buildings-uv"" offset=""2"" set=""0""/>
     <p>{wallIndices}</p>
    </triangles>");
            }
            if (roofTriangles > 0)
            {
                groups.Append($@"
    <triangles material=""grm_roof"" count=""{roofTriangles}"">
     <input semantic=""VERTEX"" source=""#buildings-verts"" offset=""0""/>
     <input semantic=""NORMAL"" source=""#buildings-nrm"" offset=""1""/>
     <input semantic=""TEXCOORD"" source=""#buildings-uv"" offset=""2"" set=""0""/>
     <p>{roofIndices}</p>
    </triangles>");
            }

            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<COLLADA xmlns=""http://www.collada.org/2005/11/COLLADASchema"" version=""1.4.1"">
 <asset><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name=""meter"" meter=""1""/><up_axis>Z_UP</up_axis></asset>
 <library_effects>
  <effect id=""grm_wall_face-effect""><profile_COMMON><technique sid=""common""><lambert><diffuse><color>0.72 0.70 0.66 1</color></diffuse></lambert></technique></profile_COMMON></effect>
  <effect id=""grm_roof-effect""><profile_COMMON><technique sid=""common""><lambert><diffuse><color>0.42 0.24 0.18 1</color></diffuse></lambert></technique></profile_COMMON></effect>
 </library_effects>
 <library_materials>
  <material id=""grm_wall_face-material"" name=""grm_wall_face""><instance_effect url=""#grm_wall_face-effect""/></material>
  <material id=""grm_roof-material"" name=""grm_roof""><instance_effect url=""#grm_roof-effect""/></material>
 </library_materials>
 <library_geometries>
  <geometry id=""buildings-mesh"" name=""buildings"">
   <mesh>
    <source id=""buildings-pos"">
     <float_array id=""buildings-pos-array"" count=""{vertexCount * 3}"">{positions}</float_array>
     <technique_common><accessor source=""#buildings-pos-array"" count=""{vertexCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <source id=""buildings-nrm"">
     <float_array id=""buildings-nrm-array"" count=""{normalCount * 3}"">{normals}</float_array>
     <technique_common><accessor source=""#buildings-nrm-array"" count=""{normalCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>
    <source id=""buildings-uv"">
     <float_array id=""buildings-uv-array"" count=""{vertexCount * 2}"">{uvs}</float_array>
     <technique_common><accessor source=""#buildings-uv-array"" count=""{vertexCount}"" stride=""2""><param name=""S"" type=""float""/><param name=""T"" type=""float""/></accessor></technique_common>
    </source>
    <vertices id=""buildings-verts""><input semantic=""POSITION"" source=""#buildings-pos""/></vertices>{groups}
   </mesh>
  </geometry>
 </library_geometries>
 <library_visual_scenes>
  <visual_scene id=""Scene"" name=""Scene"">
   <node id=""buildings"" name=""buildings"" type=""NODE"">
    <instance_geometry url=""#buildings-mesh"">
     <bind_material><technique_common>
      <instance_material symbol=""grm_wall_face"" target=""#grm_wall_face-material""/>
      <instance_material symbol=""grm_roof"" target=""#grm_roof-material""/>
     </technique_common></bind_material>
    </instance_geometry>
   </node>
  </visual_scene>
 </library_visual_scenes>
 <scene><instance_visual_scene url=""#Scene""/></scene>
</COLLADA>";
        }

        /// <summary>Legacy material for a building surface, matched to the road ones.</summary>
        private static Dictionary<string, object> BuildingMaterial(string name, string texture)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["mapTo"] = name,
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["Stages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["colorMap"] = texture,
                        ["specularPower"] = 1,
                        ["useAnisotropic"] = true,
                    },
                    new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                },
                ["materialTag0"] = "beamng",
                // Same reason as the roads: left at the default the sun turns a wall into a mirror
                ["specularStrength0"] = "0",
            };
        }

        /// <summary>
        /// A tiling facade: plaster, a storey band, and one row of windows.
        /// </summary>
        /// <remarks>
        /// One tile covers three metres of wall in both directions, which is a storey, so the window
        /// row lands where a window belongs whatever the size of the building. The windows are
        /// invented: swissBUILDINGS3D says where a wall is and nothing about what is on it, and no
        /// open Swiss dataset carries facade imagery.
        /// </remarks>
        private static void WriteFacadePng(ZipArchive zip, string entryName)
        {
            const int Size = 256;
            using var image = new Image<Rgba32>(Size, Size);
            var seed = entryName.GetHashCode();
            var random = new Random(seed);

            // Plaster, with enough grain to survive minification
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var noise = random.Next(-8, 9);
                    image[x, y] = new Rgba32(
                        (byte)Math.Clamp(198 + noise, 0, 255),
                        (byte)Math.Clamp(191 + noise, 0, 255),
                        (byte)Math.Clamp(178 + noise, 0, 255),
                        255);
                }
            }

            // Two windows across the tile, set in the upper two thirds so the ground floor of each
            // storey stays solid wall
            var frame = new Rgba32(72, 66, 58, 255);
            var glass = new Rgba32(58, 74, 86, 255);
            foreach (var cx in new[] { Size / 4, Size * 3 / 4 })
            {
                var left = cx - (Size / 12);
                var right = cx + (Size / 12);
                var top = Size / 5;
                var bottom = Size * 3 / 5;
                for (var y = top; y <= bottom; y++)
                {
                    for (var x = left; x <= right; x++)
                    {
                        if (x < 0 || x >= Size || y < 0 || y >= Size)
                        {
                            continue;
                        }
                        var onFrame = x <= left + 2 || x >= right - 2 || y <= top + 2 || y >= bottom - 2;
                        image[x, y] = onFrame ? frame : glass;
                    }
                }
            }

            SavePng(zip, entryName, image);
        }

        /// <summary>Tiling roof cover: rows of tiles, four metres of roof per tile.</summary>
        private static void WriteRoofPng(ZipArchive zip, string entryName)
        {
            const int Size = 256;
            const int Rows = 16;
            using var image = new Image<Rgba32>(Size, Size);
            var random = new Random(entryName.GetHashCode());

            for (var y = 0; y < Size; y++)
            {
                // Every row a shade of its own, so the courses read from a distance
                var row = y * Rows / Size;
                var shade = ((row % 2) == 0 ? 6 : -6) + random.Next(-5, 6);
                var isJoint = ((y * Rows) % Size) < Rows;
                for (var x = 0; x < Size; x++)
                {
                    var jitter = random.Next(-6, 7);
                    var r = 138 + shade + jitter;
                    var g = 74 + shade + jitter;
                    var b = 56 + shade + jitter;
                    if (isJoint || ((x + (row * 7)) % (Size / 8)) < 2)
                    {
                        r -= 28;
                        g -= 18;
                        b -= 14;
                    }
                    image[x, y] = new Rgba32(
                        (byte)Math.Clamp(r, 0, 255),
                        (byte)Math.Clamp(g, 0, 255),
                        (byte)Math.Clamp(b, 0, 255),
                        255);
                }
            }

            SavePng(zip, entryName, image);
        }

        private static string LoadShapeMaterialsJson() => LoadResource("BeamNGShapeMaterials.json");

        private static string LoadTerrainMaterialsJson() => LoadResource("BeamNGTerrainMaterials.json");

        private static string LoadResource(string name)
        {
            using var stream = typeof(BeamNGLevelWriter).Assembly.GetManifestResourceStream(
                "GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.Export.BeamNG." + name)
                ?? throw new ApplicationException($"{name} resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Walls, fences and hedges as vertical strips following the ground, merged per zone.
        /// </summary>
        private List<Dictionary<string, object>> WriteFences(ZipArchive zip, string basePath, float floor, float half, IProgressScope scope)
        {
            var items = new List<Dictionary<string, object>>();
            if (fences == null || fences.Count == 0)
            {
                return items;
            }
            var byKindAndZone = new Dictionary<(BeamNGFenceKind, int, int), List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle>>();
            foreach (var fence in fences)
            {
                var points = fence.Points;
                if (points == null || points.Count < 2)
                {
                    continue;
                }
                var (height, thickness) = fence.Kind switch
                {
                    BeamNGFenceKind.Wall => (1.8f, 0.25f),
                    BeamNGFenceKind.Hedge => (1.6f, 0.7f),
                    _ => (1.2f, 0.06f),
                };
                for (var i = 1; i < points.Count; i++)
                {
                    var a = points[i - 1];
                    var b = points[i];
                    var dx = b.X - a.X;
                    var dy = b.Y - a.Y;
                    var length = MathF.Sqrt(dx * dx + dy * dy);
                    if (length < 0.5f || length > 200f)
                    {
                        continue; // skip degenerate and suspicious spans
                    }
                    var nx = -dy / length * thickness / 2f;
                    var ny = dx / length * thickness / 2f;
                    var za = ElevationAt(a.X, a.Y) - floor - 0.2f;
                    var zb = ElevationAt(b.X, b.Y) - floor - 0.2f;
                    var key = (fence.Kind, (int)MathF.Floor(a.X / BuildingZoneSize), (int)MathF.Floor(a.Y / BuildingZoneSize));
                    if (!byKindAndZone.TryGetValue(key, out var triangles))
                    {
                        byKindAndZone.Add(key, triangles = new List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle>());
                    }
                    // Two parallel faces plus a cap, giving the strip some thickness
                    foreach (var side in new[] { 1f, -1f })
                    {
                        var p0 = new System.Numerics.Vector3(a.X + nx * side, a.Y + ny * side, za);
                        var p1 = new System.Numerics.Vector3(b.X + nx * side, b.Y + ny * side, zb);
                        var p2 = new System.Numerics.Vector3(b.X + nx * side, b.Y + ny * side, zb + height);
                        var p3 = new System.Numerics.Vector3(a.X + nx * side, a.Y + ny * side, za + height);
                        triangles.Add(new(p0, p1, p2));
                        triangles.Add(new(p0, p2, p3));
                    }
                    var t0 = new System.Numerics.Vector3(a.X + nx, a.Y + ny, za + height);
                    var t1 = new System.Numerics.Vector3(b.X + nx, b.Y + ny, zb + height);
                    var t2 = new System.Numerics.Vector3(b.X - nx, b.Y - ny, zb + height);
                    var t3 = new System.Numerics.Vector3(a.X - nx, a.Y - ny, za + height);
                    triangles.Add(new(t0, t1, t2));
                    triangles.Add(new(t0, t2, t3));
                }
            }

            var index = 0;
            foreach (var group in byKindAndZone.Where(g => g.Value.Count > 0).Take(MaxBuildingZones))
            {
                index++;
                var (kind, zx, zy) = group.Key;
                var material = kind switch
                {
                    BeamNGFenceKind.Wall => "grm_wall",
                    BeamNGFenceKind.Hedge => "grm_hedge",
                    _ => "grm_fence",
                };
                var cx = (zx + 0.5f) * BuildingZoneSize;
                var cy = (zy + 0.5f) * BuildingZoneSize;
                var cz = group.Value.Min(t => MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)));
                var shapeFile = $"art/shapes/furniture/{material}_{index:0000}.dae";
                WriteText(zip, $"{basePath}/{shapeFile}", BuildFurnitureCollada(group.Value, cx, cy, cz, material));

                var item = Item("TSStatic", $"{material}_{index:0000}", "Furniture");
                item["position"] = new object[] { MathF.Round(cx - half, 3), MathF.Round(cy - half, 3), MathF.Round(cz, 3) };
                item["shapeName"] = $"levels/{levelName}/{shapeFile}";
                item["collisionType"] = "Visible Mesh";
                item["decalType"] = "Visible Mesh";
                item["useInstanceRenderData"] = true;
                items.Add(item);
            }

            if (items.Count > 0)
            {
                scope.WriteLine($"Furniture: {items.Count} wall/fence/hedge objects");
            }
            return items;
        }

        private static Dictionary<string, object> FlatMaterial(string name, double r, double g, double b)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["mapTo"] = name,
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["Stages"] = new object[]
                {
                    new Dictionary<string, object> { ["diffuseColor"] = new[] { r, g, b, 1.0 }, ["specularPower"] = 1 },
                    new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                },
                ["doubleSided"] = true,
                ["materialTag0"] = "beamng",
            };
        }

        private string BuildFurnitureCollada(List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle> triangles,
            float cx, float cy, float cz, string material)
        {
            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var indices = new StringBuilder();
            var vertexCount = 0;
            var normalCount = 0;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var triangle in triangles)
            {
                var a = new System.Numerics.Vector3(triangle.A.X - cx, triangle.A.Y - cy, triangle.A.Z - cz);
                var b = new System.Numerics.Vector3(triangle.B.X - cx, triangle.B.Y - cy, triangle.B.Z - cz);
                var c = new System.Numerics.Vector3(triangle.C.X - cx, triangle.C.Y - cy, triangle.C.Z - cz);
                var normal = System.Numerics.Vector3.Cross(b - a, c - a);
                var length = normal.Length();
                if (length < 0.0001f)
                {
                    continue;
                }
                normal /= length;
                foreach (var vertex in new[] { a, b, c })
                {
                    positions.Append(vertex.X.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Y.ToString("0.###", culture)).Append(' ')
                             .Append(vertex.Z.ToString("0.###", culture)).Append(' ');
                }
                normals.Append(normal.X.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Y.ToString("0.###", culture)).Append(' ')
                       .Append(normal.Z.ToString("0.###", culture)).Append(' ');
                indices.Append($"{vertexCount} {normalCount} {vertexCount + 1} {normalCount} {vertexCount + 2} {normalCount} ");
                vertexCount += 3;
                normalCount += 1;
            }
            return ColladaDocument(positions.ToString(), normals.ToString(), indices.ToString(), vertexCount, normalCount, vertexCount / 3, material);
        }

        private static bool IsTextureSlot(string key)
        {
            return key.EndsWith("Tex", StringComparison.Ordinal)
                || key == "macroMap" || key == "normalMap" || key == "detailMap" || key == "diffuseMap";
        }

        /// <summary>
        /// Point a texture slot of an official template to the equivalent file generated in this level.
        /// </summary>
        private static string ResolveTextureSlot(string key, string layer, string satellitePath, Func<string, string> terrainFile)
        {
            if (key == "baseColorBaseTex" || key == "diffuseMap")
            {
                return satellitePath; // far view keeps the real satellite imagery
            }
            if (key.StartsWith("baseColor", StringComparison.Ordinal) || key == "macroMap" || key == "detailMap")
            {
                return terrainFile($"{layer}_d.png");
            }
            if (key.StartsWith("normal", StringComparison.Ordinal))
            {
                return key == "normalBaseTex" ? terrainFile("shared_nm.png") : terrainFile($"{layer}_n.png");
            }
            if (key.StartsWith("roughness", StringComparison.Ordinal))
            {
                return key == "roughnessBaseTex" ? terrainFile("shared_r.png") : terrainFile($"{layer}_r.png");
            }
            if (key.StartsWith("height", StringComparison.Ordinal))
            {
                return key == "heightBaseTex" ? terrainFile("shared_r.png") : terrainFile($"{layer}_h.png");
            }
            // ao*: the base slot uses the full-size neutral map, detail/macro the detail-size one
            return key == "aoBaseTex" ? terrainFile("shared_ao.png") : terrainFile("shared_ao_detail.png");
        }

        /// <summary>
        /// Generate the tileable colour / normal / roughness / height detail textures of one surface.
        /// </summary>
        private static void WriteDetailTextures(ZipArchive zip, string basePath, string layer, Rgb24 color, int noiseAmount)
        {
            const int size = DetailTextureSize;
            var height = TileableNoise(size, layer.GetHashCode());

            using (var colorImage = new Image<Rgb24>(size, size))
            using (var roughImage = new Image<L8>(size, size))
            using (var heightImage = new Image<L8>(size, size))
            {
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var n = height[x, y];
                        var delta = (int)((n - 0.5f) * 2f * noiseAmount);
                        colorImage[x, y] = new Rgb24(
                            (byte)Math.Clamp(color.R + delta, 0, 255),
                            (byte)Math.Clamp(color.G + delta, 0, 255),
                            (byte)Math.Clamp(color.B + delta, 0, 255));
                        roughImage[x, y] = new L8((byte)Math.Clamp(150 + (n - 0.5f) * 90f, 0f, 255f));
                        heightImage[x, y] = new L8((byte)Math.Clamp(n * 255f, 0f, 255f));
                    }
                }
                SavePng(zip, $"{basePath}/art/terrains/{layer}_d.png", colorImage);
                SavePng(zip, $"{basePath}/art/terrains/{layer}_r.png", roughImage);
                SavePng(zip, $"{basePath}/art/terrains/{layer}_h.png", heightImage);
            }

            // Normal map from the height gradient (wrapping, so the tile stays seamless)
            using (var normalImage = new Image<Rgb24>(size, size))
            {
                const float strength = 3.5f;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var left = height[(x - 1 + size) % size, y];
                        var right = height[(x + 1) % size, y];
                        var up = height[x, (y - 1 + size) % size];
                        var down = height[x, (y + 1) % size];
                        var nx = (left - right) * strength;
                        var ny = (up - down) * strength;
                        var normal = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(nx, ny, 1f));
                        normalImage[x, y] = new Rgb24(
                            (byte)((normal.X * 0.5f + 0.5f) * 255f),
                            (byte)((normal.Y * 0.5f + 0.5f) * 255f),
                            (byte)((normal.Z * 0.5f + 0.5f) * 255f));
                    }
                }
                SavePng(zip, $"{basePath}/art/terrains/{layer}_n.png", normalImage);
            }
        }

        /// <summary>
        /// Seamless value noise (two octaves, wrap-around interpolation).
        /// </summary>
        private static float[,] TileableNoise(int size, int seed)
        {
            var result = new float[size, size];
            foreach (var (cells, weight) in new[] { (8, 0.6f), (32, 0.3f), (64, 0.1f) })
            {
                var random = new Random(seed ^ cells);
                var grid = new float[cells, cells];
                for (var gy = 0; gy < cells; gy++)
                {
                    for (var gx = 0; gx < cells; gx++)
                    {
                        grid[gx, gy] = (float)random.NextDouble();
                    }
                }
                var scale = (float)cells / size;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var fx = x * scale;
                        var fy = y * scale;
                        var x0 = (int)fx;
                        var y0 = (int)fy;
                        var tx = fx - x0;
                        var ty = fy - y0;
                        // Smoothstep for a softer, less blocky result
                        tx = tx * tx * (3f - 2f * tx);
                        ty = ty * ty * (3f - 2f * ty);
                        var x1 = (x0 + 1) % cells;
                        var y1 = (y0 + 1) % cells;
                        var top = grid[x0, y0] + (grid[x1, y0] - grid[x0, y0]) * tx;
                        var bottom = grid[x0, y1] + (grid[x1, y1] - grid[x0, y1]) * tx;
                        result[x, y] += (top + (bottom - top) * ty) * weight;
                    }
                }
            }
            return result;
        }

        private static void SavePng<TPixel>(ZipArchive zip, string entryName, Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        private static void WriteUniformPng(ZipArchive zip, string entryName, int size, Rgb24 color)
        {
            using var image = new Image<Rgb24>(size, size, color);
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        /// <summary>
        /// Volumetric grass over the grass layer. Types sample the four quadrants of the generated
        /// grass atlas, so tufts vary instead of repeating a single billboard.
        /// </summary>
        private Dictionary<string, object> GroundCoverObject(float worldSize, float centerHeight)
        {
            // Radius is the band around the camera where tufts are seeded, not a map extent:
            // scaling it with the map size spreads the element budget over kilometers and shows
            // nothing. A fixed band keeps the grass dense where it is actually visible.
            const float radius = 160f;
            // The object name must differ from the material name: BeamNG registers objects and
            // materials in the same global namespace, and the duplicate silently kills the object
            var cover = Item("GroundCover", "grm_groundcover", "vegetation");
            cover["position"] = new object[] { 0, 0, MathF.Round(centerHeight, 3) };
            cover["material"] = "grm_grass_cover";
            cover["gridSize"] = 3;
            cover["radius"] = radius;
            cover["dissolveRadius"] = radius * 0.75f;
            cover["shapeCullRadius"] = radius;
            cover["maxBillboardTiltAngle"] = 40;
            cover["maxElements"] = 180_000;
            cover["windGustLength"] = 1.7;
            cover["windGustStrength"] = 0.2;
            cover["windTurbulenceFrequency"] = 0.3;
            cover["seed"] = 11;
            cover["Types"] = new object[]
            {
                GroundCoverType(new[] { 0.0, 0.0, 0.5, 0.5 }, "grm_grass", 1.0, 0.45, 0.75, 10, 4),
                GroundCoverType(new[] { 0.5, 0.0, 0.5, 0.5 }, "grm_grass", 0.7, 0.4, 0.68, 8, 3),
                GroundCoverType(new[] { 0.0, 0.5, 0.5, 0.5 }, "grm_grass", 0.55, 0.35, 0.6, 7, 3),
                // Sparse dry tufts on ploughed fields so they are not bare
                GroundCoverType(new[] { 0.5, 0.5, 0.5, 0.5 }, "grm_dirt", 0.28, 0.3, 0.5, 4, 1),
                new Dictionary<string, object>(), new Dictionary<string, object>(),
                new Dictionary<string, object>(), new Dictionary<string, object>(),
            };
            return cover;
        }

        private static Dictionary<string, object> GroundCoverType(double[] uv, string layer, double probability,
            double sizeMin, double sizeMax, int maxClump, int minClump)
        {
            return new Dictionary<string, object>
            {
                ["billboardUVs"] = uv,
                ["clumpRadius"] = 1.5,
                ["layer"] = layer,
                ["maxClumpCount"] = maxClump,
                ["minClumpCount"] = minClump,
                ["probability"] = probability,
                ["sizeMax"] = sizeMax,
                ["sizeMin"] = sizeMin,
                ["windScale"] = 0.2,
            };
        }

        /// <summary>
        /// Grass billboard atlas: four quadrants of tufts drawn on transparent background.
        /// </summary>
        private void WriteGrassCoverTexture(ZipArchive zip, string basePath)
        {
            const int size = 512;
            const int half = size / 2;
            using var image = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 0));
            var random = new Random(4242);
            var palettes = new[]
            {
                new[] { new Rgba32(96, 132, 58, 255), new Rgba32(120, 158, 70, 255) },   // lush grass
                new[] { new Rgba32(86, 120, 52, 255), new Rgba32(110, 146, 64, 255) },   // darker grass
                new[] { new Rgba32(112, 140, 66, 255), new Rgba32(140, 168, 82, 255) },  // light grass
                new[] { new Rgba32(146, 134, 86, 255), new Rgba32(168, 156, 104, 255) }, // dry tufts
            };
            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var ox = (quadrant % 2) * half;
                var oy = (quadrant / 2) * half;
                var palette = palettes[quadrant];
                var blades = quadrant == 3 ? 26 : 44;
                for (var b = 0; b < blades; b++)
                {
                    // Blade rooted on the bottom edge, curving up and sideways
                    var rootX = ox + half * 0.15f + (float)random.NextDouble() * half * 0.7f;
                    var height = half * (0.45f + (float)random.NextDouble() * 0.5f);
                    var bend = (float)(random.NextDouble() - 0.5) * half * 0.35f;
                    var thickness = 1.6f + (float)random.NextDouble() * 1.6f;
                    var color = palette[random.Next(palette.Length)];
                    var steps = (int)height;
                    for (var s = 0; s < steps; s++)
                    {
                        var t = (float)s / steps;
                        var x = rootX + bend * t * t;
                        var y = oy + half - 1 - t * height;
                        var w = thickness * (1f - t * 0.85f);
                        for (var dx = -w; dx <= w; dx += 0.5f)
                        {
                            var px = (int)MathF.Round(x + dx);
                            var py = (int)MathF.Round(y);
                            if (px >= ox && px < ox + half && py >= oy && py < oy + half)
                            {
                                // Slight darkening towards the base gives the tuft some depth
                                var shade = 0.75f + 0.25f * t;
                                image[px, py] = new Rgba32(
                                    (byte)(color.R * shade), (byte)(color.G * shade), (byte)(color.B * shade), 255);
                            }
                        }
                    }
                }
            }
            SavePng(zip, $"{basePath}/art/terrains/grass_cover.png", image);
        }

        /// <summary>
        /// Material of the grass billboards. It has to live in a main.materials.json next to the
        /// texture: BeamNG only scans files with that name.
        /// </summary>
        private Dictionary<string, object> GrassCoverMaterial()
        {
            return new Dictionary<string, object>
            {
                ["name"] = "grm_grass_cover",
                ["mapTo"] = "grm_grass_cover",
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["Stages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["colorMap"] = $"levels/{levelName}/art/terrains/grass_cover.png",
                        ["specularPower"] = 1,
                    },
                    new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                },
                ["alphaRef"] = 64,
                ["alphaTest"] = true,
                ["doubleSided"] = true,
                ["translucentBlendOp"] = "None",
                ["groundType"] = "GRASS",
                ["annotation"] = "GRASS",
                ["materialTag0"] = "beamng",
                ["materialTag1"] = "vegetation",
            };
        }

        private Dictionary<string, object> ForestObject()
        {
            var forest = Item("Forest", "theForest", "vegetation");
            forest["lodReflectScalar"] = 0;
            return forest;
        }

        // ── Roads ──────────────────────────────────────────────────────────────

        private List<Dictionary<string, object>> BuildDecalRoads(float floor, float half)
        {
            var result = new List<Dictionary<string, object>>();
            if (roads == null)
            {
                return result;
            }
            var roadIndex = 0;
            foreach (var road in roads)
            {
                var points = road.Points;
                if (points == null || points.Count < 2)
                {
                    continue;
                }
                var isDirt = road.IsDirt;
                // Real widths feel cramped behind the wheel: widen a bit and enforce a drivable
                // minimum. Pedestrian ways are dropped by the callers.
                var width = Math.Max(isDirt ? 4f : 6.5f, road.Width * RoadWidthFactor);
                var halfWidth = width / 2f;

                // Decimate then chunk so BeamNG splines stay smooth and objects stay small
                var nodes = new List<float[]>();
                TerrainPoint? last = null;
                for (var i = 0; i < points.Count; i++)
                {
                    var pt = points[i];
                    if (last != null && i < points.Count - 1)
                    {
                        var dx = pt.X - last.X;
                        var dy = pt.Y - last.Y;
                        if (Math.Sqrt(dx * dx + dy * dy) < MinRoadNodeSpacing)
                        {
                            continue;
                        }
                    }
                    var z = ElevationAt(pt.X, pt.Y) - floor + RoadSurfaceLift;
                    // The final point is always kept, so a polyline that ends a few centimetres
                    // after its previous node leaves a stub of a segment behind: road_296 finished
                    // on a 0.59 m leg. Moving the node it would have stubbed against keeps the road
                    // ending in exactly the right place without the stub.
                    if (i == points.Count - 1 && last != null && nodes.Count >= 2
                        && MathF.Sqrt(((pt.X - last.X) * (pt.X - last.X)) + ((pt.Y - last.Y) * (pt.Y - last.Y))) < MinRoadNodeSpacing)
                    {
                        nodes[^1] = new[] { pt.X - half, pt.Y - half, z };
                        last = pt;
                        continue;
                    }
                    last = pt;
                    nodes.Add(new[] { pt.X - half, pt.Y - half, z });
                    if (nodes.Count == MaxDecalRoadNodes && i < points.Count - 1)
                    {
                        EmitRoadChunk(result, nodes, halfWidth, road, ref roadIndex);
                        nodes = new List<float[]> { nodes[^1] };
                    }
                }
                if (nodes.Count >= 2)
                {
                    EmitRoadChunk(result, nodes, halfWidth, road, ref roadIndex);
                }
            }
            return result;
        }

        /// <summary>
        /// One road chunk. Uses only BeamNG global decalroad materials (art_shapes.zip), which resolve
        /// in every level. Base = plain asphalt/dirt (no baked markings, so narrow roads stay grey),
        /// soft grass edge blend, thin dashed center line on wide asphalt roads only.
        /// </summary>
        private void EmitRoadDecals(List<Dictionary<string, object>> result, List<float[]> centerNodes, float halfWidth, BeamNGRoadInput road, ref int roadIndex)
        {
            roadIndex++;
            var isDirt = road.IsDirt;
            // Self-contained materials shipped in this level (see WriteRoadMaterials): materials of
            // other official levels resolve their textures from those levels' packages, which are
            // not mounted here, and would render plain white.
            // A DecalRoad node is [x, y, z, full width]: passing the half width made every road
            // come out at half its intended size
            // Every carriageway gets its own priority. Two decals that share one have no defined
            // order, and where the stitched ends overlap at a junction the pair tore into a mosaic
            // of triangles from the two ribbons. The game's own levels do the same: on east coast
            // usa a single asphalt material is spread across priorities 3 to 50.
            var surface = MakeDecalRoad(ToNodes(centerNodes, 0f, halfWidth * 2f), isDirt ? "grm_road_dirt" : "grm_road_asphalt",
                $"road_{roadIndex}", 10 + (roadIndex % 40), isDirt ? 10 : 12);
            // Drivability puts the road in the AI navigation graph, which enables traffic and GPS
            surface["drivability"] = road.Drivability;
            surface["speedLimit"] = road.SpeedLimit;
            surface["oneWay"] = false;
            surface["improvedSpline"] = true;
            if (road.IsBridge || road.ProjectOverObjects)
            {
                // Project the surface onto the deck object, not only onto the terrain below it.
                // Deliberately separate from IsBridge: passing near a bridge must change how the
                // decal is projected and nothing else. Driving that flag from proximity also put
                // the whole segment through the bridge terrain pass, which cuts the ground down to
                // a straight chord between its two ends -- on rolling relief that chord runs tens
                // of metres underground and digs a trench. Measured on Malden: a 50 m step seven
                // metres from road_48, whose nearest bridge was 527 m away.
                surface["overObjects"] = true;
            }
            result.Add(surface);

            // Center dashed white line on wide asphalt roads (2-lane)
            if (!isDirt && halfWidth >= 3f)
            {
                // DecalRoads are drawn in descending renderPriority, so the marking needs a LOWER
                // number than the road to end up on top of it. At 15 against the road's 10 the
                // asphalt was painted over the line, and it only showed up from far away.
                // Kept under 10 so a marking still beats every carriageway, and varied for the same
                // reason the carriageways are: two markings crossing at a junction tear as well.
                var line = MakeDecalRoad(ToNodes(centerNodes, 0f, 0.16f), "grm_line_white",
                    $"road_{roadIndex}_center", 1 + (roadIndex % 8), 8);
                line["drivability"] = -1; // markings must stay out of the AI graph
                result.Add(line);
            }
        }

        /// <summary>
        /// Gives every pair of decals that overlap a different draw priority.
        /// </summary>
        /// <remarks>
        /// Spreading the priorities over a fixed cycle got the clashes from eighty six down to one,
        /// which is exactly what a modulo does: two roads whose indices happen to differ by the
        /// length of the cycle land back on the same number. Colouring the overlap graph instead
        /// leaves none, and costs nothing at this size.
        /// <para>
        /// Markings keep their own band below the carriageways. A lower number draws on top, so any
        /// value under ten beats every carriageway whatever it was given.
        /// </para>
        /// </remarks>
        private static void AssignDecalPriorities(List<Dictionary<string, object>> decals)
        {
            const int CarriagewayFloor = 10;
            const int MarkingFloor = 1;
            const int MarkingCeiling = 9;

            var carriageways = decals.Where(d => d.TryGetValue("material", out var m)
                                                 && ((string)m).StartsWith("grm_road", StringComparison.Ordinal)).ToList();
            var markings = decals.Where(d => d.TryGetValue("material", out var m)
                                             && ((string)m).StartsWith("grm_line", StringComparison.Ordinal)).ToList();

            Colour(carriageways, CarriagewayFloor, 40);
            Colour(markings, MarkingFloor, MarkingCeiling - MarkingFloor + 1);

            static void Colour(List<Dictionary<string, object>> group, int floor, int band)
            {
                var boxes = group.Select(Box).ToList();
                var assigned = new int[group.Count];
                for (var i = 0; i < group.Count; i++)
                {
                    var taken = new HashSet<int>();
                    for (var j = 0; j < i; j++)
                    {
                        if (Intersects(boxes[i], boxes[j]))
                        {
                            taken.Add(assigned[j]);
                        }
                    }
                    // Start each road at its own place in the band rather than all at the floor.
                    // Colouring alone put 164 of Malden's 216 carriageways on priority 10, and two
                    // that merely cross in open road never register as neighbours, so they kept the
                    // same number and tore into each other. Spreading first and resolving clashes
                    // afterwards gives both: neighbours always differ, and strangers rarely match.
                    var priority = floor + (i % band);
                    for (var step = 0; step < band && taken.Contains(priority); step++)
                    {
                        priority = floor + ((priority - floor + 1) % band);
                    }
                    assigned[i] = priority;
                    group[i]["renderPriority"] = priority;
                }
            }

            // Bounding box of a ribbon, widened by its own half width. Two roads that cross in the
            // middle overlap just as surely as two that meet end to end, and the endpoint test this
            // replaced could not see it.
            static (float MinX, float MinY, float MaxX, float MaxY) Box(Dictionary<string, object> decal)
            {
                var nodes = (List<object[]>)decal["nodes"];
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                var half = 0f;
                foreach (var node in nodes)
                {
                    var x = Convert.ToSingle(node[0]);
                    var y = Convert.ToSingle(node[1]);
                    minX = MathF.Min(minX, x);
                    minY = MathF.Min(minY, y);
                    maxX = MathF.Max(maxX, x);
                    maxY = MathF.Max(maxY, y);
                    half = MathF.Max(half, Convert.ToSingle(node[3]) / 2f);
                }
                return (minX - half, minY - half, maxX + half, maxY + half);
            }

            static bool Intersects((float MinX, float MinY, float MaxX, float MaxY) a,
                                   (float MinX, float MinY, float MaxX, float MaxY) b)
            {
                return a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;
            }
        }

        /// <summary>
        /// Emit one road chunk. A bridge gets a level deck and a visible concrete slab underneath,
        /// instead of a road draped into the valley it is supposed to cross.
        /// </summary>
        private void EmitRoadChunk(List<Dictionary<string, object>> result, List<float[]> nodes, float halfWidth, BeamNGRoadInput road, ref int roadIndex)
        {
            if (road.IsBridge)
            {
                FlattenBridgeDeck(nodes);
                if (!QueueArmaBridge(nodes, halfWidth))
                {
                    AddBridgeDeck(nodes, halfWidth);
                }
            }
            EmitRoadDecals(result, nodes, halfWidth, road, ref roadIndex);
        }

        /// <summary>One Arma bridge span to place: where, facing where, and which model.</summary>
        private readonly record struct ArmaBridgeSpan(string Model, float X, float Y, float Z, float Heading);

        private readonly List<ArmaBridgeSpan> armaBridgeSpans = new();

        /// <summary>
        /// Arma's own bridge decks, laid end to end along the crossing.
        /// </summary>
        /// <remarks>
        /// A real bridge is built from spans and so are Arma's models, each a fixed length: 44 m for
        /// the highway deck, 35 m for the small one. Stretching a single model to fit would stretch
        /// its piers and its parapet with it, and picking the nearest length would leave the deck
        /// hanging short or running past the bank. Repeating the span is both what a bridge actually
        /// is and the only way to keep the model's own proportions.
        /// <para>
        /// Returns false when the library has nothing suitable, and the generated slab is used
        /// instead, so a map without a ported bridge still gets a crossing.
        /// </para>
        /// </remarks>
        private bool QueueArmaBridge(List<float[]> nodes, float halfWidth)
        {
            var model = ChooseBridgeModel(halfWidth);
            if (model == null)
            {
                return false;
            }
            var (spanLength, deck) = model.Value.Span;

            // Cumulative length along the crossing
            var marks = new float[nodes.Count];
            for (var i = 1; i < nodes.Count; i++)
            {
                var dx = nodes[i][0] - nodes[i - 1][0];
                var dy = nodes[i][1] - nodes[i - 1][1];
                marks[i] = marks[i - 1] + MathF.Sqrt((dx * dx) + (dy * dy));
            }
            var total = marks[^1];
            if (total < 1f)
            {
                return false;
            }

            // At least one span, whatever the crossing measures. Requiring whole spans to fit was
            // the first rule and it placed almost nothing: the models are 34 m and 44 m long while
            // most road bridges are ten to thirty, so every one of them fell through to the plain
            // slab. A short crossing gets a single deck, overhanging its banks the way a real short
            // bridge does; a long one gets as many as it takes, spread evenly so the joins land at
            // regular intervals instead of leaving a stub at the far end.
            var count = Math.Max(1, (int)MathF.Round(total / spanLength));
            for (var n = 0; n < count; n++)
            {
                var along = total * (n + 0.5f) / count;
                var segment = 1;
                while (segment < nodes.Count - 1 && marks[segment] < along)
                {
                    segment++;
                }
                var span = marks[segment] - marks[segment - 1];
                var t = span < 0.01f ? 0f : (along - marks[segment - 1]) / span;
                var ax = nodes[segment - 1][0];
                var ay = nodes[segment - 1][1];
                var bx = nodes[segment][0];
                var by = nodes[segment][1];
                armaBridgeSpans.Add(new ArmaBridgeSpan(
                    model.Value.Name,
                    ax + ((bx - ax) * t),
                    ay + ((by - ay) * t),
                    nodes[segment - 1][2] + ((nodes[segment][2] - nodes[segment - 1][2]) * t) - deck,
                    MathF.Atan2(by - ay, bx - ax)));
            }
            return true;
        }

        /// <summary>
        /// The ported bridge whose deck is wide enough for the road, with its span and deck height.
        /// </summary>
        private (string Name, (float Length, float Deck) Span)? ChooseBridgeModel(float halfWidth)
        {
            if (string.IsNullOrEmpty(modelLibraryDirectory))
            {
                return null;
            }
            // Narrowest first, so the smallest deck that takes the carriageway wins. Ordered by
            // what a road of that width would really carry: a plank bridge over a ditch, an asphalt
            // span for an ordinary road, a full deck for a wide one. The 24 m asphalt module beats
            // the 34 m and 44 m decks used first, because a road bridge is usually shorter than
            // either and a span that overhangs its banks by ten metres looks like what it is.
            foreach (var name in new[]
            {
                "bridgewooden_01_f",
                "Bridge_Asphalt_02_center_F",
                "Bridge_Asphalt_F",
                "bridge_highway_f",
            })
            {
                if (!bridgeModels.TryGetValue(name, out var span))
                {
                    var file = Path.Combine(modelLibraryDirectory, "dae", name + ".dae");
                    if (!File.Exists(file))
                    {
                        bridgeModels[name] = null;
                        continue;
                    }
                    var dae = File.ReadAllText(file);
                    var box = ShapeFootprint(dae);
                    // The deck runs along the model's own Y, so the long half extent is the span
                    var length = Math.Max(box.HalfLength, box.HalfWidth) * 2f;
                    var across = Math.Min(box.HalfLength, box.HalfWidth) * 2f;
                    span = (length, DeckOffset(dae), across);
                    bridgeModels[name] = span;
                }
                if (span != null && span.Value.Across >= halfWidth * 2f && span.Value.Length > 5f)
                {
                    return (name, (span.Value.Length, span.Value.Deck));
                }
            }
            return null;
        }

        private readonly Dictionary<string, (float Length, float Deck, float Across)?> bridgeModels = new(StringComparer.OrdinalIgnoreCase);

        // One entry per bridge span, so each one becomes its own movable object in the editor
        private readonly List<List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle>> bridgeSpans = new();

        /// <summary>
        /// Concrete slab and parapets under a bridge span, in level coordinates.
        /// </summary>
        private void AddBridgeDeck(List<float[]> nodes, float halfWidth)
        {
            const float thickness = 0.9f;
            const float parapet = 0.7f;
            var bridgeTriangles = new List<GameRealisticMap.ManMade.Buildings.SwissBuildings3dDownloader.MeshTriangle>();
            for (var i = 1; i < nodes.Count; i++)
            {
                var a = nodes[i - 1];
                var b = nodes[i];
                var dx = b[0] - a[0];
                var dy = b[1] - a[1];
                var length = MathF.Sqrt(dx * dx + dy * dy);
                if (length < 0.01f)
                {
                    continue;
                }
                var nx = -dy / length * halfWidth;
                var ny = dx / length * halfWidth;

                void Quad(System.Numerics.Vector3 p0, System.Numerics.Vector3 p1, System.Numerics.Vector3 p2, System.Numerics.Vector3 p3)
                {
                    bridgeTriangles.Add(new(p0, p1, p2));
                    bridgeTriangles.Add(new(p0, p2, p3));
                }

                var la = new System.Numerics.Vector3(a[0] - nx, a[1] - ny, a[2]);
                var ra = new System.Numerics.Vector3(a[0] + nx, a[1] + ny, a[2]);
                var lb = new System.Numerics.Vector3(b[0] - nx, b[1] - ny, b[2]);
                var rb = new System.Numerics.Vector3(b[0] + nx, b[1] + ny, b[2]);
                var down = new System.Numerics.Vector3(0, 0, -thickness);
                // Slab: underside plus both flanks
                Quad(la + down, ra + down, rb + down, lb + down);
                Quad(la, la + down, lb + down, lb);
                Quad(ra, rb, rb + down, ra + down);
                // Parapets along each side
                var up = new System.Numerics.Vector3(0, 0, parapet);
                Quad(la, lb, lb + up, la + up);
                Quad(ra, ra + up, rb + up, rb);
            }
            if (bridgeTriangles.Count > 0)
            {
                bridgeSpans.Add(bridgeTriangles);
            }
        }

        /// <summary>
        /// Interpolate the deck height linearly between both ends, so a bridge spans the gap
        /// instead of diving into the river or valley it crosses.
        /// </summary>
        private static void FlattenBridgeDeck(List<float[]> nodes)
        {
            if (nodes.Count < 3)
            {
                return;
            }
            var lengths = new float[nodes.Count];
            var total = 0f;
            for (var i = 1; i < nodes.Count; i++)
            {
                var dx = nodes[i][0] - nodes[i - 1][0];
                var dy = nodes[i][1] - nodes[i - 1][1];
                total += MathF.Sqrt(dx * dx + dy * dy);
                lengths[i] = total;
            }
            if (total <= 0f)
            {
                return;
            }
            var startZ = nodes[0][2];
            var endZ = nodes[^1][2];
            for (var i = 1; i < nodes.Count - 1; i++)
            {
                var t = lengths[i] / total;
                // Keep the deck above the terrain it crosses
                nodes[i][2] = MathF.Max(nodes[i][2], startZ + (endZ - startZ) * t);
            }
        }

        /// <summary>
        /// Road textures and materials generated into the level itself, so roads never depend on
        /// assets owned by another level.
        /// </summary>
        private void WriteRoadMaterials(ZipArchive zip, string basePath)
        {
            // Lighter than real tarmac on purpose: the satellite image already darkens the road,
            // and a road that starts at 58 grey ends up reading as a black stripe.
            WriteNoisePng(zip, $"{basePath}/art/roads/asphalt.png", 512, new Rgba32(78, 78, 82, 255), 26);
            WriteNoisePng(zip, $"{basePath}/art/roads/dirt.png", 512, new Rgba32(122, 101, 78, 255), 26);
            WriteDashedLinePng(zip, $"{basePath}/art/roads/line_white.png", 64, 512);

            var materials = new Dictionary<string, object>
            {
                ["grm_road_asphalt"] = RoadMaterial("grm_road_asphalt", $"levels/{levelName}/art/roads/asphalt.png", "ASPHALT", false),
                ["grm_road_dirt"] = RoadMaterial("grm_road_dirt", $"levels/{levelName}/art/roads/dirt.png", "DIRT", false),
                ["grm_line_white"] = RoadMaterial("grm_line_white", $"levels/{levelName}/art/roads/line_white.png", "SOLID_LINE", true),
            };
            WriteJson(zip, $"{basePath}/art/roads/main.materials.json", materials);
        }

        private static Dictionary<string, object> RoadMaterial(string name, string texture, string annotation, bool transparent)
        {
            var stage = new Dictionary<string, object>
            {
                ["colorMap"] = texture,
                ["specularPower"] = 1,
                ["useAnisotropic"] = true,
            };
            var material = new Dictionary<string, object>
            {
                ["name"] = name,
                ["mapTo"] = name,
                ["class"] = "Material",
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["Stages"] = new object[] { stage, new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>() },
                ["annotation"] = annotation,
                ["materialTag0"] = "beamng",
                ["materialTag1"] = "Road",
                // Without this the road turns into a mirror and blows out to pure white in daylight,
                // with only the grain of its own texture showing through. specularPower 1 is not the
                // problem in itself, it is the most common value in the game's own materials, but
                // every one of those pairs it with a specular strength of zero. Written as a string
                // because that is how all 192 vanilla materials that set it are written.
                ["specularStrength0"] = "0",
            };
            if (transparent)
            {
                material["translucent"] = true;
                material["translucentBlendOp"] = "LerpAlpha";
                material["alphaTest"] = false;
                material["alphaRef"] = 0;
            }
            return material;
        }

        /// <summary>
        /// A tiling road surface: fractal value noise, not per pixel noise.
        /// </summary>
        /// <remarks>
        /// Single pixel noise averages straight back to a flat colour as soon as the mip chain kicks
        /// in, which is why the asphalt read as plain black from a few metres away: measured over a
        /// 32x32 downsample it held six levels of contrast out of 255, against forty eight for the
        /// dirt. Stacking octaves puts variation at the scale of aggregate, patches and repairs, and
        /// that is coarse enough to survive minification.
        /// </remarks>
        private static void WriteNoisePng(ZipArchive zip, string entryName, int size, Rgba32 baseColor, int noise)
        {
            var seed = entryName.GetHashCode();
            var field = new float[size * size];
            var amplitude = 1f;
            var total = 0f;
            for (var period = 4; period <= size / 4; period *= 2)
            {
                AddNoiseOctave(field, size, period, amplitude, seed ^ period);
                total += amplitude;
                amplitude *= 0.62f;
            }
            // A little single pixel grain on top, so the surface is not glassy under the wheels
            var random = new Random(seed);
            var scale = total > 0f ? 1f / total : 1f;

            using var image = new Image<Rgba32>(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var delta = (field[(y * size) + x] * scale * noise)
                        + (random.Next(-noise, noise + 1) * 0.35f);
                    image[x, y] = new Rgba32(
                        (byte)Math.Clamp(baseColor.R + delta, 0, 255),
                        (byte)Math.Clamp(baseColor.G + delta, 0, 255),
                        (byte)Math.Clamp(baseColor.B + delta, 0, 255),
                        255);
                }
            }
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        /// <summary>
        /// Adds one octave of tileable value noise: a <paramref name="period"/> square lattice of
        /// random values, bilinearly interpolated and wrapping at the edges so the texture still
        /// tiles seamlessly.
        /// </summary>
        private static void AddNoiseOctave(float[] field, int size, int period, float amplitude, int seed)
        {
            var random = new Random(seed);
            var lattice = new float[period * period];
            for (var i = 0; i < lattice.Length; i++)
            {
                lattice[i] = (float)((random.NextDouble() * 2d) - 1d);
            }

            var step = (float)size / period;
            for (var y = 0; y < size; y++)
            {
                var gy = y / step;
                var y0 = (int)gy;
                var fy = gy - y0;
                fy = fy * fy * (3f - (2f * fy)); // smoothstep, so the lattice does not show as a grid
                var y1 = (y0 + 1) % period;
                y0 %= period;
                for (var x = 0; x < size; x++)
                {
                    var gx = x / step;
                    var x0 = (int)gx;
                    var fx = gx - x0;
                    fx = fx * fx * (3f - (2f * fx));
                    var x1 = (x0 + 1) % period;
                    x0 %= period;

                    var top = (lattice[(y0 * period) + x0] * (1f - fx)) + (lattice[(y0 * period) + x1] * fx);
                    var bottom = (lattice[(y1 * period) + x0] * (1f - fx)) + (lattice[(y1 * period) + x1] * fx);
                    field[(y * size) + x] += ((top * (1f - fy)) + (bottom * fy)) * amplitude;
                }
            }
        }

        /// <summary>
        /// Transparent strip with a white dash in the middle, tiled along the road by textureLength.
        /// </summary>
        private static void WriteDashedLinePng(ZipArchive zip, string entryName, int width, int height)
        {
            using var image = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 0));
            var dashStart = height / 4;
            var dashEnd = height * 3 / 4;
            for (var y = dashStart; y < dashEnd; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    image[x, y] = new Rgba32(245, 245, 245, 255);
                }
            }
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        /// <summary>
        /// Convert center nodes into DecalRoad nodes [x, y, z, halfWidth], laterally offset by
        /// <paramref name="offset"/> meters (perpendicular to the local road direction).
        /// </summary>
        private static List<object[]> ToNodes(List<float[]> centerNodes, float offset, float halfWidth)
        {
            var nodes = new List<object[]>(centerNodes.Count);
            for (var i = 0; i < centerNodes.Count; i++)
            {
                var current = centerNodes[i];
                var x = current[0];
                var y = current[1];
                if (offset != 0f)
                {
                    var previous = centerNodes[Math.Max(0, i - 1)];
                    var next = centerNodes[Math.Min(centerNodes.Count - 1, i + 1)];
                    var dx = next[0] - previous[0];
                    var dy = next[1] - previous[1];
                    var length = MathF.Sqrt(dx * dx + dy * dy);
                    if (length > 0.001f)
                    {
                        x += -dy / length * offset;
                        y += dx / length * offset;
                    }
                }
                nodes.Add(new object[]
                {
                    MathF.Round(x, 3),
                    MathF.Round(y, 3),
                    MathF.Round(current[2], 3),
                    halfWidth
                });
            }
            return nodes;
        }

        private static Dictionary<string, object> MakeDecalRoad(List<object[]> nodes, string material, string name, int renderPriority, double textureLength)
        {
            var decal = Item("DecalRoad", name, "Decal_Roads");
            decal["position"] = new object[] { nodes[0][0], nodes[0][1], nodes[0][2] };
            decal["improvedSpline"] = true;
            decal["material"] = material;
            decal["nodes"] = nodes;
            decal["breakAngle"] = 1.0;
            decal["renderPriority"] = renderPriority;
            decal["textureLength"] = textureLength;
            decal["startEndFade"] = new[] { 1, 1 };
            decal["detail"] = 0.1;
            return decal;
        }

        // ── Surface layer map ──────────────────────────────────────────────────

        /// <summary>
        /// Rasterize the road network into the layer map (asphalt or gravel at real width),
        /// so that roads get road physics whatever the ground below is painted with.
        /// </summary>
        private void BurnRoadsIntoLayerMap(byte[] layerMap, int size)
        {
            if (roads == null || roads.Count == 0)
            {
                return;
            }
            // Pixel value = layer index + 1, 0 = untouched. Same axes as the layer map
            // (x east, row y south to north) — only read back by the loop below.
            using var image = new Image<L8>(size, size);
            var options = new DrawingOptions
            {
                GraphicsOptions = new GraphicsOptions { Antialias = false }
            };
            image.Mutate(ctx =>
            {
                foreach (var road in roads)
                {
                    var points = road.Points;
                    if (points == null || points.Count < 2)
                    {
                        continue;
                    }
                    var layer = (byte)(road.IsDirt ? 3 : 1); // gravel or asphalt
                    var widthPixels = Math.Max(1f, road.Width / cellSize);
                    var pen = Pens.Solid(Color.FromPixel(new L8((byte)(layer + 1))), widthPixels);
                    var line = points.Select(p => new PointF(p.X / cellSize, p.Y / cellSize)).ToArray();
                    ctx.DrawLine(options, pen, line);
                }
            });
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var value = image[x, y].PackedValue;
                    if (value > 0)
                    {
                        layerMap[y * size + x] = (byte)(value - 1);
                    }
                }
            }
        }

        private async Task<byte[]?> BuildLayerMap(int size)
        {
            if (idMap == null || materials == null)
            {
                return null;
            }
            var known = new Dictionary<Rgb24, byte>();
            foreach (var definition in materials)
            {
                known[definition.Material.Id] = ClassifyMaterial(definition.Material.ColorTexture);
            }
            using var small = await DownscaleHugeImage(idMap, size, true).ConfigureAwait(false);
            var layerMap = new byte[size * size];
            var cache = new Dictionary<Rgba32, byte>();
            for (var y = 0; y < size; y++)
            {
                var imgY = size - 1 - y; // PNG row 0 is north, layer map row 0 is south
                for (var x = 0; x < size; x++)
                {
                    var color = small[x, imgY];
                    if (!cache.TryGetValue(color, out var layer))
                    {
                        var rgb = new Rgb24(color.R, color.G, color.B);
                        if (!known.TryGetValue(rgb, out layer))
                        {
                            layer = NearestKnown(known, rgb);
                        }
                        cache[color] = layer;
                    }
                    layerMap[y * size + x] = layer;
                }
            }
            return layerMap;
        }

        private static byte NearestKnown(Dictionary<Rgb24, byte> known, Rgb24 color)
        {
            var best = (byte)0;
            var bestDistance = int.MaxValue;
            foreach (var (candidate, layer) in known)
            {
                var dr = candidate.R - color.R;
                var dg = candidate.G - color.G;
                var db = candidate.B - color.B;
                var distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = layer;
                }
            }
            return best;
        }

        private static byte ClassifyMaterial(string colorTexture)
        {
            var name = colorTexture.ToLowerInvariant();
            if (name.Contains("asphalt") || name.Contains("concrete") || name.Contains("tarmac") || name.Contains("road")) return 1;
            if (name.Contains("gravel") || name.Contains("pebble")) return 3;
            if (name.Contains("sand") || name.Contains("beach")) return 4;
            if (name.Contains("rock") || name.Contains("stone") || name.Contains("cliff")) return 5;
            if (name.Contains("mud") || name.Contains("wet")) return 6;
            if (name.Contains("forest") || name.Contains("leaf") || name.Contains("needle") || name.Contains("pine")) return 2;
            if (name.Contains("soil") || name.Contains("dirt") || name.Contains("earth") || name.Contains("dry")) return 2;
            return 0; // grass
        }

        // ── Shared primitives ──────────────────────────────────────────────────

        private static Dictionary<string, object> Item(string cls, string name, string? parent)
        {
            var item = new Dictionary<string, object>();
            if (parent != null)
            {
                item["__parent"] = parent;
            }
            item["class"] = cls;
            item["name"] = name;
            item["persistentId"] = Guid.NewGuid().ToString();
            return item;
        }

        private static Dictionary<string, object> PondWaterBlock(BeamNGPond pond, float floor, float half, int index)
        {
            var c = MathF.Cos(pond.YawRad);
            var s = MathF.Sin(pond.YawRad);
            var water = Item("WaterBlock", $"lake_{index}", "Water");
            // Position z is the water surface (top of the block)
            water["position"] = new object[] { MathF.Round(pond.X - half, 3), MathF.Round(pond.Y - half, 3), MathF.Round(pond.SurfaceZ - floor, 3) };
            water["rotationMatrix"] = new object[] { MathF.Round(c, 6), MathF.Round(s, 6), 0, MathF.Round(-s, 6), MathF.Round(c, 6), 0, 0, 0, 1 };
            water["scale"] = new object[] { MathF.Round(pond.Size + 0.5f, 3), MathF.Round(pond.Size + 0.5f, 3), 3 };
            water["cubemap"] = "cubemap_ocean_reflection";
            water["depthGradientTex"] = "/levels/italy/art/water/depthcolor_ramp_italy_muddy.png";
            water["foamTex"] = "levels/italy/art/water/foam2.dds";
            water["rippleTex"] = "/levels/italy/art/water/ripple.dds";
            water["baseColor"] = new[] { 253, 254, 254, 0 };
            water["clarity"] = 0.3;
            water["depthGradientMax"] = 10;
            water["fresnelBias"] = -0.1;
            water["fresnelPower"] = 0.8;
            water["reflectivity"] = 0.25;
            water["specularPower"] = 210;
            water["underwaterColor"] = new[] { 70, 160, 170, 253 };
            water["viscosity"] = 0.001;
            water["waterFogDensity"] = 1.2;
            water["waterFogDensityOffset"] = 0.1;
            return water;
        }

        private static Dictionary<string, object> SeaWaterPlane(float seaLevelZ)
        {
            var water = Item("WaterPlane", "ocean", "Water");
            water["position"] = new object[] { 0, 0, MathF.Round(seaLevelZ, 3) };
            water["cubemap"] = "cubemap_ocean_reflection";
            water["depthGradientTex"] = "/levels/italy/art/water/depthcolor_ramp_italy_muddy.png";
            water["foamTex"] = "levels/italy/art/water/foam2.dds";
            water["rippleTex"] = "/levels/italy/art/water/ripple.dds";
            water["Foam"] = new object[]
            {
                new Dictionary<string, object> { ["foamDir"] = new[] { 0, 1 }, ["foamSpeed"] = 0.01 },
                new Dictionary<string, object> { ["foamDir"] = new[] { 0, -1 }, ["foamOpacity"] = 5, ["foamSpeed"] = 0.01, ["foamTexScale"] = new[] { 4, 4 } },
            };
            water["Ripples (texture animation)"] = new object[]
            {
                new Dictionary<string, object> { ["rippleDir"] = new[] { 0, -1 }, ["rippleMagnitude"] = 0.5, ["rippleSpeed"] = 0.008, ["rippleTexScale"] = new[] { 12, 12 } },
                new Dictionary<string, object> { ["rippleDir"] = new[] { 0.707, 0.707 }, ["rippleMagnitude"] = 0.5, ["rippleSpeed"] = 0.05, ["rippleTexScale"] = new[] { 2, 2 } },
                new Dictionary<string, object> { ["rippleDir"] = new[] { -0.5, 0.86 }, ["rippleMagnitude"] = 0.35, ["rippleSpeed"] = 0.003, ["rippleTexScale"] = new[] { 120, 120 } },
            };
            water["Waves (vertex undulation)"] = new object[]
            {
                new Dictionary<string, object> { ["waveDir"] = new[] { 0, -1 }, ["waveMagnitude"] = 0.5, ["waveSpeed"] = 1 },
                new Dictionary<string, object> { ["waveDir"] = new[] { 0.25, 0.2 }, ["waveMagnitude"] = 0.2, ["waveSpeed"] = 2 },
                new Dictionary<string, object> { ["waveDir"] = new[] { 0.1, -0.7 }, ["waveMagnitude"] = 0.2, ["waveSpeed"] = 3 },
            };
            water["baseColor"] = new[] { 253, 254, 254, 0 };
            water["clarity"] = 0.25;
            water["depthGradientMax"] = 70;
            water["fresnelBias"] = -0.1;
            water["fresnelPower"] = 0.8;
            water["gridSize"] = 100;
            water["overallFoamOpacity"] = 3.5;
            water["overallRippleMagnitude"] = 1;
            water["overallWaveMagnitude"] = 0.15;
            water["reflectivity"] = 0.2;
            water["specularPower"] = 210;
            water["underwaterColor"] = new[] { 60, 223, 254, 253 };
            water["viscosity"] = 0.001;
            water["waterFogDensity"] = 0.8;
            water["waterFogDensityOffset"] = 0.1;
            return water;
        }

        private void WriteTer(ZipArchive zip, string entryName, int size, float floor, float range, byte[]? layerMap, string[] materialNames)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)9); // version
            writer.Write((uint)size);

            // Heightmap: row 0 = south edge; elevation grid y axis points north
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var value = (Height(x, y) - floor) / range * 65535f;
                    writer.Write((ushort)Math.Clamp(value, 0f, 65535f));
                }
            }

            if (layerMap != null)
            {
                writer.Write(layerMap);
            }
            else
            {
                var zeros = new byte[size];
                for (var y = 0; y < size; y++)
                {
                    writer.Write(zeros);
                }
            }

            writer.Write((uint)materialNames.Length);
            foreach (var name in materialNames)
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
            }
        }

        private void WriteHeightmapPng(ZipArchive zip, string entryName, int size, float floor, float range)
        {
            var outSize = Math.Min(size, HeightmapPngMaxSize);
            var scale = (float)size / outSize;
            using var image = new Image<L8>(outSize, outSize);
            for (var y = 0; y < outSize; y++)
            {
                var srcY = Math.Min(size - 1, (int)(y * scale));
                for (var x = 0; x < outSize; x++)
                {
                    var srcX = Math.Min(size - 1, (int)(x * scale));
                    // PNG row 0 is north
                    var value = (Height(srcX, size - 1 - srcY) - floor) / range * 255f;
                    image[x, y] = new L8((byte)Math.Clamp(value, 0f, 255f));
                }
            }
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            image.SaveAsPng(stream);
        }

        private void WriteBaseTextureAndPreview(ZipArchive zip, string basePath, Image<Rgba32>? baseTexture, int size, float floor, float range)
        {
            var ownTexture = false;
            if (baseTexture == null)
            {
                // No imagery: hypsometric shading so the terrain is still readable
                ownTexture = true;
                baseTexture = new Image<Rgba32>(1024, 1024);
                var scale = (float)size / 1024;
                for (var y = 0; y < 1024; y++)
                {
                    var srcY = Math.Min(size - 1, (int)(y * scale));
                    for (var x = 0; x < 1024; x++)
                    {
                        var srcX = Math.Min(size - 1, (int)(x * scale));
                        var altitude = Height(srcX, size - 1 - srcY);
                        var t = Math.Clamp((altitude - floor) / range, 0f, 1f);
                        baseTexture[x, y] = altitude < 0.1f
                            ? new Rgba32(70, 140, 160)
                            : new Rgba32((byte)(90 + t * 120), (byte)(120 + t * 80), (byte)(70 + t * 60));
                    }
                }
            }
            try
            {
                var texEntry = zip.CreateEntry($"{basePath}/art/terrains/terrain.png");
                using (var stream = texEntry.Open())
                {
                    baseTexture.SaveAsPng(stream);
                }

                using (var preview = baseTexture.Clone(c => c.Resize(PreviewSize, PreviewSize)))
                {
                    var previewEntry = zip.CreateEntry($"{basePath}/preview.png");
                    using var stream = previewEntry.Open();
                    preview.SaveAsPng(stream);
                }
            }
            finally
            {
                if (ownTexture)
                {
                    baseTexture.Dispose();
                }
            }
        }

        /// <summary>
        /// Downscale a huge image window by window (never materializes the full image).
        /// Read-only access: works with the render-on-read storages used by satmap/idmap.
        /// </summary>
        private static async Task<Image<Rgba32>> DownscaleHugeImage<TPixel>(HugeImage<TPixel> source, int targetSize, bool nearestNeighbor)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var target = new Image<Rgba32>(targetSize, targetSize);
            var srcSize = source.Size.Width;
            var scale = (double)targetSize / srcSize;
            const int windowSize = 4096;
            for (var wy = 0; wy < srcSize; wy += windowSize)
            {
                for (var wx = 0; wx < srcSize; wx += windowSize)
                {
                    var w = Math.Min(windowSize, srcSize - wx);
                    var h = Math.Min(windowSize, srcSize - wy);
                    var tx = (int)Math.Round(wx * scale);
                    var ty = (int)Math.Round(wy * scale);
                    var tw = Math.Max(1, (int)Math.Round((wx + w) * scale) - tx);
                    var th = Math.Max(1, (int)Math.Round((wy + h) * scale) - ty);
                    using var window = new Image<TPixel>(w, h);
                    var sourcePoint = new Point(wx, wy);
                    await window.MutateAsync(async ctx => await ctx.DrawHugeImageAsync(source, sourcePoint, new Point(0, 0), new Size(w, h)).ConfigureAwait(false)).ConfigureAwait(false);
                    window.Mutate(c => c.Resize(new ResizeOptions
                    {
                        Size = new Size(tw, th),
                        Sampler = nearestNeighbor ? KnownResamplers.NearestNeighbor : KnownResamplers.Bicubic,
                        Mode = ResizeMode.Stretch,
                    }));
                    target.Mutate(c => c.DrawImage(window, new Point(tx, ty), 1f));
                }
                await source.OffloadAsync().ConfigureAwait(false);
            }
            return target;
        }

        private static void WriteJson(ZipArchive zip, string entryName, object value)
        {
            WriteText(zip, entryName, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void WriteNdJson(ZipArchive zip, string entryName, params Dictionary<string, object>[] items)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.Append(JsonSerializer.Serialize(item));
                sb.Append('\n');
            }
            WriteText(zip, entryName, sb.ToString());
        }

        private static void WriteText(ZipArchive zip, string entryName, string content)
        {
            // Small entries are stored uncompressed: when deflate does not shrink a file, the
            // compressed and raw sizes are equal and BeamNG's VFS then reads the raw deflate
            // bytes as if the entry was stored, corrupting the file
            var entry = zip.CreateEntry(entryName, content.Length < 512 ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
