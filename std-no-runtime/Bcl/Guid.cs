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
    public struct Guid
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
    }
}
