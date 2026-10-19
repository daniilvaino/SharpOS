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
        // the stamp is the current epoch. An open table of _seenSize slots,
        // one entry of 16 bytes a slot (step208): it was three tables -
        // address, index, stamp - and a probe touched three cache lines,
        // which with addresses spread by the hash were three misses; finding
        // and then inserting probed twice. 45 % of laying out 30 000 objects.
        private struct Seen
        {
            public ulong At;
            public int Index;
            public uint Epoch;
        }

        private Chunked<Seen> _seen = new Chunked<Seen>(128);
        private int _seenSize = 128;
        private int _seenShift = 64 - 7;
        private int _hashCount;
        private uint _epoch;

        // The shadow map (step208): the same answer by address instead of by
        // hash. A page covers 64 KiB of addresses, a slot each 16 bytes - an
        // object is 16-aligned and never shorter, so no two share one. The
        // objects of a graph mostly lie in the heap in the order the walk
        // meets them, so the slots it reads follow one another, where the
        // hash sent every probe to a random place in a megabyte (38 % of a
        // layout, a cache miss each). Pages are made as addresses need them
        // and kept for later messages - the epoch says which slots are this
        // message's. Past MaxPages, addresses go to the hash table: a graph
        // scattered over a large heap must not cost megabytes of map.
        private struct Shadow
        {
            public int Index;
            public uint Epoch;
        }

        private const int PageShift = 16;
        private const int PageSlots = 1 << (PageShift - 4);
        private const int MaxPages = 128;
        private ulong[] _pageKeys = new ulong[256];       // key + 1; 0 is empty
        private Shadow[][] _pages = new Shadow[256][];
        private int _pageCount;
        private ulong _lastPageKey = ulong.MaxValue;
        private Shadow[] _lastPage;
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
            _hashCount = 0;
            if (++_epoch == 0)
            {
                _seen.Clear(_seenSize);
                for (int i = 0; i < _pages.Length; i++)
                    if (_pages[i] != null) System.Array.Clear(_pages[i], 0, PageSlots);
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
                // The payload's size is what Lay advanced the cursor by.
                ulong next = i + 1 < _count ? _offsets[i + 1] : Size;
                SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(payload, (void*)obj, next - _offsets[i] - Region.HeaderSize);
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
            int index = Find(target, out int empty);
            if (index < 0)
            {
                index = Add(target, ref cursor, empty);
                if (index < 0) return false;
            }
            _targets.Ensure(_targetCount + 1);
            _targets[_targetCount++] = index;
            return true;
        }

        // `empty`: the free slot Find stopped at, or -1.
        private int Add(ulong obj, ref ulong cursor, int empty = -1)
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
            Insert(obj, index, empty);
            return index;
        }

        private int Slot(ulong address) => (int)(((address >> 3) * 0x9E3779B97F4A7C15UL) >> _seenShift);

        // The shadow page of an address, made when missing; null past
        // MaxPages (the address is the hash table's).
        private Shadow[] PageOf(ulong address)
        {
            ulong key = address >> PageShift;
            if (key == _lastPageKey) return _lastPage;
            int mask = _pageKeys.Length - 1;
            int at = (int)((key * 0x9E3779B97F4A7C15UL) >> 32) & mask;
            while (_pageKeys[at] != 0 && _pageKeys[at] != key + 1) at = (at + 1) & mask;
            Shadow[] page = _pages[at];
            if (_pageKeys[at] == 0)
            {
                if (_pageCount >= MaxPages) return null;   // not cached: the next address may have a page
                page = new Shadow[PageSlots];
                _pageKeys[at] = key + 1;
                _pages[at] = page;
                _pageCount++;
            }
            _lastPageKey = key;
            _lastPage = page;
            return page;
        }

        // The record index, or -1 with the free slot the probe stopped at
        // (-2: the address is the shadow map's).
        private int Find(ulong address, out int empty)
        {
            Shadow[] page = PageOf(address);
            if (page != null)
            {
                ref Shadow sh = ref page[(int)(address >> 4) & (PageSlots - 1)];
                empty = -2;
                return sh.Epoch == _epoch ? sh.Index : -1;
            }

            int mask = _seenSize - 1;
            for (int at = Slot(address); ; at = (at + 1) & mask)
            {
                ref Seen e = ref _seen[at];
                if (e.Epoch != _epoch) { empty = at; return -1; }
                if (e.At == address) { empty = -1; return e.Index; }
            }
        }

        // Into `empty` when Find gave one and the table has not grown since.
        private void Insert(ulong address, int index, int empty)
        {
            Shadow[] page = PageOf(address);
            if (page != null)
            {
                ref Shadow sh = ref page[(int)(address >> 4) & (PageSlots - 1)];
                sh.Index = index;
                sh.Epoch = _epoch;
                return;
            }

            if (2 * ++_hashCount > _seenSize)
            {
                Grow();
                empty = -1;
            }
            int at = empty;
            if (at < 0)
            {
                int mask = _seenSize - 1;
                at = Slot(address);
                while (_seen[at].Epoch == _epoch) at = (at + 1) & mask;
            }
            ref Seen e = ref _seen[at];
            e.At = address;
            e.Index = index;
            e.Epoch = _epoch;
        }

        // Twice the room; only this message's entries move.
        private void Grow()
        {
            Chunked<Seen> old = _seen;
            int oldSize = _seenSize;
            int size = oldSize * 2;
            _seen = new Chunked<Seen>(size);
            _seenSize = size;
            _seenShift--;
            int mask = size - 1;
            for (int i = 0; i < oldSize; i++)
            {
                ref Seen o = ref old[i];
                if (o.Epoch != _epoch) continue;
                int at = Slot(o.At);
                while (_seen[at].Epoch == _epoch) at = (at + 1) & mask;
                _seen[at] = o;
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
