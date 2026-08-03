using System.Runtime;

namespace SharpOS.AppSdk
{
    // ILC emits calls to RhpByRefAssignRef for ref-to-ref struct copies whose type
    // holds object references (List<T> of a reference type moving elements, for
    // instance). The helper uses a non-standard convention — rdi = destination,
    // rsi = source, both incremented by 8, rcx clobbered — which a managed body
    // (Win64 ABI: rcx/rdx) cannot express.
    //
    // Same shape as the kernel's OS/src/Boot/ByRefAssignRefStub.cs +
    // ByRefAssignRefPatcher: keep a symbol with the right name so ILC reserves an
    // address, then overwrite the body with hand-written bytes at startup. Unlike
    // InterfaceDispatchTrampoline this needs no handoff from the kernel: our GC is
    // non-moving mark-sweep with no card table, so the whole helper is a qword copy
    // plus two increments and the app can write it itself.
    internal static unsafe class ByRefAssignRefStub
    {
        [RuntimeExport("RhpByRefAssignRef")]
        private static void RhpByRefAssignRef()
        {
            // Unpatched fallback. The body must be at least 15 bytes so the patch
            // fits; the string load + call + halt loop is comfortably larger.
            AppHost.WriteString("RhpByRefAssignRef: stub not patched\r\n");
            for (; ; ) { }
        }

        private static void* GetMethodAddress()
        {
            delegate*<void> fn = &RhpByRefAssignRef;
            return (void*)fn;
        }

        // 15 bytes, identical to the kernel's:
        //   mov rcx, [rsi]    ; 48 8B 0E
        //   mov [rdi], rcx    ; 48 89 0F
        //   add rdi, 8        ; 48 83 C7 08
        //   add rsi, 8        ; 48 83 C6 08
        //   ret               ; C3
        public static bool TryInstall()
        {
            byte* target = (byte*)GetMethodAddress();
            if (target == null)
                return false;

            target[0]  = 0x48; target[1]  = 0x8B; target[2]  = 0x0E;
            target[3]  = 0x48; target[4]  = 0x89; target[5]  = 0x0F;
            target[6]  = 0x48; target[7]  = 0x83; target[8]  = 0xC7; target[9]  = 0x08;
            target[10] = 0x48; target[11] = 0x83; target[12] = 0xC6; target[13] = 0x08;
            target[14] = 0xC3;

            // Readback: fails silently if .text came in read-only.
            return target[0] == 0x48 && target[1] == 0x8B && target[2] == 0x0E;
        }
    }
}
