// System.Runtime.InteropServices.NativeMemory — BCL's surface over the
// managed heap (step223, ZstdSharp's malloc/free). Neither tier has a C heap
// to hand out, but neither collector moves objects: a block is a byte array,
// kept alive from a table until Free, and its address is stable.
//
// Alignment: Alloc gives 16 bytes, as malloc on x64; AlignedAlloc any power
// of two. The array is over-allocated and the address rounded up; the table
// maps the address handed out back to its array.
//
// SharpOS cut: the memory counts against the managed heap and its limits
// (in an app, docs/nativeaot-nostd-kernel-limits.md: about 31.75 MiB for one
// large object), not against a separate native heap.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Runtime.InteropServices
{
    public static unsafe class NativeMemory
    {
        private const nuint MallocAlignment = 16;

        // Created on first use, under s_lock: no class constructor.
        private static Dictionary<nint, byte[]> s_blocks = null!;
        private static int s_lock;

        public static void* Alloc(nuint byteCount) => AlignedAllocCore(byteCount, MallocAlignment, zero: false);

        public static void* Alloc(nuint elementCount, nuint elementSize) =>
            AlignedAllocCore(checked(elementCount * elementSize), MallocAlignment, zero: false);

        public static void* AllocZeroed(nuint byteCount) => AlignedAllocCore(byteCount, MallocAlignment, zero: true);

        public static void* AllocZeroed(nuint elementCount, nuint elementSize) =>
            AlignedAllocCore(checked(elementCount * elementSize), MallocAlignment, zero: true);

        public static void* AlignedAlloc(nuint byteCount, nuint alignment)
        {
            if (alignment == 0 || (alignment & (alignment - 1)) != 0)
                throw new ArgumentException("The alignment must be a power of two.", nameof(alignment));
            return AlignedAllocCore(byteCount, alignment, zero: false);
        }

        public static void Free(void* ptr)
        {
            if (ptr == null) return;
            Enter();
            try
            {
                if (s_blocks == null || !s_blocks.Remove((nint)ptr))
                    throw new InvalidOperationException("NativeMemory.Free: not a block NativeMemory allocated.");
            }
            finally
            {
                Exit();
            }
        }

        public static void AlignedFree(void* ptr) => Free(ptr);

        public static void* Realloc(void* ptr, nuint byteCount)
        {
            if (ptr == null) return Alloc(byteCount);
            nuint old = SizeOf(ptr);
            void* fresh = Alloc(byteCount == 0 ? 1 : byteCount);
            Buffer.MemoryCopy(ptr, fresh, (long)byteCount, (long)(old < byteCount ? old : byteCount));
            Free(ptr);
            return fresh;
        }

        public static void* AlignedRealloc(void* ptr, nuint byteCount, nuint alignment)
        {
            if (ptr == null) return AlignedAlloc(byteCount, alignment);
            nuint old = SizeOf(ptr);
            void* fresh = AlignedAlloc(byteCount == 0 ? 1 : byteCount, alignment);
            Buffer.MemoryCopy(ptr, fresh, (long)byteCount, (long)(old < byteCount ? old : byteCount));
            Free(ptr);
            return fresh;
        }

        public static void Clear(void* ptr, nuint byteCount) => Unsafe.InitBlockUnaligned(ptr, 0, checked((uint)byteCount));

        public static void Copy(void* source, void* destination, nuint byteCount) =>
            Buffer.MemoryCopy(source, destination, (long)byteCount, (long)byteCount);

        public static void Fill(void* ptr, nuint byteCount, byte value) => Unsafe.InitBlockUnaligned(ptr, value, checked((uint)byteCount));

        private static void* AlignedAllocCore(nuint byteCount, nuint alignment, bool zero)
        {
            // Zero bytes still gives a unique, freeable address, as malloc(0)
            // may.
            nuint total = checked((byteCount == 0 ? 1 : byteCount) + alignment);
            if (total > int.MaxValue) throw new OutOfMemoryException();
            byte[] block = new byte[(int)total];   // the heap zeroes it; zero is free
            _ = zero;

            byte* start = (byte*)Unsafe.AsPointer(ref block[0]);
            byte* aligned = (byte*)(((nuint)start + alignment - 1) & ~(alignment - 1));

            Enter();
            try
            {
                s_blocks ??= new Dictionary<nint, byte[]>();
                s_blocks.Add((nint)aligned, block);
            }
            finally
            {
                Exit();
            }
            return aligned;
        }

        private static nuint SizeOf(void* ptr)
        {
            Enter();
            try
            {
                if (s_blocks == null || !s_blocks.TryGetValue((nint)ptr, out byte[]? block))
                    throw new InvalidOperationException("NativeMemory: not a block NativeMemory allocated.");
                byte* start = (byte*)Unsafe.AsPointer(ref block[0]);
                return (nuint)(block.Length - ((byte*)ptr - start));
            }
            finally
            {
                Exit();
            }
        }

        private static void Enter()
        {
            SpinWait spin = default;
            while (Interlocked.CompareExchange(ref s_lock, 1, 0) != 0)
                spin.SpinOnce();
        }

        private static void Exit() => Volatile.Write(ref s_lock, 0);
    }
}
