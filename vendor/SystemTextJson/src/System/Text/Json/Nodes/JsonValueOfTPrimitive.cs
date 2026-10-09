// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;

namespace System.Text.Json.Nodes
{
    /// <summary>
    /// A JsonValue encapsulating a primitive value using a built-in converter for the type.
    /// </summary>
    internal sealed class JsonValuePrimitive<TValue> : JsonValue<TValue>
    {
        // SharpOS cut: the JsonConverter<TValue> field — the serializer is not
        // ported; JsonNodePrimitives.Write writes what the built-in converter
        // would (JsonValue.SharpOS.cs).
        private readonly JsonValueKind _valueKind;

        public JsonValuePrimitive(TValue value, JsonNodeOptions? options) : base(value, options)
        {
            Debug.Assert(TypeIsSupportedPrimitive, $"The type {typeof(TValue)} is not a supported primitive.");

            _valueKind = DetermineValueKind(value);
        }

        private protected override JsonValueKind GetValueKindCore() => _valueKind;
        internal override JsonNode DeepCloneCore() => new JsonValuePrimitive<TValue>(Value, Options);

        internal override bool DeepEqualsCore(JsonNode otherNode)
        {
            if (otherNode is JsonValue otherValue && otherValue.TryGetValue(out TValue? v))
            {
                // Because TValue is equatable and otherNode returns a matching
                // type we can short circuit the comparison in this case.
                return EqualityComparer<TValue>.Default.Equals(Value, v);
            }

            return base.DeepEqualsCore(otherNode);
        }

        public override void WriteTo(Utf8JsonWriter writer) // SharpOS cut: JsonSerializerOptions
        {
            ArgumentNullException.ThrowIfNull(writer);

            JsonNodePrimitives.Write(writer, Value);
        }
    }
}
