// GcMemorySource of an app: addresses from the process's heap range, pages
// from the kernel as the heap grows (step196).
//
// Until step196 this was a 64 MiB struct in the image's .bss: every start
// zeroed and mapped all of it (half of a start's time on the laptop), and
// the heap carved it into 256 KiB segments for good — once it was carved,
// nothing larger than a segment could be allocated, however much was free.
//
// Now the kernel gives a range of addresses (HeapBase/HeapBytes, 640 MiB)
// and a budget of pages (HeapBudget, 64 MiB). A block takes addresses —
// freed ones first, first fit, else fresh — and asks for zeroed pages under
// them (HeapCommit); a block the collector lets go (an empty segment) gives
// its pages back (HeapRelease) and its addresses to the free list. A large
// object is a segment of its own size, so it can be had at any time while
// the budget lasts. Linked into apps only; the kernel has its own variant.

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GcMemorySource
    {
        private const ulong PageSize = 4096;
        private const int MaxFreeRanges = 256;

        // Freed address ranges, kept sorted by address and merged with
        // neighbours. A fixed table outside the managed heap: the heap is
        // what asks for blocks, it cannot be the place this lives.
        [StructLayout(LayoutKind.Sequential)]
        private struct FreeRanges
        {
            public fixed ulong Start[MaxFreeRanges];
            public fixed ulong Bytes[MaxFreeRanges];
        }

        private static FreeRanges s_free;
        private static int s_freeCount;
        private static ulong s_base;
        private static ulong s_end;
        private static ulong s_cursor;
        private static ulong s_budget;
        private static ulong s_committed;
        private static delegate* unmanaged<ulong, ulong, int> s_commit;
        private static delegate* unmanaged<ulong, ulong, int> s_release;

        public static bool IsInitialized => EnsureRange();

        /// <summary>Blocks a collection may give back: the kernel takes the pages.</summary>
        public static bool CanRelease => true;

        /// <summary>Bytes of pages the heap holds now.</summary>
        public static ulong Committed => s_committed;

        public static ulong Budget => s_budget;

        private static bool EnsureRange()
        {
            if (s_base != 0) return true;
            SharpOS.AppSdk.AppServiceTable* services = SharpOS.AppSdk.AppRuntime.Services;
            if (services == null || services->HeapBase == 0 || services->HeapCommitAddress == 0)
                return false;
            s_base = services->HeapBase;
            s_end = s_base + services->HeapBytes;
            s_cursor = s_base;
            s_budget = services->HeapBudget;
            s_commit = (delegate* unmanaged<ulong, ulong, int>)services->HeapCommitAddress;
            s_release = (delegate* unmanaged<ulong, ulong, int>)services->HeapReleaseAddress;
            return true;
        }

        public static void* AllocateBlock(uint size)
        {
            if (!EnsureRange()) return null;
            ulong bytes = ((ulong)size + PageSize - 1) & ~(PageSize - 1);
            if (s_committed + bytes > s_budget) return null;

            ulong at = TakeFree(bytes);
            if (at == 0)
            {
                if (s_cursor + bytes > s_end) return null;
                at = s_cursor;
                s_cursor += bytes;
            }
            if (s_commit(at, bytes) != 0)
            {
                GiveFree(at, bytes);
                return null;
            }
            s_committed += bytes;
            return (void*)at;
        }

        /// <summary>Gives a block back: its pages to the kernel, its addresses to the free list.</summary>
        public static void ReleaseBlock(void* block, uint size)
        {
            ulong at = (ulong)block;
            ulong bytes = ((ulong)size + PageSize - 1) & ~(PageSize - 1);
            if (s_release == null || s_release(at, bytes) != 0) return;
            s_committed -= bytes;
            GiveFree(at, bytes);
        }

        // First fit; the rest of the range stays free.
        private static ulong TakeFree(ulong bytes)
        {
            for (int i = 0; i < s_freeCount; i++)
            {
                if (s_free.Bytes[i] < bytes) continue;
                ulong at = s_free.Start[i];
                s_free.Start[i] += bytes;
                s_free.Bytes[i] -= bytes;
                if (s_free.Bytes[i] == 0) RemoveAt(i);
                return at;
            }
            return 0;
        }

        private static void GiveFree(ulong at, ulong bytes)
        {
            // The range at the cursor's edge just moves the cursor back.
            if (at + bytes == s_cursor)
            {
                s_cursor = at;
                // ...and a free range now at the edge goes with it.
                if (s_freeCount > 0 && s_free.Start[s_freeCount - 1] + s_free.Bytes[s_freeCount - 1] == s_cursor)
                {
                    s_cursor = s_free.Start[s_freeCount - 1];
                    s_freeCount--;
                }
                return;
            }
            int i = 0;
            while (i < s_freeCount && s_free.Start[i] < at) i++;
            bool joinsPrevious = i > 0 && s_free.Start[i - 1] + s_free.Bytes[i - 1] == at;
            bool joinsNext = i < s_freeCount && at + bytes == s_free.Start[i];
            if (joinsPrevious && joinsNext)
            {
                s_free.Bytes[i - 1] += bytes + s_free.Bytes[i];
                RemoveAt(i);
            }
            else if (joinsPrevious) s_free.Bytes[i - 1] += bytes;
            else if (joinsNext)
            {
                s_free.Start[i] = at;
                s_free.Bytes[i] += bytes;
            }
            else if (s_freeCount < MaxFreeRanges)
            {
                for (int k = s_freeCount; k > i; k--)
                {
                    s_free.Start[k] = s_free.Start[k - 1];
                    s_free.Bytes[k] = s_free.Bytes[k - 1];
                }
                s_free.Start[i] = at;
                s_free.Bytes[i] = bytes;
                s_freeCount++;
            }
            // A full table drops the range's addresses — the pages are back
            // already; only addresses, of which there are 640 MiB, are lost.
        }

        private static void RemoveAt(int i)
        {
            for (int k = i; k < s_freeCount - 1; k++)
            {
                s_free.Start[k] = s_free.Start[k + 1];
                s_free.Bytes[k] = s_free.Bytes[k + 1];
            }
            s_freeCount--;
        }
    }
}
