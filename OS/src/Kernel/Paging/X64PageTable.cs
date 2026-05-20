namespace OS.Kernel.Paging
{
    internal static unsafe class X64PageTable
    {
        public const ulong PageSize = 4096;
        private const ulong LargePage2MBSize = 2UL * 1024UL * 1024UL;
        private const ulong LargePage1GBSize = 1024UL * 1024UL * 1024UL;

        private const ulong PresentMask = 1UL << 0;
        private const ulong WritableMask = 1UL << 1;
        private const ulong UserMask = 1UL << 2;
        private const ulong PageSizeMask = 1UL << 7;
        private const ulong AddressMask = 0x000FFFFFFFFFF000UL;
        private const ulong AddressMask2MB = 0x000FFFFFFFE00000UL;
        private const ulong AddressMask1GB = 0x000FFFFFC0000000UL;

        private const ulong LeafFlagMask =
            (ulong)(PageFlags.Present | PageFlags.Writable | PageFlags.User |
            PageFlags.WriteThrough | PageFlags.CacheDisable | PageFlags.Global | PageFlags.NoExecute);

        private static ulong s_rootTable;
        private static ulong s_kernelRootTable;
        private static ulong s_kernelCr3;
        private static ulong s_pagerCr3;
        private static ulong s_sparePageList;
        private static uint s_sparePageCount;

        private static uint s_tablePages;
        private static uint s_mappedPages;
        private static uint s_mapCalls;
        private static uint s_mapFailures;
        private static uint s_queryCalls;
        private static uint s_queryHits;
        private static uint s_unmapCalls;
        private static uint s_unmapFailures;

        private enum ResolvedEntryLevel : byte
        {
            Pt = 1,
            PdLarge = 2,
            PdptLarge = 3,
        }

        public static ulong RootTablePhysical => s_rootTable;

        public static ulong KernelRootTablePhysical => s_kernelRootTable;

        public static void SetExecBuffer(void* buffer, uint size)
        {
            Cr3Accessor.SetExecBuffer(buffer, size);
        }

        public static void SetJumpStubBuffer(void* buffer, uint size)
        {
            global::OS.Kernel.Exec.JumpStub.SetExecBuffer(buffer, size);
        }

        public static bool Init(PagingRequirements requirements)
        {
            ResetState();

            if (!Cr3Accessor.TryInitialize())
                return false;

            if (!Cr3Accessor.TryRead(out ulong currentCr3))
                return false;

            s_kernelCr3 = currentCr3;
            s_kernelRootTable = currentCr3 & AddressMask;
            if (s_kernelRootTable == 0)
                return false;

            if (!TryCloneTableRecursive(s_kernelRootTable, 4, out s_rootTable))
                return false;

            s_pagerCr3 = (s_rootTable & AddressMask) | (s_kernelCr3 & ~AddressMask);

            uint reservePages = requirements.InitialPageTablePages;
            if (reservePages == 0)
                reservePages = 1;

            for (uint i = 0; i < reservePages; i++)
            {
                ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (page == 0)
                    return false;

                ZeroPage(page);
                PushSparePage(page);
                s_tablePages++;
            }

            return s_rootTable != 0;
        }

        public static bool TryActivatePagerCr3()
        {
            if (s_pagerCr3 == 0)
                return false;

            return Cr3Accessor.TryWrite(s_pagerCr3);
        }

        public static bool TryActivateKernelCr3()
        {
            if (s_kernelCr3 == 0)
                return false;

            return Cr3Accessor.TryWrite(s_kernelCr3);
        }

        public static bool IsPagerCr3Active()
        {
            if (!Cr3Accessor.TryRead(out ulong activeCr3))
                return false;

            return (activeCr3 & AddressMask) == (s_rootTable & AddressMask);
        }

        public static bool TryGetActiveCr3(out ulong activeCr3)
        {
            return Cr3Accessor.TryRead(out activeCr3);
        }

        public static bool TryGetPagerCr3(out ulong pagerCr3)
        {
            pagerCr3 = s_pagerCr3;
            return pagerCr3 != 0;
        }

        public static bool TryGetKernelCr3(out ulong kernelCr3)
        {
            kernelCr3 = s_kernelCr3;
            return kernelCr3 != 0;
        }

        public static bool Map(ulong virtualAddress, ulong physicalAddress, PageFlags flags)
        {
            s_mapCalls++;

            if (s_rootTable == 0)
            {
                s_mapFailures++;
                return false;
            }

            bool userPage = (flags & PageFlags.User) == PageFlags.User;

            ulong* pml4 = (ulong*)s_rootTable;
            if (!GetOrCreateNextTable(pml4, Pml4Index(virtualAddress), userPage, 4, out ulong* pdpt))
            {
                s_mapFailures++;
                return false;
            }

            if (!GetOrCreateNextTable(pdpt, PdptIndex(virtualAddress), userPage, 3, out ulong* pd))
            {
                s_mapFailures++;
                return false;
            }

            if (!GetOrCreateNextTable(pd, PdIndex(virtualAddress), userPage, 2, out ulong* pt))
            {
                s_mapFailures++;
                return false;
            }

            uint ptIndex = PtIndex(virtualAddress);
            if ((pt[ptIndex] & PresentMask) != 0)
            {
                s_mapFailures++;
                return false;
            }

            pt[ptIndex] = CreateLeafEntry(physicalAddress, flags);
            s_mappedPages++;
            return true;
        }

        // Map a fresh VA→PA into the **active** PML4. Post Phase E1 the
        // active table is `s_rootTable` (our pager clone, activated right
        // after `Pager.Init`). Pre-E1 historically this routed to the
        // firmware PML4 `s_kernelRootTable`; that split-brain is closed —
        // both `Map` and `MapKernel` write to the same active root now.
        // The "Kernel" suffix is historical and kept only because callers
        // (VirtualMemory.MapFixed / Commit) tag their intent ("kernel-owned
        // live mapping") and we may want to log/audit them differently later.
        // Intermediate tables auto-created (zeroed) via GetOrCreateNextTable.
        // Caller flushes TLB after a batch.
        public static bool MapKernel(ulong virtualAddress, ulong physicalAddress, PageFlags flags)
        {
            if (s_rootTable == 0)
                return false;

            bool userPage = (flags & PageFlags.User) == PageFlags.User;
            ulong* pml4 = (ulong*)s_rootTable;
            if (!GetOrCreateNextTable(pml4, Pml4Index(virtualAddress), userPage, 4, out ulong* pdpt))
                return false;
            if (!GetOrCreateNextTable(pdpt, PdptIndex(virtualAddress), userPage, 3, out ulong* pd))
                return false;
            if (!GetOrCreateNextTable(pd, PdIndex(virtualAddress), userPage, 2, out ulong* pt))
                return false;

            uint ptIndex = PtIndex(virtualAddress);
            if ((pt[ptIndex] & PresentMask) != 0)
                return false;   // already mapped — caller treats as error

            pt[ptIndex] = CreateLeafEntry(physicalAddress, flags);
            s_mappedPages++;
            return true;
        }

        // Unmap a single 4 KiB VA. If the existing mapping resolves at a
        // large-page level (PD 2 MiB or PDPT 1 GiB) — typical for firmware
        // identity in low memory — split the large entry down to PT
        // granularity first, then clear just the target 4 KiB PTE. Without
        // the split, `*entry = 0` would wipe the entire 2 MiB / 1 GiB
        // region (the bug that surfaced after Phase E1 activated the
        // clone). Walk allocates table pages only for the split path;
        // missing intermediate directories still fail Unmap.
        public static bool Unmap(ulong virtualAddress)
        {
            s_unmapCalls++;

            if (s_rootTable == 0)
            {
                s_unmapFailures++;
                return false;
            }

            ulong* pml4 = (ulong*)s_rootTable;
            uint pml4Idx = Pml4Index(virtualAddress);
            ulong pml4Entry = pml4[pml4Idx];
            if ((pml4Entry & PresentMask) == 0 || (pml4Entry & PageSizeMask) != 0)
            {
                s_unmapFailures++;
                return false;
            }
            ulong* pdpt = (ulong*)(pml4Entry & AddressMask);

            uint pdptIdx = PdptIndex(virtualAddress);
            ulong pdptEntry = pdpt[pdptIdx];
            if ((pdptEntry & PresentMask) == 0)
            {
                s_unmapFailures++;
                return false;
            }
            if ((pdptEntry & PageSizeMask) != 0)
            {
                if (!TrySplitLargeEntry(pdpt, pdptIdx, 3))
                {
                    s_unmapFailures++;
                    return false;
                }
                pdptEntry = pdpt[pdptIdx];
            }
            ulong* pd = (ulong*)(pdptEntry & AddressMask);

            uint pdIdx = PdIndex(virtualAddress);
            ulong pdEntry = pd[pdIdx];
            if ((pdEntry & PresentMask) == 0)
            {
                s_unmapFailures++;
                return false;
            }
            if ((pdEntry & PageSizeMask) != 0)
            {
                if (!TrySplitLargeEntry(pd, pdIdx, 2))
                {
                    s_unmapFailures++;
                    return false;
                }
                pdEntry = pd[pdIdx];
            }
            ulong* pt = (ulong*)(pdEntry & AddressMask);

            uint ptIdx = PtIndex(virtualAddress);
            if ((pt[ptIdx] & PresentMask) == 0)
            {
                s_unmapFailures++;
                return false;
            }

            pt[ptIdx] = 0;
            if (s_mappedPages > 0)
                s_mappedPages--;
            FlushTlbAll();
            return true;
        }

        public static bool TryQuery(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            s_queryCalls++;
            bool found = TryQueryForRoot(s_rootTable, virtualAddress, out physicalAddress, out flags);
            if (found)
                s_queryHits++;

            return found;
        }

        public static bool TryQueryKernel(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            // Post-E1: kernel-tagged query reads the active root same as
            // TryQuery. Kept as a named API so call-sites stay self-
            // describing ("looking up kernel-owned mapping"); the underlying
            // table is identical.
            return TryQueryForRoot(s_rootTable, virtualAddress, out physicalAddress, out flags);
        }

        // Modify the protection flags (Present/Writable/NoExecute/etc.) of an
        // existing 4 KiB mapping в the **active** PML4 (post-E1: s_rootTable,
        // activated immediately after Pager.Init). Pre-E1 this routed to
        // the firmware PML4 s_kernelRootTable; the split is closed.
        // Address bits are preserved; only the low 12 + bit 63 fields are
        // rewritten. Used by SharpOSHost_AllocExecutable / VirtualProtect
        // to flip a freshly-allocated EfiLoaderCode page to full RWX so
        // CoreCLR's JIT can write code, then execute it.
        //
        // Returns false if no leaf mapping covers the address (no 4 KiB PT
        // entry) or if the leaf is a large page (2 MiB / 1 GiB — not safe to
        // narrow without splitting). Caller must invalidate TLB после успеха.
        // Try to narrow the protection of a single 4 KiB page. If the active
        // mapping resolves to a large page (PD 2 MiB / PDPT 1 GiB), we report
        // it via `wasLargePage = true` but still consider the call successful
        // if the existing large-page flags already satisfy `requiredMask`
        // (the bits the caller absolutely needs set — e.g. Writable). Caller
        // can use this signal to skip splitting когда already acceptable.
        public static bool TrySetKernelFlags(ulong virtualAddress, PageFlags newFlags)
        {
            return TrySetKernelFlagsEx(virtualAddress, newFlags, (PageFlags)PresentMask, out _);
        }

        public static bool TrySetKernelFlagsEx(ulong virtualAddress, PageFlags newFlags, PageFlags requiredMask, out bool wasLargePage)
        {
            wasLargePage = false;
            // Post-E1: walks the active root (s_rootTable). See comment above.
            if (!TryResolveMappedEntryForRoot(s_rootTable, virtualAddress, out ulong* entry, out ulong value, out ResolvedEntryLevel level))
                return false;
            if (entry == null) return false;

            // Sanitize input flags + always force Present.
            ulong sanitized = (ulong)(newFlags & (PageFlags)LeafFlagMask);
            if ((sanitized & PresentMask) == 0)
                sanitized |= PresentMask;

            if (level == ResolvedEntryLevel.Pt)
            {
                // Regular 4 KiB PTE — fully overwrite with sanitized flags.
                ulong physBits = value & AddressMask;
                *entry = physBits | sanitized;
                return true;
            }

            // Large page (2 MiB / 1 GiB): NEVER tighten protection on the
            // shared sibling pages — they may contain kernel code (NX=0
            // required) или data (W=1 required). We only ever LOOSEN:
            //   - clear NX if caller wants exec (safe — was non-exec, becomes
            //     exec, still readable/writable)
            //   - set Writable if caller wants write (safe — was read-only,
            //     becomes RW)
            // Setting NX or clearing W on a 2 MiB region containing kernel code
            // or data would triple-fault the next kernel instruction-fetch /
            // write. To fully match caller's intent on a sub-page basis, a
            // large-page-split into 512 × 4 KiB PTEs is needed; deferred.
            wasLargePage = true;
            const ulong NX = 1UL << 63;
            ulong newPte = value;
            bool wantExec = (sanitized & NX) == 0;     // caller didn't set NX → wants exec
            bool wantWrite = (sanitized & WritableMask) != 0;
            if (wantExec)  newPte &= ~NX;              // clear NX (safe loosen)
            if (wantWrite) newPte |= WritableMask;     // set W (safe loosen)
            // NEVER `newPte |= NX` or `newPte &= ~WritableMask` here.
            _ = requiredMask;
            *entry = newPte;
            return true;
        }

        public static bool TryGetKernelLeafPte(ulong virtualAddress, out ulong rawPte)
        {
            rawPte = 0;
            // Post-E1: walks the active root (s_rootTable). See MapKernel comment.
            if (!TryResolveMappedEntryForRoot(s_rootTable, virtualAddress, out ulong* entry, out ulong value, out _))
                return false;
            if (entry == null) return false;
            rawPte = value;
            return true;
        }

        // Flush all TLB entries (non-global) by reloading CR3. Single-core
        // kernel, so no SMP fence needed. Cheaper alternative would be INVLPG
        // per page, but we'd need new shellcode; CR3 reload reuses existing
        // Cr3Accessor and is fine for the JIT-allocation cadence (rare).
        public static void FlushTlbAll()
        {
            if (Cr3Accessor.TryRead(out ulong cr3))
                Cr3Accessor.TryWrite(cr3);
        }

        public static bool TryGetWalkInfo(ulong virtualAddress, out PageWalkInfo walkInfo)
        {
            walkInfo = default;
            walkInfo.VirtualAddress = virtualAddress;
            walkInfo.Pml4Index = Pml4Index(virtualAddress);
            walkInfo.PdptIndex = PdptIndex(virtualAddress);
            walkInfo.PdIndex = PdIndex(virtualAddress);
            walkInfo.PtIndex = PtIndex(virtualAddress);

            if (s_rootTable == 0)
                return false;

            ulong* pml4 = (ulong*)s_rootTable;
            walkInfo.Pml4Entry = pml4[walkInfo.Pml4Index];
            walkInfo.Pml4Present = IsPresent(walkInfo.Pml4Entry);
            if (!walkInfo.Pml4Present || IsLargePage(walkInfo.Pml4Entry))
                return true;

            ulong* pdpt = (ulong*)(walkInfo.Pml4Entry & AddressMask);
            walkInfo.PdptEntry = pdpt[walkInfo.PdptIndex];
            walkInfo.PdptPresent = IsPresent(walkInfo.PdptEntry);
            if (!walkInfo.PdptPresent || IsLargePage(walkInfo.PdptEntry))
                return true;

            ulong* pd = (ulong*)(walkInfo.PdptEntry & AddressMask);
            walkInfo.PdEntry = pd[walkInfo.PdIndex];
            walkInfo.PdPresent = IsPresent(walkInfo.PdEntry);
            if (!walkInfo.PdPresent || IsLargePage(walkInfo.PdEntry))
                return true;

            ulong* pt = (ulong*)(walkInfo.PdEntry & AddressMask);
            walkInfo.PtEntry = pt[walkInfo.PtIndex];
            walkInfo.PtPresent = IsPresent(walkInfo.PtEntry);
            walkInfo.LeafEntry = walkInfo.PtEntry;
            walkInfo.LeafPresent = walkInfo.PtPresent;
            return true;
        }

        public static void GetSummary(out PagingSummary summary)
        {
            summary = default;
            summary.RootTablePhysical = s_rootTable;
            summary.KernelRootTablePhysical = s_kernelRootTable;
            summary.KernelCr3 = s_kernelCr3;
            summary.PagerCr3 = s_pagerCr3;
            if (Cr3Accessor.TryRead(out ulong activeCr3))
            {
                summary.ActiveCr3 = activeCr3;
                summary.IsPagerRootActive = (activeCr3 & AddressMask) == (s_rootTable & AddressMask);
            }

            summary.TablePages = s_tablePages;
            summary.SpareTablePages = s_sparePageCount;
            summary.MappedPages = s_mappedPages;
            summary.MapCalls = s_mapCalls;
            summary.MapFailures = s_mapFailures;
            summary.QueryCalls = s_queryCalls;
            summary.QueryHits = s_queryHits;
            summary.UnmapCalls = s_unmapCalls;
            summary.UnmapFailures = s_unmapFailures;
        }

        private static bool TryGetLeafEntry(ulong virtualAddress, out ulong* entry)
        {
            entry = null;
            if (s_rootTable == 0)
                return false;

            ulong* pml4 = (ulong*)s_rootTable;
            if (!TryGetNextTable(pml4, Pml4Index(virtualAddress), out ulong* pdpt))
                return false;

            if (!TryGetNextTable(pdpt, PdptIndex(virtualAddress), out ulong* pd))
                return false;

            if (!TryGetNextTable(pd, PdIndex(virtualAddress), out ulong* pt))
                return false;

            entry = &pt[PtIndex(virtualAddress)];
            return true;
        }

        private static bool TryResolveMappedEntry(
            ulong virtualAddress,
            out ulong* entry,
            out ulong value,
            out ResolvedEntryLevel level)
        {
            return TryResolveMappedEntryForRoot(s_rootTable, virtualAddress, out entry, out value, out level);
        }

        private static bool TryResolveMappedEntryForRoot(
            ulong rootTable,
            ulong virtualAddress,
            out ulong* entry,
            out ulong value,
            out ResolvedEntryLevel level)
        {
            entry = null;
            value = 0;
            level = 0;

            if (rootTable == 0)
                return false;

            ulong* pml4 = (ulong*)rootTable;
            uint pml4Index = Pml4Index(virtualAddress);
            ulong pml4Entry = pml4[pml4Index];
            if (!IsPresent(pml4Entry) || IsLargePage(pml4Entry))
                return false;

            ulong* pdpt = (ulong*)(pml4Entry & AddressMask);
            uint pdptIndex = PdptIndex(virtualAddress);
            ulong pdptEntry = pdpt[pdptIndex];
            if (!IsPresent(pdptEntry))
                return false;

            if (IsLargePage(pdptEntry))
            {
                entry = &pdpt[pdptIndex];
                value = pdptEntry;
                level = ResolvedEntryLevel.PdptLarge;
                return true;
            }

            ulong* pd = (ulong*)(pdptEntry & AddressMask);
            uint pdIndex = PdIndex(virtualAddress);
            ulong pdEntry = pd[pdIndex];
            if (!IsPresent(pdEntry))
                return false;

            if (IsLargePage(pdEntry))
            {
                entry = &pd[pdIndex];
                value = pdEntry;
                level = ResolvedEntryLevel.PdLarge;
                return true;
            }

            ulong* pt = (ulong*)(pdEntry & AddressMask);
            uint ptIndex = PtIndex(virtualAddress);
            ulong ptEntry = pt[ptIndex];

            entry = &pt[ptIndex];
            value = ptEntry;
            level = ResolvedEntryLevel.Pt;
            return true;
        }

        private static bool TryQueryForRoot(ulong rootTable, ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            physicalAddress = 0;
            flags = PageFlags.None;

            if (!TryResolveMappedEntryForRoot(rootTable, virtualAddress, out ulong* entry, out ulong value, out ResolvedEntryLevel level))
                return false;

            if ((value & PresentMask) == 0)
                return false;

            ulong basePhysical;
            ulong offset;
            if (level == ResolvedEntryLevel.Pt)
            {
                basePhysical = value & AddressMask;
                offset = virtualAddress & (PageSize - 1);
            }
            else if (level == ResolvedEntryLevel.PdLarge)
            {
                basePhysical = value & AddressMask2MB;
                offset = virtualAddress & (LargePage2MBSize - 1);
            }
            else
            {
                basePhysical = value & AddressMask1GB;
                offset = virtualAddress & (LargePage1GBSize - 1);
            }

            if (basePhysical > 0xFFFFFFFFFFFFFFFFUL - offset)
                return false;

            physicalAddress = basePhysical + offset;
            flags = (PageFlags)(value & LeafFlagMask);
            return true;
        }

        // `parentLevel` describes the directory the entry lives in (used
        // only for split semantics): 2 = PD (so a large entry here is
        // 2 MiB → split into PT with 512 × 4 KiB), 3 = PDPT (1 GiB →
        // split into PD with 512 × 2 MiB), 4 = PML4 (no large pages
        // architecturally — caller should never request split here).
        // Pass 0 to disable split: callers that genuinely cannot tolerate
        // a large-page entry (e.g., diagnostic walks) pass 0 and get the
        // historical "return false on large" behaviour.
        private static bool GetOrCreateNextTable(ulong* table, uint index, bool userPage, uint parentLevel, out ulong* nextTable)
        {
            ulong entry = table[index];

            if ((entry & PresentMask) == 0)
            {
                ulong page = AllocateTablePage();
                if (page == 0)
                {
                    nextTable = null;
                    return false;
                }

                ulong newEntry = (page & AddressMask) | PresentMask | WritableMask;
                if (userPage)
                    newEntry |= UserMask;

                table[index] = newEntry;
                nextTable = (ulong*)page;
                return true;
            }

            if ((entry & PageSizeMask) != 0)
            {
                // E1 fix: firmware identity-maps low memory with 2 MiB (PD)
                // or 1 GiB (PDPT) large pages. Once the clone is the live
                // CR3 those mappings are CPU-visible; new finer-grained
                // mappings on the same VA range would have been blocked
                // (or, in Unmap, wiped out the whole large region — the
                // bug that landed us here). Split on demand: replace the
                // large-page entry with a freshly-allocated child table
                // whose 512 entries inherit the large-page identity at
                // the next finer granularity.
                if (parentLevel == 2 || parentLevel == 3)
                {
                    if (!TrySplitLargeEntry(table, index, parentLevel))
                    {
                        nextTable = null;
                        return false;
                    }
                    entry = table[index];   // re-read post-split
                }
                else
                {
                    nextTable = null;
                    return false;
                }
            }

            if (userPage && (entry & UserMask) == 0)
            {
                entry |= UserMask;
                table[index] = entry;
            }

            nextTable = (ulong*)(entry & AddressMask);
            return true;
        }

        // Replace a present large-page directory entry with a freshly
        // allocated child table at the next finer level. Inherits the
        // large page's PA + leaf flags into 512 child entries.
        //
        //   parentLevel = 2 (PD entry, 2 MiB large): child = PT, 512 × 4 KiB
        //   parentLevel = 3 (PDPT entry, 1 GiB large): child = PD, 512 × 2 MiB large
        //
        // PML4 never holds large pages in x86-64 — caller's responsibility
        // not to ask. The split preserves cache/protection flags but does
        // NOT preserve the large-page PAT bit (bit 12) — kernel and firmware
        // mappings use default cache attributes in our deployment. If a
        // mapping with non-default PAT lands here, it gets normalised
        // silently. Acceptable for low-memory firmware identity; revisit
        // if MMIO ever needs splitting.
        private static bool TrySplitLargeEntry(ulong* parentTable, uint index, uint parentLevel)
        {
            ulong entry = parentTable[index];
            if ((entry & PresentMask) == 0 || (entry & PageSizeMask) == 0)
                return false;

            ulong childPage = AllocateTablePage();
            if (childPage == 0)
                return false;

            // Carry leaf flags from the parent large entry into each child
            // entry. Strip the address bits and the PageSizeMask bit
            // (PSE/PAT). For PD→PT split, PageSizeMask on a PTE is the PAT
            // bit and we want it 0. For PDPT→PD split, PageSizeMask must
            // stay set on the child PD entries (they're still 2 MiB large
            // pages), so we re-add it after the strip.
            ulong childFlags = (entry & ~AddressMask) & ~PageSizeMask;

            ulong baseAddress;
            ulong stride;
            if (parentLevel == 2)
            {
                // PD large = 2 MiB → split into PT (4 KiB entries).
                baseAddress = entry & AddressMask2MB;
                stride = PageSize;                   // 4 KiB
                // childFlags stays as-is — PageSizeMask=0 in PTE means 4 KiB page.
            }
            else if (parentLevel == 3)
            {
                // PDPT large = 1 GiB → split into PD (2 MiB large entries).
                baseAddress = entry & AddressMask1GB;
                stride = LargePage2MBSize;           // 2 MiB
                childFlags |= PageSizeMask;          // child PDEs remain 2 MiB large.
            }
            else
            {
                return false;
            }

            ulong* child = (ulong*)childPage;
            for (uint i = 0; i < 512; i++)
                child[i] = (baseAddress + (ulong)i * stride) | childFlags;

            // Build the new parent entry: directory pointer (no PageSizeMask).
            // PML4E/PDPTE/PDE pointing-to-next-table act as a MASK over
            // their children — if the directory bit is restrictive, every
            // child inherits the restriction regardless of its own bit.
            // We make directories maximally permissive (P | W | U, NX=0)
            // so leaves decide. Children inherited the original NX/W/U
            // from the large entry (see childFlags above), so the original
            // protection of inherited entries is preserved at leaf level.
            // Newly-mapped leaves (post-split) get caller-provided flags
            // unrestricted. Matches GetOrCreateNextTable's fresh-allocation
            // path semantics — split should produce the same shape that
            // a fresh allocation would, otherwise post-Phase-E1 ELF code
            // pages re-mapped over a formerly-NX large region get blocked
            // at the directory level (instruction-fetch #PF with P=1, I=1
            // on the leaf even though leaf NX=0 — observed empirically).
            ulong newParentEntry = (childPage & AddressMask) | PresentMask | WritableMask | UserMask;
            parentTable[index] = newParentEntry;

            // TLB caches large-page translations; the directory entry just
            // changed shape. Flush all (we don't have INVLPG shellcode for
            // a range; CR3 reload is cheap relative to a launch).
            FlushTlbAll();
            return true;
        }

        private static bool TryGetNextTable(ulong* table, uint index, out ulong* nextTable)
        {
            nextTable = null;
            ulong entry = table[index];

            if ((entry & PresentMask) == 0)
                return false;

            if ((entry & PageSizeMask) != 0)
                return false;

            nextTable = (ulong*)(entry & AddressMask);
            return true;
        }

        private static ulong CreateLeafEntry(ulong physicalAddress, PageFlags flags)
        {
            ulong normalizedFlags = ((ulong)(flags | PageFlags.Present)) & LeafFlagMask;
            return (physicalAddress & AddressMask) | normalizedFlags;
        }

        private static bool IsPresent(ulong entry)
        {
            return (entry & PresentMask) != 0;
        }

        private static bool IsLargePage(ulong entry)
        {
            return (entry & PageSizeMask) != 0;
        }

        private static ulong AllocateTablePage()
        {
            ulong page = PopSparePage();
            if (page != 0)
                return page;

            page = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (page == 0)
                return 0;

            ZeroPage(page);
            s_tablePages++;
            return page;
        }

        private static bool TryCloneTableRecursive(ulong sourceTablePage, uint level, out ulong clonedTablePage)
        {
            clonedTablePage = 0;
            if (sourceTablePage == 0 || level == 0)
                return false;

            ulong newTablePage = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (newTablePage == 0)
                return false;

            s_tablePages++;
            OS.Kernel.Util.Memory.MemCopy((void*)newTablePage, (void*)sourceTablePage, (uint)PageSize);

            if (level == 1)
            {
                clonedTablePage = newTablePage;
                return true;
            }

            ulong* entries = (ulong*)newTablePage;
            for (uint i = 0; i < 512; i++)
            {
                ulong entry = entries[i];
                if ((entry & PresentMask) == 0 || (entry & PageSizeMask) != 0)
                    continue;

                ulong childSourceTable = entry & AddressMask;
                if (childSourceTable == 0)
                    return false;

                if (!TryCloneTableRecursive(childSourceTable, level - 1, out ulong childClonedTable))
                    return false;

                // Directory pointers (non-large present entries at this
                // level) are repointed to the cloned child. We also force
                // them MAXIMALLY PERMISSIVE: P | W | U, NX=0. Reason: in
                // x86-64 paging the directory bits act as a MASK over all
                // descendants — if firmware happened to set NX=1 (or W=0,
                // U=0) on a parent, every leaf underneath is forced to
                // inherit the restriction. After Phase E1 the clone is the
                // live CR3, and ELF code pages re-mapped into a formerly-
                // NX directory get instruction-fetch #PF even with leaf
                // NX=0 (observed empirically). Leaves keep their original
                // NX/W/U bits — directory-level permissive simply means
                // "let the leaves decide."
                entries[i] = (childClonedTable & AddressMask) | PresentMask | WritableMask | UserMask;
            }

            clonedTablePage = newTablePage;
            return true;
        }

        private static void ZeroPage(ulong pageAddress)
        {
            ulong* page = (ulong*)pageAddress;
            for (uint i = 0; i < 512; i++)
                page[i] = 0;
        }

        private static void PushSparePage(ulong page)
        {
            *((ulong*)page) = s_sparePageList;
            s_sparePageList = page;
            s_sparePageCount++;
        }

        private static ulong PopSparePage()
        {
            if (s_sparePageList == 0)
                return 0;

            ulong page = s_sparePageList;
            s_sparePageList = *((ulong*)page);
            s_sparePageCount--;
            ZeroPage(page);
            return page;
        }

        private static uint Pml4Index(ulong virtualAddress) => (uint)((virtualAddress >> 39) & 0x1FFUL);

        private static uint PdptIndex(ulong virtualAddress) => (uint)((virtualAddress >> 30) & 0x1FFUL);

        private static uint PdIndex(ulong virtualAddress) => (uint)((virtualAddress >> 21) & 0x1FFUL);

        private static uint PtIndex(ulong virtualAddress) => (uint)((virtualAddress >> 12) & 0x1FFUL);

        private static void ResetState()
        {
            s_rootTable = 0;
            s_kernelRootTable = 0;
            s_kernelCr3 = 0;
            s_pagerCr3 = 0;
            s_sparePageList = 0;
            s_sparePageCount = 0;
            s_tablePages = 0;
            s_mappedPages = 0;
            s_mapCalls = 0;
            s_mapFailures = 0;
            s_queryCalls = 0;
            s_queryHits = 0;
            s_unmapCalls = 0;
            s_unmapFailures = 0;
        }
    }
}
