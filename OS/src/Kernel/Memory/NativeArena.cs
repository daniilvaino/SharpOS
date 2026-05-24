using OS.Hal;
using OS.Kernel;

namespace OS.Kernel.Memory
{
    // Page-backed bump arena for non-managed native blobs that have
    // no business living in the kernel GcHeap: CoreCLR CRT mallocs,
    // PE file image buffers, TEB/TLS blocks, SEH dispatcher structs,
    // TPA buffer, fake-file FileState handles. They share three
    // properties:
    //   - never freed (lifetime = forever, like the old "free no-op"),
    //   - have no MethodTable (GC mark walker can't reason about them),
    //   - can hold native pointers internally (PE blobs especially),
    //     which would be mis-traced as OBJECTREF by a kernel GC walk.
    //
    // Sitting them in GcHeap is the historical reason kernel GC sweep
    // had to be frozen via `GC.ReclamationDisabled = true` for the
    // whole hosted session (memory-ownership.md §3, §7.1). Routing
    // them through NativeArena removes that requirement at the source:
    // GcHeap then contains only real managed kernel objects, mark/sweep
    // becomes safe again, and `ReclamationDisabled` can be lifted (M5).
    //
    // Allocation policy:
    //   - small/mid (< ChunkBytes): bump within a 64 KiB chunk that
    //     refills from `PhysicalMemory.AllocPages` on demand,
    //   - large (>= ChunkBytes): own page run via `PhysicalMemory.
    //     AllocPages` directly (e.g. the 22.5 MiB CoreLib buffer),
    //   - everything goes through `VirtualMemory.MapFixed` identity-map
    //     so virt == phys and the buffer is plain RW kernel memory,
    //   - allocations are zero-filled to match the old `GcHeap.
    //     AllocateRaw` semantics callers relied on.
    //
    // No lock — same single-threaded discipline as KernelHeap (memory-
    // ownership.md §7.5). SMP/preempt will need one alongside the
    // KernelHeap lock.
    internal static unsafe class NativeArena
    {
        private const int ChunkBytes = 64 * 1024;
        private const ulong PageSize = 4096UL;

        private static byte* s_cursor;
        private static ulong s_remaining;

        // Diagnostics — what the arena has handed out so far.
        private static ulong s_totalBytes;
        private static uint  s_chunkCount;
        private static uint  s_directCount;
        private static uint  s_allocCount;

        public static ulong TotalBytes  => s_totalBytes;
        public static uint  ChunkCount  => s_chunkCount;
        public static uint  DirectCount => s_directCount;
        public static uint  AllocCount  => s_allocCount;

        public static void* Allocate(ulong size)
        {
            if (size == 0) size = 1;

            // Alignment policy: pick the natural alignment of the
            // allocation size, capped at PageSize. Reasons:
            //   - 16-byte minimum: CoreCLR Context/ExceptionRecord embed
            //     XMM_SAVE_AREA32, MOVAPS faults #GP on misaligned access,
            //   - page-aligned for >= 4096-byte allocs: TEB layout and
            //     gs-base addressing assume the structure starts at a
            //     page, and PE buffers are page-walked for RWX flip,
            //   - matches what GcHeap accidentally provided via segment
            //     base alignment + sequential same-sized allocs (which
            //     is why this didn't blow up before NativeArena split
            //     the lifetimes apart).
            ulong align = NaturalAlignment(size);
            ulong mask = align - 1UL;
            size = (size + mask) & ~mask;

            s_allocCount++;

            if (size >= (ulong)ChunkBytes)
                return AllocateDedicated(size);

            if (s_remaining < size)
            {
                if (!RefillChunk())
                    return null;
            }

            // Bump cursor to the alignment boundary before consuming.
            ulong curVa = (ulong)s_cursor;
            ulong alignedVa = (curVa + mask) & ~mask;
            ulong skip = alignedVa - curVa;
            if (skip > 0)
            {
                if (skip > s_remaining)
                {
                    if (!RefillChunk()) return null;
                    curVa = (ulong)s_cursor;
                    alignedVa = (curVa + mask) & ~mask;
                    skip = alignedVa - curVa;
                }
                s_cursor += skip;
                s_remaining -= skip;
            }
            if (s_remaining < size)
            {
                if (!RefillChunk()) return null;
            }

            void* result = s_cursor;
            s_cursor += size;
            s_remaining -= size;
            return result;
        }

        private static ulong NaturalAlignment(ulong size)
        {
            // 16-byte minimum is the SSE MOVAPS / fxsave requirement.
            // Anything >= one page (TEB, Context arrays, PE buffers)
            // gets page alignment — TEB layout and the FileOpen RWX
            // page-walk both assume page boundaries.
            if (size >= PageSize) return PageSize;
            return 16UL;
        }

        private static void* AllocateDedicated(ulong size)
        {
            ulong bytes = (size + PageSize - 1UL) & ~(PageSize - 1UL);
            uint pages = (uint)(bytes / PageSize);
            ulong phys = PhysicalMemory.AllocPages(pages);
            if (phys == 0) return null;
            if (!VirtualMemory.MapFixed((void*)phys, phys, bytes, exec: false))
                return null;
            ZeroFill((byte*)phys, bytes);
            s_totalBytes += bytes;
            s_directCount++;
            return (void*)phys;
        }

        private static bool RefillChunk()
        {
            const ulong bytes = (ulong)ChunkBytes;
            uint pages = (uint)(bytes / PageSize);
            ulong phys = PhysicalMemory.AllocPages(pages);
            if (phys == 0) return false;
            if (!VirtualMemory.MapFixed((void*)phys, phys, bytes, exec: false))
                return false;
            ZeroFill((byte*)phys, bytes);
            s_cursor = (byte*)phys;
            s_remaining = bytes;
            s_totalBytes += bytes;
            s_chunkCount++;
            return true;
        }

        private static void ZeroFill(byte* p, ulong n)
        {
            for (ulong i = 0; i < n; i++) p[i] = 0;
        }
    }
}
