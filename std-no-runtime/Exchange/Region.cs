using System;
using System.Collections.Generic;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// A self-contained region: objects one after another, references stored as
    /// offsets, a type key in each table word instead of a table pointer.
    /// </summary>
    /// <remarks>
    /// Offsets instead of addresses: the region can lie anywhere. Keys instead of
    /// tables: the reader never touches the producer's image. Odd keys: an
    /// untranslated object used as an object faults at once.
    ///
    /// Record: [header 8][object from its table word]. The header word is zero —
    /// objects here have none, but a stock runtime keeps its lock word there,
    /// and the format is meant to be the same for both. A record's size comes
    /// from the reader's own table and the length inside the object.
    ///
    /// A reference is the offset of the target's TABLE WORD from the start of
    /// the region, never zero (the first record begins with its header), so zero
    /// stays null. The record offset would be zero for the root, and a reference
    /// to the root would come back as null.
    ///
    /// The region must live in memory no collector owns (the exchange heap): a
    /// collector that met these objects inside its own segments would walk a
    /// graph it did not build.
    /// </remarks>
    public static unsafe class Region
    {
        public const int HeaderSize = 8;

        /// <summary>Where each object of a graph goes, before any byte is written.</summary>
        public sealed class Plan
        {
            // The graph is held below by address only, which no collector
            // follows; this reference keeps it alive for as long as the plan
            // is — through Lay and Write. Without it a collection inside Lay
            // (its lists grow) freed a message nobody else held, the
            // temporary in `writer.Copy(Build())`, and Write copied freed
            // memory (found by GC stress).
            internal object Root;
            internal List<ulong> Objects = new List<ulong>();
            internal List<ulong> Offsets = new List<ulong>();
            internal Dictionary<ulong, int> Index = new Dictionary<ulong, int>();
            internal int MaxReferences;

            /// <summary>Bytes the region takes.</summary>
            public ulong Size;

            /// <summary>Objects in it.</summary>
            public int Count => Objects.Count;
        }

        /// <summary>
        /// Lays out the graph under <paramref name="root"/>. Null and a complaint
        /// when a type in it has no key.
        /// </summary>
        /// <remarks>
        /// Identity by dictionary, not by a mark in the object: there is no
        /// header word to put one in. The collectors here do not move objects,
        /// so the addresses in the dictionary stay valid while the graph is alive.
        /// </remarks>
        public static Plan Lay(object root, out string complaint)
        {
            complaint = null;
            if (root == null)
            {
                complaint = "no root";
                return null;
            }

            var plan = new Plan();
            plan.Root = root;
            var buffer = new ulong[64];
            var frontier = new Stack<ulong>();
            frontier.Push(AddressOf(root));
            ulong cursor = 0;

            while (frontier.Count > 0)
            {
                ulong obj = frontier.Pop();
                if (plan.Index.ContainsKey(obj))
                    continue;

                ulong table = ObjectLayout.TableOf(obj);
                if (!TypeKeys.TryKey(table, out _))
                {
                    complaint = IsDelegate(table)
                        ? $"a delegate in the graph: table 0x{table:x}"
                        : $"type outside the catalog: table 0x{table:x}, base {ObjectLayout.BaseSizeOf(table)}";
                    return null;
                }

                int max = ObjectLayout.MaxReferences(obj, table);
                if (max > plan.MaxReferences) plan.MaxReferences = max;
                if (buffer.Length < max) buffer = new ulong[max];

                plan.Index[obj] = plan.Objects.Count;
                plan.Objects.Add(obj);
                plan.Offsets.Add(cursor);
                cursor += HeaderSize + PayloadSize(obj, table);

                int found = ObjectLayout.References(obj, buffer);
                for (int i = 0; i < found; i++)
                    frontier.Push(buffer[i]);
            }

            plan.Size = cursor;
            return plan;
        }

        /// <summary>
        /// Writes a laid-out graph to <paramref name="at"/> (at least plan.Size
        /// bytes, zeroed): keys in table words, offsets in reference slots.
        /// </summary>
        public static void Write(Plan plan, byte* at)
        {
            var slots = new ulong[plan.MaxReferences > 0 ? plan.MaxReferences : 1];
            for (int n = 0; n < plan.Objects.Count; n++)
            {
                ulong obj = plan.Objects[n];
                ulong table = ObjectLayout.TableOf(obj);
                ulong size = PayloadSize(obj, table);
                byte* record = at + plan.Offsets[n];
                *(ulong*)record = 0;

                byte* payload = record + HeaderSize;
                SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(payload, (void*)obj, size);

                TypeKeys.TryKey(table, out ulong key);
                *(ulong*)payload = key;

                // Slots are found on the source, rewritten in the copy.
                int found = ObjectLayout.ReferenceSlots(obj, table, slots);
                for (int i = 0; i < found; i++)
                {
                    ulong fieldOffset = slots[i] - obj;
                    ulong* slot = (ulong*)(payload + fieldOffset);
                    ulong target = *(ulong*)slots[i];
                    *slot = plan.Offsets[plan.Index[target]] + HeaderSize;
                }
            }
        }

        /// <summary>
        /// Translates a region in place: keys to this image's tables, offsets to
        /// addresses. All or nothing.
        /// </summary>
        /// <remarks>
        /// Two passes. The first checks every key and every bound and writes
        /// nothing, so a refusal leaves the region exactly as it came — a half
        /// translated region, where some objects answer `is` and some do not, is
        /// impossible by construction. The second writes.
        /// </remarks>
        public static bool Resolve(byte* at, ulong size, out object root, out string complaint)
        {
            root = null;
            complaint = null;
            if (size < HeaderSize + 8)
            {
                complaint = "region too small";
                return false;
            }

            var keys = new ulong[16];
            var tables = new ulong[16];
            int distinct = 0;
            int maxReferences = 1;
            for (ulong cursor = 0; cursor < size;)
            {
                if (size - cursor < HeaderSize + 16)
                {
                    complaint = $"truncated record at {cursor}";
                    return false;
                }
                ulong objectAt = (ulong)at + cursor + HeaderSize;
                ulong key = *(ulong*)objectAt;
                ulong table = Lookup(keys, tables, ref distinct, key);
                if (table == 0)
                {
                    complaint = $"key 0x{key:x} not declared here (record at {cursor})";
                    return false;
                }
                ulong next = cursor + HeaderSize + PayloadSize(objectAt, table);
                if (next > size || next <= cursor)
                {
                    complaint = $"record at {cursor} runs past the region ({next} > {size})";
                    return false;
                }
                int max = ObjectLayout.MaxReferences(objectAt, table);
                if (max > maxReferences) maxReferences = max;
                cursor = next;
            }

            // References are checked before any write too: each must be the table
            // word of a record. A forged offset would otherwise become a pointer
            // into the middle of an object, or out of the region.
            var starts = new HashSet<ulong>();
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = (ulong)at + cursor + HeaderSize;
                starts.Add(cursor + HeaderSize);
                cursor += HeaderSize + PayloadSize(objectAt, Lookup(keys, tables, ref distinct, *(ulong*)objectAt));
            }
            var slots = new ulong[maxReferences];
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = (ulong)at + cursor + HeaderSize;
                ulong table = Lookup(keys, tables, ref distinct, *(ulong*)objectAt);
                int found = ObjectLayout.ReferenceSlots(objectAt, table, slots);
                for (int i = 0; i < found; i++)
                {
                    ulong target = *(ulong*)slots[i];
                    if (!starts.Contains(target))
                    {
                        complaint = $"reference {target} in record at {cursor} is not a record";
                        return false;
                    }
                }
                cursor += HeaderSize + PayloadSize(objectAt, table);
            }

            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = (ulong)at + cursor + HeaderSize;
                ulong table = Lookup(keys, tables, ref distinct, *(ulong*)objectAt);
                ulong objectSize = PayloadSize(objectAt, table);

                // Slots are found by the table, so they are found before the table
                // word is overwritten — the order does not matter here, the table
                // is passed in.
                int found = ObjectLayout.ReferenceSlots(objectAt, table, slots);
                for (int i = 0; i < found; i++)
                {
                    ulong* slot = (ulong*)slots[i];
                    *slot = (ulong)at + *slot;
                }
                *(ulong*)objectAt = table;
                cursor += HeaderSize + objectSize;
            }

            ulong rootAt = (ulong)at + HeaderSize;
            root = System.Runtime.CompilerServices.Unsafe.As<ulong, object>(ref rootAt);
            return true;
        }

        /// <summary>
        /// The reverse of Resolve, for sending a received region on (pipe spec
        /// Р10, Р12): tables back to keys, addresses back to offsets. All or
        /// nothing: every table must be in the catalog and every reference
        /// inside the block before the first write.
        /// </summary>
        public static bool Release(byte* at, ulong size, out string complaint)
        {
            complaint = null;
            ulong low = (ulong)at;
            ulong high = low + size;
            int maxReferences = 1;
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + HeaderSize;
                ulong table = *(ulong*)objectAt & ~1UL;
                if (!TypeKeys.TryKey(table, out _))
                {
                    complaint = $"record at {cursor} has a type outside the catalog (table 0x{table:x})";
                    return false;
                }
                int max = ObjectLayout.MaxReferences(objectAt, table);
                if (max > maxReferences) maxReferences = max;
                cursor += HeaderSize + PayloadSize(objectAt, table);
            }

            var slots = new ulong[maxReferences];
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + HeaderSize;
                ulong table = *(ulong*)objectAt & ~1UL;
                int found = ObjectLayout.ReferenceSlots(objectAt, table, slots);
                for (int i = 0; i < found; i++)
                {
                    ulong target = *(ulong*)slots[i];
                    if (target < low + HeaderSize || target >= high)
                    {
                        complaint = $"record at {cursor} refers outside the block (0x{target:x})";
                        return false;
                    }
                }
                cursor += HeaderSize + PayloadSize(objectAt, table);
            }

            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + HeaderSize;
                ulong table = *(ulong*)objectAt & ~1UL;
                ulong objectSize = PayloadSize(objectAt, table);
                int found = ObjectLayout.ReferenceSlots(objectAt, table, slots);
                for (int i = 0; i < found; i++)
                    *(ulong*)slots[i] -= low;
                TypeKeys.TryKey(table, out ulong key);
                *(ulong*)objectAt = key;
                cursor += HeaderSize + objectSize;
            }
            return true;
        }

        /// <summary>
        /// A copy of a translated region in this image's heap: ordinary objects,
        /// free to change, alive after the region is gone (pipe spec Р9).
        /// </summary>
        public static object ToHeap(byte* at, ulong size)
        {
            ulong low = (ulong)at;
            var sources = new List<ulong>();
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + HeaderSize;
                sources.Add(objectAt);
                cursor += HeaderSize + PayloadSize(objectAt, *(ulong*)objectAt & ~1UL);
            }

            // The copies are held by this array while their references still
            // point into the region: the collector passes those by.
            var copies = new object[sources.Count];
            var index = new Dictionary<ulong, int>();
            int maxReferences = 1;
            for (int n = 0; n < sources.Count; n++)
            {
                ulong objectAt = sources[n];
                ulong table = *(ulong*)objectAt & ~1UL;
                var mt = (SharpOS.Std.NoRuntime.GcMethodTable*)table;
                uint full = (uint)(PayloadSize(objectAt, table) + 8);
                void* raw = mt->HasComponentSize
                    ? SharpOS.Std.NoRuntime.GcHeap.AllocateArray(full, mt, *(int*)(objectAt + 8))
                    : SharpOS.Std.NoRuntime.GcHeap.AllocateObject(full, mt);
                if (raw == null) throw new OutOfMemoryException();
                nint address = (nint)raw;
                copies[n] = System.Runtime.CompilerServices.Unsafe.As<nint, object>(ref address);
                index[objectAt] = n;
                int max = ObjectLayout.MaxReferences(objectAt, table);
                if (max > maxReferences) maxReferences = max;
            }

            var slots = new ulong[maxReferences];
            for (int n = 0; n < sources.Count; n++)
            {
                ulong objectAt = sources[n];
                ulong table = *(ulong*)objectAt & ~1UL;
                ulong copy = AddressOf(copies[n]);
                ulong bytes = PayloadSize(objectAt, table) - 8;
                SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy((void*)(copy + 8), (void*)(objectAt + 8), bytes);
                int found = ObjectLayout.ReferenceSlots(copy, table, slots);
                for (int i = 0; i < found; i++)
                {
                    ulong* slot = (ulong*)slots[i];
                    *slot = index.TryGetValue(*slot, out int target) ? AddressOf(copies[target]) : 0;
                }
            }
            return copies.Length == 0 ? null : copies[0];
        }

        /// <summary>
        /// Overwrites every table word of a block going back to the exchange heap
        /// with a value that is no table and no key, so a reference that outlived
        /// the region faults on its next use instead of reading the next tenant.
        /// </summary>
        public static void Scrub(byte* at, ulong size)
        {
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = (ulong)at + cursor + HeaderSize;
                ulong word = *(ulong*)objectAt;
                ulong table = word & ~1UL;
                if (TypeKeys.IsKeyWord(word) && !TypeKeys.TryTable(word, out table))
                    return;
                if (table == 0)
                    return;
                ulong next = cursor + HeaderSize + PayloadSize(objectAt, table);
                *(ulong*)objectAt = ScrubWord;
                if (next <= cursor) return;
                cursor = next;
            }
        }

        /// <summary>What Scrub leaves in a table word: odd, non-canonical, never a declared key.</summary>
        public const ulong ScrubWord = 0x8BAD_F00D_DEAD_0001UL;

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

        /// <summary>Bytes of an object from its table word, by the given table and the object's length.</summary>
        public static ulong PayloadSize(ulong objectAt, ulong table)
        {
            ulong total = ObjectLayout.BaseSizeOf(table);
            if ((ObjectLayout.FlagsOf(table) & ObjectLayout.HasComponentSizeFlag) != 0)
                total += (ulong)*(uint*)(objectAt + 8) * ObjectLayout.ComponentSizeOf(table);
            // BaseSize counts the header word before the object; the copy starts
            // at the table word.
            return ((total + 7) & ~7UL) - 8;
        }

        private static ulong Lookup(ulong[] keys, ulong[] tables, ref int distinct, ulong key)
        {
            for (int i = 0; i < distinct; i++)
                if (keys[i] == key)
                    return tables[i];
            if ((key & 1) == 0 || !TypeKeys.TryTable(key, out ulong table))
                return 0;
            if (distinct < keys.Length)
            {
                keys[distinct] = key;
                tables[distinct++] = table;
            }
            return table;
        }

        private static ulong AddressOf(object o)
            => System.Runtime.CompilerServices.Unsafe.As<object, ulong>(ref o);
    }
}
