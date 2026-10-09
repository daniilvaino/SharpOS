// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace System.Text.Json.Nodes
{
    /// <summary>
    /// Represents a mutable JSON value.
    /// </summary>
    public abstract partial class JsonValue : JsonNode
    {
        internal const string CreateUnreferencedCodeMessage = "Creating JsonValue instances with non-primitive types is not compatible with trimming. It can result in non-primitive types being serialized, which may have their members trimmed.";
        internal const string CreateDynamicCodeMessage = "Creating JsonValue instances with non-primitive types requires generating code at runtime.";

        private protected JsonValue(JsonNodeOptions? options) : base(options) { }

        /// <summary>
        ///   Tries to obtain the current JSON value and returns a value that indicates whether the operation succeeded.
        /// </summary>
        /// <remarks>
        ///   {T} can be the type or base type of the underlying value.
        ///   If the underlying value is a <see cref="JsonElement"/> then {T} can also be the type of any primitive
        ///   value supported by current <see cref="JsonElement"/>.
        ///   Specifying the <see cref="object"/> type for {T} will always succeed and return the underlying value as <see cref="object"/>.<br />
        ///   The underlying value of a <see cref="JsonValue"/> after deserialization is an instance of <see cref="JsonElement"/>,
        ///   otherwise it's the value specified when the <see cref="JsonValue"/> was created.
        /// </remarks>
        /// <seealso cref="JsonNode.GetValue{T}"></seealso>
        /// <typeparam name="T">The type of value to obtain.</typeparam>
        /// <param name="value">When this method returns, contains the parsed value.</param>
        /// <returns><see langword="true"/> if the value can be successfully obtained; otherwise, <see langword="false"/>.</returns>
        public abstract bool TryGetValue<T>([NotNullWhen(true)] out T? value);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <returns>
        ///   The new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </returns>
        /// <typeparam name="T">The type of value to create.</typeparam>
        /// <param name="value">The value to create.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [RequiresUnreferencedCode(CreateUnreferencedCodeMessage + " Use the overload that takes a JsonTypeInfo, or make sure all of the required types are preserved.")]
        [RequiresDynamicCode(CreateDynamicCodeMessage)]
        public static JsonValue? Create<T>(T? value, JsonNodeOptions? options = null)
        {
            if (value is null)
            {
                return null;
            }

            if (value is JsonNode)
            {
                ThrowHelper.ThrowArgumentException_NodeValueNotAllowed(nameof(value));
            }

            if (value is JsonElement element)
            {
                return CreateFromElement(ref element, options);
            }

            return CreateFromPrimitive(value, options);
        }

        // SharpOS cut: Create<T>(T, JsonTypeInfo<T>, JsonNodeOptions?) — JsonTypeInfo
        // is the serializer's, not ported.

        internal override bool DeepEqualsCore(JsonNode otherNode)
        {
            if (GetValueKind() != otherNode.GetValueKind())
            {
                return false;
            }

            // Fall back to slow path that converts the nodes to JsonElement.
            JsonElement thisElement = ToJsonElement(this, out JsonDocument? thisDocument);
            JsonElement otherElement = ToJsonElement(otherNode, out JsonDocument? otherDocument);
            try
            {
                return JsonElement.DeepEquals(thisElement, otherElement);
            }
            finally
            {
                thisDocument?.Dispose();
                otherDocument?.Dispose();
            }

            static JsonElement ToJsonElement(JsonNode node, out JsonDocument? backingDocument)
            {
                if (node.UnderlyingElement is { } element)
                {
                    backingDocument = null;
                    return element;
                }

                // SharpOS cut: Utf8JsonWriterCache (JsonNode.To.cs).
                var output = new System.Buffers.ArrayBufferWriter<byte>(BufferSizeDefault);
                using (var writer = new Utf8JsonWriter(output, default(JsonWriterOptions)))
                {
                    node.WriteTo(writer);
                    writer.Flush();
                }
                Utf8JsonReader reader = new(output.WrittenSpan);
                backingDocument = JsonDocument.ParseValue(ref reader);
                return backingDocument.RootElement;
            }
        }

        internal sealed override void GetPath(ref ValueStringBuilder path, JsonNode? child)
        {
            Debug.Assert(child == null);

            Parent?.GetPath(ref path, this);
        }

        // SharpOS: CreateFromTypeInfo without the type info. Built-in primitives
        // become JsonValuePrimitive<T>, as they did upstream; anything else
        // went to JsonValueCustomized<T> over the serializer (cut).
        internal static JsonValue CreateFromPrimitive<T>(T value, JsonNodeOptions? options = null)
        {
            Debug.Assert(value != null);

            if (JsonNodePrimitives.IsSupported<T>())
            {
                return new JsonValuePrimitive<T>(value, options);
            }

            throw new NotSupportedException("JsonValue.Create of a non-primitive type needs the serializer, which is not ported");
        }

        internal static JsonValue? CreateFromElement(ref readonly JsonElement element, JsonNodeOptions? options = null)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    return null;

                case JsonValueKind.Object or JsonValueKind.Array:
                    // Force usage of JsonArray and JsonObject instead of supporting those in an JsonValue.
                    ThrowHelper.ThrowInvalidOperationException_NodeElementCannotBeObjectOrArray();
                    return null;

                default:
                    return new JsonValueOfElement(element, options);
            }
        }
    }
}
