namespace OS.Boot.EH
{
    // NativeAOT-specific length-prefix varint reader.
    //
    // This is NOT continuation-bit varint (LEB128). Encoding lives in
    //   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/inc/varint.h
    // and works as follows for unsigned values:
    //
    //   value < 128            : 1 byte,  encoded as `value*2 + 0`            (low 1 bit = 0)
    //   value < 128*128        : 2 bytes, encoded as `value*4 + 1`, `>>6`     (low 2 bits = 01)
    //   value < 128*128*128    : 3 bytes, `value*8 + 3`, `>>5`, `>>13`        (low 3 bits = 011)
    //   value < 128*128*128*128: 4 bytes, `value*16 + 7`, `>>4`, `>>12`,>>20  (low 4 bits = 0111)
    //   else                   : 5 bytes, `0x0F`, then 4 LE bytes of value    (low 4 bits = 1111)
    //
    // Length detection on the first byte:
    //   (b0 & 0x01) == 0  → 1 byte
    //   (b0 & 0x03) == 1  → 2 bytes
    //   (b0 & 0x07) == 3  → 3 bytes
    //   (b0 & 0x0F) == 7  → 4 bytes
    //   (b0 & 0x0F) == 15 → 5 bytes
    //
    // The encoding is little-endian: low bytes contain low value bits
    // (after the length tag bits are shifted away).
    internal static unsafe class VarInt
    {
        // Reads one unsigned varint from `*pp` and advances `*pp` past it.
        public static uint ReadUnsigned(ref byte* p)
        {
            byte b0 = p[0];

            if ((b0 & 0x01) == 0)
            {
                // 1 byte: 7 bits of value in upper 7 bits of b0.
                p += 1;
                return (uint)b0 >> 1;
            }

            if ((b0 & 0x02) == 0)
            {
                // 2 bytes: 14 bits of value, encoded as `value*4 + 1`.
                ushort raw = *(ushort*)p;
                p += 2;
                return (uint)(raw >> 2);
            }

            if ((b0 & 0x04) == 0)
            {
                // 3 bytes: 21 bits, encoded as `value*8 + 3`.
                uint raw = (uint)p[0]
                         | ((uint)p[1] << 8)
                         | ((uint)p[2] << 16);
                p += 3;
                return raw >> 3;
            }

            if ((b0 & 0x08) == 0)
            {
                // 4 bytes: 28 bits, encoded as `value*16 + 7`.
                uint raw = *(uint*)p;
                p += 4;
                return raw >> 4;
            }

            // 5 bytes: 0x0F (or 0xFF) marker byte, then 4 LE bytes of value.
            p += 1;
            uint val = *(uint*)p;
            p += 4;
            return val;
        }
    }
}
