using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Memory;

namespace OS.PAL.SharpOSHost
{
    // C-ABI surface over the SharpOS VM manager. The CoreCLR fork's
    // gc/sharpos/gcenv.sharpos.cpp (GCToOSInterface::Virtual*) and
    // pal/sharpos/crt_imp_stubs.cpp (VirtualAlloc) call these instead of
    // routing through the kernel managed GcHeap (wired in S3).
    //
    // POD-only across the C-ABI line. int returns: 1=ok, 0=fail.
    internal static unsafe class VirtualMemoryHost
    {
        [RuntimeExport("SharpOSHost_VMReserve")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_VMReserve")]
        public static void* VMReserve(ulong size, ulong alignment)
            => VirtualMemory.Reserve(size, alignment);

        [RuntimeExport("SharpOSHost_VMCommit")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_VMCommit")]
        public static int VMCommit(void* addr, ulong size, int exec)
            => VirtualMemory.Commit(addr, size, exec != 0) ? 1 : 0;

        [RuntimeExport("SharpOSHost_VMDecommit")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_VMDecommit")]
        public static int VMDecommit(void* addr, ulong size)
            => VirtualMemory.Decommit(addr, size) ? 1 : 0;

        [RuntimeExport("SharpOSHost_VMRelease")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_VMRelease")]
        public static int VMRelease(void* addr, ulong size)
            => VirtualMemory.Release(addr, size) ? 1 : 0;

        [RuntimeExport("SharpOSHost_VMMapFixed")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_VMMapFixed")]
        public static int VMMapFixed(void* va, ulong pa, ulong size, int exec)
            => VirtualMemory.MapFixed(va, pa, size, exec != 0) ? 1 : 0;

        [RuntimeExport("SharpOSHost_VMProtect")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_VMProtect")]
        public static int VMProtect(void* addr, ulong size, int exec, int write)
            => VirtualMemory.Protect(addr, size, exec != 0, write != 0) ? 1 : 0;
    }
}
