// Partial System.Text.Encoding for the NoStdLib kernel/std environment.
//
// This is NOT the full BCL Encoding hierarchy. It provides the concrete
// encodings and the GetString/GetBytes surface that real BCL-consuming code
// (e.g. the vendored PeNet PE parser) actually calls, so such code compiles
// against our std without source edits. Cut relative to dotnet/runtime:
//
//   - EncoderFallback / DecoderFallback / *NLS state machines (invalid input
//     is mapped to a replacement char '?' / U+FFFD and never surfaces a
//     fallback buffer; the one exception is UTF8Encoding constructed with
//     throwOnInvalidBytes: true, which throws DecoderFallbackException /
//     EncoderFallbackException like the BCL's exception fallbacks).
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

        public unsafe string GetString(byte* bytes, int byteCount)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
            return GetString(new ReadOnlySpan<byte>(bytes, byteCount));
        }

        // --- encode: string -> bytes ------------------------------------

        public abstract int GetByteCount(string s);

        // The most bytes charCount chars can become. The original is abstract;
        // here a safe default (UTF-8 and UTF-16 both stay within it), tightened
        // where the encoding is known.
        public virtual int GetMaxByteCount(int charCount)
        {
            if (charCount < 0) throw new ArgumentOutOfRangeException(nameof(charCount));
            return (charCount + 1) * 4;
        }
        public abstract byte[] GetBytes(string s);

        public byte[] GetBytes(char[] chars)
            => GetBytes(new string(chars));

        // --- spans ------------------------------------------------------
        //
        // Built on the members above rather than declared abstract, so that
        // adding them costs no subclass a line: every encoding here already
        // knows how to turn a string into bytes and bytes into a string, and
        // these are that, with the copy the caller asked for.
        //
        // They allocate where the BCL's do not - a string in the middle of
        // what should be a straight conversion. Said out loud rather than
        // hidden: the callers that exist (System.Formats.Cbor) convert short
        // keys and text strings, and an honest extra allocation beats a
        // hand-rolled second decoder that can disagree with the first.

        public virtual int GetByteCount(ReadOnlySpan<char> chars)
            => GetByteCount(StringOf(chars));

        public virtual int GetBytes(ReadOnlySpan<char> source, Span<byte> destination)
        {
            byte[] bytes = GetBytes(StringOf(source));
            if (bytes.Length > destination.Length)
                throw new ArgumentException("Destination too short.", nameof(destination));

            new ReadOnlySpan<byte>(bytes).CopyTo(destination);
            return bytes.Length;
        }

        // BCL .NET 8, verbatim (Encoding.cs). The number formatter's UTF-8
        // path copies culture strings ("NaN", "Infinity") through it.
        public virtual bool TryGetBytes(ReadOnlySpan<char> chars, Span<byte> bytes, out int bytesWritten)
        {
            int required = GetByteCount(chars);
            if (required <= bytes.Length)
            {
                bytesWritten = GetBytes(chars, bytes);
                return true;
            }

            bytesWritten = 0;
            return false;
        }

        public virtual int GetCharCount(ReadOnlySpan<byte> bytes)
            => GetString(bytes).Length;

        public virtual int GetChars(ReadOnlySpan<byte> source, Span<char> destination)
        {
            string text = GetString(source);
            if (text.Length > destination.Length)
                throw new ArgumentException("Destination too short.", nameof(destination));

            for (int i = 0; i < text.Length; i++)
                destination[i] = text[i];

            return text.Length;
        }

        private static string StringOf(ReadOnlySpan<char> chars)
        {
            if (chars.Length == 0) return "";

            char[] buffer = new char[chars.Length];
            chars.CopyTo(buffer);
            return new string(buffer, 0, buffer.Length);
        }
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
        // Whether malformed input is an error or a replacement character.
        //
        // Added for System.Formats.Cbor, which asks for a strict decoder on
        // every conformance mode but Lax: a CBOR text string carrying invalid
        // UTF-8 is a malformed document, and a decoder that quietly substitutes
        // U+FFFD turns "this data is wrong" into "this data says a question
        // mark" - a silence exactly like the ones that cost this project its
        // longest evenings.
        private readonly bool _throwOnInvalidBytes;

        public UTF8Encoding() { }

        // Encoding.UTF8's own bound: a char is at most three bytes, and one
        // more slot for a surrogate left over from a previous call.
        public override int GetMaxByteCount(int charCount)
        {
            if (charCount < 0) throw new ArgumentOutOfRangeException(nameof(charCount));
            return (charCount + 1) * 3;
        }

        public UTF8Encoding(bool encoderShouldEmitUTF8Identifier)
        {
            // The byte-order mark is the caller's business at write time, and
            // nothing here emits one; kept so the BCL's three constructors all
            // exist rather than only the shapes we happen to call.
            _ = encoderShouldEmitUTF8Identifier;
        }

        public UTF8Encoding(bool encoderShouldEmitUTF8Identifier, bool throwOnInvalidBytes)
        {
            _ = encoderShouldEmitUTF8Identifier;
            _throwOnInvalidBytes = throwOnInvalidBytes;
        }

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
                    continue;
                }

                // Length from the lead byte, then the continuation bytes and
                // the code point together - because every way a sequence can
                // be wrong has to be caught in one place if strict mode is to
                // mean anything.
                int need;
                int cp;
                if ((b0 & 0xE0) == 0xC0) { need = 2; cp = b0 & 0x1F; }
                else if ((b0 & 0xF0) == 0xE0) { need = 3; cp = b0 & 0x0F; }
                else if ((b0 & 0xF8) == 0xF0) { need = 4; cp = b0 & 0x07; }
                else { if (!Bad(ref ci, chars)) return Throw(); i += 1; continue; }

                if (i + need > n) { if (!Bad(ref ci, chars)) return Throw(); i += 1; continue; }

                bool ok = true;
                for (int k = 1; k < need; k++)
                {
                    byte bk = bytes[i + k];
                    if ((bk & 0xC0) != 0x80) { ok = false; break; }
                    cp = (cp << 6) | (bk & 0x3F);
                }

                // Overlong forms, the surrogate range and anything past the
                // last plane are all malformed, and all of them decode to a
                // plausible character if nobody checks.
                if (ok)
                {
                    if (need == 2 && cp < 0x80) ok = false;
                    else if (need == 3 && cp < 0x800) ok = false;
                    else if (need == 4 && cp < 0x10000) ok = false;
                    else if (cp >= 0xD800 && cp <= 0xDFFF) ok = false;
                    else if (cp > 0x10FFFF) ok = false;
                }

                if (!ok) { if (!Bad(ref ci, chars)) return Throw(); i += 1; continue; }

                if (cp < 0x10000)
                {
                    chars[ci++] = (char)cp;
                }
                else
                {
                    cp -= 0x10000;
                    chars[ci++] = (char)(0xD800 + (cp >> 10));
                    chars[ci++] = (char)(0xDC00 + (cp & 0x3FF));
                }
                i += need;
            }
            return new string(chars, 0, ci);

            // Writes the replacement character and says whether decoding may
            // continue; false means the strict decoder has to give up.
            bool Bad(ref int at, char[] buffer)
            {
                if (_throwOnInvalidBytes) return false;
                buffer[at++] = '�';
                return true;
            }

            // BCL: the strict decoder's DecoderExceptionFallback throws DecoderFallbackException
            // (an ArgumentException). Callers - System.Formats.Cbor, System.Text.Json - catch
            // exactly that type to rewrap it.
            string Throw() => throw new DecoderFallbackException("Unable to translate bytes to the target code page: invalid UTF-8 byte sequence.");
        }

        public override int GetByteCount(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80) count += 1;
                else if (c < 0x800) count += 2;
                else if (IsPairAt(s, i)) { count += 4; i++; } // high surrogate + low
                else if (c >= 0xD800 && c <= 0xDFFF) { ThrowIfStrict(c, i); count += 3; } // lone: U+FFFD
                else count += 3;
            }
            return count;
        }

        // A high surrogate at i with a low surrogate right after it.
        private static bool IsPairAt(string s, int i)
            => s[i] >= 0xD800 && s[i] <= 0xDBFF && i + 1 < s.Length && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF;

        // BCL: an unpaired surrogate is invalid UTF-16. The strict encoder
        // (throwOnInvalidBytes: true -> EncoderExceptionFallback) throws
        // EncoderFallbackException; the default one writes U+FFFD instead.
        private void ThrowIfStrict(char c, int index)
        {
            if (_throwOnInvalidBytes)
                throw new EncoderFallbackException("Unable to translate Unicode character \\u" + ((int)c).ToString("X4") + " at index " + index.ToString() + " to specified code page.");
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
                else if (IsPairAt(s, i))
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
                    // An unpaired surrogate becomes U+FFFD (GetByteCount threw already in strict mode).
                    if (c >= 0xD800 && c <= 0xDFFF) c = (char)0xFFFD;
                    bytes[bi++] = (byte)(0xE0 | (c >> 12));
                    bytes[bi++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    bytes[bi++] = (byte)(0x80 | (c & 0x3F));
                }
            }
            return bytes;
        }
    }
}
