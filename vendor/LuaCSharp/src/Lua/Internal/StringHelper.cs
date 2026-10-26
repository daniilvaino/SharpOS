using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
// SharpOS cut: System.Text.RegularExpressions and ToRegex (no Regex here; nothing called it).

namespace Lua.Internal;

static class StringHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> Slice(string s, int i, int j)
    {
        if (i < 0)
            i = s.Length + i + 1;
        if (j < 0)
            j = s.Length + j + 1;

        if (i < 1)
            i = 1;
        if (j > s.Length)
            j = s.Length;

        return i > j ? "" : s.AsSpan()[(i - 1)..j];
    }

    public static bool TryFromStringLiteral(
        ReadOnlySpan<char> literal,
        [NotNullWhen(true)] out string? result
    )
    {
        ValueStringBuilder builder = new(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            var c = literal[i];
            if (c is '\\' && i < literal.Length - 1)
            {
                i++;
                c = literal[i];

                switch (c)
                {
                    case '\n':
                        builder.Append('\n');
                        break;
                    case '\r':
                        builder.Append('\n');
                        // check CRLF
                        if (i + 1 < literal.Length && literal[i + 1] is '\n')
                        {
                            i++;
                        }

                        break;
                    case 'a':
                        builder.Append('\a');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'v':
                        builder.Append('\v');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '\"':
                        builder.Append('\"');
                        break;
                    case '\'':
                        builder.Append('\'');
                        break;
                    case '[':
                        builder.Append('[');
                        break;
                    case ']':
                        builder.Append(']');
                        break;
                    case 'x':
                        i++;
                        if (i >= literal.Length)
                        {
                            result = null;
                            return false;
                        }

                        c = literal[i];
                        if (IsDigit(c))
                        {
                            var start = i;
                            for (var j = 0; j < 2; j++)
                            {
                                i++;
                                if (i >= literal.Length)
                                {
                                    break;
                                }

                                c = literal[i];
                                if (!IsDigit(c))
                                {
                                    break;
                                }
                            }

                            builder.Append(
                                (char)int.Parse(literal[start..i], NumberStyles.HexNumber)
                            );
                            i--;
                        }
                        else
                        {
                            result = null;
                            return false;
                        }

                        break;
                    default:
                        if (IsNumber(c))
                        {
                            var start = i;
                            for (var j = 0; j < 3; j++)
                            {
                                i++;
                                if (i >= literal.Length)
                                {
                                    break;
                                }

                                c = literal[i];
                                if (!IsNumber(c))
                                {
                                    break;
                                }
                            }

                            builder.Append((char)int.Parse(literal[start..i]));
                            i--;
                        }
                        else
                        {
                            result = null;
                            return false;
                        }

                        break;
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        result = builder.ToString();
        return true;
    }

    public static string Escape(ReadOnlySpan<char> str)
    {
        ValueStringBuilder builder = new(str.Length);

        for (var i = 0; i < str.Length; i++)
        {
            var c = str[i];

            switch (c)
            {
                case '\a':
                    builder.Append("\\7");
                    break;
                case '\b':
                    builder.Append("\\8");
                    break;
                case '\f':
                    builder.Append("\\12");
                    break;
                case '\n':
                    builder.Append("\\\n");
                    break;
                case '\r':
                    builder.Append("\\13");
                    break;
                case '\t':
                    builder.Append("\\9");
                    break;
                case '\v':
                    builder.Append("\\11");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\"':
                    builder.Append("\\\"");
                    break;
                case '\'':
                    builder.Append("\\\'");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// toupper in the C locale: only 'a'-'z' change, every other char (including bytes 0x80-0xFF) is kept.
    /// </summary>
    public static string ToUpperAscii(string s)
    {
        var i = 0;
        while (i < s.Length && !(s[i] >= 'a' && s[i] <= 'z'))
        {
            i++;
        }

        if (i == s.Length)
        {
            return s;
        }

        return string.Create(
            s.Length,
            s,
            static (span, source) =>
            {
                for (var k = 0; k < span.Length; k++)
                {
                    var c = source[k];
                    span[k] = c >= 'a' && c <= 'z' ? (char)(c - 32) : c;
                }
            }
        );
    }

    /// <summary>
    /// tolower in the C locale: only 'A'-'Z' change, every other char (including bytes 0x80-0xFF) is kept.
    /// </summary>
    public static string ToLowerAscii(string s)
    {
        var i = 0;
        while (i < s.Length && !(s[i] >= 'A' && s[i] <= 'Z'))
        {
            i++;
        }

        if (i == s.Length)
        {
            return s;
        }

        return string.Create(
            s.Length,
            s,
            static (span, source) =>
            {
                for (var k = 0; k < span.Length; k++)
                {
                    var c = source[k];
                    span[k] = c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
                }
            }
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumber(char c)
    {
        return '0' <= c && c <= '9';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDigit(char c)
    {
        return IsNumber(c) || ('a' <= c && c <= 'f') || ('A' <= c && c <= 'F');
    }
}
