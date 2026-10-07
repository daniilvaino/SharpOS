using System;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// Translates received regions in place and back (step195): keys to this
    /// image's tables and offsets to addresses, and the reverse before a
    /// region goes on. All or nothing either way. Reusable: its buffers grow
    /// to the largest region it has seen and are not allocated again. One per
    /// reader (or writer, for the reverse); not for two threads at once.
    /// </summary>
    /// <remarks>
    /// Three passes for a translation. The first finds every record's plan by
    /// its key and its bounds, and marks where records start in a bitmap (a
    /// bit per 8 bytes of the block). The second checks every reference
    /// against the bitmap. Only the third writes — a refusal leaves the block
    /// exactly as it came.
    /// </remarks>
    public sealed unsafe class RegionReader
    {
        // In pieces (Chunked), for the same reason as the writer's.
        // Plans by address (see RegionWriter): held by TypePlans for good.
        private Chunked<ulong> _plans = new Chunked<ulong>(64);
        private Chunked<ulong> _records = new Chunked<ulong>(64);
        private int _count;
        private Chunked<ulong> _starts = new Chunked<ulong>(16);

        // Neighbouring records are mostly of one type: the last plan, by key
        // and by table, answers before the index is asked.
        private ulong _lastKey;
        private TypePlan _lastKeyPlan;
        private ulong _lastTable;
        private TypePlan _lastTablePlan;

        /// <summary>Why the last pass refused.</summary>
        public string Complaint { get; private set; }

        /// <summary>The key no type here has, when that was the refusal.</summary>
        public ulong MissingKey { get; private set; }

        /// <summary>Translates a region in place; false, with a complaint, and the block untouched otherwise.</summary>
        public bool Resolve(byte* at, ulong size, out object root)
        {
            root = null;
            Complaint = null;
            MissingKey = 0;
            if (size < Region.HeaderSize + 8)
                return Refuse("region too small");

            int words = (int)((size / 8 + 63) / 64);
            _starts.Ensure(words);
            _starts.Clear(words);

            _count = 0;
            for (ulong cursor = 0; cursor < size;)
            {
                if (size - cursor < Region.HeaderSize + 16)
                    return Refuse($"truncated record at {cursor}");
                ulong objectAt = (ulong)at + cursor + Region.HeaderSize;
                ulong key = *(ulong*)objectAt;
                TypePlan plan = key == _lastKey ? _lastKeyPlan : TypePlans.ByKey(key);
                if (plan == null)
                {
                    MissingKey = key;
                    return Refuse($"key 0x{key:x} not declared here (record at {cursor})");
                }
                _lastKey = key;
                _lastKeyPlan = plan;
                ulong next = cursor + Region.HeaderSize + plan.PayloadSize(objectAt);
                if (next > size || next <= cursor)
                    return Refuse($"record at {cursor} runs past the region ({next} > {size})");
                ulong start = (cursor + Region.HeaderSize) >> 3;
                _starts[(int)(start >> 6)] |= 1UL << (int)(start & 63);
                Note(objectAt, plan);
                cursor = next;
            }

            // Each reference is the table word of a record: a forged offset
            // would otherwise become a pointer into the middle of an object,
            // or out of the region.
            for (int i = 0; i < _count; i++)
                if (!CheckTargets(_records[i], PlanAt(i), size, out ulong bad))
                    return Refuse($"reference {bad} in record at {_records[i] - (ulong)at - Region.HeaderSize} is not a record");

            for (int i = 0; i < _count; i++)
            {
                Shift(_records[i], PlanAt(i), (ulong)at, add: true);
                *(ulong*)_records[i] = PlanAt(i).Table;
            }

            ulong rootAt = (ulong)at + Region.HeaderSize;
            root = System.Runtime.CompilerServices.Unsafe.As<ulong, object>(ref rootAt);
            return true;
        }

        /// <summary>
        /// The reverse, before a region goes on: tables back to keys, addresses
        /// back to offsets. Every table must be in the catalog and every
        /// reference inside the block before the first write.
        /// </summary>
        public bool Release(byte* at, ulong size)
        {
            Complaint = null;
            ulong low = (ulong)at;
            ulong high = low + size;
            _count = 0;
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + Region.HeaderSize;
                ulong table = *(ulong*)objectAt & ~1UL;
                TypePlan plan = table == _lastTable ? _lastTablePlan : TypePlans.ByTable(table);
                if (plan == null)
                    return Refuse($"record at {cursor} has a type outside the catalog (table 0x{table:x})");
                _lastTable = table;
                _lastTablePlan = plan;
                if (!CheckInside(objectAt, plan, low + Region.HeaderSize, high, out ulong bad))
                    return Refuse($"record at {cursor} refers outside the block (0x{bad:x})");
                Note(objectAt, plan);
                cursor += Region.HeaderSize + plan.PayloadSize(objectAt);
            }
            for (int i = 0; i < _count; i++)
            {
                Shift(_records[i], PlanAt(i), low, add: false);
                *(ulong*)_records[i] = PlanAt(i).Key;
            }
            return true;
        }

        private TypePlan PlanAt(int i)
        {
            ulong at = _plans[i];
            return System.Runtime.CompilerServices.Unsafe.As<ulong, TypePlan>(ref at);
        }

        private static ulong AddressOf(object o)
            => System.Runtime.CompilerServices.Unsafe.As<object, ulong>(ref o);

        private bool Refuse(string why)
        {
            Complaint = why;
            return false;
        }

        private void Note(ulong objectAt, TypePlan plan)
        {
            if (_count == _plans.Capacity)
            {
                _plans.Ensure(_count * 2);
                _records.Ensure(_count * 2);
            }
            _plans[_count] = AddressOf(plan);
            _records[_count++] = objectAt;
        }

        private bool IsStart(ulong target, ulong size)
        {
            if (target >= size || (target & 7) != 0) return false;
            ulong bit = target >> 3;
            return (_starts[(int)(bit >> 6)] & (1UL << (int)(bit & 63))) != 0;
        }

        private bool CheckTargets(ulong obj, TypePlan plan, ulong size, out ulong bad)
        {
            bad = 0;
            int[] slots = plan.Slots;
            for (int s = 0; s < slots.Length; s++)
            {
                ulong t = *(ulong*)(obj + (ulong)slots[s]);
                if (t != 0 && !IsStart(t, size)) { bad = t; return false; }
            }
            if (plan.Elements == TypePlan.ReferenceElements)
            {
                ulong* element = (ulong*)(obj + (ulong)plan.FirstElement);
                uint length = *(uint*)(obj + 8);
                for (uint k = 0; k < length; k++)
                    if (element[k] != 0 && !IsStart(element[k], size)) { bad = element[k]; return false; }
            }
            else if (plan.Elements == TypePlan.StructElements)
            {
                int[] inner = plan.ElementSlots;
                ulong element = obj + (ulong)plan.FirstElement;
                uint length = *(uint*)(obj + 8);
                for (uint k = 0; k < length; k++, element += plan.ComponentSize)
                    for (int s = 0; s < inner.Length; s++)
                    {
                        ulong t = *(ulong*)(element + (ulong)inner[s]);
                        if (t != 0 && !IsStart(t, size)) { bad = t; return false; }
                    }
            }
            return true;
        }

        private static bool CheckInside(ulong obj, TypePlan plan, ulong low, ulong high, out ulong bad)
        {
            bad = 0;
            int[] slots = plan.Slots;
            for (int s = 0; s < slots.Length; s++)
            {
                ulong t = *(ulong*)(obj + (ulong)slots[s]);
                if (t != 0 && (t < low || t >= high)) { bad = t; return false; }
            }
            if (plan.Elements == TypePlan.ReferenceElements)
            {
                ulong* element = (ulong*)(obj + (ulong)plan.FirstElement);
                uint length = *(uint*)(obj + 8);
                for (uint k = 0; k < length; k++)
                    if (element[k] != 0 && (element[k] < low || element[k] >= high)) { bad = element[k]; return false; }
            }
            else if (plan.Elements == TypePlan.StructElements)
            {
                int[] inner = plan.ElementSlots;
                ulong element = obj + (ulong)plan.FirstElement;
                uint length = *(uint*)(obj + 8);
                for (uint k = 0; k < length; k++, element += plan.ComponentSize)
                    for (int s = 0; s < inner.Length; s++)
                    {
                        ulong t = *(ulong*)(element + (ulong)inner[s]);
                        if (t != 0 && (t < low || t >= high)) { bad = t; return false; }
                    }
            }
            return true;
        }

        // Every non-null reference of the object moved by the block's base:
        // an offset becomes an address, or the other way.
        private static void Shift(ulong obj, TypePlan plan, ulong delta, bool add)
        {
            int[] slots = plan.Slots;
            for (int s = 0; s < slots.Length; s++)
            {
                ulong* slot = (ulong*)(obj + (ulong)slots[s]);
                if (*slot != 0) *slot = add ? *slot + delta : *slot - delta;
            }
            if (plan.Elements == TypePlan.ReferenceElements)
            {
                ulong* element = (ulong*)(obj + (ulong)plan.FirstElement);
                uint length = *(uint*)(obj + 8);
                for (uint k = 0; k < length; k++)
                    if (element[k] != 0) element[k] = add ? element[k] + delta : element[k] - delta;
            }
            else if (plan.Elements == TypePlan.StructElements)
            {
                int[] inner = plan.ElementSlots;
                ulong element = obj + (ulong)plan.FirstElement;
                uint length = *(uint*)(obj + 8);
                for (uint k = 0; k < length; k++, element += plan.ComponentSize)
                    for (int s = 0; s < inner.Length; s++)
                    {
                        ulong* slot = (ulong*)(element + (ulong)inner[s]);
                        if (*slot != 0) *slot = add ? *slot + delta : *slot - delta;
                    }
            }
        }
    }
}
