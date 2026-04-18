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
    }
}
