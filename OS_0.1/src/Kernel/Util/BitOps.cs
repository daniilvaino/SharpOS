namespace OS.Kernel.Util
{
    internal static class BitOps
    {
        public static uint BitCount(uint value)
        {
            uint count = 0;
            while (value != 0)
            {
                count += value & 1U;
                value >>= 1;
            }

            return count;
        }

        public static uint BitCount(ulong value)
        {
            uint count = 0;
            while (value != 0)
            {
                count += (uint)(value & 1UL);
                value >>= 1;
            }

            return count;
        }

        public static uint NextPowerOf2(uint value)
        {
            if (value == 0)
                return 1;

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        public static uint AlignUp(uint value, uint alignment)
        {
            if (alignment == 0)
                return value;

            uint remainder = value % alignment;
            if (remainder == 0)
                return value;

            return value + (alignment - remainder);
        }

        public static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment == 0)
                return value;

            ulong remainder = value % alignment;
            if (remainder == 0)
                return value;

            return value + (alignment - remainder);
        }

        public static bool IsPowerOf2(uint value)
        {
            return value != 0 && (value & (value - 1)) == 0;
        }
    }
}
