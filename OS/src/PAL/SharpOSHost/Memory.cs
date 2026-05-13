using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // L3 Memory surface — per D9 (memory forward to SharpOSHost) и
    // D11 firewall (kernel memory not directly exposed; all CoreCLR
    // memory ops route here).
    //
    // Maps to Win32 names CoreCLR `vm/` calls через `pal/sharpos/`:
    //   VirtualAlloc            → SharpOSHost_AllocPages
    //   VirtualFree             → SharpOSHost_FreePages
    //   VirtualProtect          → SharpOSHost_ProtectPages
    //   VirtualQuery            → SharpOSHost_QueryPages
    //   CreateFileMapping +     → SharpOSHost_MapFile
    //     MapViewOfFile
    //   UnmapViewOfFile         → SharpOSHost_UnmapFile
    //
    // Phase 6.1.0 baseline: все вызовы Panic.Fail с named message.
    // Phase 6.1.a fills VirtualAlloc/Free/Protect для GC heap init.
    // Phase 6.1.b adds Query + MapFile для assembly загрузки из memory blob.
    //
    // Protection flags mirror Win32 PAGE_*:
    //   0x01 = NOACCESS
    //   0x02 = READONLY
    //   0x04 = READWRITE
    //   0x10 = EXECUTE
    //   0x20 = EXECUTE_READ
    //   0x40 = EXECUTE_READWRITE
    //
    // Allocation flags:
    //   0x1000 = MEM_COMMIT
    //   0x2000 = MEM_RESERVE
    //   0x4000 = MEM_DECOMMIT
    //   0x8000 = MEM_RELEASE
    internal static unsafe class SharpOSHostMemory
    {
        [RuntimeExport("SharpOSHost_AllocPages")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_AllocPages")]
        public static void* AllocPages(void* address, ulong size, uint allocFlags, uint protectFlags)
        {
            Panic.Fail("SharpOSHost_AllocPages not implemented (Phase 6.1.a)");
            return null;
        }

        [RuntimeExport("SharpOSHost_FreePages")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_FreePages")]
        public static int FreePages(void* address, ulong size, uint freeFlags)
        {
            Panic.Fail("SharpOSHost_FreePages not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_ProtectPages")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ProtectPages")]
        public static int ProtectPages(void* address, ulong size, uint newProtect, uint* oldProtect)
        {
            Panic.Fail("SharpOSHost_ProtectPages not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_QueryPages")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_QueryPages")]
        public static ulong QueryPages(void* address, void* buffer, ulong bufferSize)
        {
            Panic.Fail("SharpOSHost_QueryPages not implemented (Phase 6.1.b)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_MapFile")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_MapFile")]
        public static void* MapFile(void* fileBlob, ulong blobSize, ulong offset, ulong viewSize, uint protectFlags)
        {
            Panic.Fail("SharpOSHost_MapFile not implemented (Phase 6.1.b)");
            return null;
        }

        [RuntimeExport("SharpOSHost_UnmapFile")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_UnmapFile")]
        public static int UnmapFile(void* view)
        {
            Panic.Fail("SharpOSHost_UnmapFile not implemented (Phase 6.1.b)");
            return 0;
        }
    }
}
