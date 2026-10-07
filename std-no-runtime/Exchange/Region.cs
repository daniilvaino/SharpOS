using System;

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
        /// <remarks>
        /// A RegionWriter of its own: the static API is for probes and one-off
        /// copies. A pipe writer keeps one writer for all its messages
        /// (step195), and allocates nothing per message.
        /// </remarks>
        public sealed class Plan
        {
            internal RegionWriter Writer;

            /// <summary>Bytes the region takes.</summary>
            public ulong Size => Writer.Size;

            /// <summary>Objects in it.</summary>
            public int Count => Writer.Count;
        }

        /// <summary>
        /// Lays out the graph under <paramref name="root"/>. Null and a complaint
        /// when a type in it has no key.
        /// </summary>
        public static Plan Lay(object root, out string complaint)
        {
            var writer = new RegionWriter();
            if (!writer.Lay(root))
            {
                complaint = writer.Complaint;
                return null;
            }
            complaint = null;
            return new Plan { Writer = writer };
        }

        /// <summary>
        /// Writes a laid-out graph to <paramref name="at"/> (at least plan.Size
        /// bytes): keys in table words, offsets in reference slots.
        /// </summary>
        public static void Write(Plan plan, byte* at) => plan.Writer.Write(at);

        /// <summary>
        /// Translates a region in place: keys to this image's tables, offsets to
        /// addresses. All or nothing (RegionReader).
        /// </summary>
        public static bool Resolve(byte* at, ulong size, out object root, out string complaint)
            => Resolve(at, size, out root, out complaint, out _);

        /// <summary>The same; <paramref name="missingKey"/> is the key this image has no type for, when that was the refusal.</summary>
        public static bool Resolve(byte* at, ulong size, out object root, out string complaint, out ulong missingKey)
        {
            var reader = new RegionReader();
            bool ok = reader.Resolve(at, size, out root);
            complaint = reader.Complaint;
            missingKey = reader.MissingKey;
            return ok;
        }

        /// <summary>
        /// The reverse of Resolve, for sending a received region on (pipe spec
        /// Р10, Р12): tables back to keys, addresses back to offsets. All or
        /// nothing.
        /// </summary>
        public static bool Release(byte* at, ulong size, out string complaint)
        {
            var reader = new RegionReader();
            bool ok = reader.Release(at, size);
            complaint = reader.Complaint;
            return ok;
        }

        /// <summary>
        /// A copy of a translated region in this image's heap: ordinary objects,
        /// free to change, alive after the region is gone (pipe spec Р9).
        /// </summary>
        /// <remarks>
        /// Each record's header word (zero in a region) holds its copy's address
        /// while the copies are made, so a reference finds its copy without a
        /// dictionary (step195); the words are zero again afterwards. The copies
        /// are held by an array meanwhile: the header words are outside every
        /// collector's view.
        /// </remarks>
        public static object ToHeap(byte* at, ulong size)
        {
            ulong low = (ulong)at;
            int count = 0;
            for (ulong cursor = 0; cursor < size; count++)
            {
                ulong objectAt = low + cursor + HeaderSize;
                cursor += HeaderSize + PlanOf(objectAt).PayloadSize(objectAt);
            }

            var copies = new Chunked<object>(count);
            int n = 0;
            for (ulong cursor = 0; cursor < size; n++)
            {
                ulong objectAt = low + cursor + HeaderSize;
                TypePlan plan = PlanOf(objectAt);
                copies[n] = Allocate(objectAt, plan);
                *(ulong*)(objectAt - HeaderSize) = AddressOf(copies[n]);
                cursor += HeaderSize + plan.PayloadSize(objectAt);
            }

            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + HeaderSize;
                TypePlan plan = PlanOf(objectAt);
                ulong payload = plan.PayloadSize(objectAt);
                CopyInto(*(ulong*)(objectAt - HeaderSize), objectAt, plan, payload, low, low + size);
                cursor += HeaderSize + payload;
            }

            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = low + cursor + HeaderSize;
                *(ulong*)(objectAt - HeaderSize) = 0;
                cursor += HeaderSize + PlanOf(objectAt).PayloadSize(objectAt);
            }
            return count == 0 ? null : copies[0];
        }

        /// <summary>
        /// A copy in this image's heap of the graph under one object of a
        /// translated region: that object and everything it reaches inside the
        /// region; references leaving the region are kept as they are.
        /// </summary>
        /// <remarks>
        /// The same header words: first the index + 1 of each object reached
        /// (a mark that it was), then its copy's address.
        /// </remarks>
        public static object ToHeapFrom(ulong start)
        {
            if (!ExchangeArena.TryBlock(start, out ulong low, out ulong high))
                return null;

            var sources = new Reached();
            sources.Add(start);
            *(ulong*)(start - HeaderSize) = 1;
            for (int i = 0; i < sources.Count; i++)
            {
                ulong objectAt = sources.At[i];
                VisitTargets(objectAt, PlanOf(objectAt), low, high, sources);
            }

            var copies = new Chunked<object>(sources.Count);
            for (int i = 0; i < sources.Count; i++)
            {
                ulong objectAt = sources.At[i];
                copies[i] = Allocate(objectAt, PlanOf(objectAt));
                *(ulong*)(objectAt - HeaderSize) = AddressOf(copies[i]);
            }
            for (int i = 0; i < sources.Count; i++)
            {
                ulong objectAt = sources.At[i];
                TypePlan plan = PlanOf(objectAt);
                CopyInto(AddressOf(copies[i]), objectAt, plan, plan.PayloadSize(objectAt), low, high);
            }
            for (int i = 0; i < sources.Count; i++)
                *(ulong*)(sources.At[i] - HeaderSize) = 0;
            return copies[0];
        }

        // A translated record's plan: its table word is this image's table.
        private static TypePlan PlanOf(ulong objectAt)
        {
            TypePlan plan = TypePlans.ByTable(*(ulong*)objectAt & ~1UL);
            if (plan == null) throw new InvalidOperationException("a record of a type outside the catalog");
            return plan;
        }

        private static object Allocate(ulong objectAt, TypePlan plan)
        {
            var mt = (SharpOS.Std.NoRuntime.GcMethodTable*)plan.Table;
            uint full = (uint)(plan.PayloadSize(objectAt) + 8);
            void* raw = plan.HasComponents
                ? SharpOS.Std.NoRuntime.GcHeap.AllocateArray(full, mt, *(int*)(objectAt + 8))
                : SharpOS.Std.NoRuntime.GcHeap.AllocateObject(full, mt);
            if (raw == null) throw new OutOfMemoryException();
            nint address = (nint)raw;
            return System.Runtime.CompilerServices.Unsafe.As<nint, object>(ref address);
        }

        // The bytes after the table word, then every reference into the region
        // turned to its target's copy (the address in the target's header word).
        private static void CopyInto(ulong copy, ulong objectAt, TypePlan plan, ulong payload, ulong low, ulong high)
        {
            SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy((void*)(copy + 8), (void*)(objectAt + 8), payload - 8);
            int[] slots = plan.Slots;
            for (int s = 0; s < slots.Length; s++)
                Forward((ulong*)(copy + (ulong)slots[s]), low, high);
            if (plan.Elements == TypePlan.ReferenceElements)
            {
                ulong* element = (ulong*)(copy + (ulong)plan.FirstElement);
                uint length = *(uint*)(objectAt + 8);
                for (uint k = 0; k < length; k++) Forward(element + k, low, high);
            }
            else if (plan.Elements == TypePlan.StructElements)
            {
                int[] inner = plan.ElementSlots;
                ulong element = copy + (ulong)plan.FirstElement;
                uint length = *(uint*)(objectAt + 8);
                for (uint k = 0; k < length; k++, element += plan.ComponentSize)
                    for (int s = 0; s < inner.Length; s++) Forward((ulong*)(element + (ulong)inner[s]), low, high);
            }
        }

        private static void Forward(ulong* slot, ulong low, ulong high)
        {
            ulong target = *slot;
            if (target >= low && target < high && target != 0)
                *slot = *(ulong*)(target - HeaderSize);
        }

        // The region objects a translated object reaches, each noted once.
        // The objects ToHeapFrom reached, in the order it reached them.
        private sealed class Reached
        {
            public readonly Chunked<ulong> At = new Chunked<ulong>(64);
            public int Count;

            public void Add(ulong objectAt)
            {
                At.Ensure(Count + 1);
                At[Count++] = objectAt;
            }
        }

        private static void VisitTargets(ulong objectAt, TypePlan plan, ulong low, ulong high, Reached sources)
        {
            int[] slots = plan.Slots;
            for (int s = 0; s < slots.Length; s++)
                Reach(*(ulong*)(objectAt + (ulong)slots[s]), low, high, sources);
            if (plan.Elements == TypePlan.ReferenceElements)
            {
                ulong* element = (ulong*)(objectAt + (ulong)plan.FirstElement);
                uint length = *(uint*)(objectAt + 8);
                for (uint k = 0; k < length; k++) Reach(element[k], low, high, sources);
            }
            else if (plan.Elements == TypePlan.StructElements)
            {
                int[] inner = plan.ElementSlots;
                ulong element = objectAt + (ulong)plan.FirstElement;
                uint length = *(uint*)(objectAt + 8);
                for (uint k = 0; k < length; k++, element += plan.ComponentSize)
                    for (int s = 0; s < inner.Length; s++) Reach(*(ulong*)(element + (ulong)inner[s]), low, high, sources);
            }
        }

        private static void Reach(ulong target, ulong low, ulong high, Reached sources)
        {
            if (target == 0 || target < low || target >= high) return;
            if (*(ulong*)(target - HeaderSize) != 0) return;
            sources.Add(target);
            *(ulong*)(target - HeaderSize) = (ulong)sources.Count;
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
                TypePlan plan = TypeKeys.IsKeyWord(word) ? TypePlans.ByKey(word) : TypePlans.ByTable(word & ~1UL);
                if (plan == null)
                    return;
                ulong next = cursor + HeaderSize + plan.PayloadSize(objectAt);
                *(ulong*)objectAt = ScrubWord;
                if (next <= cursor) return;
                cursor = next;
            }
        }

        /// <summary>What Scrub leaves in a table word: odd, non-canonical, never a declared key.</summary>
        public const ulong ScrubWord = 0x8BAD_F00D_DEAD_0001UL;

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

        private static ulong AddressOf(object o)
            => System.Runtime.CompilerServices.Unsafe.As<object, ulong>(ref o);
    }
}
