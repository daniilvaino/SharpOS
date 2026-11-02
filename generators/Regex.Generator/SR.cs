// SR for System.Text.RegularExpressions source generator: what the dotnet/runtime build's
// resource generator (arcade GenerateResxSource) writes, generated here from
// Resources/Strings.resx, which the project embeds under the same name.

using System.Resources;

namespace FxResources.System.Text.RegularExpressions.Generator
{
    internal static class SR { }
}

namespace System
{
    internal static partial class SR
    {
        private static ResourceManager? s_resourceManager;

        internal static ResourceManager ResourceManager => s_resourceManager ??=
            new ResourceManager("FxResources.System.Text.RegularExpressions.Generator.SR", typeof(FxResources.System.Text.RegularExpressions.Generator.SR).Assembly);

        internal static string GetResourceString(string resourceKey) => ResourceManager.GetString(resourceKey) ?? resourceKey;

        internal static string Format(string resourceFormat, params object?[] args) => string.Format(resourceFormat, args);

        internal static string InvalidGeneratedRegexAttributeTitle => GetResourceString("InvalidGeneratedRegexAttributeTitle");
        internal static string InvalidGeneratedRegexAttributeMessage => GetResourceString("InvalidGeneratedRegexAttributeMessage");
        internal static string MultipleGeneratedRegexAttributesMessage => GetResourceString("MultipleGeneratedRegexAttributesMessage");
        internal static string InvalidRegexArgumentsMessage => GetResourceString("InvalidRegexArgumentsMessage");
        internal static string RegexMemberMustHaveValidSignatureMessage => GetResourceString("RegexMemberMustHaveValidSignatureMessage");
        internal static string LimitedSourceGenerationTitle => GetResourceString("LimitedSourceGenerationTitle");
        internal static string LimitedSourceGenerationMessage => GetResourceString("LimitedSourceGenerationMessage");
        internal static string Generic => GetResourceString("Generic");
        internal static string AlternationHasNamedCapture => GetResourceString("AlternationHasNamedCapture");
        internal static string AlternationHasComment => GetResourceString("AlternationHasComment");
        internal static string Arg_ArrayPlusOffTooSmall => GetResourceString("Arg_ArrayPlusOffTooSmall");
        internal static string ShorthandClassInCharacterRange => GetResourceString("ShorthandClassInCharacterRange");
        internal static string BeginIndexNotNegative => GetResourceString("BeginIndexNotNegative");
        internal static string QuantifierOrCaptureGroupOutOfRange => GetResourceString("QuantifierOrCaptureGroupOutOfRange");
        internal static string CaptureGroupOfZero => GetResourceString("CaptureGroupOfZero");
        internal static string CountTooSmall => GetResourceString("CountTooSmall");
        internal static string EnumNotStarted => GetResourceString("EnumNotStarted");
        internal static string AlternationHasMalformedCondition => GetResourceString("AlternationHasMalformedCondition");
        internal static string IllegalDefaultRegexMatchTimeoutInAppDomain => GetResourceString("IllegalDefaultRegexMatchTimeoutInAppDomain");
        internal static string UnescapedEndingBackslash => GetResourceString("UnescapedEndingBackslash");
        internal static string ReversedQuantifierRange => GetResourceString("ReversedQuantifierRange");
        internal static string InvalidUnicodePropertyEscape => GetResourceString("InvalidUnicodePropertyEscape");
        internal static string CaptureGroupNameInvalid => GetResourceString("CaptureGroupNameInvalid");
        internal static string LengthNotNegative => GetResourceString("LengthNotNegative");
        internal static string MalformedNamedReference => GetResourceString("MalformedNamedReference");
        internal static string AlternationHasMalformedReference => GetResourceString("AlternationHasMalformedReference");
        internal static string MalformedUnicodePropertyEscape => GetResourceString("MalformedUnicodePropertyEscape");
        internal static string MakeException => GetResourceString("MakeException");
        internal static string MissingControlCharacter => GetResourceString("MissingControlCharacter");
        internal static string NestedQuantifiersNotParenthesized => GetResourceString("NestedQuantifiersNotParenthesized");
        internal static string NoResultOnFailed => GetResourceString("NoResultOnFailed");
        internal static string InsufficientClosingParentheses => GetResourceString("InsufficientClosingParentheses");
        internal static string NotSupported_ReadOnlyCollection => GetResourceString("NotSupported_ReadOnlyCollection");
        internal static string PlatformNotSupported_CompileToAssembly => GetResourceString("PlatformNotSupported_CompileToAssembly");
        internal static string QuantifierAfterNothing => GetResourceString("QuantifierAfterNothing");
        internal static string RegexMatchTimeoutException_Occurred => GetResourceString("RegexMatchTimeoutException_Occurred");
        internal static string ReversedCharacterRange => GetResourceString("ReversedCharacterRange");
        internal static string ExclusionGroupNotLast => GetResourceString("ExclusionGroupNotLast");
        internal static string InsufficientOrInvalidHexDigits => GetResourceString("InsufficientOrInvalidHexDigits");
        internal static string AlternationHasTooManyConditions => GetResourceString("AlternationHasTooManyConditions");
        internal static string InsufficientOpeningParentheses => GetResourceString("InsufficientOpeningParentheses");
        internal static string UndefinedNumberedReference => GetResourceString("UndefinedNumberedReference");
        internal static string UndefinedNamedReference => GetResourceString("UndefinedNamedReference");
        internal static string AlternationHasUndefinedReference => GetResourceString("AlternationHasUndefinedReference");
        internal static string UnrecognizedUnicodeProperty => GetResourceString("UnrecognizedUnicodeProperty");
        internal static string UnrecognizedControlCharacter => GetResourceString("UnrecognizedControlCharacter");
        internal static string UnrecognizedEscape => GetResourceString("UnrecognizedEscape");
        internal static string InvalidGroupingConstruct => GetResourceString("InvalidGroupingConstruct");
        internal static string UnterminatedBracket => GetResourceString("UnterminatedBracket");
        internal static string UnterminatedComment => GetResourceString("UnterminatedComment");
        internal static string UseRegexSourceGeneratorMessage => GetResourceString("UseRegexSourceGeneratorMessage");
        internal static string UseRegexSourceGeneratorTitle => GetResourceString("UseRegexSourceGeneratorTitle");
    }
}
