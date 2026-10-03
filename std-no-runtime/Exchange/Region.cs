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
                    complaint = $"type not declared: table 0x{table:x}, base {ObjectLayout.BaseSizeOf(table)}";
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
