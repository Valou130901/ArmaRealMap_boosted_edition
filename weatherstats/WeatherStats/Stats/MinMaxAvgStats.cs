using System.Diagnostics;
using System.Text.Json.Serialization;

namespace WeatherStats.Stats
{
    [DebuggerDisplay("{Avg.Avg} ({Min.Avg} to {Max.Avg})")]
    public sealed class MinMaxAvgStats
    {
        [JsonConstructor]
        public MinMaxAvgStats(MinMaxAvg min, MinMaxAvg avg, MinMaxAvg max)
        {
            Min = min;
            Avg = avg;
            Max = max;
        }

        [JsonPropertyName("i")]
        public MinMaxAvg Min { get; }

        [JsonPropertyName("v")]
        public MinMaxAvg Avg { get; }

        [JsonPropertyName("a")]
        public MinMaxAvg Max { get; }

        public static MinMaxAvgStats From(IEnumerable<MinMaxAvg> values)
        {
            return new MinMaxAvgStats(
                MinMaxAvg.From(values.Select(v => v.Min)),
                MinMaxAvg.From(values.Select(v => v.Avg)),
                MinMaxAvg.From(values.Select(v => v.Max)));
        }

        public static MinMaxAvgStats From(float[] values, int slice = 24)
        {
            var result = new List<MinMaxAvg>(31);
            for (int i = 0; i < values.Length; i += slice)
            {
                result.Add(MinMaxAvg.From(values, i, slice));
            }
            return From(result);
        }

        internal static MinMaxAvgStats Average(IEnumerable<MinMaxAvgStats> enumerable)
        {
            return new MinMaxAvgStats(
                MinMaxAvg.Average(enumerable.Select(i => i.Min)),
                MinMaxAvg.Average(enumerable.Select(i => i.Avg)),
                MinMaxAvg.Average(enumerable.Select(i => i.Max)));
        }
    }
}
