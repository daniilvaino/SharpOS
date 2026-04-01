namespace OS.Kernel.Util
{
    internal unsafe struct BinaryReaderLite
    {
        private readonly byte* s_pointer;
        private readonly uint s_length;
        private uint s_position;

        public BinaryReaderLite(void* pointer, uint length)
        {
            s_pointer = (byte*)pointer;
            s_length = length;
            s_position = 0;
        }

        public uint Position => s_position;

        public uint Remaining => s_position <= s_length ? s_length - s_position : 0;

        public void Reset()
        {
            s_position = 0;
        }

        public bool TrySkip(uint count)
        {
            if (count > Remaining)
                return false;

            s_position += count;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (!TryReadRaw(out byte* pointer, 1))
                return false;

            value = pointer[0];
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            value = 0;
            if (!TryReadRaw(out byte* pointer, 2))
                return false;

            value = (ushort)(
                pointer[0] |
                (pointer[1] << 8));

            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (!TryReadRaw(out byte* pointer, 4))
                return false;

            value =
                pointer[0] |
                ((uint)pointer[1] << 8) |
                ((uint)pointer[2] << 16) |
                ((uint)pointer[3] << 24);

            return true;
        }

        public bool TryReadUInt64(out ulong value)
        {
            value = 0;
            if (!TryReadRaw(out byte* pointer, 8))
                return false;

            value =
                pointer[0] |
                ((ulong)pointer[1] << 8) |
                ((ulong)pointer[2] << 16) |
                ((ulong)pointer[3] << 24) |
                ((ulong)pointer[4] << 32) |
                ((ulong)pointer[5] << 40) |
                ((ulong)pointer[6] << 48) |
                ((ulong)pointer[7] << 56);

            return true;
        }

        public bool TryRead7BitInt(out int value)
        {
            value = 0;

            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                if (!TryReadByte(out byte part))
                    return false;

                value |= (part & 0x7F) << shift;
                if ((part & 0x80) == 0)
                    return true;

                shift += 7;
            }

            value = 0;
            return false;
        }

        public bool TryReadPrefixedBlock(out MemoryBlock block)
        {
            block = default;
            uint startPosition = s_position;

            if (!TryRead7BitInt(out int payloadLength))
            {
                s_position = startPosition;
                return false;
            }

            if (payloadLength < 0)
            {
                s_position = startPosition;
                return false;
            }

            uint payload = (uint)payloadLength;
            if (payload > Remaining)
            {
                s_position = startPosition;
                return false;
            }

            block = new MemoryBlock(s_pointer + s_position, payload);
            s_position += payload;
            return true;
        }

        private bool TryReadRaw(out byte* pointer, uint size)
        {
            pointer = null;
            if (s_pointer == null || size == 0 || size > Remaining)
                return false;

            pointer = s_pointer + s_position;
            s_position += size;
            return true;
        }
    }
}
