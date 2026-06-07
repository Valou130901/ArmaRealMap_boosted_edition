using System.Numerics;

namespace WeatherStats
{
    public static class VariableConvert
    {
        private const float beta = 17.625f;
        private const float lambda = 243.04f;

        public static float RelativeHumidity(float dewpointInCelcius, float temperatureInCelcius)
        {
            return MathF.Max(MathF.Min(100 * MathF.Exp(beta * dewpointInCelcius / (lambda + dewpointInCelcius)) / MathF.Exp(beta * temperatureInCelcius / (lambda + temperatureInCelcius)), 100), 0);
        }

        public static float KelvinToCelcius(float valueInKelvin)
        {
            return valueInKelvin - 273.15f;
        }

        private static readonly WindDirection8[] Directions8 = new[]
        {
            WindDirection8.West, // -4
            WindDirection8.SouthWest, // -3
            WindDirection8.South, // -2
            WindDirection8.SouthEast, // -1
            WindDirection8.East, // 0
            WindDirection8.NorthEast, // 1
            WindDirection8.North, // 2
            WindDirection8.NorthWest, // 3
            WindDirection8.West // 4
        };

        public static WindDirection8 GetWindDirection8(Vector2 windSpeed)
        {
            return Directions8[(int)MathF.Round(MathF.Atan2(windSpeed.Y, windSpeed.X) * 4 / MathF.PI) + 4];
        }
    }
}
