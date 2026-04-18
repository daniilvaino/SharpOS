// Static roots registry for the GC.
//
// Kernel code that holds managed references in static fields registers
// those fields' addresses here at init time. GcRoots.MarkAll iterates the
// registry and invokes GcMark.MarkFromRoot on each slot's current value.
//
// Registry is a fixed-size array of `object*` slots in .bss (no dynamic
// growth — we don't have managed arrays that GC ignores). Capacity picked
// comfortably above the number of static references we expect in kernel.
//
// Registration is typically done once, at kernel startup:
//   GcRoots.Register(ref s_keep1);
//   GcRoots.Register(ref s_static_list);
// Each call captures the field's *address*, not its current value — so
// later writes to the field are visible to the next GC pass.

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential, Size = GcRoots.Capacity * 8)]
    internal struct GcRootsStorage { }

    internal static unsafe class GcRoots
    {
        public const int Capacity = 256;

        private static GcRootsStorage s_slots;
        private static int s_count;
        private static nint s_stackTop;

        public static int Count => s_count;
        public static nint StackTop => s_stackTop;

        // Snapshot current stack location as the top boundary for later
        // conservative scans. Must be called from the highest frame you
        // want the GC to scan — typically early in kernel Main, before
        // any managed references are put on the stack.
        public static void CaptureStackTop()
        {
            byte local;
            s_stackTop = (nint)(&local);
        }

        // Conservative scan of the current thread's stack. Reads each 8-byte
        // word from `rspLower` (passed by caller, usually address of caller's
        // local) up to the stored StackTop. For every word that looks like
        // a pointer into our GcHeap — calls GcMark.MarkFromRoot.
        //
        // Safe: words that happen to match some random value but don't point
        // into our heap are rejected by GcMark via FindSegmentContaining.
        // False positives cost only an extra MarkFromRoot call that does nothing.
        public static void ScanStack(nint rspLower)
        {
            if (s_stackTop == 0 || rspLower == 0 || rspLower >= s_stackTop)
                return;

            // Align lower bound down to 8 bytes (pointer-aligned words).
            rspLower = rspLower & ~(nint)7;

            nint* p = (nint*)rspLower;
            nint* end = (nint*)s_stackTop;

            while (p < end)
            {
                nint value = *p;
                if (value != 0)
                    GcMark.MarkFromRoot(value);
                p++;
            }
        }

        // Register a static field as a GC root. The slot holds the ADDRESS of
        // the field; the value read at collect-time reflects whatever the
        // field currently points to.
        public static void Register(ref object field)
        {
            if (s_count >= Capacity)
                return; // silently drop (shouldn't happen with reasonable Capacity)

            fixed (object* fieldPtr = &field)
            fixed (GcRootsStorage* basePtr = &s_slots)
            {
                nint** slots = (nint**)basePtr;
                slots[s_count] = (nint*)fieldPtr;
                s_count++;
            }
        }

        // Iterate registered static slots + conservative stack scan.
        // GcMark.Begin must have been called by the caller.
        //
        // Stack scan starts from the caller's stack frame: we capture
        // the address of a local here, giving us a lower-bound pointer
        // inside THIS function's frame. Scanning UP from there covers
        // the caller and everything higher.
        public static void MarkAll()
        {
            // Static slots first
            fixed (GcRootsStorage* basePtr = &s_slots)
            {
                nint** slots = (nint**)basePtr;
                for (int i = 0; i < s_count; i++)
                {
                    nint* slot = slots[i];
                    if (slot == null) continue;
                    nint value = *slot;
                    if (value != 0)
                        GcMark.MarkFromRoot(value);
                }
            }

            // Stack scan — address of our own local marks the bottom
            byte marker;
            ScanStack((nint)(&marker));
        }
    }
}
