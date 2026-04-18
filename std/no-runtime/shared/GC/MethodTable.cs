// NativeAOT MethodTable (EEType) layout parser.
//
// Adapted from Kevin Gosse's ManagedDotnetGC (MIT-licensed):
//   https://github.com/kevingosse/ManagedDotnetGC
//
// IMPORTANT: Kevin Gosse's original targets CoreCLR MethodTable, where Flags
// is uint32 overlapping with ComponentSize. NativeAOT's EEType is different:
// Flags is uint16 at offset 2, WITHOUT overlap. We use NativeAOT layout here.
//
// NativeAOT EEType layout (from dotnet/runtime/src/coreclr/nativeaot/Runtime/inc/eetype.h):
//   offset 0  — ComponentSize (uint16): non-zero for arrays/strings
//   offset 2  — Flags (uint16): Kind in low 2 bits + attribute flags
//   offset 4  — BaseSize (uint32): size of fixed part
//   offset 8  — RelatedType (EEType*): ParentType for classes, ElementType for arrays
//   offset 16 — NumInterfaces (uint16), NumVtableSlots (uint16)
//   offset 20 — HashCode (uint32)
//   offset 24 — VtableSlot[0] ...
//
// Verified offsets via SUPER-2 phase 0 reconnaissance (see gc-experiment/PLAN.md).
//
// Phase 1 exposes only the layout fields we're confident about. Attribute flag
// queries (IsArray, IsValueType, ContainsGCPointers) deferred to Phase 3 when
// we actually need them for mark phase — they require careful research into
// NativeAOT flag semantics vs CoreCLR's.

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct GcMethodTable
    {
        [FieldOffset(0)]
        public ushort ComponentSize;

        [FieldOffset(2)]
        public ushort Flags;

        [FieldOffset(4)]
        public uint BaseSize;

        [FieldOffset(8)]
        public GcMethodTable* RelatedType;

        // Universal rule: an EEType has a variable-size tail iff its ComponentSize > 0.
        // Works for both arrays (T[] with element-size component) and strings
        // (which are variable-size, component = sizeof(char) = 2).
        public bool HasComponentSize => ComponentSize != 0;
    }
}
