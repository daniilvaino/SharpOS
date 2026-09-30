// NativeFormat variable-length unsigned integer encoding decoder.
//
// Source of truth: NativeFormatReader / NativePrimitiveDecoder in dotnet/runtime
// (classes are external to our snapshot but the format is stable).
//
// Encoding:
//   0xxxxxxx                                          — 7-bit  (1 byte)
//   10xxxxxx yyyyyyyy                                 — 14-bit (2 bytes)
//   110xxxxx yyyyyyyy zzzzzzzz                        — 21-bit (3 bytes)
//   1110xxxx yyyyyyyy zzzzzzzz wwwwwwww               — 28-bit (4 bytes)
//   1111xxxx yyyyyyyy zzzzzzzz wwwwwwww vvvvvvvv      — 32-bit (5 bytes, first-byte low bits ignored)

namespace SharpOS.Std.NoRuntime
{
    public static unsafe class NativeFormatDecoder
    {
        public static byte ReadUInt8(ref byte* stream)
        {
            byte b = *stream;
            stream++;
            return b;
        }

        public static uint DecodeUnsigned(ref byte* stream)
        {
            uint value = 0;
            uint val = *stream;

            if ((val & 1) == 0)
            {
                value = (val >> 1);
                stream += 1;
            }
            else if ((val & 2) == 0)
            {
                value = (val >> 2) |
                        (((uint)stream[1]) << 6);
                stream += 2;
            }
            else if ((val & 4) == 0)
            {
                value = (val >> 3) |
                        (((uint)stream[1]) << 5) |
                        (((uint)stream[2]) << 13);
                stream += 3;
            }
            else if ((val & 8) == 0)
            {
                value = (val >> 4) |
                        (((uint)stream[1]) << 4) |
                        (((uint)stream[2]) << 12) |
                        (((uint)stream[3]) << 20);
                stream += 4;
            }
            else if ((val & 16) == 0)
            {
                value = ((uint)stream[1]) |
                        (((uint)stream[2]) << 8) |
                        (((uint)stream[3]) << 16) |
                        (((uint)stream[4]) << 24);
                stream += 5;
            }
            else
            {
                // Invalid encoding — caller should not encounter this in well-formed streams.
                value = 0;
                stream += 1;
            }

            return value;
        }
    }
}
