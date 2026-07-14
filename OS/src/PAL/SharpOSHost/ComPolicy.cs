using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.10 — kernel-side policy for ole32/combase COM init surface.
    // PowerShell calls CoInitializeEx to set up COM threading model
    // (apartment-threaded for STA, multi-threaded for MTA). On unikernel
    // there's no real COM activation possible (no other processes to
    // marshal to, no CLSID registry to resolve), but PowerShell's init
    // expects CoInitializeEx to succeed before it proceeds.
    //
    // We accept all init calls with S_OK / S_FALSE depending on whether
    // this is the first call (S_OK) or a re-init within same apartment
    // (S_FALSE). For simplicity always return S_OK.
    //
    // CoCreateInstance / CoCreateInstanceEx would land here too if any
    // managed code reaches that point — currently PowerShell doesn't on
    // bare-init path, but if a user later writes `New-Object -ComObject
    // Foo`, those will need stubs returning REGDB_E_CLASSNOTREG
    // (0x80040154) so the failure is a clean catchable exception.
    internal static unsafe class ComPolicy
    {
        public const int S_OK    = 0;
        public const int S_FALSE = 1;

        // HRESULT codes for COM operations:
        public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
        public const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

        [RuntimeExport("SharpOSHost_CoInitializeEx")]
        public static int CoInitializeEx(int coInit) { _ = coInit; return S_OK; }

        [RuntimeExport("SharpOSHost_CoInitialize")]
        public static int CoInitialize() => S_OK;

        [RuntimeExport("SharpOSHost_CoUninitialize")]
        public static void CoUninitialize() { /* no-op */ }

        // CoCreateInstance: no CLSIDs registered — every call is "class
        // not registered". Catchable as System.Runtime.InteropServices.
        // COMException by BCL.
        [RuntimeExport("SharpOSHost_CoCreateInstance")]
        public static int CoCreateInstance(void** outIface)
        {
            if (outIface != null) *outIface = null;
            return REGDB_E_CLASSNOTREG;
        }

        // CoTaskMemAlloc / CoTaskMemFree — COM heap allocator. Used by
        // SHGetKnownFolderPath to return PWSTR caller must free. We use
        // the kernel NativeArena (same as SharpOSHost_HeapAlloc); Free is
        // a no-op — blocks become unreachable once C++ drops the pointer.
        [RuntimeExport("SharpOSHost_CoTaskMemAlloc")]
        public static void* CoTaskMemAlloc(ulong size)
        {
            if (size == 0) return null;
            return OS.Kernel.Memory.NativeArena.Allocate(size);
        }

        [RuntimeExport("SharpOSHost_CoTaskMemFree")]
        public static void CoTaskMemFree(void* ptr)
        {
            // No-op — managed-side ownership.
        }
    }
}
