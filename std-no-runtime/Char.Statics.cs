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

        // BCL Char.ToString(char): upstream calls string.CreateFromChar.
        public static string ToString(char c) => new string(c, 1);

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

        // One culture, the invariant one (Globalization.cs): its casing.
        public static char ToUpper(char c, System.Globalization.CultureInfo culture)
        {
            if (culture == null) throw new ArgumentNullException(nameof(culture));
            return ToUpperInvariant(c);
        }

        public static char ToLower(char c, System.Globalization.CultureInfo culture)
        {
            if (culture == null) throw new ArgumentNullException(nameof(culture));
            return ToLowerInvariant(c);
        }

        // The Unicode category, from the BCL's tables (Globalization/CharUnicodeInfo.cs).
        public static System.Globalization.UnicodeCategory GetUnicodeCategory(char c)
            => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);

        public static System.Globalization.UnicodeCategory GetUnicodeCategory(string s, int index)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if ((uint)index >= (uint)s.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return System.Globalization.CharUnicodeInfo.GetUnicodeCategoryInternal(s, index);
        }

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

        public static bool IsAsciiLetterLower(char c) => IsBetween(c, 'a', 'z');

        public static bool IsAsciiLetterUpper(char c) => IsBetween(c, 'A', 'Z');

        public static bool IsAsciiLetterOrDigit(char c)
            => IsAsciiLetter(c) || IsAsciiDigit(c);

        public static bool IsAsciiHexDigit(char c)
            => IsAsciiDigit(c) || IsBetween((char)(c | 0x20), 'a', 'f');

        public static bool IsAsciiHexDigitUpper(char c)
            => IsAsciiDigit(c) || IsBetween(c, 'A', 'F');

        public static bool IsAsciiHexDigitLower(char c)
            => IsAsciiDigit(c) || IsBetween(c, 'a', 'f');

        public static bool IsAscii(char c) => c <= 0x7F;

        public static bool IsHighSurrogate(char c) => c >= 0xD800 && c <= 0xDBFF;

        public static bool IsLowSurrogate(char c) => c >= 0xDC00 && c <= 0xDFFF;

        public static bool IsSurrogate(char c) => c >= 0xD800 && c <= 0xDFFF;

        public static bool IsSurrogatePair(char high, char low) =>
            IsHighSurrogate(high) && IsLowSurrogate(low);

        // The (string, int) forms, as in the BCL (Char.cs): null and index checks, then the char form.
        public static bool IsHighSurrogate(string s, int index)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if ((uint)index >= (uint)s.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return IsHighSurrogate(s[index]);
        }

        public static bool IsLowSurrogate(string s, int index)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if ((uint)index >= (uint)s.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return IsLowSurrogate(s[index]);
        }

        public static bool IsSurrogate(string s, int index)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if ((uint)index >= (uint)s.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return IsSurrogate(s[index]);
        }

        public static bool IsSurrogatePair(string s, int index)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if ((uint)index >= (uint)s.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (index + 1 < s.Length)
            {
                return IsSurrogatePair(s[index], s[index + 1]);
            }

            return false;
        }

        // The (string, int) form, as in the BCL (Char.cs): a surrogate pair at index gives the
        // supplementary code point; a lone surrogate throws ArgumentException.
        public static int ConvertToUtf32(string s, int index)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (index < 0 || index >= s.Length) throw new ArgumentOutOfRangeException(nameof(index), SR.ArgumentOutOfRange_IndexMustBeLess);

            // Check if the character at index is a high surrogate.
            int temp1 = s[index] - 0xD800;
            if (temp1 >= 0 && temp1 <= 0x7ff)
            {
                // Found a surrogate char.
                if (temp1 <= 0x3ff)
                {
                    // Found a high surrogate.
                    if (index < s.Length - 1)
                    {
                        int temp2 = s[index + 1] - 0xDC00;
                        if (temp2 >= 0 && temp2 <= 0x3ff)
                        {
                            // Found a low surrogate.
                            return (temp1 * 0x400) + temp2 + 0x10000;
                        }
                        else
                        {
                            throw new ArgumentException(SR.Format(SR.Argument_InvalidHighSurrogate, index), nameof(s));
                        }
                    }
                    else
                    {
                        // Found a high surrogate at the end of the string.
                        throw new ArgumentException(SR.Format(SR.Argument_InvalidHighSurrogate, index), nameof(s));
                    }
                }
                else
                {
                    // Find a low surrogate at the character pointed by index.
                    throw new ArgumentException(SR.Format(SR.Argument_InvalidLowSurrogate, index), nameof(s));
                }
            }

            // Not a high-surrogate or low-surrogate. Generate the UTF32 value for the BMP characters.
            return (int)s[index];
        }

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
