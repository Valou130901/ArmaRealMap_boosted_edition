using GameRealisticMap.Geometries;
using GameRealisticMap.Reporting;
using Pmad.ProgressTracking;

namespace GameRealisticMap.Nature.Ocean
{
    internal class CoastlineBuilder : IDataBuilder<CoastlineData>
    {
        public CoastlineData Build(IBuildContext context, IProgressScope scope)
        {
            var coastlines = context.OsmSource.Ways
                .Where(w => w.Tags != null && w.Tags.GetValue("natural") == "coastline")
                .SelectMany(w => context.OsmSource.Interpret(w))
                .SelectMany(w => TerrainPath.FromGeometry(w, context.Area.LatLngToTerrainPoint))
                .Where(p => p.EnveloppeIntersects(context.Area.TerrainBounds))
                .SelectMany(p => p.ClippedBy(context.Area.TerrainBounds))
                .SelectMany(p => TerrainPolygon.FromPath(p.Points, CoastlineData.Width))
                .SelectMany(p => p.ClippedBy(context.Area.TerrainBounds))
                .ToList();

            if (context.Options.OsmBoundaryId != null)
            {
                var relation = context.OsmSource.Relations.FirstOrDefault(r => r.Id == context.Options.OsmBoundaryId.Value);
                if (relation != null)
                {
                    var mapPolygons = context.OsmSource.Interpret(relation)
                        .SelectMany(g => TerrainPolygon.FromGeometry(g, context.Area.LatLngToTerrainPoint));
                        
                    foreach (var poly in mapPolygons)
                    {
                        var ringPath = new TerrainPath(poly.Shell);
                        if (ringPath.EnveloppeIntersects(context.Area.TerrainBounds))
                        {
                            var clippedPaths = ringPath.ClippedBy(context.Area.TerrainBounds);
                            foreach (var p in clippedPaths)
                            {
                                coastlines.AddRange(TerrainPolygon.FromPath(p.Points, CoastlineData.Width).SelectMany(cp => cp.ClippedBy(context.Area.TerrainBounds)));
                            }
                        }
                    }
                }
            }

            using var report = scope.CreateInteger("Merge", coastlines.Count);
            var result = TerrainPolygon.MergeAll(coastlines, report);
            return new CoastlineData(result);
        }
    }
}
