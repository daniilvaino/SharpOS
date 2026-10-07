using System;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// Lays out and writes graphs into regions, again and again, without
    /// allocating once its buffers have grown to the graphs it sees (step195).
    /// One per writer; not for two threads at once.
    /// </summary>
    /// <remarks>
    /// The graph is walked breadth first over its own record list — the list
    /// is the queue, so there is no recursion and no stack to overflow. Each
    /// object is found once in the table of objects already seen: an open
    /// table stamped with an epoch per message, so a new message starts with
    /// an empty table without clearing it. The record index of every
    /// reference's target is noted during the walk, in slot order, so writing
    /// looks nothing up.
    /// </remarks>
    public sealed unsafe class RegionWriter
    {
        // In pieces (Chunked): a graph of a hundred thousand objects needs
        // more than an app's heap gives in one piece.
        private Chunked<ulong> _objects = new Chunked<ulong>(64);
        private Chunked<ulong> _offsets = new Chunked<ulong>(64);
        // Plans by address: a value-type piece is indexed by specialised code,
        // a reference-type one through the shared generic path. The plans
        // themselves are held by TypePlans for good.
        private Chunked<ulong> _plans = new Chunked<ulong>(64);
        private Chunked<int> _targets = new Chunked<int>(128);
        private int _count;
        private int _targetCount;

        // Objects seen in this message: address → record index, valid where
        // the stamp is the current epoch. An open table of _seenSize slots.
        private Chunked<ulong> _seenAt = new Chunked<ulong>(128);
        private Chunked<int> _seenIndex = new Chunked<int>(128);
        private Chunked<uint> _seenEpoch = new Chunked<uint>(128);
        private int _seenSize = 128;
        private int _seenShift = 64 - 7;
        private uint _epoch;
        private ulong _lastTable;
        private TypePlan _lastPlan;

        // The graph is held below by address only, which no collector
        // follows; this keeps it alive from Lay through Write (a buffer that
        // grows allocates, and a collection then would free a message nobody
        // else holds — the temporary in `writer.Copy(Build())`).
        private object _root;

        /// <summary>Bytes the region takes.</summary>
        public ulong Size { get; private set; }

        /// <summary>Objects in it.</summary>
        public int Count => _count;

        /// <summary>Why the last Lay refused.</summary>
        public string Complaint { get; private set; }

        /// <summary>
        /// Lays out the graph under <paramref name="root"/>: false, with a
        /// complaint, when a type in it is not in the catalog.
        /// </summary>
        public bool Lay(object root)
        {
            Complaint = null;
            _count = 0;
            _targetCount = 0;
            Size = 0;
            if (root == null)
            {
                Complaint = "no root";
                return false;
            }
            _root = root;
            if (++_epoch == 0)
            {
                _seenEpoch.Clear(_seenSize);
                _epoch = 1;
            }

            ulong cursor = 0;
            if (Add(AddressOf(root), ref cursor) < 0) return Fail();
            for (int i = 0; i < _count; i++)
            {
                ulong obj = _objects[i];
                TypePlan plan = PlanAt(i);
                int[] slots = plan.Slots;
                for (int s = 0; s < slots.Length; s++)
                    if (!Follow(*(ulong*)(obj + (ulong)slots[s]), ref cursor)) return Fail();

                if (plan.Elements == TypePlan.ReferenceElements)
                {
                    ulong* element = (ulong*)(obj + (ulong)plan.FirstElement);
                    uint length = *(uint*)(obj + 8);
                    for (uint k = 0; k < length; k++)
                        if (!Follow(element[k], ref cursor)) return Fail();
                }
                else if (plan.Elements == TypePlan.StructElements)
                {
                    int[] inner = plan.ElementSlots;
                    ulong element = obj + (ulong)plan.FirstElement;
                    uint length = *(uint*)(obj + 8);
                    for (uint k = 0; k < length; k++, element += plan.ComponentSize)
                        for (int s = 0; s < inner.Length; s++)
                            if (!Follow(*(ulong*)(element + (ulong)inner[s]), ref cursor)) return Fail();
                }
            }
            Size = cursor;
            return true;
        }

        /// <summary>
        /// Writes the graph laid out last to <paramref name="at"/> (at least
        /// Size bytes; every byte is written): keys in table words, offsets in
        /// reference slots.
        /// </summary>
        public void Write(byte* at)
        {
            int r = 0;
            for (int i = 0; i < _count; i++)
            {
                ulong obj = _objects[i];
                TypePlan plan = PlanAt(i);
                byte* record = at + _offsets[i];
                *(ulong*)record = 0;
                byte* payload = record + Region.HeaderSize;
                SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(payload, (void*)obj, plan.PayloadSize(obj));
                *(ulong*)payload = plan.Key;

                // Slots in the order Lay took them; a null stays the zero it was copied as.
                int[] slots = plan.Slots;
                for (int s = 0; s < slots.Length; s++)
                    if (*(ulong*)(obj + (ulong)slots[s]) != 0)
                        *(ulong*)(payload + slots[s]) = _offsets[_targets[r++]] + Region.HeaderSize;

                if (plan.Elements == TypePlan.ReferenceElements)
                {
                    ulong* source = (ulong*)(obj + (ulong)plan.FirstElement);
                    ulong* target = (ulong*)(payload + plan.FirstElement);
                    uint length = *(uint*)(obj + 8);
                    for (uint k = 0; k < length; k++)
                        if (source[k] != 0)
                            target[k] = _offsets[_targets[r++]] + Region.HeaderSize;
                }
                else if (plan.Elements == TypePlan.StructElements)
                {
                    int[] inner = plan.ElementSlots;
                    uint length = *(uint*)(obj + 8);
                    for (uint k = 0; k < length; k++)
                    {
                        ulong offset = (ulong)plan.FirstElement + k * plan.ComponentSize;
                        for (int s = 0; s < inner.Length; s++)
                            if (*(ulong*)(obj + offset + (ulong)inner[s]) != 0)
                                *(ulong*)(payload + offset + (ulong)inner[s]) = _offsets[_targets[r++]] + Region.HeaderSize;
                    }
                }
            }
            _root = null;
        }

        /// <summary>Lets the graph go without writing it (a refused send).</summary>
        public void Forget() => _root = null;

        private bool Fail()
        {
            _root = null;
            return false;
        }

        // A reference: its target's record index, the target laid out first
        // if this is its first sight.
        private bool Follow(ulong target, ref ulong cursor)
        {
            if (target == 0) return true;
            int index = Find(target);
            if (index < 0)
            {
                index = Add(target, ref cursor);
                if (index < 0) return false;
            }
            _targets.Ensure(_targetCount + 1);
            _targets[_targetCount++] = index;
            return true;
        }

        private int Add(ulong obj, ref ulong cursor)
        {
            ulong table = *(ulong*)obj & ~1UL;
            // Neighbours are mostly of one type (an array's elements): the
            // last plan answers before the table is asked.
            TypePlan plan = table == _lastTable ? _lastPlan : TypePlans.ByTable(table);
            if (plan != null)
            {
                _lastTable = table;
                _lastPlan = plan;
            }
            if (plan == null)
            {
                Complaint = IsDelegate(table)
                    ? $"a delegate in the graph: table 0x{table:x}"
                    : $"type outside the catalog: table 0x{table:x}, base {ObjectLayout.BaseSizeOf(table)}";
                return -1;
            }
            if (_count == _objects.Capacity)
            {
                _objects.Ensure(_count * 2);
                _offsets.Ensure(_count * 2);
                _plans.Ensure(_count * 2);
            }
            int index = _count++;
            _objects[index] = obj;
            _offsets[index] = cursor;
            _plans[index] = AddressOf(plan);
            cursor += Region.HeaderSize + plan.PayloadSize(obj);
            Insert(obj, index);
            return index;
        }

        private int Slot(ulong address) => (int)(((address >> 3) * 0x9E3779B97F4A7C15UL) >> _seenShift);

        private int Find(ulong address)
        {
            int mask = _seenSize - 1;
            for (int at = Slot(address); ; at = (at + 1) & mask)
            {
                if (_seenEpoch[at] != _epoch) return -1;
                if (_seenAt[at] == address) return _seenIndex[at];
            }
        }

        private void Insert(ulong address, int index)
        {
            if (2 * _count > _seenSize) Grow();
            int mask = _seenSize - 1;
            int at = Slot(address);
            while (_seenEpoch[at] == _epoch) at = (at + 1) & mask;
            _seenAt[at] = address;
            _seenIndex[at] = index;
            _seenEpoch[at] = _epoch;
        }

        // Twice the room; only this message's entries move.
        private void Grow()
        {
            Chunked<ulong> oldAt = _seenAt;
            Chunked<int> oldIndex = _seenIndex;
            Chunked<uint> oldEpoch = _seenEpoch;
            int oldSize = _seenSize;
            int size = oldSize * 2;
            _seenAt = new Chunked<ulong>(size);
            _seenIndex = new Chunked<int>(size);
            _seenEpoch = new Chunked<uint>(size);
            _seenSize = size;
            _seenShift--;
            int mask = size - 1;
            for (int i = 0; i < oldSize; i++)
            {
                if (oldEpoch[i] != _epoch) continue;
                int at = Slot(oldAt[i]);
                while (_seenEpoch[at] == _epoch) at = (at + 1) & mask;
                _seenAt[at] = oldAt[i];
                _seenIndex[at] = oldIndex[i];
                _seenEpoch[at] = _epoch;
            }
        }

        private static bool IsDelegate(ulong table)
        {
            ulong delegateTable = (ulong)typeof(System.Delegate)._handle;
            var mt = (SharpOS.Std.NoRuntime.GcMethodTable*)table;
            for (int depth = 0; mt != null && depth < 32; depth++)
            {
                if ((ulong)mt == delegateTable) return true;
                mt = mt->GetBaseType();
            }
            return false;
        }

        private TypePlan PlanAt(int i)
        {
            ulong at = _plans[i];
            return System.Runtime.CompilerServices.Unsafe.As<ulong, TypePlan>(ref at);
        }

        private static ulong AddressOf(object o)
            => System.Runtime.CompilerServices.Unsafe.As<object, ulong>(ref o);
    }
}
