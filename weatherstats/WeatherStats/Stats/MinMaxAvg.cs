using System.Diagnostics;
using System.Text.Json.Serialization;

namespace WeatherStats.Stats
{
    [DebuggerDisplay("{Avg} ({Min} to {Max})")]
    public sealed class MinMaxAvg
    {
        [JsonConstructor]
        public MinMaxAvg(float min, float avg, float max)
        {
            Min = min;
            Avg = avg;
            Max = max;
        }

        [JsonPropertyName("i")]
        public float Min { get; }

        [JsonPropertyName("v")]
        public float Avg { get; }

        [JsonPropertyName("a")]
        public float Max { get; }

        public static MinMaxAvg From(float[] values, int start, int length)
        {
            return From(new ArraySegment<float>(values, start, length));
        }

        public static MinMaxAvg From(IEnumerable<float> values)
        {
            var count = 0;
            var total = 0d;
            var min = 0f;
            var max = 0f;
            foreach (var v in values)
            {
                if (!float.IsNaN(v))
                {
                    if (count == 0)
                    {
                        total = v;
                        min = v;
                        max = v;
                    }
                    else
                    {
                        total += v;
                        min = MathF.Min(min, v);
                        max = MathF.Max(max, v);
                    }
                    count++;
                }
            }
            return new MinMaxAvg(min, (float)(total / count), max);
        }

        public static MinMaxAvg Average(IEnumerable<MinMaxAvg> enumerable)
        {
            return new MinMaxAvg(
                enumerable.Select(i => i.Min).Average(),
                enumerable.Select(i => i.Avg).Average(),
                enumerable.Select(i => i.Max).Average());
        }
    }
}
