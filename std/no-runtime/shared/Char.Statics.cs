// char.IsLetter(c) and friends — the spelling everyone actually writes.
//
// The classification itself has lived in CharHelpers for a long time, reachable
// only as CharHelpers.IsDigit(c). Its own header noted the obvious next step and
// why it had not been taken: putting these on the primitive Char needs it to be
// partial in each tier's MinimalRuntime. Terminal.Gui — and our own Rune code —
// call char.IsHighSurrogate and char.ToUpper the ordinary way, so the step is
// worth taking now.
//
// Behaviour is unchanged, and the limits come with it: classification is ASCII,
// and case conversion leaves everything above 0x7F alone. That is wrong for
// Cyrillic and Greek, where it silently returns the input rather than failing —
// stated here because the BCL names promise more than this delivers.

namespace System
{
    public partial struct Char
    {
        public static bool IsDigit(char c) => SharpOS.Std.NoRuntime.CharHelpers.IsDigit(c);

        public static bool IsLetter(char c) => SharpOS.Std.NoRuntime.CharHelpers.IsLetter(c);

        public static bool IsLetterOrDigit(char c) => SharpOS.Std.NoRuntime.CharHelpers.IsLetterOrDigit(c);

        public static bool IsWhiteSpace(char c) => SharpOS.Std.NoRuntime.CharHelpers.IsWhiteSpace(c);

        public static bool IsUpper(char c) => c >= 'A' && c <= 'Z';

        public static bool IsLower(char c) => c >= 'a' && c <= 'z';

        public static bool IsControl(char c) => c < 0x20 || (c >= 0x7F && c <= 0x9F);

        public static bool IsPunctuation(char c) =>
            (c >= '!' && c <= '/') || (c >= ':' && c <= '@') ||
            (c >= '[' && c <= '`') || (c >= '{' && c <= '~');

        public static bool IsSymbol(char c) =>
            c == '+' || c == '<' || c == '=' || c == '>' || c == '|' || c == '~' || c == '$' || c == '^';

        public static char ToUpper(char c) => SharpOS.Std.NoRuntime.CharHelpers.ToUpperInvariant(c);

        public static char ToLower(char c) => SharpOS.Std.NoRuntime.CharHelpers.ToLowerInvariant(c);

        public static char ToUpperInvariant(char c) => SharpOS.Std.NoRuntime.CharHelpers.ToUpperInvariant(c);

        public static char ToLowerInvariant(char c) => SharpOS.Std.NoRuntime.CharHelpers.ToLowerInvariant(c);

        // Surrogates: the two halves a codepoint above 0xFFFF is stored as in a
        // UTF-16 string. Anything walking text by index has to know about them,
        // or it will hand back half a character.
        // .NET 7 additions. Inclusive at both ends, and the ASCII ones answer
        // false for everything above 0x7F rather than consulting Unicode — the
        // BCL definitions, verbatim in behaviour.
        public static bool IsBetween(char c, char minInclusive, char maxInclusive)
            => (uint)(c - minInclusive) <= (uint)(maxInclusive - minInclusive);

        public static bool IsAsciiDigit(char c) => IsBetween(c, '0', '9');

        public static bool IsAsciiLetter(char c)
            => IsBetween((char)(c | 0x20), 'a', 'z');

        public static bool IsAsciiLetterOrDigit(char c)
            => IsAsciiLetter(c) || IsAsciiDigit(c);

        public static bool IsAsciiHexDigit(char c)
            => IsAsciiDigit(c) || IsBetween((char)(c | 0x20), 'a', 'f');

        public static bool IsAscii(char c) => c <= 0x7F;

        public static bool IsHighSurrogate(char c) => c >= 0xD800 && c <= 0xDBFF;

        public static bool IsLowSurrogate(char c) => c >= 0xDC00 && c <= 0xDFFF;

        public static bool IsSurrogate(char c) => c >= 0xD800 && c <= 0xDFFF;

        public static bool IsSurrogatePair(char high, char low) =>
            IsHighSurrogate(high) && IsLowSurrogate(low);

        public static int ConvertToUtf32(char high, char low)
        {
            if (!IsSurrogatePair(high, low))
                throw new ArgumentOutOfRangeException(nameof(high), "Not a surrogate pair.");

            return ((high - 0xD800) << 10) + (low - 0xDC00) + 0x10000;
        }

        public static string ConvertFromUtf32(int utf32)
        {
            if (utf32 < 0 || utf32 > 0x10FFFF || IsSurrogate((char)utf32))
                throw new ArgumentOutOfRangeException(nameof(utf32));

            if (utf32 < 0x10000)
                return new string(new char[] { (char)utf32 });

            utf32 -= 0x10000;
            return new string(new char[]
            {
                (char)((utf32 >> 10) + 0xD800),
                (char)((utf32 & 0x3FF) + 0xDC00),
            });
        }
    }
}
