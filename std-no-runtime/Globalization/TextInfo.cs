// System.Globalization.TextInfo as globalization-invariant mode has it.
//
// API from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos),
// src/libraries/System.Private.CoreLib/src/System/Globalization/TextInfo.cs
// (MIT). Every culture here has the invariant data (Globalization.cs), so a
// TextInfo is the invariant one under the culture's name: left to right,
// "," between list items, and casing by simple case mapping
// (InvariantModeCasing over CharUnicodeInfo), char by char, code point by
// code point across surrogate pairs — what the BCL does with
// InvariantGlobalization=true.
//
// Cuts: ToTitleCase, ANSI/OEM/EBCDIC/Mac code pages, IsReadOnly/Clone/
// ReadOnly, the span ChangeCase helpers. Recorded in
// docs/nativeaot-nostd-kernel-limits.md.

using System.Text;

namespace System.Globalization
{
    public sealed class TextInfo
    {
        private readonly string _cultureName;

        internal TextInfo(string cultureName) => _cultureName = cultureName ?? "";

        public string CultureName => _cultureName;

        public bool IsRightToLeft => false;

        public string ListSeparator => ",";

        public int LCID => 0x007F; // LOCALE_INVARIANT

        public char ToLower(char c) => CharUnicodeInfo.ToLower(c);

        public char ToUpper(char c) => CharUnicodeInfo.ToUpper(c);

        public string ToLower(string str)
        {
            ArgumentNullException.ThrowIfNull(str);
            return ChangeCase(str, toUpper: false);
        }

        public string ToUpper(string str)
        {
            ArgumentNullException.ThrowIfNull(str);
            return ChangeCase(str, toUpper: true);
        }

        private static string ChangeCase(string source, bool toUpper)
        {
            var result = new StringBuilder(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    uint codePoint = (uint)char.ConvertToUtf32(c, source[i + 1]);
                    codePoint = toUpper ? CharUnicodeInfo.ToUpper(codePoint) : CharUnicodeInfo.ToLower(codePoint);
                    result.Append(char.ConvertFromUtf32((int)codePoint));
                    i++;
                    continue;
                }
                result.Append(toUpper ? CharUnicodeInfo.ToUpper(c) : CharUnicodeInfo.ToLower(c));
            }
            return result.ToString();
        }

        public override bool Equals(object? obj) => obj is TextInfo that && CultureName == that.CultureName;

        public override int GetHashCode() => CultureName.GetHashCode();

        public override string ToString() => "TextInfo - " + _cultureName;
    }
}
