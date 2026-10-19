// A small strict JSON parser (RFC 8259). Objects become JsonObject (last duplicate wins),
// arrays become List<object>, strings stay strings; numbers, true, false and null become
// JsonOther so that the validator can reject them with a useful message.
using System;
using System.Collections.Generic;
using System.Text;

sealed class JsonObject : Dictionary<string, object>
{
    public JsonObject() : base(StringComparer.Ordinal) { }
}

sealed class JsonOther
{
    public readonly string Kind;
    public JsonOther(string kind) { Kind = kind; }
}

sealed class JsonException : Exception
{
    public JsonException(string message) : base(message) { }
}

sealed class JsonParser
{
    readonly string s;
    int i;

    JsonParser(string text) { s = text; }

    public static object Parse(string text)
    {
        var p = new JsonParser(text);
        p.SkipWhitespace();
        var value = p.ParseValue();
        p.SkipWhitespace();
        if (p.i != p.s.Length) p.Fail("unexpected data after the JSON value");
        return value;
    }

    void Fail(string message) => throw new JsonException($"input is not valid JSON: {message} at offset {i}");

    void SkipWhitespace()
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    void Expect(char c)
    {
        if (i >= s.Length || s[i] != c) Fail($"expected '{c}'");
        i++;
    }

    object ParseValue()
    {
        if (i >= s.Length) Fail("unexpected end of input");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject();
            case '[': return ParseArray();
            case '"': return ParseString();
            case 't': ParseLiteral("true"); return new JsonOther("true");
            case 'f': ParseLiteral("false"); return new JsonOther("false");
            case 'n': ParseLiteral("null"); return new JsonOther("null");
            default:
                if (c == '-' || (c >= '0' && c <= '9')) { ParseNumber(); return new JsonOther("number"); }
                Fail($"unexpected character '{c}'");
                return null!;
        }
    }

    void ParseLiteral(string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0) Fail("invalid literal");
        i += word.Length;
    }

    void ParseNumber()
    {
        if (s[i] == '-') i++;
        if (i < s.Length && s[i] == '0') i++;
        else if (i < s.Length && s[i] >= '1' && s[i] <= '9') Digits();
        else Fail("invalid number");
        if (i < s.Length && s[i] == '.') { i++; if (!Digits()) Fail("invalid number"); }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            if (!Digits()) Fail("invalid number");
        }
    }

    bool Digits()
    {
        int start = i;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        return i > start;
    }

    JsonObject ParseObject()
    {
        var obj = new JsonObject();
        i++; // '{'
        SkipWhitespace();
        if (i < s.Length && s[i] == '}') { i++; return obj; }
        while (true)
        {
            SkipWhitespace();
            if (i >= s.Length || s[i] != '"') Fail("expected a member name");
            string name = ParseString();
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();
            obj[name] = ParseValue(); // duplicate names: the last occurrence wins
            SkipWhitespace();
            if (i < s.Length && s[i] == ',') { i++; continue; }
            Expect('}');
            return obj;
        }
    }

    List<object> ParseArray()
    {
        var list = new List<object>();
        i++; // '['
        SkipWhitespace();
        if (i < s.Length && s[i] == ']') { i++; return list; }
        while (true)
        {
            SkipWhitespace();
            list.Add(ParseValue());
            SkipWhitespace();
            if (i < s.Length && s[i] == ',') { i++; continue; }
            Expect(']');
            return list;
        }
    }

    string ParseString()
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) Fail("unterminated string");
            char c = s[i++];
            if (c == '"') break;
            if (c < 0x20) Fail("control character in string");
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) Fail("unterminated string");
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) Fail("invalid \\u escape");
                    int code = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int h = HexValue(s[i++]);
                        if (h < 0) Fail("invalid \\u escape");
                        code = code * 16 + h;
                    }
                    sb.Append((char)code);
                    break;
                default: Fail("invalid escape"); break;
            }
        }
        string result = sb.ToString();
        // §3.5: escapes may denote unpaired surrogates, which cannot be encoded as UTF-8.
        for (int k = 0; k < result.Length; k++)
        {
            if (char.IsHighSurrogate(result[k]) && k + 1 < result.Length && char.IsLowSurrogate(result[k + 1])) k++;
            else if (char.IsSurrogate(result[k])) Fail("string contains an unpaired surrogate");
        }
        return result;
    }

    static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
