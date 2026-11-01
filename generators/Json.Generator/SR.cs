// SR for System.Text.Json's source generator: what the dotnet/runtime build's
// resource generator (arcade GenerateResxSource) writes, generated here from
// Resources/Strings.resx, which the project embeds under the same name.

using System.Resources;

namespace FxResources.System.Text.Json.SourceGeneration
{
    internal static class SR { }
}

namespace System
{
    internal static partial class SR
    {
        private static ResourceManager? s_resourceManager;

        internal static ResourceManager ResourceManager => s_resourceManager ??=
            new ResourceManager("FxResources.System.Text.Json.SourceGeneration.SR", typeof(FxResources.System.Text.Json.SourceGeneration.SR).Assembly);

        internal static string GetResourceString(string resourceKey) => ResourceManager.GetString(resourceKey) ?? resourceKey;

        internal static string Format(string resourceFormat, params object?[] args) => string.Format(resourceFormat, args);

        internal static string DuplicateTypeNameMessageFormat => GetResourceString("DuplicateTypeNameMessageFormat");
        internal static string DuplicateTypeNameTitle => GetResourceString("DuplicateTypeNameTitle");
        internal static string TypeNotSupportedMessageFormat => GetResourceString("TypeNotSupportedMessageFormat");
        internal static string TypeNotSupportedTitle => GetResourceString("TypeNotSupportedTitle");
        internal static string ContextClassesMustBePartialMessageFormat => GetResourceString("ContextClassesMustBePartialMessageFormat");
        internal static string ContextClassesMustBePartialTitle => GetResourceString("ContextClassesMustBePartialTitle");
        internal static string MultipleJsonConstructorAttributeFormat => GetResourceString("MultipleJsonConstructorAttributeFormat");
        internal static string MultipleJsonConstructorAttributeTitle => GetResourceString("MultipleJsonConstructorAttributeTitle");
        internal static string MultipleJsonExtensionDataAttributeFormat => GetResourceString("MultipleJsonExtensionDataAttributeFormat");
        internal static string MultipleJsonExtensionDataAttributeTitle => GetResourceString("MultipleJsonExtensionDataAttributeTitle");
        internal static string DataExtensionPropertyInvalidFormat => GetResourceString("DataExtensionPropertyInvalidFormat");
        internal static string DataExtensionPropertyInvalidTitle => GetResourceString("DataExtensionPropertyInvalidTitle");
        internal static string InitOnlyPropertyDeserializationNotSupportedTitle => GetResourceString("InitOnlyPropertyDeserializationNotSupportedTitle");
        internal static string InitOnlyPropertyDeserializationNotSupportedFormat => GetResourceString("InitOnlyPropertyDeserializationNotSupportedFormat");
        internal static string InaccessibleJsonIncludePropertiesNotSupportedTitle => GetResourceString("InaccessibleJsonIncludePropertiesNotSupportedTitle");
        internal static string InaccessibleJsonIncludePropertiesNotSupportedFormat => GetResourceString("InaccessibleJsonIncludePropertiesNotSupportedFormat");
        internal static string FastPathPolymorphismNotSupportedTitle => GetResourceString("FastPathPolymorphismNotSupportedTitle");
        internal static string FastPathPolymorphismNotSupportedMessageFormat => GetResourceString("FastPathPolymorphismNotSupportedMessageFormat");
        internal static string JsonStringEnumConverterNotSupportedTitle => GetResourceString("JsonStringEnumConverterNotSupportedTitle");
        internal static string JsonStringEnumConverterNotSupportedMessageFormat => GetResourceString("JsonStringEnumConverterNotSupportedMessageFormat");
        internal static string JsonConverterAttributeInvalidTypeTitle => GetResourceString("JsonConverterAttributeInvalidTypeTitle");
        internal static string JsonConverterAttributeInvalidTypeMessageFormat => GetResourceString("JsonConverterAttributeInvalidTypeMessageFormat");
        internal static string DerivedJsonConverterAttributesNotSupportedTitle => GetResourceString("DerivedJsonConverterAttributesNotSupportedTitle");
        internal static string DerivedJsonConverterAttributesNotSupportedMessageFormat => GetResourceString("DerivedJsonConverterAttributesNotSupportedMessageFormat");
        internal static string JsonUnsupportedLanguageVersionTitle => GetResourceString("JsonUnsupportedLanguageVersionTitle");
        internal static string JsonUnsupportedLanguageVersionMessageFormat => GetResourceString("JsonUnsupportedLanguageVersionMessageFormat");
        internal static string JsonConstructorInaccessibleTitle => GetResourceString("JsonConstructorInaccessibleTitle");
        internal static string JsonConstructorInaccessibleMessageFormat => GetResourceString("JsonConstructorInaccessibleMessageFormat");
        internal static string JsonSerializableAttributeOnNonContextTypeTitle => GetResourceString("JsonSerializableAttributeOnNonContextTypeTitle");
        internal static string JsonSerializableAttributeOnNonContextTypeMessageFormat => GetResourceString("JsonSerializableAttributeOnNonContextTypeMessageFormat");
        internal static string TypeContainsRefLikeMemberTitle => GetResourceString("TypeContainsRefLikeMemberTitle");
        internal static string TypeContainsRefLikeMemberFormat => GetResourceString("TypeContainsRefLikeMemberFormat");
    }
}
