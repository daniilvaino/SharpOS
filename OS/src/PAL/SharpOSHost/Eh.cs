using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // L3 EH function table surface — per D13 (EH via Phase 1 unwinder).
    //
    // Win32 names → SharpOSHost mapping:
    //   RtlInstallFunctionTableCallback → SharpOSHost_RegisterFunctionTable
    //   RtlDeleteFunctionTable          → SharpOSHost_UnregisterFunctionTable
    //
    // CoreCLR JIT emits code into RX pages, then registers .pdata entries
    // dynamically for unwinder to find them. Without this, EH unwinder
    // can't trace stack через JIT'ed frames.
    //
    // Phase 6.1.a: must REAL implementation. Phase 1 unwinder already
    // has function-table lookup mechanics; extend для dynamic registration.
    //
    // The actual primitive function (not exported via SharpOSHost — это
    // managed-side helper called by JIT entry stubs) lives в
    // OS/src/Boot/EH/ unwinder code.
    //
    // Parameters mirror Win32 RtlInstallFunctionTableCallback:
    //   tableId   — handle (low 2 bits = 11 для disambiguation от static .pdata)
    //   baseAddr  — start of code region (for callback's relative offsets)
    //   sizeOfRange — size of code region
    //   callback  — fn(controlPc, context) → PRUNTIME_FUNCTION
    //   context   — opaque passed to callback
    //   moduleName — кanonical UTF-16 string или null
    internal static unsafe class SharpOSHostEh
    {
        [RuntimeExport("SharpOSHost_RegisterFunctionTable")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegisterFunctionTable")]
        public static int RegisterFunctionTable(
            ulong tableId,
            ulong baseAddr,
            uint sizeOfRange,
            void* callback,
            void* context,
            char* moduleName)
        {
            Panic.Fail("SharpOSHost_RegisterFunctionTable not implemented (Phase 6.1.c, D13 EH integration)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_UnregisterFunctionTable")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_UnregisterFunctionTable")]
        public static int UnregisterFunctionTable(ulong tableId)
        {
            Panic.Fail("SharpOSHost_UnregisterFunctionTable not implemented (Phase 6.1.c)");
            return 0;
        }
    }
}
