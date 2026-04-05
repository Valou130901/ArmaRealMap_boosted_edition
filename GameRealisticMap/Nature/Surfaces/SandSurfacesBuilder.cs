using GameRealisticMap.Geometries;
using OsmSharp.Tags;

namespace GameRealisticMap.Nature.Surfaces
{
    internal class SandSurfacesBuilder : BasicBuilderBase<SandSurfacesData>
    {
        protected override SandSurfacesData CreateWrapper(List<TerrainPolygon> polygons)
        {
            return new SandSurfacesData(polygons);
        }

        protected override bool IsTargeted(TagsCollectionBase tags)
        {
            switch (tags.GetValue("natural"))
            {
                case "sand":
                case "beach":
                    return true;
            }
            return tags.GetValue("surface") == "sand";
        }

        protected override IEnumerable<TerrainPolygon> GetPriority(IBuildContext context)
        {
            if (context.Options.OsmBoundaryId != null)
            {
                var oceanData = context.GetData<GameRealisticMap.Nature.Ocean.OceanData>();
                return oceanData.Land.SelectMany(p => p.InnerCrown(20f)).ToList();
            }

            return Enumerable.Empty<TerrainPolygon>();
        }
    }
}
