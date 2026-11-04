using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JsonCheckApp
{
    public enum Level { Low = 1, High = 3 }

    public sealed class Point
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class Shape
    {
        [JsonPropertyName("shape_name")]
        public string Name { get; set; } = "";

        public List<Point> Points { get; set; } = new List<Point>();

        public Dictionary<string, double> Weights { get; set; } = new Dictionary<string, double>();

        public int? Optional { get; set; }

        public Level Level { get; set; }

        public DateTime When { get; set; }

        public Guid Id { get; set; }

        public TimeSpan Span { get; set; }

        public bool[] Flags { get; set; } = Array.Empty<bool>();

        [JsonIgnore]
        public string Hidden { get; set; } = "hidden";
    }

    public sealed record Pair(string Key, int Value);

    [JsonSerializable(typeof(Shape))]
    [JsonSerializable(typeof(Pair))]
    [JsonSerializable(typeof(List<int>))]
    internal sealed partial class SampleContext : JsonSerializerContext
    {
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(Point))]
    internal sealed partial class CamelContext : JsonSerializerContext
    {
    }

    // JsonSerializer through the source generator (step224): the .NET 10 pack's
    // System.Text.Json.SourceGeneration writes SampleContext's metadata at
    // build time; there is no reflection at run time.
    internal static unsafe partial class AppEntry
    {
        private static void Serializer()
        {
            try
            {
                SerializerChecks();
            }
            catch (Exception e)
            {
                Check("serializer: " + e.GetType().ToString() + ": " + e.Message, false);
            }
        }

        private static void SerializerChecks()
        {
            var shape = new Shape
            {
                Name = "triangle",
                Points = { new Point { X = 1, Y = 2 }, new Point { X = -3, Y = 4 } },
                Weights = { ["a"] = 0.5 },
                Optional = null,
                Level = Level.High,
                When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Id = new Guid("12345678-9abc-def0-1234-56789abcdef0"),
                Span = new TimeSpan(1, 2, 3),
                Flags = new[] { true, false },
            };

            string json = JsonSerializer.Serialize(shape, SampleContext.Default.Shape);
            Check("serializer: object to JSON",
                  "{\"shape_name\":\"triangle\",\"Points\":[{\"X\":1,\"Y\":2},{\"X\":-3,\"Y\":4}],\"Weights\":{\"a\":0.5},\"Optional\":null," +
                  "\"Level\":3,\"When\":\"2024-01-02T03:04:05Z\",\"Id\":\"12345678-9abc-def0-1234-56789abcdef0\",\"Span\":\"01:02:03\",\"Flags\":[true,false]}",
                  json);

            Shape back = JsonSerializer.Deserialize(json, SampleContext.Default.Shape)!;
            Check("serializer: JSON back to the object",
                  back.Name == "triangle" && back.Points.Count == 2 && back.Points[1].X == -3 && back.Weights["a"] == 0.5
                  && back.Optional == null && back.Level == Level.High && back.When == shape.When && back.Id == shape.Id
                  && back.Span == shape.Span && back.Flags.Length == 2 && back.Flags[0] && back.Hidden == "hidden");

            Pair pair = JsonSerializer.Deserialize("{\"Key\":\"k\",\"Value\":7}", SampleContext.Default.Pair)!;
            Check("serializer: a record through its constructor", pair.Key == "k" && pair.Value == 7);

            List<int> list = JsonSerializer.Deserialize("[1, 2, 3]", SampleContext.Default.ListInt32)!;
            Check("serializer: List<int>", list.Count == 3 && list[2] == 3);

            Check("serializer: camelCase and indented options from the context",
                  "{\n  \"x\": 5,\n  \"y\": 6\n}",
                  JsonSerializer.Serialize(new Point { X = 5, Y = 6 }, CamelContext.Default.Point).Replace("\r\n", "\n"));

            var stream = new MemoryStream();
            JsonSerializer.SerializeAsync(stream, shape, SampleContext.Default.Shape).Wait();
            stream.Position = 0;
            Shape fromStream = JsonSerializer.DeserializeAsync(stream, SampleContext.Default.Shape).AsTask().Result!;
            Check("serializer: async through a stream", fromStream.Points.Count == 2 && fromStream.Id == shape.Id);

            bool threwForMissing = false;
            try
            {
                JsonSerializer.Deserialize<Shape>("{}", SampleContext.Default.Shape);
                JsonSerializer.Deserialize("{\"shape_name\": 1}", SampleContext.Default.Shape);
            }
            catch (JsonException)
            {
                threwForMissing = true;
            }
            Check("serializer: a wrong token type is JsonException", threwForMissing);

            // Without a context there is no metadata: the reflection resolver
            // is off, as upstream with IsReflectionEnabledByDefault false.
            bool refused = false;
            try
            {
                JsonSerializer.Serialize(new Point());
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            catch (NotSupportedException)
            {
                refused = true;
            }
            Check("serializer: no context, no reflection — refused with an exception", refused);
        }
    }
}
