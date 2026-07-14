using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Crypto;
using OS.Kernel.Memory;

namespace OS.PAL.SharpOSHost
{
    // step 99 pass 7 -- SHA-256 hash bridge. Forked CoreCLR's PAL no
    // longer contains the SHA-256 algorithm; it forwards through these
    // exports to the kernel-side OS.Kernel.Crypto.Sha256 implementation
    // (CLAUDE.md invariant: no algorithmic code in PAL).
    //
    // Surface:
    //   SharpOSHost_Sha256_Create()                -> opaque handle
    //   SharpOSHost_Sha256_Update(h, data, len)
    //   SharpOSHost_Sha256_Final(h, out32)
    //   SharpOSHost_Sha256_Snapshot(h, out32)      -- non-destructive
    //   SharpOSHost_Sha256_Destroy(h)
    //   SharpOSHost_Sha256_OneShot(data, len, out32)
    //
    // Handles are pointers to heap-allocated Sha256State boxes; PAL
    // treats them as opaque void*. The C++ side never inspects the
    // struct interior.
    internal static unsafe class Sha256Bridge
    {
        [RuntimeExport("SharpOSHost_Sha256_Create")]
        public static Sha256State* Create()
        {
            Sha256State* state = (Sha256State*)NativeArena.Allocate((ulong)sizeof(Sha256State));
            if (state == null) return null;
            Sha256.Init(state);
            return state;
        }

        [RuntimeExport("SharpOSHost_Sha256_Update")]
        public static void Update(Sha256State* state, byte* data, uint len)
        {
            if (state == null || data == null || len == 0) return;
            Sha256.Update(state, data, len);
        }

        [RuntimeExport("SharpOSHost_Sha256_Final")]
        public static void Final(Sha256State* state, byte* out32)
        {
            if (state == null || out32 == null) return;
            Sha256.Final(state, out32);
        }

        [RuntimeExport("SharpOSHost_Sha256_Snapshot")]
        public static void Snapshot(Sha256State* state, byte* out32)
        {
            if (state == null || out32 == null) return;
            Sha256.Snapshot(state, out32);
        }

        [RuntimeExport("SharpOSHost_Sha256_Reset")]
        public static void Reset(Sha256State* state)
        {
            if (state == null) return;
            Sha256.Init(state);
        }

        [RuntimeExport("SharpOSHost_Sha256_Destroy")]
        public static void Destroy(Sha256State* state)
        {
            // GcHeap is mark-sweep; the box becomes unreachable once the
            // caller drops the handle. No explicit free needed (and our
            // GC has no Free).
            _ = state;
        }

        [RuntimeExport("SharpOSHost_Sha256_OneShot")]
        public static void OneShot(byte* data, uint len, byte* out32)
        {
            if (out32 == null) return;
            // Sha256.OneShot calls Update only when len > 0, so null data
            // with len == 0 is safe.
            Sha256.OneShot(data, len, out32);
        }
    }
}
