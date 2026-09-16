// NumberStyles, declared so BCL-shaped parsing calls compile.
//
// The values are the real ones, because a caller may combine them and compare
// them. What is NOT here is any code that honours them: parsing in this std is
// plain decimal with an optional leading sign, invariant culture, and the
// overloads that take a style ignore it. That covers NumberStyles.None and
// NumberStyles.Integer exactly and everything else approximately — hex,
// thousands separators, currency and parentheses-for-negative are not read.
//
// Ported from dotnet/runtime v8.0, System.Globalization.NumberStyles.

namespace System.Globalization
{
    [Flags]
    public enum NumberStyles
    {
        None = 0x00000000,
        AllowLeadingWhite = 0x00000001,
        AllowTrailingWhite = 0x00000002,
        AllowLeadingSign = 0x00000004,
        AllowTrailingSign = 0x00000008,
        AllowParentheses = 0x00000010,
        AllowDecimalPoint = 0x00000020,
        AllowThousands = 0x00000040,
        AllowExponent = 0x00000080,
        AllowCurrencySymbol = 0x00000100,
        AllowHexSpecifier = 0x00000200,

        Integer = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign,
        HexNumber = AllowLeadingWhite | AllowTrailingWhite | AllowHexSpecifier,
        Number = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign
                 | AllowTrailingSign | AllowDecimalPoint | AllowThousands,
        Float = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign
                | AllowDecimalPoint | AllowExponent,
        Currency = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign
                   | AllowTrailingSign | AllowParentheses | AllowDecimalPoint
                   | AllowThousands | AllowCurrencySymbol,
        Any = Float | Currency,
    }
}
