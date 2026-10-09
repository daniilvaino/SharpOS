// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

// SharpOS cut: every JsonValuePrimitive below lost its second argument, the
// built-in serializer converter (JsonMetadataServices.*Converter): the
// serializer is not ported, JsonValue.SharpOS.cs writes the primitives.

namespace System.Text.Json.Nodes
{
    public partial class JsonValue
    {
        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(bool value, JsonNodeOptions? options = null) => new JsonValuePrimitive<bool>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(bool? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<bool>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(byte value, JsonNodeOptions? options = null) => new JsonValuePrimitive<byte>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(byte? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<byte>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(char value, JsonNodeOptions? options = null) => new JsonValuePrimitive<char>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(char? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<char>(value.Value, options) : null;

        // SharpOS cut: Create(DateTime), Create(DateTimeOffset), Create(Guid) and their
        // nullable forms — their writers are cut from this snapshot (PROVENANCE.md).

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(decimal value, JsonNodeOptions? options = null) => new JsonValuePrimitive<decimal>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(decimal? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<decimal>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(double value, JsonNodeOptions? options = null) => new JsonValuePrimitive<double>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(double? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<double>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(short value, JsonNodeOptions? options = null) => new JsonValuePrimitive<short>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(short? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<short>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(int value, JsonNodeOptions? options = null) => new JsonValuePrimitive<int>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(int? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<int>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(long value, JsonNodeOptions? options = null) => new JsonValuePrimitive<long>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(long? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<long>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue Create(sbyte value, JsonNodeOptions? options = null) => new JsonValuePrimitive<sbyte>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue? Create(sbyte? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<sbyte>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue Create(float value, JsonNodeOptions? options = null) => new JsonValuePrimitive<float>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(float? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<float>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [return: NotNullIfNotNull(nameof(value))]
        public static JsonValue? Create(string? value, JsonNodeOptions? options = null) => value != null ? new JsonValuePrimitive<string>(value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue Create(ushort value, JsonNodeOptions? options = null) => new JsonValuePrimitive<ushort>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue? Create(ushort? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<ushort>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue Create(uint value, JsonNodeOptions? options = null) => new JsonValuePrimitive<uint>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue? Create(uint? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<uint>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue Create(ulong value, JsonNodeOptions? options = null) => new JsonValuePrimitive<ulong>(value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        [CLSCompliantAttribute(false)]
        public static JsonValue? Create(ulong? value, JsonNodeOptions? options = null) => value.HasValue ? new JsonValuePrimitive<ulong>(value.Value, options) : null;

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(JsonElement value, JsonNodeOptions? options = null) => JsonValue.CreateFromElement(ref value, options);

        /// <summary>
        ///   Initializes a new instance of the <see cref="JsonValue"/> class that contains the specified value.
        /// </summary>
        /// <param name="value">The underlying value of the new <see cref="JsonValue"/> instance.</param>
        /// <param name="options">Options to control the behavior.</param>
        /// <returns>The new instance of the <see cref="JsonValue"/> class that contains the specified value.</returns>
        public static JsonValue? Create(JsonElement? value, JsonNodeOptions? options = null) => value is JsonElement element ? JsonValue.CreateFromElement(ref element, options) : null;
    }
}
