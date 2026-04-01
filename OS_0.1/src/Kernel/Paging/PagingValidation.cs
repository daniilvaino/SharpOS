using OS.Hal;

namespace OS.Kernel.Paging
{
    internal static class PagingValidation
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const ulong ValidationBaseVirtual = 0xFFFF800000100000UL;

        public static void Run()
        {
            Log.Write(LogLevel.Info, "pager validation start");
            ValidateOffsetAndDuplicates();
            ValidateNeighborsAndFlags();
            ValidateRangeApi();
            Log.Write(LogLevel.Info, "pager validation done");
        }

        private static void ValidateOffsetAndDuplicates()
        {
            ulong virtualAddress = ValidationBaseVirtual + 0x1000UL;
            ulong physicalAddress = AllocPageOrFail("pager validation: physical page alloc failed (offset)");
            PageFlags flags = PageFlags.Writable | PageFlags.Global | PageFlags.NoExecute;

            Require(Pager.Map(virtualAddress, physicalAddress, flags), "pager validation: map failed (offset)");
            Require(!Pager.Map(virtualAddress, physicalAddress + PageSize, flags), "pager validation: duplicate map must fail");

            ulong offsetAddress = virtualAddress + 0x345UL;
            Require(Pager.TryQuery(offsetAddress, out ulong queriedPhysicalAddress, out PageFlags queriedFlags), "pager validation: query failed (offset)");
            Require(queriedPhysicalAddress == physicalAddress + 0x345UL, "pager validation: offset physical mismatch");
            Require(queriedFlags == (flags | PageFlags.Present), "pager validation: offset flags mismatch");
            Log.Write(LogLevel.Info, "pager check: offset query pass");

            Require(Pager.Unmap(virtualAddress), "pager validation: unmap failed");
            Require(!Pager.Unmap(virtualAddress), "pager validation: duplicate unmap must fail");
            Require(!Pager.TryQuery(virtualAddress, out _, out _), "pager validation: query after unmap must fail");
            Log.Write(LogLevel.Info, "pager check: duplicate map/unmap pass");

            PagingDiagnostics.DumpMapping(offsetAddress);
        }

        private static void ValidateNeighborsAndFlags()
        {
            ulong firstVirtual = ValidationBaseVirtual + 0x8000UL;
            ulong secondVirtual = firstVirtual + PageSize;
            ulong thirdVirtual = secondVirtual + PageSize;

            ulong firstPhysical = AllocPageOrFail("pager validation: physical page alloc failed (neighbor 1)");
            ulong secondPhysical = AllocPageOrFail("pager validation: physical page alloc failed (neighbor 2)");
            ulong thirdPhysical = AllocPageOrFail("pager validation: physical page alloc failed (flags)");

            PageFlags firstFlags = PageFlags.Writable | PageFlags.WriteThrough;
            PageFlags secondFlags = PageFlags.Writable | PageFlags.User | PageFlags.CacheDisable;
            PageFlags thirdFlags = PageFlags.Writable | PageFlags.Global | PageFlags.NoExecute;

            Require(Pager.Map(firstVirtual, firstPhysical, firstFlags), "pager validation: map failed (neighbor first)");
            Pager.GetSummary(out PagingSummary summaryAfterFirstMap);

            Require(Pager.Map(secondVirtual, secondPhysical, secondFlags), "pager validation: map failed (neighbor second)");
            Pager.GetSummary(out PagingSummary summaryAfterSecondMap);

            Require(summaryAfterSecondMap.TablePages == summaryAfterFirstMap.TablePages, "pager validation: extra table pages for neighbor map");
            Require(summaryAfterSecondMap.SpareTablePages == summaryAfterFirstMap.SpareTablePages, "pager validation: spare table mismatch for neighbor map");
            Log.Write(LogLevel.Info, "pager check: neighbor pages reuse tables");

            Require(Pager.Map(thirdVirtual, thirdPhysical, thirdFlags), "pager validation: map failed (flags)");
            RequireFlagsRoundTrip(firstVirtual, firstPhysical, firstFlags);
            RequireFlagsRoundTrip(secondVirtual, secondPhysical, secondFlags);
            RequireFlagsRoundTrip(thirdVirtual, thirdPhysical, thirdFlags);
            Log.Write(LogLevel.Info, "pager check: multi-flag round trip pass");

            PagingDiagnostics.DumpMapping(firstVirtual);
            PagingDiagnostics.DumpMapping(secondVirtual);
            PagingDiagnostics.DumpMapping(thirdVirtual);

            Require(Pager.Unmap(firstVirtual), "pager validation: unmap failed (neighbor first)");
            Require(Pager.Unmap(secondVirtual), "pager validation: unmap failed (neighbor second)");
            Require(Pager.Unmap(thirdVirtual), "pager validation: unmap failed (flags)");
        }

        private static void ValidateRangeApi()
        {
            const uint rangePages = 3;
            ulong virtualStart = ValidationBaseVirtual + 0x12000UL;
            ulong physicalStart = AllocPagesOrFail(rangePages, "pager validation: physical pages alloc failed (range)");
            PageFlags flags = PageFlags.Writable | PageFlags.Global;

            Require(Pager.MapRange(virtualStart, physicalStart, rangePages, flags), "pager validation: map range failed");

            for (uint i = 0; i < rangePages; i++)
            {
                ulong virtualAddress = virtualStart + ((ulong)i * PageSize);
                ulong expectedPhysical = physicalStart + ((ulong)i * PageSize);
                Require(Pager.TryQuery(virtualAddress, out ulong queriedPhysical, out PageFlags queriedFlags), "pager validation: range query failed");
                Require(queriedPhysical == expectedPhysical, "pager validation: range physical mismatch");
                Require(queriedFlags == (flags | PageFlags.Present), "pager validation: range flags mismatch");
            }

            Require(!Pager.MapRange(virtualStart, physicalStart, rangePages, flags), "pager validation: duplicate map range must fail");
            Require(Pager.UnmapRange(virtualStart, rangePages), "pager validation: unmap range failed");

            for (uint i = 0; i < rangePages; i++)
            {
                ulong virtualAddress = virtualStart + ((ulong)i * PageSize);
                Require(!Pager.TryQuery(virtualAddress, out _, out _), "pager validation: range query after unmap must fail");
            }

            Require(!Pager.UnmapRange(virtualStart, rangePages), "pager validation: duplicate unmap range must fail");
            Log.Write(LogLevel.Info, "pager check: map/unmap range pass");

            PagingDiagnostics.DumpMapping(virtualStart);
        }

        private static void RequireFlagsRoundTrip(ulong virtualAddress, ulong expectedPhysicalAddress, PageFlags expectedFlags)
        {
            Require(Pager.TryQuery(virtualAddress, out ulong physicalAddress, out PageFlags flags), "pager validation: query failed (flags round trip)");
            Require(physicalAddress == expectedPhysicalAddress, "pager validation: physical mismatch (flags round trip)");
            Require(flags == (expectedFlags | PageFlags.Present), "pager validation: flags mismatch (flags round trip)");
        }

        private static ulong AllocPageOrFail(string panicMessage)
        {
            ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
            Require(page != 0, panicMessage);
            return page;
        }

        private static ulong AllocPagesOrFail(uint pageCount, string panicMessage)
        {
            ulong page = global::OS.Kernel.PhysicalMemory.AllocPages(pageCount);
            Require(page != 0, panicMessage);
            return page;
        }

        private static void Require(bool condition, string panicMessage)
        {
            if (!condition)
                global::OS.Kernel.Panic.Fail(panicMessage);
        }
    }
}
