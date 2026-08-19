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
                if (pp == 0)
                {
                    // Returning null without a word is how a failure here
                    // reaches the runtime as a bare "unspecified error" with
                    // no hint of its origin. Say which step failed.
                    Console.Write("[AllocExec] FAIL no frames pages=");
                    Console.WriteInt((int)np);
                    Console.WriteLine("");
                    return null;
                }
                if (!global::OS.Kernel.Memory.VirtualMemory.MapFixed(
                        (void*)pp, pp, np * PG, exec: true))
                {
                    Console.Write("[AllocExec] FAIL map pa=0x");
                    Console.WriteHex(pp);
                    Console.Write(" pages=");
                    Console.WriteInt((int)np);
                    Console.WriteLine("");
                    return null;
                }
                NoteRegion(pp, pp + np * PG, TagAllocExec);
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
        // Last few protection changes, kept so a fault can ask "was this page
        // ever made executable?" instead of us guessing. Absence of a log line
        // proved nothing earlier — the fork's debug print is gated off — so the
        // record lives on this side, where it is unconditional.
        private const int HistorySlots = 16;
        private struct ProtectRecord { public ulong Start; public ulong End; public uint Flags; public int Ok; }
        private static ProtectRecord[] s_history = null!;
        private static int s_historyNext;
        private static ulong s_protectCalls;
        private static ulong s_protectFails;

        public static ulong ProtectCalls => s_protectCalls;
        public static ulong ProtectFails => s_protectFails;

        // Tags for regions recorded by paths other than ProtectExecutable, so
        // the dump answers "how did this page get its protection?" and not only
        // "was it ever re-protected?". The failing address showed up in neither
        // — which is itself the finding that sent us here.
        public const uint TagCommitExec   = 0x1C0;
        public const uint TagCommitData   = 0x1D0;
        public const uint TagDemandCommit = 0x1E0;
        public const uint TagAllocExec    = 0x1F0;

        // A ring of the last N regions cannot answer "was THIS page ever made
        // executable?" once the volume exceeds N — and it did: 103 calls into
        // 16 slots. This set answers it exactly, at one bit of bookkeeping per
        // page, so a miss is a fact rather than an eviction.
        private const int ExecPagesSlots = 8192;   // power of two, open addressing
        private static ulong[] s_execPages = null!;
        private static ulong s_execPagesCount;

        // Set once any of the three page sets runs out of room. A full set
        // answers "no" to every question, which reads exactly like "we never
        // saw that page" — and that lie sent this investigation down a wrong
        // path. Saying so out loud is the difference between a fact and a
        // guess dressed as one.
        private static bool s_setsSaturated;

        // Span of everything ever granted execute. If the faulting address sits
        // far outside this span, the question stops being "who lost this page's
        // permission" and becomes "does this whole class of memory ever get
        // execute at all" — a systematic gap, not a race.
        private static ulong s_execLow = ulong.MaxValue;
        private static ulong s_execHigh;

        private static void NoteExecPage(ulong page)
        {
            if (s_execPages == null) s_execPages = new ulong[ExecPagesSlots];
            if (page < s_execLow) s_execLow = page;
            if (page > s_execHigh) s_execHigh = page;
            ulong h = (page * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (ExecPagesSlots - 1));
            for (int n = 0; n < ExecPagesSlots; n++)
            {
                ulong cur = s_execPages[i];
                if (cur == page) return;
                if (cur == 0) { s_execPages[i] = page; s_execPagesCount++; return; }
                i = (i + 1) & (ExecPagesSlots - 1);
            }
            s_setsSaturated = true;
        }

        // A second exact set, for pages committed as DATA. "Never granted
        // exec" alone cannot tell two very different stories apart: the
        // runtime asked for this page as data (so a jump into it means the
        // POINTER is wrong), or we never saw the page at all (so the mapping
        // came from somewhere we are not watching). One bit each way.
        private static ulong[] s_dataPages = null!;
        private static ulong s_dataPagesCount;

        private static void NoteDataPage(ulong page)
        {
            if (s_dataPages == null) s_dataPages = new ulong[ExecPagesSlots];
            ulong h = (page * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (ExecPagesSlots - 1));
            for (int n = 0; n < ExecPagesSlots; n++)
            {
                ulong cur = s_dataPages[i];
                if (cur == page) return;
                if (cur == 0) { s_dataPages[i] = page; s_dataPagesCount++; return; }
                i = (i + 1) & (ExecPagesSlots - 1);
            }
            s_setsSaturated = true;
        }

        // Third exact set: pages materialised by a write fault rather than by an
        // explicit commit. They were falling through both other sets, so the
        // verdict called them "never seen" — which read as "mapped by some
        // path we do not control" when in fact we mapped them ourselves, on
        // demand, with data permissions because a write fault cannot tell that
        // the bytes being written are code.
        private static ulong[] s_faultPages = null!;
        private static ulong s_faultPagesCount;

        private static void NoteFaultPage(ulong page)
        {
            if (s_faultPages == null) s_faultPages = new ulong[ExecPagesSlots];
            ulong h = (page * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (ExecPagesSlots - 1));
            for (int n = 0; n < ExecPagesSlots; n++)
            {
                ulong cur = s_faultPages[i];
                if (cur == page) return;
                if (cur == 0) { s_faultPages[i] = page; s_faultPagesCount++; return; }
                i = (i + 1) & (ExecPagesSlots - 1);
            }
            s_setsSaturated = true;
        }

        public static bool WasDemandCommitted(ulong address)
        {
            if (s_faultPages == null) return false;
            ulong page = address & ~0xFFFUL;
            ulong h = (page * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (ExecPagesSlots - 1));
            for (int n = 0; n < ExecPagesSlots; n++)
            {
                ulong cur = s_faultPages[i];
                if (cur == page) return true;
                if (cur == 0) return false;
                i = (i + 1) & (ExecPagesSlots - 1);
            }
            return false;
        }

        public static bool WasEverData(ulong address)
        {
            if (s_dataPages == null) return false;
            ulong page = address & ~0xFFFUL;
            ulong h = (page * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (ExecPagesSlots - 1));
            for (int n = 0; n < ExecPagesSlots; n++)
            {
                ulong cur = s_dataPages[i];
                if (cur == page) return true;
                if (cur == 0) return false;
                i = (i + 1) & (ExecPagesSlots - 1);
            }
            return false;
        }

        public static bool WasEverExecutable(ulong address)
        {
            if (s_execPages == null) return false;
            ulong page = address & ~0xFFFUL;
            ulong h = (page * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (ExecPagesSlots - 1));
            for (int n = 0; n < ExecPagesSlots; n++)
            {
                ulong cur = s_execPages[i];
                if (cur == page) return true;
                if (cur == 0) return false;
                i = (i + 1) & (ExecPagesSlots - 1);
            }
            return false;
        }

        public static void NoteRegion(ulong start, ulong end, uint tag)
        {
            if (!OS.Kernel.Diagnostics.Probes.TracePageOrigins) return;

            if (s_history == null) s_history = new ProtectRecord[HistorySlots];
            int slot = s_historyNext;
            s_historyNext = (s_historyNext + 1) % HistorySlots;
            s_history[slot].Start = start;
            s_history[slot].End = end;
            s_history[slot].Flags = tag;
            s_history[slot].Ok = 1;
            if (tag == TagCommitExec || tag == TagAllocExec)
                for (ulong pg = start & ~0xFFFUL; pg < end; pg += 0x1000UL) NoteExecPage(pg);
            else if (tag == TagCommitData)
                for (ulong pg = start & ~0xFFFUL; pg < end; pg += 0x1000UL) NoteDataPage(pg);
            else if (tag == TagDemandCommit)
                for (ulong pg = start & ~0xFFFUL; pg < end; pg += 0x1000UL) NoteFaultPage(pg);
        }

        /// <summary>Did any ProtectExecutable call cover this address?</summary>
        public static void DumpHistoryFor(ulong address)
        {
            Console.Write("  [protect-history] calls=");
            Console.WriteULong(s_protectCalls);
            Console.Write(" fails=");
            Console.WriteULong(s_protectFails);
            if (s_history == null) { Console.WriteLine(" (none)"); return; }

            bool covered = false;
            for (int i = 0; i < HistorySlots; i++)
            {
                ref ProtectRecord r = ref s_history[i];
                if (r.End == 0) continue;
                if (address >= r.Start && address < r.End) covered = true;
                Console.Write(" | 0x");
                Console.WriteHex(r.Start);
                Console.Write("..0x");
                Console.WriteHex(r.End);
                Console.Write(" pr=0x");
                Console.WriteHex(r.Flags);
                Console.Write(r.Ok != 0 ? " ok" : " FAIL");
            }
            Console.Write(covered ? "  <= ADDRESS WAS COVERED" : "  <= address not in the last records");
            Console.Write(" | exec-granted pages=");
            Console.WriteULong(s_execPagesCount);
            Console.Write(" span=0x");
            Console.WriteHex(s_execLow);
            Console.Write("..0x");
            Console.WriteHex(s_execHigh);
            if (WasEverExecutable(address))
            {
                Console.WriteLine(" <= PAGE WAS GRANTED EXEC (protection lost afterwards)");
            }
            else if (WasEverData(address))
            {
                Console.Write(" <= PAGE COMMITTED AS DATA on purpose (data pages=");
                Console.WriteULong(s_dataPagesCount);
                Console.WriteLine(") — the pointer is wrong, not the protection");
            }
            else if (WasDemandCommitted(address))
            {
                Console.Write(" <= PAGE MATERIALISED BY A WRITE FAULT (fault pages=");
                Console.WriteULong(s_faultPagesCount);
                Console.WriteLine(") — it got data permissions because a write cannot say it is code");
            }
            else if (s_setsSaturated)
            {
                Console.WriteLine(" <= UNKNOWN: the page sets are full, so a miss proves nothing");
            }
            else
            {
                Console.WriteLine(" <= PAGE NEVER SEEN by any of our paths");
            }
        }

        [RuntimeExport("SharpOSHost_ProtectExecutable")]
        public static int ProtectExecutable(void* address, ulong size, uint flProtect)
        {
            if (address == null || size == 0) return 0;
            const ulong PAGE = 4096UL;
            ulong va = ((ulong)address) & ~(PAGE - 1);
            ulong end = ((ulong)address + size + PAGE - 1) & ~(PAGE - 1);

            s_protectCalls++;
            if (s_history == null) s_history = new ProtectRecord[HistorySlots];
            int slot = s_historyNext;
            s_historyNext = (s_historyNext + 1) % HistorySlots;
            s_history[slot].Start = va;
            s_history[slot].End = end;
            s_history[slot].Flags = flProtect;
            s_history[slot].Ok = 0;

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
                    s_protectFails++;
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
            s_history[slot].Ok = 1;
            if (isExec)
                for (ulong pg = va & ~0xFFFUL; pg < end; pg += 0x1000UL) NoteExecPage(pg);
            return 1;
        }
    }
}
