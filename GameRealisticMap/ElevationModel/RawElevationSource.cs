using GeoAPI.Geometries;
using Pmad.Cartography;
using Pmad.Cartography.Databases;
using Pmad.Cartography.DataCells;

namespace GameRealisticMap.ElevationModel
{
    internal class RawElevationSource
    {
        private readonly Func<Coordinates, double> surfaceOnly;
        private readonly IDemDataCell ground;

        public RawElevationSource(List<string> dbCredits, Func<Coordinates, double> view, IDemDataCell viewFull)
        {
            this.Credits = dbCredits;
            this.surfaceOnly = view;
            this.ground = viewFull;
        }

        public List<string> Credits { get; }

        internal float GetElevation(Coordinate latLong, byte oceanMaskValue)
        {
            if (oceanMaskValue == 0)
            {
                return (float)GetSurfaceElevation(latLong);
            }
            if (oceanMaskValue == 255)
            {
                return (float)GetOceanDepth(latLong);
            }
            var factor = oceanMaskValue / 255.0;
            return (float)((GetOceanDepth(latLong) * factor) + (GetSurfaceElevation(latLong) * (1 - factor)));
        }

        private double GetOceanDepth(Coordinate latLong)
        {
            var elevation = ground.GetLocalElevation(new Coordinates(latLong.Y, latLong.X), DefaultInterpolation.Instance);
            if (elevation > -1)
            {
                return -1;
            }
            return elevation;
        }

        private double GetSurfaceElevation(Coordinate latLong)
        {
            var elevation = surfaceOnly(new Coordinates(latLong.Y, latLong.X));
            if (double.IsNaN(elevation) || elevation < 0.5f)
            {
                elevation = 0.5f;
            }
            return elevation;
        }

        internal double GetElevationNoMask(Coordinate latLong)
        {
            var point = new Coordinates(latLong.Y, latLong.X);
            var detail = surfaceOnly(point);
            if (double.IsNaN(detail) || Math.Abs(detail) < 0.1)
            {
                return ground.GetLocalElevation(point, DefaultInterpolation.Instance);
            }
            return detail;
        }
    }
}