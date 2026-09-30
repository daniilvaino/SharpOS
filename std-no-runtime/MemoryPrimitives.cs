namespace SharpOS.Std.NoRuntime
{
    // memset / memcpy / memmove for everything linked against this std — the
    // kernel exports them under the CRT names, so the hosted CoreCLR's own
    // copies land here too.
    //
    // Eight bytes a step, four steps unrolled, bytes only for the tail. These
    // were byte loops, and the runtime's exception path copies a 1232-byte
    // CONTEXT several times per frame: memcpy and memset were 15 % of a throw
    // under QEMU (step170). x64 does not care about alignment, so there is no
    // head to align first. General-purpose registers only, not vectors: these
    // run on every path, interrupt handlers included, and nothing here should
    // depend on whose SSE state is live.
    //
    // Plain loops over pointers on purpose: a helper such as Buffer.MemoryCopy
    // or Unsafe.CopyBlock may be lowered back into a call to memcpy — which is
    // this.
    public static unsafe class MemoryPrimitives
    {
        public static void* Memset(void* destination, byte value, ulong count)
        {
            byte* dst = (byte*)destination;
            ulong pattern = value * 0x0101010101010101UL;

            while (count >= 32)
            {
                *(ulong*)dst = pattern;
                *(ulong*)(dst + 8) = pattern;
                *(ulong*)(dst + 16) = pattern;
                *(ulong*)(dst + 24) = pattern;
                dst += 32;
                count -= 32;
            }
            while (count >= 8)
            {
                *(ulong*)dst = pattern;
                dst += 8;
                count -= 8;
            }
            while (count > 0)
            {
                *dst++ = value;
                count--;
            }

            return destination;
        }

        public static void* Memcpy(void* destination, void* source, ulong count)
        {
            CopyForward((byte*)destination, (byte*)source, count);
            return destination;
        }

        public static void* Memmove(void* destination, void* source, ulong count)
        {
            byte* dst = (byte*)destination;
            byte* src = (byte*)source;

            if (dst == src || count == 0)
                return destination;

            // Forward is safe whenever the destination starts below the
            // source: each step reads its bytes before any write can reach
            // them. Otherwise, if the ranges overlap, copy from the end.
            if (dst < src || dst >= src + count)
                CopyForward(dst, src, count);
            else
                CopyBackward(dst, src, count);

            return destination;
        }

        // Each 8-byte step reads before it writes, so forward copying is also
        // correct for an overlapping destination below the source.
        private static void CopyForward(byte* dst, byte* src, ulong count)
        {
            while (count >= 32)
            {
                ulong a = *(ulong*)src;
                ulong b = *(ulong*)(src + 8);
                ulong c = *(ulong*)(src + 16);
                ulong d = *(ulong*)(src + 24);
                *(ulong*)dst = a;
                *(ulong*)(dst + 8) = b;
                *(ulong*)(dst + 16) = c;
                *(ulong*)(dst + 24) = d;
                dst += 32;
                src += 32;
                count -= 32;
            }
            while (count >= 8)
            {
                *(ulong*)dst = *(ulong*)src;
                dst += 8;
                src += 8;
                count -= 8;
            }
            while (count > 0)
            {
                *dst++ = *src++;
                count--;
            }
        }

        // The mirror image, for a destination that overlaps the source from
        // above: all four reads of a block happen before its writes.
        private static void CopyBackward(byte* dst, byte* src, ulong count)
        {
            dst += count;
            src += count;

            while (count >= 32)
            {
                dst -= 32;
                src -= 32;
                ulong a = *(ulong*)src;
                ulong b = *(ulong*)(src + 8);
                ulong c = *(ulong*)(src + 16);
                ulong d = *(ulong*)(src + 24);
                *(ulong*)(dst + 24) = d;
                *(ulong*)(dst + 16) = c;
                *(ulong*)(dst + 8) = b;
                *(ulong*)dst = a;
                count -= 32;
            }
            while (count >= 8)
            {
                dst -= 8;
                src -= 8;
                *(ulong*)dst = *(ulong*)src;
                count -= 8;
            }
            while (count > 0)
            {
                *--dst = *--src;
                count--;
            }
        }
    }
}
