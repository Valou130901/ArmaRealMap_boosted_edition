using GameRealisticMap.Configuration;

namespace GameRealisticMap
{
    public class MapProcessingOptions : IMapProcessingOptions
    {
        public static IMapProcessingOptions Default { get; } = new MapProcessingOptions();

        public MapProcessingOptions(double resolution = 1, float privateServiceRoadThreshold = 25, long? osmBoundaryId = null)
        {
            Resolution = resolution;
            PrivateServiceRoadThreshold = privateServiceRoadThreshold;
            OsmBoundaryId = osmBoundaryId;
        }

        public double Resolution { get; }

        public float PrivateServiceRoadThreshold { get; }

        public ISatelliteImageOptions Satellite => new SatelliteImageOptions();

        public long? OsmBoundaryId { get; }
    }
}
