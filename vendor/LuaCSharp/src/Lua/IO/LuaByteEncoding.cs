using System.Text;

namespace Lua.IO;

/// <summary>
/// Maps one byte to one <see cref="char"/> (0x00-0xFF) and back, so that a Lua string can carry
/// arbitrary bytes (for example UTF-8 text) with the same <c>#s</c>, <c>string.sub</c> and
/// <c>string.byte</c> results as the reference C implementation.
/// Characters above 0xFF cannot be represented and are written as '?'.
/// </summary>
sealed class LuaByteEncoding : Encoding
{
    public static LuaByteEncoding Instance { get; } = new();

    // SharpOS cut: our Encoding has no virtual array/pointer overloads, no
    // GetMaxCharCount and no string.Create. Those stay as plain methods; the
    // span and string members (and GetString/GetBytes at the end) carry the
    // overriding.
    public int GetByteCount(char[] chars, int index, int count) => count;

    public override int GetByteCount(string s) => s.Length;

    public override int GetByteCount(ReadOnlySpan<char> chars) => chars.Length;

    // Encoding's pointer defaults copy through temporary arrays. Its string,
    // encoder and decoder APIs use these overloads even when span overrides exist.
    public unsafe int GetByteCount(char* chars, int count)
    {
        if (chars == null)
            throw new ArgumentNullException(nameof(chars));
        return new ReadOnlySpan<char>(chars, count).Length;
    }

    public unsafe int GetBytes(char* chars, int charCount, byte* bytes, int byteCount)
    {
        if (chars == null)
            throw new ArgumentNullException(nameof(chars));
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        return GetBytes(new ReadOnlySpan<char>(chars, charCount), new Span<byte>(bytes, byteCount));
    }

    public int GetBytes(
        char[] chars,
        int charIndex,
        int charCount,
        byte[] bytes,
        int byteIndex
    ) => GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));

    public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        if (bytes.Length < chars.Length)
        {
            throw new ArgumentException("Destination is too small.", nameof(bytes));
        }

        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            bytes[i] = c <= 0xFF ? (byte)c : (byte)'?';
        }

        return chars.Length;
    }

    public int GetCharCount(byte[] bytes, int index, int count) => count;

    public override int GetCharCount(ReadOnlySpan<byte> bytes) => bytes.Length;

    public unsafe int GetCharCount(byte* bytes, int count)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        return new ReadOnlySpan<byte>(bytes, count).Length;
    }

    public unsafe int GetChars(byte* bytes, int byteCount, char* chars, int charCount)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (chars == null)
            throw new ArgumentNullException(nameof(chars));
        return GetChars(new ReadOnlySpan<byte>(bytes, byteCount), new Span<char>(chars, charCount));
    }

    public int GetChars(
        byte[] bytes,
        int byteIndex,
        int byteCount,
        char[] chars,
        int charIndex
    ) => GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex));

    public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        if (chars.Length < bytes.Length)
        {
            throw new ArgumentException("Destination is too small.", nameof(chars));
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return bytes.Length;
    }

    // SharpOS cut: the base's GetString(byte[], int, int) comes here.
    public override string GetString(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return new string(chars);
    }

    // SharpOS cut: abstract in our Encoding.
    public override byte[] GetBytes(string s)
    {
        var bytes = new byte[s.Length];
        GetBytes(s.AsSpan(), bytes);
        return bytes;
    }

    public override int GetMaxByteCount(int charCount) => charCount;

    public int GetMaxCharCount(int byteCount) => byteCount;
}
