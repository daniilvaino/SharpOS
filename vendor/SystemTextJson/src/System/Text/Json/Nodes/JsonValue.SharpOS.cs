// SharpOS: what System.Text.Json.Nodes took from the serializer, without the
// serializer (not ported). Two things:
//
//   - JsonNodeConverter.Create (Serialization/Converters/Node/JsonNodeConverter.cs,
//     verbatim): a JsonElement into a JsonObject / JsonArray / JsonValue.
//   - The built-in converters of the primitive types, which JsonValuePrimitive<T>
//     used to write its value: one switch over the primitive types, writing
//     what each converter writes with default options (numbers as numbers,
//     char as a one-character string, decimal through Utf8JsonWriter's decimal
//     writer).
//
// DateTime, DateTimeOffset, TimeSpan, Guid, DateOnly, TimeOnly, Uri, Version,
// Half, Int128 have no primitive JsonValue here: their writers are cut from
// this snapshot (PROVENANCE.md), so JsonValue.Create of them is cut too.

using System.Diagnostics;

namespace System.Text.Json.Nodes
{
    internal static class JsonNodePrimitives
    {
        public static JsonNode? CreateNode(JsonElement element, JsonNodeOptions? options)
        {
            JsonNode? node;

            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    node = null;
                    break;
                case JsonValueKind.Object:
                    node = new JsonObject(element, options);
                    break;
                case JsonValueKind.Array:
                    node = new JsonArray(element, options);
                    break;
                default:
                    node = new JsonValueOfElement(element, options);
                    break;
            }

            return node;
        }

        /// <summary>Whether <typeparamref name="T"/> has a primitive JsonValue here.</summary>
        public static bool IsSupported<T>() =>
            typeof(T) == typeof(bool) || typeof(T) == typeof(char) || typeof(T) == typeof(string) ||
            typeof(T) == typeof(sbyte) || typeof(T) == typeof(byte) || typeof(T) == typeof(short) || typeof(T) == typeof(ushort) ||
            typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(long) || typeof(T) == typeof(ulong) ||
            typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T) == typeof(decimal);

        public static void Write<T>(Utf8JsonWriter writer, T value)
        {
            object boxed = value!;
            switch (boxed)
            {
                case bool v: writer.WriteBooleanValue(v); return;
                case string v: writer.WriteStringValue(v); return;
                case char v: writer.WriteStringValue(v.ToString()); return;
                case sbyte v: writer.WriteNumberValue(v); return;
                case byte v: writer.WriteNumberValue(v); return;
                case short v: writer.WriteNumberValue(v); return;
                case ushort v: writer.WriteNumberValue(v); return;
                case int v: writer.WriteNumberValue(v); return;
                case uint v: writer.WriteNumberValue(v); return;
                case long v: writer.WriteNumberValue(v); return;
                case ulong v: writer.WriteNumberValue(v); return;
                case float v: writer.WriteNumberValue(v); return;
                case double v: writer.WriteNumberValue(v); return;
                case decimal v: writer.WriteNumberValue(v); return;
            }
            Debug.Fail("not a supported primitive");
            throw new NotSupportedException("JsonValue of this type needs the serializer, which is not ported");
        }
    }
}
