// Partial System.Text.Encoding for the NoStdLib kernel/std environment.
//
// This is NOT the full BCL Encoding hierarchy. It provides the concrete
// encodings and the GetString/GetBytes surface that real BCL-consuming code
// (e.g. the vendored PeNet PE parser) actually calls, so such code compiles
// against our std without source edits. Cut relative to dotnet/runtime:
//
//   - EncoderFallback / DecoderFallback / *NLS state machines (invalid input
//     is mapped to a replacement char '?' / U+FFFD, never throws / never
//     surfaces a fallback buffer).
//   - EncodingProvider / EncodingInfo / code-page registry / GetEncoding(name).
//   - Preamble/BOM handling, GetEncoder/GetDecoder streaming objects.
//   - Clone / IsReadOnly / mutable fallback slots.
//
// Static factory properties return a fresh instance each call (the encodings
// are stateless) — deliberately NOT cached static fields, which would trip the
// ClassConstructorRunner cctor path (see docs/nativeaot-nostd-kernel-limits.md
// §1). Documented as a partial surface in that limits doc.

namespace System.Text
{
    public abstract class Encoding
    {
        public static Encoding ASCII => new ASCIIEncoding();
        public static Encoding Latin1 => new Latin1Encoding();
        public static Encoding Unicode => new UnicodeEncoding(false);          // UTF-16 LE
        public static Encoding BigEndianUnicode => new UnicodeEncoding(true);
        public static Encoding UTF8 => new UTF8Encoding();

        // --- decode: bytes -> string ------------------------------------

        public abstract string GetString(ReadOnlySpan<byte> bytes);

        public string GetString(byte[] bytes)
            => GetString(new ReadOnlySpan<byte>(bytes));

        public string GetString(byte[] bytes, int index, int count)
            => GetString(new ReadOnlySpan<byte>(bytes, index, count));

        // --- encode: string -> bytes ------------------------------------

        public abstract int GetByteCount(string s);
        public abstract byte[] GetBytes(string s);

        public byte[] GetBytes(char[] chars)
            => GetBytes(new string(chars));
    }

    public sealed class ASCIIEncoding : Encoding
    {
        public override string GetString(ReadOnlySpan<byte> bytes)
        {
            int n = bytes.Length;
            char[] chars = new char[n];
            for (int i = 0; i < n; i++)
            {
                byte b = bytes[i];
                chars[i] = b <= 0x7F ? (char)b : '?';
            }
            return new string(chars, 0, n);
        }

        public override int GetByteCount(string s) => s.Length;

        public override byte[] GetBytes(string s)
        {
            int n = s.Length;
            byte[] bytes = new byte[n];
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                bytes[i] = c <= (char)0x7F ? (byte)c : (byte)'?';
            }
            return bytes;
        }
    }

    public sealed class Latin1Encoding : Encoding
    {
        public override string GetString(ReadOnlySpan<byte> bytes)
        {
            int n = bytes.Length;
            char[] chars = new char[n];
            for (int i = 0; i < n; i++)
                chars[i] = (char)bytes[i];
            return new string(chars, 0, n);
        }

        public override int GetByteCount(string s) => s.Length;

        public override byte[] GetBytes(string s)
        {
            int n = s.Length;
            byte[] bytes = new byte[n];
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                bytes[i] = c <= (char)0xFF ? (byte)c : (byte)'?';
            }
            return bytes;
        }
    }

    public sealed class UnicodeEncoding : Encoding
    {
        private readonly bool _bigEndian;

        public UnicodeEncoding() : this(false) { }
        public UnicodeEncoding(bool bigEndian) => _bigEndian = bigEndian;

        public override string GetString(ReadOnlySpan<byte> bytes)
        {
            int n = bytes.Length / 2;
            char[] chars = new char[n];
            for (int i = 0; i < n; i++)
            {
                byte b0 = bytes[i * 2];
                byte b1 = bytes[i * 2 + 1];
                chars[i] = _bigEndian
                    ? (char)((b0 << 8) | b1)
                    : (char)((b1 << 8) | b0);
            }
            return new string(chars, 0, n);
        }

        public override int GetByteCount(string s) => s.Length * 2;

        public override byte[] GetBytes(string s)
        {
            int n = s.Length;
            byte[] bytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (_bigEndian)
                {
                    bytes[i * 2] = (byte)(c >> 8);
                    bytes[i * 2 + 1] = (byte)c;
                }
                else
                {
                    bytes[i * 2] = (byte)c;
                    bytes[i * 2 + 1] = (byte)(c >> 8);
                }
            }
            return bytes;
        }
    }

    public sealed class UTF8Encoding : Encoding
    {
        public override string GetString(ReadOnlySpan<byte> bytes)
        {
            int n = bytes.Length;
            // Worst case: one char per byte (ASCII). Surrogate pairs consume
            // 4 input bytes -> 2 chars, still <= n chars.
            char[] chars = new char[n];
            int ci = 0;
            int i = 0;
            while (i < n)
            {
                byte b0 = bytes[i];
                if (b0 < 0x80)
                {
                    chars[ci++] = (char)b0;
                    i += 1;
                }
                else if ((b0 & 0xE0) == 0xC0 && i + 1 < n)
                {
                    int cp = ((b0 & 0x1F) << 6) | (bytes[i + 1] & 0x3F);
                    chars[ci++] = (char)cp;
                    i += 2;
                }
                else if ((b0 & 0xF0) == 0xE0 && i + 2 < n)
                {
                    int cp = ((b0 & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F);
                    chars[ci++] = (char)cp;
                    i += 3;
                }
                else if ((b0 & 0xF8) == 0xF0 && i + 3 < n)
                {
                    int cp = ((b0 & 0x07) << 18) | ((bytes[i + 1] & 0x3F) << 12)
                             | ((bytes[i + 2] & 0x3F) << 6) | (bytes[i + 3] & 0x3F);
                    cp -= 0x10000;
                    chars[ci++] = (char)(0xD800 + (cp >> 10));
                    chars[ci++] = (char)(0xDC00 + (cp & 0x3FF));
                    i += 4;
                }
                else
                {
                    chars[ci++] = '�';
                    i += 1;
                }
            }
            return new string(chars, 0, ci);
        }

        public override int GetByteCount(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80) count += 1;
                else if (c < 0x800) count += 2;
                else if (c >= 0xD800 && c <= 0xDBFF) { count += 4; i++; } // high surrogate + low
                else count += 3;
            }
            return count;
        }

        public override byte[] GetBytes(string s)
        {
            byte[] bytes = new byte[GetByteCount(s)];
            int bi = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80)
                {
                    bytes[bi++] = (byte)c;
                }
                else if (c < 0x800)
                {
                    bytes[bi++] = (byte)(0xC0 | (c >> 6));
                    bytes[bi++] = (byte)(0x80 | (c & 0x3F));
                }
                else if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s.Length)
                {
                    char lo = s[i + 1];
                    int cp = 0x10000 + ((c - 0xD800) << 10) + (lo - 0xDC00);
                    bytes[bi++] = (byte)(0xF0 | (cp >> 18));
                    bytes[bi++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                    bytes[bi++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                    bytes[bi++] = (byte)(0x80 | (cp & 0x3F));
                    i++;
                }
                else
                {
                    bytes[bi++] = (byte)(0xE0 | (c >> 12));
                    bytes[bi++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    bytes[bi++] = (byte)(0x80 | (c & 0x3F));
                }
            }
            return bytes;
        }
    }
}
