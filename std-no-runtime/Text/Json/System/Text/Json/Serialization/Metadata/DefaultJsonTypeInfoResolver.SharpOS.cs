// DefaultJsonTypeInfoResolver's reflection half — in place of
// DefaultJsonTypeInfoResolver.Helpers.cs and the member accessors
// (ReflectionMemberAccessor, ReflectionEmit*MemberAccessor). SharpOS cut:
// the image carries no reflection metadata, so a type's members cannot be
// discovered at run time. What upstream does with the
// System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault switch off is
// what happens here: metadata comes from a JsonSerializerContext the source
// generator writes ([JsonSerializable]). The resolver still answers for types
// with a built-in value converter, which need no members discovered
// (JsonSerializer.Serialize(42), JsonValue.Create<long>); for any other type it
// throws, naming the way out.

namespace System.Text.Json.Serialization.Metadata
{
    public partial class DefaultJsonTypeInfoResolver
    {
        internal static MemberAccessor MemberAccessor => throw ReflectionUnavailable();

        internal static void ClearMemberAccessorCaches() { }

        // The part of upstream's CreateTypeInfoCore that needs no reflection: a
        // type whose converter is a value converter (numbers, strings, dates,
        // Guid, JsonElement, JsonNode, ...) has no members to discover. Its
        // contract is the converter's. Objects, collections and anything else
        // a factory would build by MakeGenericType need a JsonSerializerContext.
        private static JsonTypeInfo CreateTypeInfoCore(Type type, JsonConverter converter, JsonSerializerOptions options)
        {
            JsonTypeInfo typeInfo = JsonTypeInfo.CreateJsonTypeInfo(type, converter, options);
            if (typeInfo.Kind != JsonTypeInfoKind.None)
                throw ReflectionUnavailable(type);

            converter.ConfigureJsonTypeInfo(typeInfo, options);
            return typeInfo;
        }

        internal static void DeterminePropertyAccessors<T>(JsonPropertyInfo<T> jsonPropertyInfo, System.Reflection.MemberInfo memberInfo, bool useNonPublicAccessors)
        {
            _ = jsonPropertyInfo;
            _ = memberInfo;
            _ = useNonPublicAccessors;
            throw ReflectionUnavailable();
        }

        private static NotSupportedException ReflectionUnavailable(Type? type = null) =>
            new NotSupportedException(
                (type is null ? "" : "JsonTypeInfo metadata for type '" + type.ToString() + "' was not provided. ") +
                "Reflection-based serialization is not available on SharpOS: give the type to a JsonSerializerContext " +
                "([JsonSerializable(typeof(...))]) and pass its JsonTypeInfo or the context to JsonSerializer.");
    }
}
