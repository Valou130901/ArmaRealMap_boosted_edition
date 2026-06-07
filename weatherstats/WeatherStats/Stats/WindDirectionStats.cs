using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WeatherStats.Stats
{
    [DebuggerDisplay("{Prevailing}")]
    public class WindDirectionStats
    {
        private static WindDirection8[] Directions = Enum.GetValues<WindDirection8>();

        [JsonConstructor]
        public WindDirectionStats(float[] probability, float[] averageSpeed)
        {
            Probability = probability;
            AverageSpeed = averageSpeed;
        }

        [JsonPropertyName("p")]
        public float[] Probability { get; }

        [JsonPropertyName("s")]
        public float[] AverageSpeed { get; }

        public float GetProbability(WindDirection8 direction)
        {
            return Probability[(int)direction];
        }

        public float GetAverageSpeed(WindDirection8 direction)
        {
            return AverageSpeed[(int)direction];
        }

        [JsonIgnore]
        public WindDirection8 Prevailing
        {
            get
            {
                var max = Probability.Max();
                return (WindDirection8)Array.IndexOf(Probability, max);
            }
        }

        public static WindDirectionStats From(float[] speed, WindDirection8[] values)
        {
            if (values.Length != speed.Length)
            {
                throw new ArgumentException();
            }
            var count = new int[Directions.Length];
            var total = new double[Directions.Length];
            for (int t = 0; t < values.Length; t++)
            {
                if (float.IsNormal(speed[t]))
                {
                    var idx = (int)values[t];
                    count[idx]++;
                    total[idx] += speed[t];
                }
            }
            var valuesCount = count.Sum();
            return new WindDirectionStats(
                count.Select(c => (float)c / valuesCount).ToArray(),
                total.Select((c, i) => count[i] > 0 ? (float)(c / count[i]) : 0).ToArray());
        }

        internal static WindDirectionStats Average(IEnumerable<WindDirectionStats> enumerable)
        {
            return new WindDirectionStats(
                    Enumerable.Range(0, 8).Select(d => enumerable.Select(i => i.Probability[d]).Average()).ToArray(),
                    Enumerable.Range(0, 8).Select(d => enumerable.Select(i => i.AverageSpeed[d]).Average()).ToArray()
                );
        }
    }
}
