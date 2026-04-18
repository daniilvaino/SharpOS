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

        public static char ToUpperInvariant(char c)
        {
            if (c >= 'a' && c <= 'z')
                return (char)(c - ('a' - 'A'));
            return c;
        }

        public static char ToLowerInvariant(char c)
        {
            if (c >= 'A' && c <= 'Z')
                return (char)(c + ('a' - 'A'));
            return c;
        }
    }
}
