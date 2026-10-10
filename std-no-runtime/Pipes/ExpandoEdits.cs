using System;
using System.Collections.Generic;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// A name of an Expando entry with its hash worked out once: a filter
    /// makes its names when it starts and looks them up in every message.
    /// </summary>
    public readonly struct ExpandoName
    {
        public readonly string Name;
        internal readonly int Hash;

        public ExpandoName(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            Name = name;
            Hash = ExpandoHash.Of(name);
        }

        public static implicit operator ExpandoName(string name) => new ExpandoName(name);
    }

    /// <summary>What an edit cost: where the bytes it needed came from.</summary>
    public enum EditCost : byte
    {
        /// <summary>Written into the entry: no byte added.</summary>
        InPlace,
        /// <summary>New records in the block's tail, in the room it already had.</summary>
        Tail,
        /// <summary>The block copied byte for byte into one twice the size.</summary>
        Grown,
        /// <summary>The live records copied into a new block, the garbage left behind.</summary>
        Compacted,
    }

    /// <summary>
    /// Edits of an Expando inside a received region the caller owns — a
    /// filter's own message, before it goes on (step201, pipe_plan.md «Умный
    /// Expando»). The block is not translated and not rebuilt from values.
    /// </summary>
    /// <remarks>
    /// <para>The entry is found by its hash: a bucket and a short chain. A
    /// number, bool or char goes into the entry, whatever it held before; so
    /// does null. A string, a new name and a full table need room: the space of
    /// the whole edit is asked for first and written after, so an edit is done
    /// whole or not at all.</para>
    /// <para>Room is the block's tail, up to the end of its exchange-heap block
    /// (the size class leaves some for free). When it runs out the block is
    /// replaced: with more than half of it live, copied byte for byte into one
    /// twice the size — references are offsets, the copy needs no parsing;
    /// otherwise its live records are copied into a block their size and the
    /// garbage stays behind. A replaced block kills every view of the old one:
    /// take the region's Root again.</para>
    /// <para>A removed entry is unlinked and marked dead; its name and value
    /// become garbage — whole records, which the format's walk still passes.
    /// A table is rebuilt from the live entries when it is full, in the tail.</para>
    /// <para>Values that are objects (another Expando, an array, a [Message]
    /// object) do not go in place: their graph would have to be laid out into
    /// the block — take ToExpando, change, Copy.</para>
    /// </remarks>
    public static unsafe class ExpandoEdits
    {
        /// <summary>Sets an entry of <paramref name="expando"/>, an Expando of <paramref name="region"/>, adding it when missing.</summary>
        public static EditCost Set(this RawRegion region, View expando, ExpandoName name, object value)
        {
            bool scalar = ExpandoValues.TryScalar(value, out byte kind, out ulong bits);
            string text = value as string;
            if (!scalar && value != null && text == null) throw NotInPlace(value.GetType().Name);
            return Store(region, expando, name, scalar, kind, bits, text);
        }

        /// <summary>
        /// The same for a number, bool or char: no box is made, and an edit of
        /// an entry that is there allocates nothing.
        /// </summary>
        public static EditCost Set<T>(this RawRegion region, View expando, ExpandoName name, T value) where T : unmanaged
        {
            if (!ExpandoValues.TryScalar(value, out byte kind, out ulong bits)) throw NotInPlace(typeof(T).Name);
            return Store(region, expando, name, true, kind, bits, null);
        }

        private static NotSupportedException NotInPlace(string type)
            => new NotSupportedException("an Expando in a region takes numbers, bool, char, strings and null in place; a "
                                         + type + " needs a copy (ToExpando, change, Copy)");

        private static EditCost Store(RawRegion region, View expando, ExpandoName name, bool scalar, byte kind, ulong bits, string text)
        {
            ViewScope scope = Owned(region, expando);
            View.ExpandoShape s = expando.ExpandoLayout();
            int i = expando.ExpandoFind(name.Name.AsSpan(), name.Hash);
            if (i >= 0 && text == null)
            {
                ulong e = Entry(scope, expando, s, i);
                *(ulong*)(e + (ulong)s.Bits) = scalar ? bits : 0;
                *(ulong*)(e + (ulong)s.Ref) = 0;
                *(byte*)(e + (ulong)s.Kind) = scalar ? kind : (byte)FieldKind.Reference;
                return EditCost.InPlace;
            }

            RegionShapes shapes = scope.Shapes;
            TypeShape stringType = shapes.ByName("System.String") ?? throw NoType("System.String");
            ulong need = text != null ? RecordSize(stringType, (uint)text.Length) : 0;
            bool add = i < 0;
            bool rebuild = false;
            int capacity = 0;
            TypeShape tableType = null, indexType = null;
            if (add)
            {
                need += RecordSize(stringType, (uint)name.Name.Length);
                int used = Used(expando, s);
                ulong entries = Ref(scope, expando, s.Entries);
                int cells = entries == 0 ? 0 : *(int*)(entries + 8);
                if (used == cells)
                {
                    rebuild = true;
                    int live = *(int*)(expando.Address + (ulong)s.Live);
                    capacity = live < 2 ? 4 : live * 2;
                    tableType = entries != 0 ? shapes[*(ulong*)entries] : shapes.ByName("SharpOS.Std.Pipes.ExpandoEntry[]");
                    ulong buckets = Ref(scope, expando, s.Buckets);
                    indexType = buckets != 0 ? shapes[*(ulong*)buckets] : shapes.ByName("System.Int32[]");
                    if (tableType == null) throw NoType("SharpOS.Std.Pipes.ExpandoEntry[]");
                    if (indexType == null) throw NoType("System.Int32[]");
                    need += RecordSize(tableType, (uint)capacity) + RecordSize(indexType, (uint)ExpandoHash.BucketCount(capacity));
                }
            }

            EditCost cost = Reserve(region, ref expando, need);
            scope = region.Scope;
            s = expando.ExpandoLayout();

            if (rebuild) RebuildTable(region, expando, s, tableType, indexType, capacity);

            ulong valueAt = text != null ? NewString(region, stringType, text) : 0;
            if (!add)
            {
                ulong e = Entry(scope, expando, s, i);
                *(ulong*)(e + (ulong)s.Ref) = valueAt;
                *(ulong*)(e + (ulong)s.Bits) = 0;
                *(byte*)(e + (ulong)s.Kind) = (byte)FieldKind.Reference;
                return cost;
            }

            ulong nameAt = NewString(region, stringType, name.Name);
            int cell = Used(expando, s);
            ulong at = Entry(scope, expando, s, cell);
            *(int*)(at + (ulong)s.Hash) = name.Hash;
            *(ulong*)(at + (ulong)s.Name) = nameAt;
            *(byte*)(at + (ulong)s.Dead) = 0;
            if (text != null)
            {
                *(ulong*)(at + (ulong)s.Ref) = valueAt;
                *(ulong*)(at + (ulong)s.Bits) = 0;
                *(byte*)(at + (ulong)s.Kind) = (byte)FieldKind.Reference;
            }
            else
            {
                *(ulong*)(at + (ulong)s.Ref) = 0;
                *(ulong*)(at + (ulong)s.Bits) = scalar ? bits : 0;
                *(byte*)(at + (ulong)s.Kind) = scalar ? kind : (byte)FieldKind.Reference;
            }
            int* bucket = Bucket(scope, expando, s, name.Hash);
            *(int*)(at + (ulong)s.Next) = *bucket;
            *bucket = cell + 1;
            *(int*)(expando.Address + (ulong)s.Used) = cell + 1;
            *(int*)(expando.Address + (ulong)s.Live) += 1;
            return cost;
        }

        /// <summary>Removes an entry of <paramref name="expando"/>: unlinked and marked dead, no byte added. False when there is none.</summary>
        public static bool Remove(this RawRegion region, View expando, ExpandoName name)
        {
            ViewScope scope = Owned(region, expando);
            View.ExpandoShape s = expando.ExpandoLayout();
            int used = Used(expando, s);
            if (used == 0) return false;
            int* head = Bucket(scope, expando, s, name.Hash);
            int previous = 0;
            int steps = 0;
            for (int at = *head; at != 0; previous = at, at = *(int*)(Entry(scope, expando, s, at - 1) + (ulong)s.Next))
            {
                if (steps++ >= used || (uint)(at - 1) >= (uint)used) throw Malformed();
                ulong e = Entry(scope, expando, s, at - 1);
                if (*(int*)(e + (ulong)s.Hash) != name.Hash || !SameName(scope, *(ulong*)(e + (ulong)s.Name), name.Name)) continue;
                int next = *(int*)(e + (ulong)s.Next);
                if (previous == 0) *head = next;
                else *(int*)(Entry(scope, expando, s, previous - 1) + (ulong)s.Next) = next;
                *(int*)(e + (ulong)s.Next) = 0;
                *(byte*)(e + (ulong)s.Dead) = 1;
                *(ulong*)(e + (ulong)s.Name) = 0;
                *(ulong*)(e + (ulong)s.Ref) = 0;
                *(ulong*)(e + (ulong)s.Bits) = 0;
                *(int*)(expando.Address + (ulong)s.Live) -= 1;
                return true;
            }
            return false;
        }

        // ---- the block ----

        private static ViewScope Owned(RawRegion region, View expando)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            ViewScope scope = region.Scope;
            if (scope == null) throw new ObjectDisposedException("RawRegion", "the region is gone: it went on, or was disposed");
            expando.EnsureAlive();
            if (!expando.IsExpandoObject || expando.Scope != scope)
                throw new ArgumentException("not an Expando of this region", nameof(expando));
            return scope;
        }

        // Room for `need` more bytes after the block's last record: there
        // already, or in a block that replaces it. The view is moved along.
        private static EditCost Reserve(RawRegion region, ref View expando, ulong need)
        {
            if (need == 0) return EditCost.InPlace;
            ulong length = region.Length;
            if (length + need <= Capacity(region)) return EditCost.Tail;

            ulong target = expando.Address - (ulong)region.Block;
            ulong live = LiveBytes(region);
            byte* block;
            ulong newLength;
            EditCost cost;
            if (live * 2 > length)
            {
                ulong size = length * 2;
                if (size < length + need) size = length + need;
                block = (byte*)PipeTransport.Allocate(size);
                if (block == null) throw new OutOfMemoryException("no exchange block of " + size.ToString() + " bytes for an edit");
                Buffer.MemoryCopy(region.Block, block, (long)size, (long)length);
                newLength = length;
                cost = EditCost.Grown;
            }
            else
            {
                ulong size = (live + need) * 3 / 2;
                block = (byte*)PipeTransport.Allocate(size);
                if (block == null) throw new OutOfMemoryException("no exchange block of " + size.ToString() + " bytes for an edit");
                newLength = Compact(region, block, ref target);
                cost = EditCost.Compacted;
            }
            PipeTransport.Free(region.Block);
            region.Relocate(block, newLength);
            expando = View.OfObject(region.Scope, (ulong)block + target);
            return cost;
        }

        // The end of the block's exchange-heap allocation, from its start.
        private static ulong Capacity(RawRegion region)
        {
            ulong start = (ulong)region.Block;
            if (ExchangeArena.TryBlock(start, out ulong payload, out ulong end) && payload == start)
                return end - payload;
            return region.Length;
        }

        private static ulong RecordSize(TypeShape t, uint length)
        {
            ulong total = t.BaseSize;
            if (t.ComponentSize != 0) total += (ulong)length * t.ComponentSize;
            return (total + 7) & ~7UL;
        }

        // A new record at the block's end, zeroed, its key and length set; the
        // offset of its table word, which is what a reference holds.
        private static ulong NewRecord(RawRegion region, TypeShape t, uint length)
        {
            ulong size = RecordSize(t, length);
            ulong start = region.Length;
            byte* at = region.Block + start;
            new Span<byte>(at, (int)size).Clear();
            *(ulong*)(at + Region.HeaderSize) = t.Key;
            if (t.ComponentSize != 0) *(uint*)(at + Region.HeaderSize + 8) = length;
            region.Extend(start + size);
            return start + Region.HeaderSize;
        }

        private static ulong NewString(RawRegion region, TypeShape stringType, string text)
        {
            ulong offset = NewRecord(region, stringType, (uint)text.Length);
            char* chars = (char*)(region.Block + offset + 12);
            for (int i = 0; i < text.Length; i++) chars[i] = text[i];
            return offset;
        }

        // ---- the table ----

        private static ulong Ref(ViewScope scope, View expando, int field)
        {
            ulong offset = *(ulong*)(expando.Address + (ulong)field);
            return offset == 0 ? 0 : (ulong)scope.Block + offset;
        }

        private static int Used(View expando, View.ExpandoShape s)
        {
            int used = *(int*)(expando.Address + (ulong)s.Used);
            return used < 0 ? 0 : used;
        }

        private static ulong Entry(ViewScope scope, View expando, View.ExpandoShape s, int i)
        {
            ulong entries = Ref(scope, expando, s.Entries);
            if (entries == 0 || (uint)i >= (uint)*(int*)(entries + 8)) throw Malformed();
            return entries + (ulong)s.First + (ulong)i * (ulong)s.Stride;
        }

        private static int* Bucket(ViewScope scope, View expando, View.ExpandoShape s, int hash)
        {
            ulong buckets = Ref(scope, expando, s.Buckets);
            if (buckets == 0) throw Malformed();
            int count = *(int*)(buckets + 8);
            if (count <= 0 || (count & (count - 1)) != 0) throw Malformed();
            return (int*)(buckets + (ulong)s.BucketFirst + 4UL * (ulong)ExpandoHash.Bucket(hash, count));
        }

        private static bool SameName(ViewScope scope, ulong offset, string name)
        {
            if (offset == 0) return false;
            ulong at = (ulong)scope.Block + offset;
            if (*(int*)(at + 8) != name.Length) return false;
            char* chars = (char*)(at + 12);
            for (int i = 0; i < name.Length; i++)
                if (chars[i] != name[i]) return false;
            return true;
        }

        // A table of `capacity` cells in the tail, the live entries copied in
        // their order (their name and value offsets stay as they are), the
        // chains linked again; the old table and index become garbage.
        private static void RebuildTable(RawRegion region, View expando, View.ExpandoShape s,
                                         TypeShape tableType, TypeShape indexType, int capacity)
        {
            int bucketCount = ExpandoHash.BucketCount(capacity);
            ulong tableOffset = NewRecord(region, tableType, (uint)capacity);
            ulong indexOffset = NewRecord(region, indexType, (uint)bucketCount);
            ViewScope scope = region.Scope;
            ulong table = (ulong)scope.Block + tableOffset;
            int* index = (int*)((ulong)scope.Block + indexOffset + (ulong)s.BucketFirst);

            int used = Used(expando, s);
            ulong old = Ref(scope, expando, s.Entries);
            int n = 0;
            for (int i = 0; i < used; i++)
            {
                ulong from = old + (ulong)s.First + (ulong)i * (ulong)s.Stride;
                if (*(byte*)(from + (ulong)s.Dead) != 0) continue;
                ulong to = table + (ulong)s.First + (ulong)n * (ulong)s.Stride;
                Buffer.MemoryCopy((void*)from, (void*)to, s.Stride, s.Stride);
                int b = ExpandoHash.Bucket(*(int*)(to + (ulong)s.Hash), bucketCount);
                *(int*)(to + (ulong)s.Next) = index[b];
                index[b] = n + 1;
                n++;
            }
            *(ulong*)(expando.Address + (ulong)s.Entries) = tableOffset;
            *(ulong*)(expando.Address + (ulong)s.Buckets) = indexOffset;
            *(int*)(expando.Address + (ulong)s.Used) = n;
            *(int*)(expando.Address + (ulong)s.Live) = n;
        }

        // ---- the records a block still reaches ----

        // Bytes of the records reachable from the root: what a compacted copy takes.
        private static ulong LiveBytes(RawRegion region)
        {
            var seen = new Dictionary<ulong, ulong>();
            var order = new List<ulong>();
            Reach(region, seen, order);
            ulong total = 0;
            for (int i = 0; i < order.Count; i++) total += SizeAt(region, order[i]);
            return total;
        }

        // Every reachable record, root first, breadth first: `seen` maps a
        // table-word offset to its place in `order`.
        private static void Reach(RawRegion region, Dictionary<ulong, ulong> seen, List<ulong> order)
        {
            ulong root = Region.HeaderSize;
            seen[root] = 0;
            order.Add(root);
            for (int q = 0; q < order.Count; q++)
            {
                ulong at = order[q];
                TypeShape t = ShapeAt(region, at);
                ForEachSlot(region, t, at, slot =>
                {
                    ulong target = *(ulong*)slot;
                    if (target == 0 || seen.ContainsKey(target)) return;
                    seen[target] = (ulong)order.Count;
                    order.Add(target);
                });
            }
        }

        private static TypeShape ShapeAt(RawRegion region, ulong offset)
        {
            TypeShape t = region.Scope.Shapes[*(ulong*)(region.Block + offset)];
            if (t == null) throw Malformed();
            return t;
        }

        private static ulong SizeAt(RawRegion region, ulong offset)
        {
            TypeShape t = ShapeAt(region, offset);
            uint length = t.ComponentSize != 0 ? *(uint*)(region.Block + offset + 8) : 0;
            return RecordSize(t, length);
        }

        // The address of every reference slot of the object at `offset`: its
        // fields, the fields of structs laid out in it, and its elements.
        private static void ForEachSlot(RawRegion region, TypeShape t, ulong offset, Action<ulong> slot)
        {
            ulong obj = (ulong)region.Block + offset;
            StructSlots(t.Fields, obj, slot);
            if (!t.IsArray) return;
            uint length = *(uint*)(obj + 8);
            FieldShape el = t.Elements;
            for (uint k = 0; k < length; k++)
            {
                ulong element = obj + (ulong)el.Offset + (ulong)k * t.ComponentSize;
                if (el.Kind == FieldKind.Reference) slot(element);
                else if (el.Kind == FieldKind.Struct) StructSlots(el.Struct.Fields, element - 8, slot);
            }
        }

        // `box` is where the fields' offsets count from.
        private static void StructSlots(FieldShape[] fields, ulong box, Action<ulong> slot)
        {
            if (fields == null) return;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldShape f = fields[i];
                if (f.Kind == FieldKind.Reference) slot(box + (ulong)f.Offset);
                else if (f.Kind == FieldKind.Struct && f.Struct != null) StructSlots(f.Struct.Fields, box + (ulong)f.Offset - 8, slot);
            }
        }

        // The reachable records into `block`, root first, every reference
        // moved to its target's new place; the new length. `target` is moved too.
        private static ulong Compact(RawRegion region, byte* block, ref ulong target)
        {
            var seen = new Dictionary<ulong, ulong>();
            var order = new List<ulong>();
            Reach(region, seen, order);

            var moved = new Dictionary<ulong, ulong>(order.Count);
            ulong cursor = 0;
            for (int i = 0; i < order.Count; i++)
            {
                ulong at = order[i];
                ulong size = SizeAt(region, at);
                moved[at] = cursor + Region.HeaderSize;
                Buffer.MemoryCopy(region.Block + at - Region.HeaderSize, block + cursor, (long)size, (long)size);
                cursor += size;
            }
            for (int i = 0; i < order.Count; i++)
            {
                ulong newAt = moved[order[i]];
                TypeShape t = ShapeAt(region, order[i]);
                // The copy's slots, read through the old shape: same layout.
                ForEachSlotAt(t, (ulong)block + newAt, s =>
                {
                    ulong old = *(ulong*)s;
                    if (old != 0) *(ulong*)s = moved[old];
                });
            }
            if (!moved.TryGetValue(target, out ulong newTarget))
                throw new InvalidOperationException("the edited Expando is not reachable from the region's root");
            target = newTarget;
            return cursor;
        }

        private static void ForEachSlotAt(TypeShape t, ulong obj, Action<ulong> slot)
        {
            StructSlots(t.Fields, obj, slot);
            if (!t.IsArray) return;
            uint length = *(uint*)(obj + 8);
            FieldShape el = t.Elements;
            for (uint k = 0; k < length; k++)
            {
                ulong element = obj + (ulong)el.Offset + (ulong)k * t.ComponentSize;
                if (el.Kind == FieldKind.Reference) slot(element);
                else if (el.Kind == FieldKind.Struct) StructSlots(el.Struct.Fields, element - 8, slot);
            }
        }

        private static InvalidOperationException Malformed()
            => new InvalidOperationException("the Expando's table in the region is malformed");

        private static InvalidOperationException NoType(string name)
            => new InvalidOperationException("the pipe's description has no " + name + ": the edit needs it");
    }
}
