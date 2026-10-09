// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Text.Json.Nodes
{
    // SharpOS cut: the JsonSerializerOptions parameter of ToJsonString and WriteTo
    // (the serializer is not ported; output is what default options give), and
    // Utf8JsonWriterCache (a [ThreadStatic] writer cache — no thread statics on
    // the app tier): each call writes into its own ArrayBufferWriter.
    public abstract partial class JsonNode
    {
        // SharpOS: JsonSerializerOptions.BufferSizeDefault.
        internal const int BufferSizeDefault = 16 * 1024;

        /// <summary>
        ///   Converts the current instance to string in JSON format.
        /// </summary>
        /// <returns>JSON representation of current instance.</returns>
        public string ToJsonString()
        {
            var output = new ArrayBufferWriter<byte>(BufferSizeDefault);
            using (var writer = new Utf8JsonWriter(output, default(JsonWriterOptions)))
            {
                WriteTo(writer);
                writer.Flush();
            }
            return JsonHelpers.Utf8GetString(output.WrittenSpan);
        }

        /// <summary>
        ///   Gets a string representation for the current value appropriate to the node type.
        /// </summary>
        /// <returns>A string representation for the current value appropriate to the node type.</returns>
        public override string ToString()
        {
            // Special case for string; don't quote it.
            if (this is JsonValue)
            {
                switch (this)
                {
                    case JsonValuePrimitive<string> jsonString:
                        return jsonString.Value;
                    case JsonValueOfElement { Value.ValueKind: JsonValueKind.String } jsonElement:
                        return jsonElement.Value.GetString()!;
                    case JsonValueOfJsonString jsonValueOfJsonString:
                        return jsonValueOfJsonString.GetValue<string>()!;
                }
            }

            var output = new ArrayBufferWriter<byte>(BufferSizeDefault);
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                WriteTo(writer);
                writer.Flush();
            }
            return JsonHelpers.Utf8GetString(output.WrittenSpan);
        }

        /// <summary>
        ///   Write the <see cref="JsonNode"/> into the provided <see cref="Utf8JsonWriter"/> as JSON.
        /// </summary>
        /// <param name="writer">The <see cref="Utf8JsonWriter"/>.</param>
        /// <exception cref="ArgumentNullException">
        ///   The <paramref name="writer"/> parameter is <see langword="null"/>.
        /// </exception>
        public abstract void WriteTo(Utf8JsonWriter writer);
    }
}
