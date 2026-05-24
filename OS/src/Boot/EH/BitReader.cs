namespace OS.Boot.EH
{
    // Bit-level cursor over a raw byte* blob. Matches the algorithm of
    // ILCompiler.Reflection.ReadyToRun NativeReader.ReadBits /
    // DecodeVarLengthUnsigned / DecodeVarLengthSigned, but works
    // directly on byte* (no Stream / ReadOnlySpan layer — kernel-tier
    // doesn't have those allocators-of-allocators).
    //
    // Used by CoffGcInfoDecoder (next chunk of step 110) to walk the
    // varint-and-bit-packed GcInfo blob produced by NativeAOT's
    // GcInfoEncoder. Algorithm reference:
    //   dotnet-runtime-sharpos/src/coreclr/inc/gcinfodecoder.h
    //     (DecodeVarLengthUnsigned / DecodeVarLengthSigned)
    //   dotnet-runtime-sharpos/src/coreclr/tools/aot/
    //     ILCompiler.Reflection.ReadyToRun/NativeReader.cs
    //     (ReadBits — same logic on Stream)
    // No code copied; clean-room port to unsafe byte*.
    internal unsafe ref struct BitReader
    {
        private const int BitsPerByte = 8;
        // Sign-extension constant matches the source — DecodeVarLengthSigned
        // sign-extends as if values were 32-bit regardless of host pointer
        // width. This is the encoding spec.
        private const int BitsPerSizeT = 32;

        private readonly byte* _base;
        private int _bitOffset;

        public BitReader(byte* basePtr)
        {
            _base = basePtr;
            _bitOffset = 0;
        }

        public int BitOffset => _bitOffset;

        public void SetBitOffset(int bitOffset) => _bitOffset = bitOffset;

        public void Advance(int bits) => _bitOffset += bits;

        // Align the cursor up to a multiple of `bits`. Used after slot-
        // table headers when the encoder pads to a byte boundary.
        public void Align(int bits)
        {
            int mask = bits - 1;
            _bitOffset = (_bitOffset + mask) & ~mask;
        }

        // Reads numBits (1..32) from the blob, advancing the cursor.
        // The returned value is right-justified and zero-extended into
        // a uint; caller does any further widening / sign-extension.
        public uint ReadBits(int numBits)
        {
            int start = _bitOffset / BitsPerByte;
            int bits  = _bitOffset % BitsPerByte;
            uint val = (uint)(_base[start] >> bits);
            bits += numBits;
            while (bits > BitsPerByte)
            {
                bits -= BitsPerByte;
                start++;
                if (bits > 0)
                {
                    uint extraBits = (uint)_base[start] << (numBits - bits);
                    val ^= extraBits;
                }
            }
            val &= (uint)((1 << numBits) - 1);
            _bitOffset += numBits;
            return val;
        }

        // Convenience: read 1 bit, return as bool.
        public bool ReadBit() => ReadBits(1) != 0;

        // GcInfo varint-style unsigned: chunks of `len` data bits + 1
        // extension bit. Extension bit set → another chunk follows. Each
        // chunk contributes `len` bits at increasing shifts.
        public uint DecodeVarLengthUnsigned(int len)
        {
            uint numEncodings = (uint)(1 << len);
            uint result = 0;
            for (int shift = 0; ; shift += len)
            {
                uint currentChunk = ReadBits(len + 1);
                result |= (currentChunk & (numEncodings - 1)) << shift;
                if ((currentChunk & numEncodings) == 0)
                    return result;
            }
        }

        // Same pattern as Unsigned but sign-extends the final result as a
        // 32-bit value (matches the encoder spec — not host word width).
        public int DecodeVarLengthSigned(int len)
        {
            int numEncodings = 1 << len;
            int result = 0;
            for (int shift = 0; ; shift += len)
            {
                int currentChunk = (int)ReadBits(len + 1);
                result |= (currentChunk & (numEncodings - 1)) << shift;
                if ((currentChunk & numEncodings) == 0)
                {
                    int sbits = BitsPerSizeT - (shift + len);
                    result <<= sbits;
                    result >>= sbits;   // arithmetic shift — sign extend
                    return result;
                }
            }
        }
    }
}
