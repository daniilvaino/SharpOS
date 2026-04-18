namespace OS.Kernel.Util
{
    internal unsafe struct MemoryBlock
    {
        private readonly byte* s_pointer;
        private readonly uint s_length;

        public MemoryBlock(void* pointer, uint length)
        {
            s_pointer = (byte*)pointer;
            s_length = length;
        }

        public byte* Pointer => s_pointer;

        public uint Length => s_length;

        public bool IsValid => s_pointer != null && s_length > 0;

        public bool TryOffset(uint offset, out MemoryBlock block)
        {
            block = default;
            if (s_pointer == null || offset > s_length)
                return false;

            block = new MemoryBlock(s_pointer + offset, s_length - offset);
            return true;
        }

        public void Clear()
        {
            if (s_pointer == null || s_length == 0)
                return;

            Memory.Zero(s_pointer, s_length);
        }

        public void Fill(byte value)
        {
            if (s_pointer == null || s_length == 0)
                return;

            Memory.MemSet(s_pointer, value, s_length);
        }

        public bool TryReadByte(uint offset, out byte value)
        {
            value = 0;
            if (!CanAccess(offset, 1))
                return false;

            value = s_pointer[offset];
            return true;
        }

        public bool TryWriteByte(uint offset, byte value)
        {
            if (!CanAccess(offset, 1))
                return false;

            s_pointer[offset] = value;
            return true;
        }

        public bool TryReadUInt16(uint offset, out ushort value)
        {
            value = 0;
            if (!CanAccess(offset, 2))
                return false;

            value = (ushort)(
                s_pointer[offset] |
                (s_pointer[offset + 1] << 8));

            return true;
        }

        public bool TryWriteUInt16(uint offset, ushort value)
        {
            if (!CanAccess(offset, 2))
                return false;

            s_pointer[offset] = (byte)(value & 0xFF);
            s_pointer[offset + 1] = (byte)((value >> 8) & 0xFF);
            return true;
        }

        public bool TryReadUInt32(uint offset, out uint value)
        {
            value = 0;
            if (!CanAccess(offset, 4))
                return false;

            value =
                s_pointer[offset] |
                ((uint)s_pointer[offset + 1] << 8) |
                ((uint)s_pointer[offset + 2] << 16) |
                ((uint)s_pointer[offset + 3] << 24);

            return true;
        }

        public bool TryWriteUInt32(uint offset, uint value)
        {
            if (!CanAccess(offset, 4))
                return false;

            s_pointer[offset] = (byte)(value & 0xFF);
            s_pointer[offset + 1] = (byte)((value >> 8) & 0xFF);
            s_pointer[offset + 2] = (byte)((value >> 16) & 0xFF);
            s_pointer[offset + 3] = (byte)((value >> 24) & 0xFF);
            return true;
        }

        public bool TryReadUInt64(uint offset, out ulong value)
        {
            value = 0;
            if (!CanAccess(offset, 8))
                return false;

            value =
                s_pointer[offset] |
                ((ulong)s_pointer[offset + 1] << 8) |
                ((ulong)s_pointer[offset + 2] << 16) |
                ((ulong)s_pointer[offset + 3] << 24) |
                ((ulong)s_pointer[offset + 4] << 32) |
                ((ulong)s_pointer[offset + 5] << 40) |
                ((ulong)s_pointer[offset + 6] << 48) |
                ((ulong)s_pointer[offset + 7] << 56);

            return true;
        }

        public bool TryWriteUInt64(uint offset, ulong value)
        {
            if (!CanAccess(offset, 8))
                return false;

            s_pointer[offset] = (byte)(value & 0xFF);
            s_pointer[offset + 1] = (byte)((value >> 8) & 0xFF);
            s_pointer[offset + 2] = (byte)((value >> 16) & 0xFF);
            s_pointer[offset + 3] = (byte)((value >> 24) & 0xFF);
            s_pointer[offset + 4] = (byte)((value >> 32) & 0xFF);
            s_pointer[offset + 5] = (byte)((value >> 40) & 0xFF);
            s_pointer[offset + 6] = (byte)((value >> 48) & 0xFF);
            s_pointer[offset + 7] = (byte)((value >> 56) & 0xFF);
            return true;
        }

        public bool TryCopyTo(MemoryBlock destination, uint count)
        {
            if (count > s_length || destination.Pointer == null || count > destination.Length)
                return false;

            Memory.MemCopy(destination.Pointer, s_pointer, count);
            return true;
        }

        public bool TryCopyFrom(MemoryBlock source, uint count)
        {
            if (source.Pointer == null || count > source.Length || count > s_length)
                return false;

            Memory.MemCopy(s_pointer, source.Pointer, count);
            return true;
        }

        public bool TryMoveFrom(MemoryBlock source, uint count)
        {
            if (source.Pointer == null || count > source.Length || count > s_length)
                return false;

            Memory.MemMove(s_pointer, source.Pointer, count);
            return true;
        }

        private bool CanAccess(uint offset, uint size)
        {
            if (s_pointer == null || size == 0)
                return false;

            if (offset >= s_length)
                return false;

            return size <= s_length - offset;
        }
    }
}
