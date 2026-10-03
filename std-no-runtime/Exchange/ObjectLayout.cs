using System;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// Reads an object's layout from its MethodTable and GCDesc: size, flags and
    /// the slots that hold references.
    /// </summary>
    /// <remarks>
    /// Shared by every image — kernel and apps — because the receiving side of a
    /// region and the sending side must read one layout one way. Offsets are those
    /// of GC/MethodTable.cs: component size at 0, flags at 2, base size at 4.
    ///
    /// TableOf masks bit 0: both collectors keep their mark there, and a table
    /// read while an object is marked would come back odd. A region's type key is
    /// odd by construction, but it is read by plain dereference, not through
    /// TableOf.
    /// </remarks>
    public static unsafe class ObjectLayout
    {
        public const int BaseSizeOffset = 4;
        public const int FlagsOffset = 2;

        public const ushort HasComponentSizeFlag = 0x8000;
        public const ushort HasPointersFlag = 0x0020;

        public static ulong TableOf(ulong obj) => obj == 0 ? 0 : *(ulong*)obj & ~1UL;

        public static ushort FlagsOf(ulong table) => *(ushort*)(table + FlagsOffset);

        public static ushort ComponentSizeOf(ulong table) => *(ushort*)table;

        public static uint BaseSizeOf(ulong table) => *(uint*)(table + BaseSizeOffset);

        public static bool HasPointers(ulong table) => (FlagsOf(table) & HasPointersFlag) != 0;

        /// <summary>Size of the object by its table and, for arrays and strings, its length; 8-aligned.</summary>
        public static uint SizeOf(ulong obj) => SizeOf(obj, TableOf(obj));

        /// <summary>The same, with the table given — for an object whose table word holds a key.</summary>
        public static uint SizeOf(ulong obj, ulong table)
        {
            uint size = BaseSizeOf(table);
            if ((FlagsOf(table) & HasComponentSizeFlag) != 0)
                size += *(uint*)(obj + 8) * ComponentSizeOf(table);
            return (size + 7) & ~7u;
        }

        /// <summary>Addresses of the non-null reference slots, so they can be rewritten in place.</summary>
        public static int ReferenceSlots(ulong obj, ulong table, ulong[] into)
            => Enumerate(obj, table, into, slots: true);

        /// <summary>Values of the non-null references — enough to walk a graph.</summary>
        public static int References(ulong obj, ulong[] into)
            => Enumerate(obj, TableOf(obj), into, slots: false);

        /// <summary>How many reference slots the object can have at most: the buffer size a walk needs.</summary>
        public static int MaxReferences(ulong obj, ulong table) => (int)(SizeOf(obj, table) / 8);

        private static int Enumerate(ulong obj, ulong table, ulong[] into, bool slots)
        {
            int found = 0;
            if (!HasPointers(table))
                return 0;

            long series = ((long*)table)[-1];
            if (series < -64 || series > 64)
                throw new InvalidOperationException("implausible GCDesc series count");

            if (series < 0)
                return ValueTypeArrayReferences(obj, table, series, into, slots);

            uint size = SizeOf(obj, table);
            for (long i = 1; i <= series; i++)
            {
                long* entry = (long*)(table - 8) - 2 * i;
                long span = entry[0] + size;
                long offset = entry[1];
                if (offset < 0 || span < 0 || (ulong)(offset + span) > size)
                    continue;

                ulong* slot = (ulong*)(obj + (ulong)offset);
                for (long k = 0; k < span / 8; k++)
                {
                    if (slot[k] == 0)
                        continue;
                    if (found >= into.Length)
                        throw new InvalidOperationException("reference buffer too small");
                    into[found++] = slots ? (ulong)(slot + k) : slot[k];
                }
            }
            return found;
        }

        // An array of structs that hold references: the series count is
        // negative, table[-2] is the offset of the first element, and below it
        // one (pointers, skip) pair per run inside an element. The same shape
        // GcObject.EnumerateObjectReferences walks.
        private static int ValueTypeArrayReferences(ulong obj, ulong table, long series, ulong[] into, bool slots)
        {
            int found = 0;
            long offset = ((long*)table)[-2];
            long* items = (long*)(table - 16);
            uint length = *(uint*)(obj + 8);

            ulong* slot = (ulong*)(obj + (ulong)offset);
            for (uint element = 0; element < length; element++)
            {
                for (long i = -1; i >= series; i--)
                {
                    long packed = items[i];
                    uint pointers = (uint)(packed & 0xFFFFFFFF);
                    uint skip = (uint)((packed >> 32) & 0xFFFFFFFF);

                    for (uint k = 0; k < pointers; k++)
                    {
                        if (*slot != 0)
                        {
                            if (found >= into.Length)
                                throw new InvalidOperationException("reference buffer too small");
                            into[found++] = slots ? (ulong)slot : *slot;
                        }
                        slot++;
                    }
                    slot = (ulong*)((byte*)slot + skip);
                }
            }
            return found;
        }
    }
}
