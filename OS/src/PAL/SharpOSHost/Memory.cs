using System.Runtime;
using System.Runtime.InteropServices;
using OS.Boot;
using OS.Hal;
using OS.Kernel;
using OS.Kernel.Paging;

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
        public static void* AllocPages(void* address, ulong size, uint allocFlags, uint protectFlags)
        {
            Panic.Fail("SharpOSHost_AllocPages not implemented (Phase 6.1.a)");
            return null;
        }

        [RuntimeExport("SharpOSHost_FreePages")]
        public static int FreePages(void* address, ulong size, uint freeFlags)
        {
            Panic.Fail("SharpOSHost_FreePages not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_ProtectPages")]
        public static int ProtectPages(void* address, ulong size, uint newProtect, uint* oldProtect)
        {
            Panic.Fail("SharpOSHost_ProtectPages not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_QueryPages")]
        public static ulong QueryPages(void* address, void* buffer, ulong bufferSize)
        {
            Panic.Fail("SharpOSHost_QueryPages not implemented (Phase 6.1.b)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_MapFile")]
        public static void* MapFile(void* fileBlob, ulong blobSize, ulong offset, ulong viewSize, uint protectFlags)
        {
            Panic.Fail("SharpOSHost_MapFile not implemented (Phase 6.1.b)");
            return null;
        }

        [RuntimeExport("SharpOSHost_UnmapFile")]
        public static int UnmapFile(void* view)
        {
            Panic.Fail("SharpOSHost_UnmapFile not implemented (Phase 6.1.b)");
            return 0;
        }

        // Executable memory allocation via UEFI BootServices->AllocatePages with
        // type EfiLoaderCode. Returns 4 KiB-aligned RX pages even when firmware
        // enforces strict W^X. CoreCLR JIT needs to first WRITE generated code
        // and then EXECUTE it — i.e. full RWX. EfiLoaderCode gives RX only,
        // so after allocation we walk the active UEFI PML4 и flip the W bit
        // (preserving NX=0) на каждой странице.
        //
        // BootServices remains valid (kernel never calls ExitBootServices) →
        // allocation works for the SharpOS lifetime. Pages are never freed.
        [RuntimeExport("SharpOSHost_AllocExecutable")]
        public static void* AllocExecutable(ulong size)
        {
            if (size == 0) return null;

            // Post-EBS: UEFI's page allocator is gone — serve exec
            // memory from PhysicalMemory, identity-mapped RWX via the
            // pager (va==phys; exec:true => Present|Writable, NX=0).
            // This is what makes the JIT/hosted tier firmware-free.
            if (Platform.BootServicesGone)
            {
                const ulong PG = 4096UL;
                ulong np = (size + PG - 1) / PG;
                ulong pp = global::OS.Kernel.PhysicalMemory.AllocPages((uint)np);
                if (pp == 0) return null;
                if (!global::OS.Kernel.Memory.VirtualMemory.MapFixed(
                        (void*)pp, pp, np * PG, exec: true))
                    return null;
                return (void*)pp;
            }

            BootInfo bi = Platform.GetBootInfo();
            if (bi.SystemTable == null) return null;
            var bs = bi.SystemTable->BootServices;
            if (bs == null || bs->AllocatePages == null) return null;

            const ulong PAGE = 4096UL;
            ulong pages = (size + PAGE - 1) / PAGE;
            ulong phys = 0;
            // AllocateAnyPages = 0 → firmware picks address.
            ulong status = bs->AllocatePages(0u, EFI_MEMORY_TYPE.EfiLoaderCode, pages, &phys);
            if (status != 0 || phys == 0)
            {
                Console.Write("[AllocExec] FAIL size=0x");
                Console.WriteHex(size);
                Console.Write(" pages=");
                Console.WriteInt((int)pages);
                Console.Write(" status=0x");
                Console.WriteHex(status);
                Console.WriteLine("");
                return null;
            }

            // Dump initial PTE flags from UEFI for the first page so we know
            // what protection EfiLoaderCode actually granted under this OVMF.
            ulong initialPte = 0;
            X64PageTable.TryGetKernelLeafPte(phys, out initialPte);

            // Flip each page to Present | Writable | (NX=0). Preserve user/
            // global/etc. bits that UEFI may have set by overriding with
            // sanitized flags (LeafFlagMask). RWX is the simplest scheme that
            // works for self-modifying JIT pages; once W^X is needed (later
            // phase) we'll split into Reserve+Commit with explicit Protect.
            ulong va = phys;
            for (ulong i = 0; i < pages; i++)
            {
                X64PageTable.TrySetKernelFlags(va, PageFlags.Present | PageFlags.Writable);
                va += PAGE;
            }
            X64PageTable.FlushTlbAll();

            ulong finalPte = 0;
            X64PageTable.TryGetKernelLeafPte(phys, out finalPte);

            Console.Write("[AllocExec] size=0x");
            Console.WriteHex(size);
            Console.Write(" pages=");
            Console.WriteInt((int)pages);
            Console.Write(" addr=0x");
            Console.WriteHex(phys);
            Console.Write(" pteBefore=0x");
            Console.WriteHex(initialPte);
            Console.Write(" pteAfter=0x");
            Console.WriteHex(finalPte);
            Console.WriteLine("");

            return (void*)phys;
        }

        // Walks the active UEFI PML4 and changes the protection of [va,va+size).
        // Win32 flProtect → PageFlags:
        //   PAGE_EXECUTE (0x10)              → Present       (NX=0,  W=0)
        //   PAGE_EXECUTE_READ (0x20)         → Present       (NX=0,  W=0)
        //   PAGE_EXECUTE_READWRITE (0x40)    → Present|W     (NX=0,  W=1)
        //   PAGE_EXECUTE_WRITECOPY (0x80)    → Present|W     (NX=0,  W=1)
        //   PAGE_READONLY (0x02)             → Present       (NX=1,  W=0)
        //   PAGE_READWRITE (0x04)            → Present|W     (NX=1,  W=1)
        //   PAGE_NOACCESS (0x01)             → 0             (P=0)
        // Returns 1 on success, 0 on failure.
        [RuntimeExport("SharpOSHost_ProtectExecutable")]
        public static int ProtectExecutable(void* address, ulong size, uint flProtect)
        {
            if (address == null || size == 0) return 0;
            const ulong PAGE = 4096UL;
            ulong va = ((ulong)address) & ~(PAGE - 1);
            ulong end = ((ulong)address + size + PAGE - 1) & ~(PAGE - 1);

            PageFlags pf = PageFlags.Present;
            bool isExec = (flProtect & 0xF0u) != 0;
            bool isWrite = (flProtect & 0x44u) != 0 || (flProtect & 0x80u) != 0;
            if (isWrite) pf |= PageFlags.Writable;
            if (!isExec) pf |= PageFlags.NoExecute;
            if ((flProtect & 0x01u) != 0) pf = PageFlags.None;   // NOACCESS

            // Required bits the caller absolutely needs to be set already on
            // a (large-page) mapping for us to consider it OK without splitting.
            // For writes we just need Writable; for exec we need !NX (which is
            // a "not-set" constraint — we approximate by requiring Present).
            PageFlags requiredMask = PageFlags.Present;
            if (isWrite) requiredMask |= PageFlags.Writable;

            int largePageMods = 0;
            ulong p = va;
            while (p < end)
            {
                if (!X64PageTable.TrySetKernelFlagsEx(p, pf, requiredMask, out bool wasLargePage))
                {
                    Console.Write("[ProtectExec] FAIL va=0x");
                    Console.WriteHex(p);
                    Console.Write(" largePage=");
                    Console.WriteInt(wasLargePage ? 1 : 0);
                    Console.WriteLine("");
                    return 0;
                }
                if (wasLargePage) largePageMods++;
                p += PAGE;
            }
            X64PageTable.FlushTlbAll();
            if (largePageMods > 0)
            {
                Console.Write("[ProtectExec] OK va=0x");
                Console.WriteHex(va);
                Console.Write(" size=0x");
                Console.WriteHex(end - va);
                Console.Write(" largePageMods=");
                Console.WriteInt(largePageMods);
                Console.WriteLine("");
            }
            return 1;
        }
    }
}
