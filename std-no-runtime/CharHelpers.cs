namespace SharpOS.Std.NoRuntime
{
    // Character classification и case-conversion для ASCII-диапазона.
    // Non-ASCII символы (>0x7F) проходят через case-conversion без изменений.
    // В BCL эти методы — `char.IsDigit(c)` и т.д.; здесь доступны как
    // `CharHelpers.IsDigit(c)`. Делать их статикой на примитивном struct Char
    // можно, но требует partial-изменений в MinimalRuntime; пока оставлено
    // как отдельный helper.

    internal static class CharHelpers
    {
        public static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        public static bool IsLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        public static bool IsLetterOrDigit(char c)
        {
            return IsLetter(c) || IsDigit(c);
        }

        public static bool IsWhiteSpace(char c)
        {
            return c == ' '
                || c == '\t'
                || c == '\n'
                || c == '\r'
                || c == '\v'
                || c == '\f';
        }

        // Partial (step208): ASCII, Latin-1, Greek and Cyrillic — the
        // alphabets file names here come in; BCL's full Unicode tables
        // (CharUnicodeInfo) are not ported. Outside these ranges a letter
        // keeps its case.
        public static char ToUpperInvariant(char c)
        {
            if (c < 0x80)
                return c >= 'a' && c <= 'z' ? (char)(c - ('a' - 'A')) : c;
            if (c >= 0xE0 && c <= 0xFE && c != 0xF7) return (char)(c - 0x20);       // à..þ, not ÷
            if (c == 0xFF) return (char)0x178;                                     // ÿ
            if (c >= 0x3B1 && c <= 0x3C9 && c != 0x3C2) return (char)(c - 0x20);    // α..ω, not final ς
            if (c == 0x3C2) return (char)0x3A3;                                    // ς → Σ
            if (c >= 0x430 && c <= 0x44F) return (char)(c - 0x20);                 // а..я
            if (c >= 0x450 && c <= 0x45F) return (char)(c - 0x50);                 // ѐ..џ (ё)
            return c;
        }

        public static char ToLowerInvariant(char c)
        {
            if (c < 0x80)
                return c >= 'A' && c <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            if (c >= 0xC0 && c <= 0xDE && c != 0xD7) return (char)(c + 0x20);       // À..Þ, not ×
            if (c == 0x178) return (char)0xFF;                                     // Ÿ
            if (c >= 0x391 && c <= 0x3A9 && c != 0x3A2) return (char)(c + 0x20);    // Α..Ω
            if (c >= 0x410 && c <= 0x42F) return (char)(c + 0x20);                 // А..Я
            if (c >= 0x400 && c <= 0x40F) return (char)(c + 0x50);                 // Ѐ..Џ (Ё)
            return c;
        }
    }
}
