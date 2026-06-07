using System.Numerics;

namespace WeatherStats.Test
{
    public class VariableConvertTest
    {
        [Fact]
        public void RelativeHumidity()
        {
            // Values based on https://en.wikipedia.org/wiki/Dew_point
            Assert.Equal(70.7, VariableConvert.RelativeHumidity(26, 32), 1);
            Assert.Equal(62.7, VariableConvert.RelativeHumidity(24, 32), 1);

            Assert.Equal(52.3, VariableConvert.RelativeHumidity(21, 32), 1);
            Assert.Equal(43.4, VariableConvert.RelativeHumidity(18, 32), 1);

            Assert.Equal(38.2, VariableConvert.RelativeHumidity(16, 32), 1);
            Assert.Equal(31.5, VariableConvert.RelativeHumidity(13, 32), 1);

            Assert.Equal(25.8, VariableConvert.RelativeHumidity(10, 32), 1);
            Assert.Equal(22.6, VariableConvert.RelativeHumidity(8, 32), 1);
        }

        [Fact]
        public void GetWindDirection8() 
        {
            Assert.Equal(WindDirection8.North, VariableConvert.GetWindDirection8(new Vector2(0, 1)));
            Assert.Equal(WindDirection8.South, VariableConvert.GetWindDirection8(new Vector2(0, -1)));

            Assert.Equal(WindDirection8.East, VariableConvert.GetWindDirection8(new Vector2(1, 0)));
            Assert.Equal(WindDirection8.West, VariableConvert.GetWindDirection8(new Vector2(-1, 0)));

            Assert.Equal(WindDirection8.NorthEast, VariableConvert.GetWindDirection8(new Vector2(1, 1)));
            Assert.Equal(WindDirection8.SouthEast, VariableConvert.GetWindDirection8(new Vector2(1, -1)));

            Assert.Equal(WindDirection8.NorthWest, VariableConvert.GetWindDirection8(new Vector2(-1, 1)));
            Assert.Equal(WindDirection8.SouthWest, VariableConvert.GetWindDirection8(new Vector2(-1, -1)));
        }

    }
}
