using OS.Kernel.Paging;

namespace OS.Kernel.Process
{
    internal static unsafe class ProcessManager
    {
        private const ulong PageSize = X64PageTable.PageSize;

        // Who is running right now — the innermost app, not the outermost.
        //
        // It used to be the outermost and only, because nothing ever replaced
        // it: a nested launch left this describing the app that started the
        // chain. That is what made a second level impossible, since suspending
        // "the current process" then unmapped the wrong image and left the
        // real parent's pages sitting where the new child was about to load.
        //
        // There is still one slot, and there does not need to be more: a
        // nested run happens inside its parent's RunExternalApp frame, so the
        // kernel stack already is the stack of contexts. The parent takes its
        // own context back on the way out.
        private static ProcessContext s_currentContext;
        private static bool s_hasCurrentProcess;

        // How many nested runs are in flight. Only reported; the depth that
        // decides addresses and limits is AppServiceBuilder's, which counts
        // the same events one frame earlier.
        private static uint s_nestingDepth;

        public static bool HasCurrentProcess => s_hasCurrentProcess;

        public static uint NestingDepth => s_nestingDepth;

        public static void SetCurrent(ref ProcessImage processImage, ref LoadedImage loadedImage)
        {
            s_currentContext = default;
            s_currentContext.State = ProcessState.Running;
            s_currentContext.ProcessImage = processImage;
            s_currentContext.LoadedImage = loadedImage;
            s_hasCurrentProcess = true;
        }

        /// <summary>
        /// Makes a freshly built image the current process and hands back the
        /// one it displaced, for the caller to put back with
        /// <see cref="RestoreCurrent"/> when the child returns.
        /// </summary>
        public static ProcessContext ExchangeCurrent(
            ref ProcessImage processImage, ref LoadedImage loadedImage, out bool hadPrevious)
        {
            ProcessContext previous = s_currentContext;
            hadPrevious = s_hasCurrentProcess;

            SetCurrent(ref processImage, ref loadedImage);
            return previous;
        }

        /// <summary>The other half of <see cref="ExchangeCurrent"/>.</summary>
        public static void RestoreCurrent(ref ProcessContext previous, bool hadPrevious)
        {
            s_currentContext = previous;
            s_hasCurrentProcess = hadPrevious;
        }

        public static void ClearCurrent()
        {
            s_currentContext = default;
            s_hasCurrentProcess = false;
            s_nestingDepth = 0;
        }

        public static bool TrySuspendCurrentForNested(out MappingContext mappingContext, out bool suspended)
        {
            mappingContext = default;
            suspended = false;

            if (!s_hasCurrentProcess)
                return true;

            // No refusal when a nested run is already in flight. The snapshot
            // goes back to the caller by value, so each level holds its own on
            // its own frame; what used to make a second level unsafe was the
            // current context, not this.
            if (!TryCaptureCurrentMappings(out mappingContext))
                return false;

            if (!TryUnmapCurrentProcessRanges())
            {
                bool _ = TryRestoreMappings(ref mappingContext);
                ReleaseMappingContext(ref mappingContext);
                return false;
            }

            s_nestingDepth++;
            suspended = true;
            return true;
        }

        public static bool TryRestoreAfterNested(ref MappingContext mappingContext)
        {
            bool restored = TryRestoreMappings(ref mappingContext);
            ReleaseMappingContext(ref mappingContext);
            if (s_nestingDepth > 0) s_nestingDepth--;
            return restored;
        }

        private static bool TryCaptureCurrentMappings(out MappingContext mappingContext)
        {
            mappingContext = default;
            ProcessImage processImage = s_currentContext.ProcessImage;

            // Only save image mappings. The stack is at a different virtual address
            // range in the child process and must NOT be unmapped while we are still
            // executing on it (pager CR3 is active during a service call).
            if (!TryCaptureRange(processImage.ImageStart, processImage.ImageEnd, out mappingContext.ImageSnapshot))
                return false;

            return true;
        }

        private static bool TryCaptureRange(ulong startInclusive, ulong endExclusive, out MappingSnapshot snapshot)
        {
            snapshot = default;
            if (endExclusive <= startInclusive)
                return true;

            ulong current = AlignDown(startInclusive);
            ulong limit = AlignUp(endExclusive);
            uint mappedCount = 0;

            while (current < limit)
            {
                if (Pager.TryQuery(current, out _, out _))
                    mappedCount++;

                if (!TryAdvancePage(ref current))
                    return false;
            }

            if (mappedCount == 0)
                return true;

            ulong bytes = (ulong)mappedCount * (ulong)sizeof(MappingPageEntry);
            if (bytes > 0xFFFFFFFFUL)
                return false;

            MappingPageEntry* entries = (MappingPageEntry*)global::OS.Kernel.Memory.KernelHeap.Alloc((uint)bytes);
            if (entries == null)
                return false;

            current = AlignDown(startInclusive);
            uint index = 0;
            while (current < limit)
            {
                if (Pager.TryQuery(current, out ulong physical, out PageFlags flags))
                {
                    entries[index].VirtualAddress = current;
                    entries[index].PhysicalAddress = physical;
                    entries[index].Flags = flags;
                    index++;
                }

                if (!TryAdvancePage(ref current))
                {
                    global::OS.Kernel.Memory.KernelHeap.Free(entries);
                    return false;
                }
            }

            if (index != mappedCount)
            {
                global::OS.Kernel.Memory.KernelHeap.Free(entries);
                return false;
            }

            snapshot.Entries = entries;
            snapshot.Count = mappedCount;
            return true;
        }

        private static bool TryUnmapCurrentProcessRanges()
        {
            ProcessImage processImage = s_currentContext.ProcessImage;

            // Take the image out of the unwind search before its pages go: a
            // stack walk in the child would otherwise read .pdata that is no
            // longer mapped, and fault deep inside the GC rather than here.
            global::OS.Boot.EH.CoffRuntimeFunctionTable.SetImageMapped(
                (byte*)processImage.ImageStart, false);

            // Only unmap the image. The stack stays mapped because we are still
            // executing on it. The child uses a different stack virtual range.
            return TryUnmapRange(processImage.ImageStart, processImage.ImageEnd);
        }

        private static bool TryUnmapRange(ulong startInclusive, ulong endExclusive)
        {
            if (endExclusive <= startInclusive)
                return true;

            ulong current = AlignDown(startInclusive);
            ulong limit = AlignUp(endExclusive);
            while (current < limit)
            {
                if (Pager.TryQuery(current, out _, out _) && !Pager.Unmap(current))
                    return false;

                if (!TryAdvancePage(ref current))
                    return false;
            }

            return true;
        }

        private static bool TryRestoreMappings(ref MappingContext mappingContext)
        {
            // Only restore image. Stack was never unmapped (child used a different range).
            bool restored = TryRestoreSnapshot(ref mappingContext.ImageSnapshot);

            // Searchable again, and with the same records: the entry was kept
            // through the suspension precisely so nothing has to be recomputed.
            if (restored && s_hasCurrentProcess)
            {
                global::OS.Boot.EH.CoffRuntimeFunctionTable.SetImageMapped(
                    (byte*)s_currentContext.ProcessImage.ImageStart, true);
            }

            return restored;
        }

        private static bool TryRestoreSnapshot(ref MappingSnapshot snapshot)
        {
            for (uint i = 0; i < snapshot.Count; i++)
            {
                MappingPageEntry entry = snapshot.Entries[i];
                PageFlags expectedFlags = PageFlagOps.NormalizeForMap(entry.Flags);

                if (Pager.TryQuery(entry.VirtualAddress, out ulong existingPhysical, out PageFlags existingFlags))
                {
                    PageFlags normalizedExisting = PageFlagOps.NormalizeForMap(existingFlags);
                    if (existingPhysical == entry.PhysicalAddress && normalizedExisting == expectedFlags)
                        continue;

                    // Stale mapping (e.g. kernel identity-map re-inserted by TrySyncKernelLowMappings).
                    // Unmap it so we can restore the correct parent page below.
                    if (!Pager.Unmap(entry.VirtualAddress))
                        return false;
                }

                if (!Pager.Map(entry.VirtualAddress, entry.PhysicalAddress, entry.Flags))
                    return false;
            }

            return true;
        }

        private static void ReleaseMappingContext(ref MappingContext mappingContext)
        {
            ReleaseSnapshot(ref mappingContext.ImageSnapshot);
            // StackSnapshot is not used (child uses a different stack virtual range).
        }

        private static void ReleaseSnapshot(ref MappingSnapshot snapshot)
        {
            if (snapshot.Entries != null)
                global::OS.Kernel.Memory.KernelHeap.Free(snapshot.Entries);

            snapshot.Entries = null;
            snapshot.Count = 0;
        }

        private static ulong AlignDown(ulong value)
        {
            return value & ~(PageSize - 1);
        }

        private static ulong AlignUp(ulong value)
        {
            ulong mask = PageSize - 1;
            if ((value & mask) == 0)
                return value;

            return (value + mask) & ~mask;
        }

        private static bool TryAdvancePage(ref ulong address)
        {
            if (address > 0xFFFFFFFFFFFFFFFFUL - PageSize)
                return false;

            address += PageSize;
            return true;
        }
    }
}
