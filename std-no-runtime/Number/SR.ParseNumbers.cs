// Resource strings ParseNumbers / Convert's base conversions throw with, from
// dotnet/runtime release/7.0 src/libraries/System.Private.CoreLib/src/Resources/Strings.resx (MIT).

namespace System
{
    internal static partial class SR
    {
        internal const string Arg_CannotHaveNegativeValue = "String cannot contain a minus sign if the base is not 10.";
        internal const string Arg_InvalidBase = "Invalid Base.";
        internal const string Format_EmptyInputString = "Input string was either empty or contained only whitespace.";
        internal const string Format_ExtraJunkAtEnd = "Additional non-parsable characters are at the end of the string.";
        internal const string Format_NoParsibleDigits = "Could not find any recognizable digits.";
        internal const string Overflow_NegativeUnsigned = "The string was being parsed as an unsigned number and could not have a negative sign.";
    }
}
