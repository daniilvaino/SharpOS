namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// Where the exchange heap lives, as every image sees it: one contiguous
    /// arena, a page table, and the block header format.
    /// </summary>
    /// <remarks>
    /// The kernel owns the heap (OS.Kernel.Memory.ExchangeHeap) and installs
    /// these values; an app installs the same values from its service table.
    /// They are what the write barrier needs: whether a destination lies in the
    /// arena at all — one subtraction and one compare — and, when it does, the
    /// bounds of the block it lies in.
    ///
    /// Page table entry, one uint per arena page: zero — never carved; else
    /// bits 0..23 = first page of the unit + 1, bits 24..31 = size class of a
    /// chunk of small blocks, or 0xFF for a run holding one large block.
    ///
    /// Block header, 32 bytes before the payload: magic (uint, +0), class
    /// (byte, +4), live (byte, +5), owner (uint, +8), pages (uint, +12).
    /// </remarks>
    public static unsafe class ExchangeArena
    {
        public const int HeaderSize = 32;
        public const uint Magic = 0x58434847;
        public const int MinClassShift = 6;
        public const byte LargeClass = 0xFF;
        public const int PageShift = 12;

        /// <summary>
        /// The address a refused store into a region touches on purpose. Never
        /// mapped; the fault handler turns a page fault here into a
        /// RegionReferenceException of the image whose code faulted.
        /// </summary>
        /// <remarks>
        /// Why a fault and not a throw: compiled code calls the write barrier
        /// from methods that set up no frame, with the stack 8 bytes off the
        /// alignment a call needs — a real barrier is a leaf and does not care.
        /// A throw from there reached the kernel's dispatcher misaligned, and
        /// its first aligned SSE store faulted. The CPU aligns the stack on a
        /// fault, and the unwind starts at the barrier, which has unwind data.
        /// </remarks>
        public const ulong StoreFaultAddress = 0x0000_7E00_0BAD_0000UL;

        /// <summary>The arena's bounds, side by side: the barrier shellcode reads both through one address.</summary>
        public struct ArenaBounds
        {
            public ulong Low;
            public ulong Span;
        }

        /// <summary>Zero before the arena is installed, so nothing is inside.</summary>
        public static ArenaBounds Bounds;

        /// <summary>First byte of the arena.</summary>
        public static ulong Low => Bounds.Low;

        /// <summary>Bytes in the arena.</summary>
        public static ulong Span => Bounds.Span;

        /// <summary>Where the bounds live, for the barrier shellcode.</summary>
        public static ArenaBounds* BoundsAddress
        {
            get
            {
                fixed (ArenaBounds* b = &Bounds)
                    return b;
            }
        }

        /// <summary>One entry per page.</summary>
        public static uint* Pages;

        public static void Install(ulong low, ulong span, uint* pages)
        {
            Pages = pages;
            Bounds.Low = low;
            Bounds.Span = span;
        }

        public static bool Contains(ulong address) => address - Bounds.Low < Bounds.Span;

        /// <summary>
        /// Whether <paramref name="value"/> may be stored at <paramref name="destination"/>,
        /// an address in the arena: null, or an address in the same live block
        /// (pipe spec, writing into a region). A destination in no live block
        /// takes nothing.
        /// </summary>
        public static bool StoreAllowed(ulong destination, ulong value)
        {
            if (value == 0) return true;
            if (!TryBlock(destination, out ulong payload, out ulong end)) return false;
            return value >= payload && value < end;
        }

        /// <summary>
        /// The payload bounds of the live block that holds <paramref name="address"/>.
        /// False when the address is in no live block (a header, a free block,
        /// a page never carved, or outside the arena).
        /// </summary>
        public static bool TryBlock(ulong address, out ulong payload, out ulong end)
        {
            payload = 0;
            end = 0;
            if (!Contains(address) || Pages == null)
                return false;

            uint entry = Pages[(address - Low) >> PageShift];
            if (entry == 0)
                return false;
            ulong unit = Low + ((ulong)((entry & 0xFFFFFF) - 1) << PageShift);
            byte cls = (byte)(entry >> 24);

            ulong block;
            ulong bytes;
            if (cls == LargeClass)
            {
                block = unit;
                bytes = (ulong)*(uint*)(block + 12) << PageShift;
            }
            else
            {
                bytes = 1UL << (MinClassShift + cls);
                block = unit + (address - unit) / bytes * bytes;
            }

            if (*(uint*)block != Magic || *(byte*)(block + 5) != 1)
                return false;
            payload = block + HeaderSize;
            end = block + bytes;
            return address >= payload;
        }
    }
}
