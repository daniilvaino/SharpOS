// Guid — BCL-compatible 128-bit identifier.
//
// Layout matches Windows/.NET System.Guid in-memory layout:
//   int (Data1) + short (Data2) + short (Data3) + 8 bytes (Data4).
// Raw memcpy/`byte[16]` of this struct gives the *in-memory* Guid layout,
// NOT the RFC 4122 network byte order. Data1/Data2/Data3 stored
// little-endian, so wire serialization would need byte-swapping
// (CoreCLR / .NET Guid.ToByteArray performs that swap itself).
//
// NewGuid() generates a v4-shaped GUID using xorshift64* fed by a
// monotonic counter. **NOT cryptographically secure** and **not globally
// unique across boots** (deterministic seed). Suitable only для early
// boot / runtime-internal IDs (event provider tokens, type-instance
// markers and similar opaque identifiers CoreCLR treats as unique by
// reference). When the kernel grows an RDTSC helper or RDRAND wrapper,
// swap entropy source без changing API.

using System.Runtime.InteropServices;

namespace System
{
    [StructLayout(LayoutKind.Sequential)]
    // In the pipe catalog (SharpOS.Std.Pipes): the same key in every image.
    [SharpOS.Std.Pipes.Message]
    public partial struct Guid
    {
        // BCL field names + order — sequential layout means these 16 bytes
        // map 1:1 with on-wire format used by CoCreateGuid output buffer.
        private int _a;
        private short _b;
        private short _c;
        private byte _d;
        private byte _e;
        private byte _f;
        private byte _g;
        private byte _h;
        private byte _i;
        private byte _j;
        private byte _k;

        public static readonly Guid Empty = default;

        public Guid(int a, short b, short c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k)
        {
            _a = a; _b = b; _c = c;
            _d = d; _e = e; _f = f; _g = g;
            _h = h; _i = i; _j = j; _k = k;
        }

        // Internal PRNG state. xorshift64* — fast, deterministic, good
        // statistical properties; seed mixes a compile-time constant с
        // a monotonic counter so the first GUID isn't all-zero.
        private static ulong s_state = 0x9E3779B97F4A7C15UL;

        private static ulong NextU64()
        {
            ulong x = s_state;
            if (x == 0) x = 0x9E3779B97F4A7C15UL;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            s_state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        public static Guid NewGuid()
        {
            ulong lo = NextU64();
            ulong hi = NextU64();

            Guid g;
            unsafe
            {
                byte* p = (byte*)&g;
                for (int n = 0; n < 8; n++) p[n]     = (byte)(lo >> (n * 8));
                for (int n = 0; n < 8; n++) p[8 + n] = (byte)(hi >> (n * 8));

                // RFC 4122 v4 version stamp.
                // In-memory: Data3 is little-endian, so memory byte 7 is
                // its MSB. High nibble of that MSB = version field of
                // wire-format Data3. Setting it to 0100b = version 4.
                p[7] = (byte)((p[7] & 0x0F) | 0x40);
                // Variant 10xx in high two bits of Data4[0] (memory byte 8).
                p[8] = (byte)((p[8] & 0x3F) | 0x80);
            }
            return g;
        }
    
        // ---- text (step197: JSON writes a Guid as a string, Into<T> reads it back) ----

        /// <summary>The "D" form: 32 hex digits in groups 8-4-4-4-12, lowercase.</summary>
        public override string ToString() => ToString("D");

        /// <summary>"D" (default), "N" (no hyphens), "B" (braces), "P" (parentheses); BCL forms.</summary>
        public string ToString(string? format)
        {
            char f = string.IsNullOrEmpty(format) ? 'D' : format![0];
            var chars = new char[38];
            int n = 0;
            bool hyphens = f != 'N' && f != 'n';
            if (f == 'B' || f == 'b') chars[n++] = '{';
            if (f == 'P' || f == 'p') chars[n++] = '(';
            n = Hex(chars, n, (uint)_a, 8);
            if (hyphens) chars[n++] = '-';
            n = Hex(chars, n, (ushort)_b, 4);
            if (hyphens) chars[n++] = '-';
            n = Hex(chars, n, (ushort)_c, 4);
            if (hyphens) chars[n++] = '-';
            n = Hex(chars, n, _d, 2);
            n = Hex(chars, n, _e, 2);
            if (hyphens) chars[n++] = '-';
            n = Hex(chars, n, _f, 2);
            n = Hex(chars, n, _g, 2);
            n = Hex(chars, n, _h, 2);
            n = Hex(chars, n, _i, 2);
            n = Hex(chars, n, _j, 2);
            n = Hex(chars, n, _k, 2);
            if (f == 'B' || f == 'b') chars[n++] = '}';
            if (f == 'P' || f == 'p') chars[n++] = ')';
            return new string(chars, 0, n);
        }

        public string ToString(string? format, IFormatProvider? provider) => ToString(format);

        private static int Hex(char[] into, int at, uint value, int digits)
        {
            for (int i = digits - 1; i >= 0; i--)
            {
                uint nibble = (value >> (i * 4)) & 0xF;
                into[at++] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
            }
            return at;
        }

        public static Guid Parse(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (!TryParse(input, out Guid result)) throw new FormatException("Unrecognized Guid format.");
            return result;
        }

        /// <summary>The "D", "N", "B" and "P" forms, any case.</summary>
        public static bool TryParse(string? input, out Guid result)
        {
            result = default;
            if (input == null) return false;
            string s = input.Trim();
            if (s.Length == 38 && ((s[0] == '{' && s[37] == '}') || (s[0] == '(' && s[37] == ')')))
                s = s.Substring(1, 36);
            if (s.Length == 36)
            {
                if (s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-') return false;
                s = s.Substring(0, 8) + s.Substring(9, 4) + s.Substring(14, 4) + s.Substring(19, 4) + s.Substring(24, 12);
            }
            if (s.Length != 32) return false;
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int hi = HexValue(s[2 * i]), lo = HexValue(s[2 * i + 1]);
                if (hi < 0 || lo < 0) return false;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            result = new Guid(
                (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3],
                (short)((bytes[4] << 8) | bytes[5]),
                (short)((bytes[6] << 8) | bytes[7]),
                bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
            return true;
        }

        private static int HexValue(char c)
            => c >= '0' && c <= '9' ? c - '0'
             : c >= 'a' && c <= 'f' ? c - 'a' + 10
             : c >= 'A' && c <= 'F' ? c - 'A' + 10
             : -1;
    }
}
