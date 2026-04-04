namespace SharpOS.Std.NoRuntime
{
    public static unsafe class MemoryPrimitives
    {
        public static void* Memset(void* destination, byte value, ulong count)
        {
            byte* dst = (byte*)destination;
            for (ulong i = 0; i < count; i++)
                dst[i] = value;

            return destination;
        }

        public static void* Memcpy(void* destination, void* source, ulong count)
        {
            byte* dst = (byte*)destination;
            byte* src = (byte*)source;
            for (ulong i = 0; i < count; i++)
                dst[i] = src[i];

            return destination;
        }

        public static void* Memmove(void* destination, void* source, ulong count)
        {
            byte* dst = (byte*)destination;
            byte* src = (byte*)source;

            if (dst == src || count == 0)
                return destination;

            if (dst < src || dst >= src + count)
            {
                for (ulong i = 0; i < count; i++)
                    dst[i] = src[i];
            }
            else
            {
                while (count > 0)
                {
                    count--;
                    dst[count] = src[count];
                }
            }

            return destination;
        }
    }
}
