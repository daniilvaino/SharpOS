using System;
using System.Collections.Generic;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// What a copy, a translation and a reverse pass need to know about one
    /// type, worked out once (step195): its key, its size, and where its
    /// references are.
    /// </summary>
    /// <remarks>
    /// Read from the type's table and GCDesc, the same layout ObjectLayout
    /// walks object by object; here it is walked once per type. Offsets are
    /// from the table word, as everywhere in a region.
    /// </remarks>
    public sealed unsafe class TypePlan
    {
        public const byte NoElements = 0;
        public const byte ReferenceElements = 1;
        public const byte StructElements = 2;

        public ulong Table;
        public ulong Key;
        public uint BaseSize;
        public ushort ComponentSize;
        public bool HasComponents;

        /// <summary>Bytes of an object of a fixed-size type, from its table word.</summary>
        public ulong FixedPayload;

        /// <summary>Reference slots of the fixed part, from the table word.</summary>
        public int[] Slots;

        /// <summary>What an array's elements hold: nothing to follow, references, or structs with references.</summary>
        public byte Elements;

        /// <summary>Offset of element 0 from the table word.</summary>
        public int FirstElement;

        /// <summary>Reference slots inside one struct element, from its start.</summary>
        public int[] ElementSlots;

        /// <summary>Bytes of the object from its table word (the record's payload).</summary>
        public ulong PayloadSize(ulong objectAt)
        {
            if (!HasComponents) return FixedPayload;
            ulong total = BaseSize + (ulong)*(uint*)(objectAt + 8) * ComponentSize;
            return ((total + 7) & ~7UL) - 8;
        }

        /// <summary>Elements of an array object (0 for anything else).</summary>
        public uint Length(ulong objectAt) => HasComponents ? *(uint*)(objectAt + 8) : 0;

        internal static TypePlan Make(ulong table, ulong key)
        {
            var plan = new TypePlan
            {
                Table = table,
                Key = key,
                BaseSize = ObjectLayout.BaseSizeOf(table),
                ComponentSize = ObjectLayout.ComponentSizeOf(table),
                HasComponents = (ObjectLayout.FlagsOf(table) & ObjectLayout.HasComponentSizeFlag) != 0,
            };
            plan.FixedPayload = (((ulong)plan.BaseSize + 7) & ~7UL) - 8;
            plan.Slots = s_none;
            plan.ElementSlots = s_none;
            if (!ObjectLayout.HasPointers(table)) return plan;

            long series = ((long*)table)[-1];
            if (series < -64 || series > 64)
                throw new InvalidOperationException("implausible GCDesc series count");

            if (series < 0)
            {
                // An array of structs with references: (pointers, skip) runs
                // inside one element, below the offset of element 0.
                plan.Elements = StructElements;
                plan.FirstElement = (int)((long*)table)[-2];
                long* items = (long*)(table - 16);
                var slots = new List<int>();
                int at = 0;
                for (long i = -1; i >= series; i--)
                {
                    long packed = items[i];
                    uint pointers = (uint)(packed & 0xFFFFFFFF);
                    uint skip = (uint)((packed >> 32) & 0xFFFFFFFF);
                    for (uint k = 0; k < pointers; k++) { slots.Add(at); at += 8; }
                    at += (int)skip;
                }
                plan.ElementSlots = slots.ToArray();
                return plan;
            }

            if (plan.HasComponents)
            {
                // An array of references: one series over the elements, its span
                // relative to the object's size (so it follows the length).
                long* entry = (long*)(table - 8) - 2;
                plan.Elements = ReferenceElements;
                plan.FirstElement = (int)entry[1];
                return plan;
            }

            var fixedSlots = new List<int>();
            long size = (long)((plan.BaseSize + 7) & ~7u);
            for (long i = 1; i <= series; i++)
            {
                long* entry = (long*)(table - 8) - 2 * i;
                long span = entry[0] + size;
                long offset = entry[1];
                if (offset < 0 || span < 0 || offset + span > size) continue;
                for (long k = 0; k < span / 8; k++) fixedSlots.Add((int)(offset + 8 * k));
            }
            plan.Slots = fixedSlots.ToArray();
            return plan;
        }

        private static int[] s_none => Array.Empty<int>();
    }

    /// <summary>
    /// Every declared type's plan, found by its table (a writer) or by its key
    /// (a reader) in O(1) without a dictionary (step195): a perfect hash —
    /// one multiplication, one shift, one comparison.
    /// </summary>
    /// <remarks>
    /// Rebuilt when a type is declared (catalogs are filled once, at first
    /// use) and published as one object, so a lookup on another thread sees
    /// an old index or a new one, never half of each.
    /// </remarks>
    public static unsafe class TypePlans
    {
        private sealed class Index
        {
            public TypePlan[] Slots;
            public ulong Multiplier;
            public int Shift;
        }

        private static List<TypePlan> s_all;
        private static Index s_byTable;
        private static Index s_byKey;
        private static bool s_dirty;

        public static int Count => s_all?.Count ?? 0;

        internal static void Add(ulong table, ulong key)
        {
            s_all ??= new List<TypePlan>();
            s_all.Add(TypePlan.Make(table, key));
            s_dirty = true;
        }

        /// <summary>The plan of a type by its table; null when it is not declared.</summary>
        public static TypePlan ByTable(ulong table)
        {
            Index index = s_dirty ? Rebuild(true) : s_byTable;
            if (index == null) return null;
            TypePlan p = index.Slots[(int)((table * index.Multiplier) >> index.Shift)];
            return p != null && p.Table == table ? p : null;
        }

        /// <summary>The plan of a type by its key; null when no type here has it.</summary>
        public static TypePlan ByKey(ulong key)
        {
            Index index = s_dirty ? Rebuild(false) : s_byKey;
            if (index == null) return null;
            TypePlan p = index.Slots[(int)((key * index.Multiplier) >> index.Shift)];
            return p != null && p.Key == key ? p : null;
        }

        private static Index Rebuild(bool byTable)
        {
            List<TypePlan> all = s_all;
            Index tables = Build(all, true);
            Index keys = Build(all, false);
            s_byTable = tables;
            s_byKey = keys;
            s_dirty = false;
            return byTable ? tables : keys;
        }

        // Multipliers tried in turn until no two plans share a slot; then a
        // table twice the size. Table pointers are 8-aligned, keys are hashes
        // already: the high bits of the product spread both.
        private static Index Build(List<TypePlan> all, bool byTable)
        {
            if (all == null || all.Count == 0) return null;
            int bits = 4;
            while ((1 << bits) < 2 * all.Count) bits++;
            for (; bits < 24; bits++)
            {
                ulong multiplier = 0x9E3779B97F4A7C15UL;
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    var slots = new TypePlan[1 << bits];
                    int shift = 64 - bits;
                    bool clash = false;
                    for (int i = 0; i < all.Count && !clash; i++)
                    {
                        ulong value = byTable ? all[i].Table : all[i].Key;
                        int at = (int)((value * multiplier) >> shift);
                        if (slots[at] != null) clash = true;
                        else slots[at] = all[i];
                    }
                    if (!clash) return new Index { Slots = slots, Multiplier = multiplier, Shift = shift };
                    // The next odd multiplier: a step of a 64-bit LCG, kept odd.
                    multiplier = (multiplier * 6364136223846793005UL + 1442695040888963407UL) | 1;
                }
            }
            throw new InvalidOperationException("no perfect hash for the type plans");
        }
    }
}
