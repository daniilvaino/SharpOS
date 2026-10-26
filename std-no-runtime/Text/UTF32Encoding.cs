// System.Text.UTF32Encoding and Encoding.UTF32, in the shape of the rest of
// std's Encoding (Text/Encoding.cs): the BCL's constructors and results, the
// conversion written directly rather than ported — std has no EncoderNLS /
// DecoderNLS / fallback machinery to port it onto.
//
// As the BCL with its default replacement fallback: a lone surrogate encodes
// as U+FFFD, and a code unit above U+10FFFF, inside the surrogate range or a
// trailing partial unit decodes as U+FFFD. No BOM is written by GetBytes (as
// the BCL; the BOM is GetPreamble's, which std's Encoding does not have).

namespace System.Text
{
    public abstract partial class Encoding
    {
        public static Encoding UTF32 => new UTF32Encoding(); // little-endian, as the BCL's
    }

    public sealed class UTF32Encoding : Encoding
    {
        private readonly bool _bigEndian;

        public UTF32Encoding() : this(false, true) { }

        public UTF32Encoding(bool bigEndian, bool byteOrderMark) => _bigEndian = bigEndian;

        public UTF32Encoding(bool bigEndian, bool byteOrderMark, bool throwOnInvalidCharacters) => _bigEndian = bigEndian;

        public override int GetMaxByteCount(int charCount)
        {
            if (charCount < 0) throw new ArgumentOutOfRangeException(nameof(charCount));
            return (charCount + 1) * 4;
        }

        public override string GetString(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length / 4 + 1);
            int i = 0;
            for (; i + 4 <= bytes.Length; i += 4)
            {
                uint cp = _bigEndian
                    ? (uint)(bytes[i] << 24 | bytes[i + 1] << 16 | bytes[i + 2] << 8 | bytes[i + 3])
                    : (uint)(bytes[i + 3] << 24 | bytes[i + 2] << 16 | bytes[i + 1] << 8 | bytes[i]);

                if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                {
                    sb.Append('�');
                }
                else if (cp >= 0x10000)
                {
                    cp -= 0x10000;
                    sb.Append((char)(0xD800 + (cp >> 10)));
                    sb.Append((char)(0xDC00 + (cp & 0x3FF)));
                }
                else
                {
                    sb.Append((char)cp);
                }
            }
            if (i < bytes.Length)
                sb.Append('�');
            return sb.ToString();
        }

        public override int GetByteCount(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    i++;
                count += 4;
            }
            return count;
        }

        public override byte[] GetBytes(string s)
        {
            byte[] bytes = new byte[GetByteCount(s)];
            int o = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                uint cp;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = 0x10000 + (((uint)c - 0xD800) << 10) + ((uint)s[i + 1] - 0xDC00);
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    cp = 0xFFFD;
                }
                else
                {
                    cp = c;
                }

                if (_bigEndian)
                {
                    bytes[o] = (byte)(cp >> 24);
                    bytes[o + 1] = (byte)(cp >> 16);
                    bytes[o + 2] = (byte)(cp >> 8);
                    bytes[o + 3] = (byte)cp;
                }
                else
                {
                    bytes[o] = (byte)cp;
                    bytes[o + 1] = (byte)(cp >> 8);
                    bytes[o + 2] = (byte)(cp >> 16);
                    bytes[o + 3] = (byte)(cp >> 24);
                }
                o += 4;
            }
            return bytes;
        }
    }
}
