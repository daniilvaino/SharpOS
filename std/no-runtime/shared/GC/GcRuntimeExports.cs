// NativeAOT runtime export symbols backed by GcHeap.
//
// NativeAOT's codegen emits calls to RhpNewFast / RhpNewArray / RhNewString
// for `new T()`, `new T[n]`, `new string(...)`. These are normally provided
// by the NativeAOT runtime (AllocFast.asm etc.). In our freestanding setup
// we define them as [RuntimeExport] C# methods that allocate from GcHeap.
//
// Layout (matches dotnet/runtime/src/coreclr/nativeaot/Runtime/portable.cpp):
//   Object : [MethodTable*](8)     — BaseSize bytes total
//   Array  : [MethodTable*](8)[Length(4)]    — BaseSize + Length*ComponentSize
//   String : same as Array (NativeAOT treats string as an array in this path)
//
// Signatures copied from portable.cpp. Linked into OS.csproj (kernel) only
// for phase 3; apps keep C-stub RhNewString until phase 4 when we migrate
// them off.

using System.Runtime;

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GcRuntimeExports
    {
        [RuntimeExport("RhpNewFast")]
        private static void* RhpNewFast(GcMethodTable* mt)
        {
            if (mt == null)
                return null;

            uint size = mt->BaseSize;
            void* obj = GcHeap.AllocateRaw(size);
            if (obj == null)
                return null;

            *(GcMethodTable**)obj = mt;
            return obj;
        }

        [RuntimeExport("RhpNewArray")]
        private static void* RhpNewArray(GcMethodTable* mt, int numElements)
        {
            if (mt == null || numElements < 0)
                return null;

            // size = BaseSize + numElements * ComponentSize, pointer-aligned
            ulong size64 = (ulong)mt->BaseSize + ((ulong)(uint)numElements * (ulong)mt->ComponentSize);
            size64 = (size64 + 7UL) & ~7UL;
            if (size64 > 0xFFFFFFFFUL)
                return null;

            void* obj = GcHeap.AllocateRaw((uint)size64);
            if (obj == null)
                return null;

            *(GcMethodTable**)obj = mt;
            // Length field lives at offset 8 (sizeof(MethodTable*) on x64).
            *(int*)((byte*)obj + 8) = numElements;
            return obj;
        }

        [RuntimeExport("RhNewString")]
        private static void* RhNewString(GcMethodTable* mt, int numElements)
        {
            // NativeAOT's portable.cpp delegates RhNewString to RhpNewArray.
            // string's MT has ComponentSize = 2 (one char) and BaseSize covers
            // the header + null-terminator slot.
            return RhpNewArray(mt, numElements);
        }

        // Write barrier for managed reference assignment (e.g. `s_field = obj`).
        // In NativeAOT's generational GC this marks the containing card dirty.
        // Our GC is single-threaded non-generational mark-sweep — no write
        // barrier needed, just plain pointer store.
        [RuntimeExport("RhpAssignRef")]
        private static void RhpAssignRef(void** dst, void* src)
        {
            *dst = src;
        }

        // Same as RhpAssignRef with a null-pointer check before write. We
        // don't care about the check (CLR uses it to protect against bad
        // targets during lazy init) — just perform the store.
        [RuntimeExport("RhpCheckedAssignRef")]
        private static void RhpCheckedAssignRef(void** dst, void* src)
        {
            *dst = src;
        }

        // Box a value type into a fresh object. ILC emits a call to RhBox for
        // implicit `object o = valueType` and for constrained virtual dispatch
        // on a value type where the target method is inherited from Object
        // (e.g., x.Equals(y) inside a generic `where T : anything`).
        //
        // Real NativeAOT RhBox handles Nullable<T> unwrapping, GC write barriers,
        // and RequiresAlign8 misaligned alloc. We skip all of that — our GC is
        // non-generational (no barrier) and Nullable<T> is a stub empty struct.
        //
        // Copy size: real runtime uses mt->ValueTypeSize; we don't have that
        // field on GcMethodTable, so we use mt->BaseSize - 8 (BaseSize includes
        // the MT* header plus padding). That may copy a few bytes of padding
        // slack, harmless because readers only look at the declared value bytes.
        [RuntimeExport("RhBox")]
        public static object RhBox(Internal.Runtime.MethodTable* mt, ref byte data)
        {
            if (mt == null) return null;

            // Our GcMethodTable shares layout with Internal.Runtime.MethodTable;
            // cast via nint/pointer-reinterpret to read BaseSize.
            GcMethodTable* gcMt = (GcMethodTable*)mt;
            void* obj = GcHeap.AllocateRaw(gcMt->BaseSize);
            if (obj == null) return null;

            *(Internal.Runtime.MethodTable**)obj = mt;

            uint payload = gcMt->BaseSize - 8;
            byte* dst = (byte*)obj + 8;
            fixed (byte* pData = &data)
            {
                for (uint i = 0; i < payload; i++)
                    dst[i] = pData[i];
            }

            object result = null;
            *(void**)&result = obj;
            return result;
        }

        // Reference-array element store (ILC generates a call to this for
        // `arr[i] = obj` where arr is object[] or any other reference-type
        // array). Signature MUST match the NativeAOT contract exactly
        // (Array, nint, object) — ILC matches [RuntimeExport] targets by
        // signature, not just name. See dotnet/runtime TypeCast.cs:745.
        //
        // The real runtime does null/bounds/covariance-type checks plus a
        // write barrier; we skip all of them (kernel code is trusted, our
        // GC is single-threaded non-generational so no barrier needed).
        //
        // Array layout (NativeAOT x64):
        //   +0:  MethodTable*
        //   +8:  Length (int32 + 4-byte pad)
        //   +16: element[0], element[1], ...   (8 bytes each for refs)
        [RuntimeExport("RhpStelemRef")]
        public static unsafe void RhpStelemRef(System.Array array, nint index, object value)
        {
            if (array == null) return;
            nint arrayAddr = *(nint*)&array;
            nint valueAddr = value == null ? 0 : *(nint*)&value;
            byte* slot = (byte*)arrayAddr + 16 + ((long)index * 8);
            *(nint*)slot = valueAddr;
        }
    }
}
