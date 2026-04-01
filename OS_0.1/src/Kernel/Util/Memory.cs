namespace OS.Kernel.Util
{
    internal static unsafe class Memory
    {
        public static void MemSet(void* destination, byte value, uint length)
        {
            if (destination == null || length == 0)
                return;

            byte* dst = (byte*)destination;
            for (uint i = 0; i < length; i++)
                dst[i] = value;
        }

        public static void MemCopy(void* destination, void* source, uint length)
        {
            if (destination == null || source == null || length == 0)
                return;

            byte* dst = (byte*)destination;
            byte* src = (byte*)source;
            for (uint i = 0; i < length; i++)
                dst[i] = src[i];
        }

        public static void MemMove(void* destination, void* source, uint length)
        {
            if (destination == null || source == null || length == 0)
                return;

            byte* dst = (byte*)destination;
            byte* src = (byte*)source;

            if (dst == src)
                return;

            if (dst < src || dst >= src + length)
            {
                for (uint i = 0; i < length; i++)
                    dst[i] = src[i];
            }
            else
            {
                uint i = length;
                while (i > 0)
                {
                    i--;
                    dst[i] = src[i];
                }
            }
        }

        public static void Zero(void* pointer, uint length)
        {
            MemSet(pointer, 0, length);
        }
    }
}
