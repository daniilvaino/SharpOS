using OS.Kernel.Elf;
using OS.Kernel.Paging;

namespace OS.Kernel.Process
{
    internal static unsafe class ProcessManager
    {
        private const ulong PageSize = X64PageTable.PageSize;

        private static ProcessContext s_currentContext;
        private static bool s_hasCurrentProcess;
        private static bool s_nestedRunActive;

        public static bool HasCurrentProcess => s_hasCurrentProcess;

        public static bool IsNestedRunActive => s_nestedRunActive;

        public static void SetCurrent(ref ProcessImage processImage, ref ElfLoadedImage loadedImage)
        {
            s_currentContext = default;
            s_currentContext.State = ProcessState.Running;
            s_currentContext.ProcessImage = processImage;
            s_currentContext.LoadedImage = loadedImage;
            s_hasCurrentProcess = true;
        }

        public static void ClearCurrent()
        {
            s_currentContext = default;
            s_hasCurrentProcess = false;
            s_nestedRunActive = false;
        }

        public static bool TrySuspendCurrentForNested(out MappingContext mappingContext, out bool suspended)
        {
            mappingContext = default;
            suspended = false;

            if (!s_hasCurrentProcess)
                return true;

            if (s_nestedRunActive)
                return false;

            if (!TryCaptureCurrentMappings(out mappingContext))
                return false;

            if (!TryUnmapCurrentProcessRanges())
            {
                bool _ = TryRestoreMappings(ref mappingContext);
                ReleaseMappingContext(ref mappingContext);
                return false;
            }

            s_nestedRunActive = true;
            suspended = true;
            return true;
        }

        public static bool TryRestoreAfterNested(ref MappingContext mappingContext)
        {
            bool restored = TryRestoreMappings(ref mappingContext);
            ReleaseMappingContext(ref mappingContext);
            s_nestedRunActive = false;
            return restored;
        }

        private static bool TryCaptureCurrentMappings(out MappingContext mappingContext)
        {
            mappingContext = default;
            ProcessImage processImage = s_currentContext.ProcessImage;

            if (!TryCaptureRange(processImage.ImageStart, processImage.ImageEnd, out mappingContext.ImageSnapshot))
                return false;

            if (!TryCaptureRange(processImage.StackBase, processImage.StackMappedTop, out mappingContext.StackSnapshot))
            {
                ReleaseSnapshot(ref mappingContext.ImageSnapshot);
                return false;
            }

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
            if (!TryUnmapRange(processImage.ImageStart, processImage.ImageEnd))
                return false;

            if (!TryUnmapRange(processImage.StackBase, processImage.StackMappedTop))
                return false;

            return true;
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
            if (!TryRestoreSnapshot(ref mappingContext.ImageSnapshot))
                return false;

            if (!TryRestoreSnapshot(ref mappingContext.StackSnapshot))
                return false;

            return true;
        }

        private static bool TryRestoreSnapshot(ref MappingSnapshot snapshot)
        {
            for (uint i = 0; i < snapshot.Count; i++)
            {
                MappingPageEntry entry = snapshot.Entries[i];
                PageFlags expectedFlags = PageFlagOps.NormalizeForMap(entry.Flags);

                if (Pager.TryQuery(entry.VirtualAddress, out ulong existingPhysical, out PageFlags existingFlags))
                {
                    if (existingPhysical != entry.PhysicalAddress)
                        return false;

                    if (PageFlagOps.NormalizeForMap(existingFlags) != expectedFlags)
                        return false;

                    continue;
                }

                if (!Pager.Map(entry.VirtualAddress, entry.PhysicalAddress, entry.Flags))
                    return false;
            }

            return true;
        }

        private static void ReleaseMappingContext(ref MappingContext mappingContext)
        {
            ReleaseSnapshot(ref mappingContext.ImageSnapshot);
            ReleaseSnapshot(ref mappingContext.StackSnapshot);
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
