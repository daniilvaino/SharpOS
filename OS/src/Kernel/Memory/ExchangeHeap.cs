using OS.Kernel.Threading;

namespace OS.Kernel.Memory
{
    // Memory that belongs to no collector: the exchange heap.
    //
    // What goes between programs (pipe regions, pipe_plan.md "Подготовить под
    // трубы", item 1) must outlive the allocation of its writer, must not be
    // traced by the reader's collector, and must not be swept by anybody's.
    // Every collector here drops addresses outside its own segments, so memory
    // outside all of them is exactly that — and it is not KernelHeap either,
    // whose blocks are the kernel collector's segments.
    //
    // Pages come from PhysicalMemory and are identity-mapped (as DmaMemory
    // does): one address for the kernel and for every app, since all of them
    // run on the same page tables. Only pages below 4 GiB are taken: an app's
    // image is mapped at 0x1_0000_0000, and an identity page there would be
    // unmapped from under us by the next launch.
    //
    // Every block has an owner. A process's blocks go back to the pool when it
    // ends (ProcessResources), whether or not it freed them.
    //
    // Layout: [header 32][payload]. Small blocks come from 64 KiB chunks cut
    // into one size class each; a request past the largest class gets a page
    // run of its own. Freed blocks of either kind are kept for reuse, never
    // handed back to PhysicalMemory.
    internal static unsafe class ExchangeHeap
    {
        public const uint OwnerKernel = 0xFFFFFFFF;

        private const uint Magic = 0x58434847;          // "GHCX"
        private const int HeaderSize = 32;
        private const int ClassCount = 10;               // 64 B .. 32 KiB
        private const int MinClassShift = 6;
        private const uint ChunkBytes = 64 * 1024;
        private const ulong PageSize = 4096;
        private const ulong IdentityLimit = 0x1_0000_0000UL;
        private const int MaxRanges = 512;
        private const byte LargeClass = 0xFF;

        private struct Header
        {
            public uint Magic;
            public byte Class;
            public byte Live;
            public ushort Reserved;
            public uint Owner;
            public uint Pages;          // large blocks: pages in the run
            public Header* Prev;        // live list, or the free list it is on
            public Header* Next;
        }

        private unsafe struct Lists
        {
            public fixed ulong FreeHead[ClassCount];
        }

        private unsafe struct RangeTable
        {
            public fixed ulong Start[MaxRanges];
            public fixed ulong End[MaxRanges];
        }

        private static Lists s_lists;
        private static Header* s_largeFree;
        private static Header* s_live;
        private static RangeTable s_ranges;
        private static int s_rangeCount;

        private static ulong s_liveBlocks;
        private static ulong s_liveBytes;
        private static ulong s_reservedBytes;

        public static ulong LiveBlocks => s_liveBlocks;
        public static ulong LiveBytes => s_liveBytes;
        public static ulong ReservedBytes => s_reservedBytes;

        /// <summary>The largest payload a small block holds.</summary>
        public static uint MaxSmallPayload => (1u << (MinClassShift + ClassCount - 1)) - HeaderSize;

        /// <summary>
        /// A zeroed block of at least <paramref name="size"/> bytes, owned by
        /// <paramref name="owner"/> (an app generation, or OwnerKernel). Null
        /// when there is no memory.
        /// </summary>
        public static void* Allocate(ulong size, uint owner)
        {
            if (size == 0 || owner == 0) return null;

            Preemption.Suppress();
            try
            {
                Header* h = size <= MaxSmallPayload
                    ? TakeSmall(ClassFor(size))
                    : TakeLarge(size);
                if (h == null) return null;

                h->Live = 1;
                h->Owner = owner;
                LinkLive(h);
                s_liveBlocks++;
                s_liveBytes += BlockBytes(h);

                byte* payload = (byte*)h + HeaderSize;
                OS.Kernel.Util.Memory.Zero(payload, (uint)(BlockBytes(h) - HeaderSize));
                return payload;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>False when the pointer is not a live block of this heap.</summary>
        public static bool Free(void* payload)
        {
            Preemption.Suppress();
            try
            {
                Header* h = HeaderOf(payload);
                if (h == null) return false;
                Release(h);
                return true;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>Hands a live block to another owner (a move between programs).</summary>
        public static bool SetOwner(void* payload, uint owner)
        {
            if (owner == 0) return false;
            Preemption.Suppress();
            try
            {
                Header* h = HeaderOf(payload);
                if (h == null) return false;
                h->Owner = owner;
                return true;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>The owner of a live block; zero when it is not one.</summary>
        public static uint OwnerOf(void* payload)
        {
            Preemption.Suppress();
            try
            {
                Header* h = HeaderOf(payload);
                return h == null ? 0 : h->Owner;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>Usable bytes of a live block; zero when it is not one.</summary>
        public static ulong SizeOf(void* payload)
        {
            Preemption.Suppress();
            try
            {
                Header* h = HeaderOf(payload);
                return h == null ? 0 : BlockBytes(h) - HeaderSize;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>Every block the owner still holds goes back. Returns how many.</summary>
        public static int ReleaseOwner(uint owner)
        {
            if (owner == 0) return 0;
            int released = 0;
            Preemption.Suppress();
            try
            {
                Header* h = s_live;
                while (h != null)
                {
                    Header* next = h->Next;
                    if (h->Owner == owner)
                    {
                        Release(h);
                        released++;
                    }
                    h = next;
                }
            }
            finally
            {
                Preemption.Allow();
            }
            return released;
        }

        /// <summary>
        /// Whether an address lies in memory this heap reserved. For checking a
        /// pointer an app hands over before reading a header through it.
        /// </summary>
        public static bool Contains(ulong address)
        {
            fixed (RangeTable* r = &s_ranges)
                for (int i = 0; i < s_rangeCount; i++)
                    if (address >= r->Start[i] && address < r->End[i])
                        return true;
            return false;
        }

        // ---- internals; callers hold the suppression ----

        private static int ClassFor(ulong size)
        {
            ulong need = size + HeaderSize;
            int c = 0;
            while ((1UL << (MinClassShift + c)) < need) c++;
            return c;
        }

        private static ulong BlockBytes(Header* h)
            => h->Class == LargeClass ? h->Pages * PageSize : 1UL << (MinClassShift + h->Class);

        // A live header only: inside a reserved range, magic in place, marked
        // live. Anything else — a stale pointer, a pointer into a payload, an
        // address an app made up — is refused without being written.
        private static Header* HeaderOf(void* payload)
        {
            ulong p = (ulong)payload;
            if (p < HeaderSize || !Contains(p - HeaderSize)) return null;
            Header* h = (Header*)(p - HeaderSize);
            if (h->Magic != Magic || h->Live != 1) return null;
            return h;
        }

        private static void Release(Header* h)
        {
            UnlinkLive(h);
            h->Live = 0;
            h->Owner = 0;
            s_liveBlocks--;
            s_liveBytes -= BlockBytes(h);

            if (h->Class == LargeClass)
            {
                h->Next = s_largeFree;
                s_largeFree = h;
                return;
            }
            fixed (Lists* l = &s_lists)
            {
                h->Next = (Header*)l->FreeHead[h->Class];
                l->FreeHead[h->Class] = (ulong)h;
            }
        }

        private static Header* TakeSmall(int cls)
        {
            fixed (Lists* l = &s_lists)
            {
                if (l->FreeHead[cls] == 0 && !CutChunk(cls))
                    return null;
                Header* h = (Header*)l->FreeHead[cls];
                l->FreeHead[cls] = (ulong)h->Next;
                h->Next = null;
                return h;
            }
        }

        private static bool CutChunk(int cls)
        {
            ulong chunk = Reserve(ChunkBytes / PageSize);
            if (chunk == 0) return false;

            uint block = 1u << (MinClassShift + cls);
            fixed (Lists* l = &s_lists)
            {
                for (ulong at = chunk + ChunkBytes - block; ; at -= block)
                {
                    Header* h = (Header*)at;
                    h->Magic = Magic;
                    h->Class = (byte)cls;
                    h->Live = 0;
                    h->Next = (Header*)l->FreeHead[cls];
                    l->FreeHead[cls] = at;
                    if (at == chunk) break;
                }
            }
            return true;
        }

        private static Header* TakeLarge(ulong size)
        {
            ulong pages = (size + HeaderSize + PageSize - 1) / PageSize;

            // First fit among freed runs.
            Header* prev = null;
            for (Header* h = s_largeFree; h != null; prev = h, h = h->Next)
            {
                if (h->Pages < pages) continue;
                if (prev == null) s_largeFree = h->Next;
                else prev->Next = h->Next;
                h->Next = null;
                return h;
            }

            ulong run = Reserve(pages);
            if (run == 0) return null;
            Header* fresh = (Header*)run;
            fresh->Magic = Magic;
            fresh->Class = LargeClass;
            fresh->Pages = (uint)pages;
            return fresh;
        }

        // Identity-mapped, below 4 GiB, recorded as ours.
        private static ulong Reserve(ulong pages)
        {
            if (s_rangeCount >= MaxRanges) return 0;

            ulong phys = PhysicalMemory.AllocPages((uint)pages);
            if (phys == 0) return 0;
            ulong bytes = pages * PageSize;
            if (phys + bytes > IdentityLimit)
            {
                for (ulong i = 0; i < pages; i++)
                    PhysicalMemory.FreePage(phys + i * PageSize);
                return 0;
            }
            if (!VirtualMemory.MapFixed((void*)phys, phys, bytes, exec: false))
                return 0;

            fixed (RangeTable* r = &s_ranges)
            {
                r->Start[s_rangeCount] = phys;
                r->End[s_rangeCount] = phys + bytes;
            }
            s_rangeCount++;
            s_reservedBytes += bytes;
            return phys;
        }

        private static void LinkLive(Header* h)
        {
            h->Prev = null;
            h->Next = s_live;
            if (s_live != null) s_live->Prev = h;
            s_live = h;
        }

        private static void UnlinkLive(Header* h)
        {
            if (h->Prev != null) h->Prev->Next = h->Next;
            else s_live = h->Next;
            if (h->Next != null) h->Next->Prev = h->Prev;
            h->Prev = null;
            h->Next = null;
        }
    }
}
