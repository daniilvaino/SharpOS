// GcObject — runtime-level object layout view with mark-bit support.
//
// Adapted from Kevin Gosse's ManagedDotnetGC (MIT):
//   https://github.com/kevingosse/ManagedDotnetGC
//
// Layout (NativeAOT on x64):
//   +0  : MethodTable* (with mark bit in lowest bit)
//   +8  : Length (for strings & arrays; for fixed-size objects, reserved)
//   +12 : user data
//
// Mark bit trick: the MethodTable* is always aligned (at least 4-byte,
// in practice 8-byte on x64), so its low bits are free. We use bit 0 to
// mark the object as reachable during GC mark phase.

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct GcObject
    {
        public GcMethodTable* RawMethodTable;
        public uint Length;

        public readonly GcMethodTable* MethodTable =>
            (GcMethodTable*)((nint)RawMethodTable & ~1);

        public bool IsMarked() => ((nint)RawMethodTable & 1) != 0;

        public void Mark() =>
            RawMethodTable = (GcMethodTable*)((nint)MethodTable | 1);

        public void Unmark() =>
            RawMethodTable = (GcMethodTable*)((nint)MethodTable & ~1);

        public readonly uint ComputeSize()
        {
            GcMethodTable* mt = MethodTable;

            if (!mt->HasComponentSize)
            {
                // Fixed-size object (class, struct instance, boxed primitive)
                return mt->BaseSize;
            }

            // Variable-size object (string, array)
            return mt->BaseSize + Length * mt->ComponentSize;
        }

        // Iterate over all managed references inside this object and call
        // `callback(ptr)` for each non-null target. Ported from Kevin Gosse's
        // GcObject.EnumerateObjectReferences (MIT), adapted to use managed
        // function pointer instead of Action<IntPtr> (no delegate alloc in
        // NoStdLib profile).
        //
        // NativeAOT GC descriptor layout (from MethodTable.h):
        //   mt[-1] = series count (signed):
        //     positive → regular series, mt[-2..-(count+1)] = GcDescSeries
        //     zero/negative → value-type array series (for T[] with struct refs)
        public static void EnumerateObjectReferences(GcObject* obj, delegate*<nint, void> callback)
        {
            if (!obj->MethodTable->HasPointers)
                return;

            nint* mt = (nint*)obj->MethodTable;
            uint objectSize = obj->ComputeSize();
            nint seriesCount = mt[-1];

            if (seriesCount > 0)
            {
                // Regular series: fields at fixed offsets within the object.
                GcDescSeries* series = (GcDescSeries*)(mt - 1);

                for (int i = 1; i <= seriesCount; i++)
                {
                    nint sz = series[-i].Size + (nint)objectSize;
                    nint off = series[-i].Offset;

                    nint* ptr = (nint*)((nint)obj + off);
                    nint count = sz / sizeof(nint);

                    for (nint j = 0; j < count; j++)
                    {
                        nint target = ptr[j];
                        if (target != 0)
                            callback(target);
                    }
                }
            }
            else
            {
                // Value-type array series: for T[] where T is a struct with
                // embedded references (e.g. KeyValuePair<K,V>[]).
                nint offset = mt[-2];
                ValSerieItem* valSeries = (ValSerieItem*)(mt - 2) - 1;

                nint* ptr = (nint*)((nint)obj + offset);
                uint length = obj->Length;

                for (uint item = 0; item < length; item++)
                {
                    for (nint i = 0; i > seriesCount; i--)
                    {
                        ValSerieItem* valSerieItem = valSeries + i;

                        for (uint j = 0; j < valSerieItem->Nptrs; j++)
                        {
                            nint target = *ptr;
                            if (target != 0)
                                callback(target);
                            ptr++;
                        }

                        ptr = (nint*)((nint)ptr + valSerieItem->Skip);
                    }
                }
            }
        }
    }
}
