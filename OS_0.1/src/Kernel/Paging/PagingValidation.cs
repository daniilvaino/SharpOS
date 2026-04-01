using OS.Hal;
using OS.Kernel.Diagnostics;
using OS.Kernel.Util;

namespace OS.Kernel.Paging
{
    internal static unsafe class PagingValidation
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const ulong ValidationBaseVirtual = 0xFFFF800000100000UL;

        public static void Run()
        {
            Log.Write(LogLevel.Info, "pager validation start");
            ValidateUtilityLayer();
            ValidateOffsetAndDuplicates();
            ValidateNeighborsAndFlags();
            ValidateRangeApi();
            Log.Write(LogLevel.Info, "pager validation done");
        }

        private static void ValidateUtilityLayer()
        {
            ulong page = AllocPageOrFail("pager validation: physical page alloc failed (util)");
            MemoryBlock block = new MemoryBlock((void*)page, 64);
            KernelAssert.True(block.IsValid, "utility block invalid");

            block.Clear();
            KernelAssert.True(block.TryWriteUInt32(0, 0x11223344U), "utility write u32 failed");
            KernelAssert.True(block.TryWriteUInt16(4, 0x5566), "utility write u16 failed");
            KernelAssert.True(block.TryWriteByte(6, 0x77), "utility write byte failed");
            KernelAssert.True(block.TryWriteByte(7, 0x88), "utility write byte failed");

            BinaryReaderLite reader = new BinaryReaderLite(block.Pointer, block.Length);
            KernelAssert.True(reader.TryReadUInt32(out uint readU32), "utility read u32 failed");
            KernelAssert.True(reader.TryReadUInt16(out ushort readU16), "utility read u16 failed");
            KernelAssert.True(reader.TryReadByte(out byte readByteA), "utility read byte failed");
            KernelAssert.True(reader.TryReadByte(out byte readByteB), "utility read byte failed");
            KernelAssert.Equal(0x11223344U, readU32, "utility read u32 mismatch");
            KernelAssert.Equal((uint)0x5566, readU16, "utility read u16 mismatch");
            KernelAssert.Equal((uint)0x77, readByteA, "utility read byte mismatch");
            KernelAssert.Equal((uint)0x88, readByteB, "utility read byte mismatch");

            block.Clear();
            KernelAssert.True(block.TryWriteByte(0, 0x03), "utility write prefixed length failed");
            KernelAssert.True(block.TryWriteByte(1, 0xAA), "utility write prefixed payload failed");
            KernelAssert.True(block.TryWriteByte(2, 0xBB), "utility write prefixed payload failed");
            KernelAssert.True(block.TryWriteByte(3, 0xCC), "utility write prefixed payload failed");

            BinaryReaderLite prefixedReader = new BinaryReaderLite(block.Pointer, block.Length);
            KernelAssert.True(prefixedReader.TryReadPrefixedBlock(out MemoryBlock payload), "utility prefixed read failed");
            KernelAssert.Equal(3U, payload.Length, "utility prefixed payload length mismatch");
            KernelAssert.True(payload.TryReadByte(0, out byte payload0), "utility prefixed payload read failed");
            KernelAssert.True(payload.TryReadByte(1, out byte payload1), "utility prefixed payload read failed");
            KernelAssert.True(payload.TryReadByte(2, out byte payload2), "utility prefixed payload read failed");
            KernelAssert.Equal((uint)0xAA, payload0, "utility prefixed payload byte 0 mismatch");
            KernelAssert.Equal((uint)0xBB, payload1, "utility prefixed payload byte 1 mismatch");
            KernelAssert.Equal((uint)0xCC, payload2, "utility prefixed payload byte 2 mismatch");

            Log.Write(LogLevel.Info, "pager check: util memory block/binary reader pass");
            HexDump.Dump("util sample", block.Pointer, 16, 16);
        }

        private static void ValidateOffsetAndDuplicates()
        {
            ulong virtualAddress = ValidationBaseVirtual + 0x1000UL;
            ulong physicalAddress = AllocPageOrFail("pager validation: physical page alloc failed (offset)");
            PageFlags flags = PageFlags.Writable | PageFlags.Global | PageFlags.NoExecute;

            KernelAssert.True(Pager.Map(virtualAddress, physicalAddress, flags), "pager validation: map failed (offset)");
            KernelAssert.False(Pager.Map(virtualAddress, physicalAddress + PageSize, flags), "pager validation: duplicate map must fail");

            ulong offsetAddress = virtualAddress + 0x345UL;
            KernelAssert.True(Pager.TryQuery(offsetAddress, out ulong queriedPhysicalAddress, out PageFlags queriedFlags), "pager validation: query failed (offset)");
            KernelAssert.Equal(physicalAddress + 0x345UL, queriedPhysicalAddress, "pager validation: offset physical mismatch");
            KernelAssert.Equal((ulong)(flags | PageFlags.Present), (ulong)queriedFlags, "pager validation: offset flags mismatch");
            Log.Write(LogLevel.Info, "pager check: offset query pass");

            KernelAssert.True(Pager.Unmap(virtualAddress), "pager validation: unmap failed");
            KernelAssert.False(Pager.Unmap(virtualAddress), "pager validation: duplicate unmap must fail");
            KernelAssert.False(Pager.TryQuery(virtualAddress, out _, out _), "pager validation: query after unmap must fail");
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

            KernelAssert.True(Pager.Map(firstVirtual, firstPhysical, firstFlags), "pager validation: map failed (neighbor first)");
            Pager.GetSummary(out PagingSummary summaryAfterFirstMap);

            KernelAssert.True(Pager.Map(secondVirtual, secondPhysical, secondFlags), "pager validation: map failed (neighbor second)");
            Pager.GetSummary(out PagingSummary summaryAfterSecondMap);

            KernelAssert.Equal(summaryAfterFirstMap.TablePages, summaryAfterSecondMap.TablePages, "pager validation: extra table pages for neighbor map");
            KernelAssert.Equal(summaryAfterFirstMap.SpareTablePages, summaryAfterSecondMap.SpareTablePages, "pager validation: spare table mismatch for neighbor map");
            Log.Write(LogLevel.Info, "pager check: neighbor pages reuse tables");

            KernelAssert.True(Pager.Map(thirdVirtual, thirdPhysical, thirdFlags), "pager validation: map failed (flags)");
            RequireFlagsRoundTrip(firstVirtual, firstPhysical, firstFlags);
            RequireFlagsRoundTrip(secondVirtual, secondPhysical, secondFlags);
            RequireFlagsRoundTrip(thirdVirtual, thirdPhysical, thirdFlags);
            Log.Write(LogLevel.Info, "pager check: multi-flag round trip pass");

            PagingDiagnostics.DumpMapping(firstVirtual);
            PagingDiagnostics.DumpMapping(secondVirtual);
            PagingDiagnostics.DumpMapping(thirdVirtual);

            KernelAssert.True(Pager.Unmap(firstVirtual), "pager validation: unmap failed (neighbor first)");
            KernelAssert.True(Pager.Unmap(secondVirtual), "pager validation: unmap failed (neighbor second)");
            KernelAssert.True(Pager.Unmap(thirdVirtual), "pager validation: unmap failed (flags)");
        }

        private static void ValidateRangeApi()
        {
            const uint rangePages = 3;
            ulong virtualStart = ValidationBaseVirtual + 0x12000UL;
            ulong physicalStart = AllocPagesOrFail(rangePages, "pager validation: physical pages alloc failed (range)");
            PageFlags flags = PageFlags.Writable | PageFlags.Global;

            KernelAssert.True(Pager.MapRange(virtualStart, physicalStart, rangePages, flags), "pager validation: map range failed");

            for (uint i = 0; i < rangePages; i++)
            {
                ulong virtualAddress = virtualStart + ((ulong)i * PageSize);
                ulong expectedPhysical = physicalStart + ((ulong)i * PageSize);
                KernelAssert.True(Pager.TryQuery(virtualAddress, out ulong queriedPhysical, out PageFlags queriedFlags), "pager validation: range query failed");
                KernelAssert.Equal(expectedPhysical, queriedPhysical, "pager validation: range physical mismatch");
                KernelAssert.Equal((ulong)(flags | PageFlags.Present), (ulong)queriedFlags, "pager validation: range flags mismatch");
            }

            KernelAssert.False(Pager.MapRange(virtualStart, physicalStart, rangePages, flags), "pager validation: duplicate map range must fail");
            KernelAssert.True(Pager.UnmapRange(virtualStart, rangePages), "pager validation: unmap range failed");

            for (uint i = 0; i < rangePages; i++)
            {
                ulong virtualAddress = virtualStart + ((ulong)i * PageSize);
                KernelAssert.False(Pager.TryQuery(virtualAddress, out _, out _), "pager validation: range query after unmap must fail");
            }

            KernelAssert.False(Pager.UnmapRange(virtualStart, rangePages), "pager validation: duplicate unmap range must fail");
            Log.Write(LogLevel.Info, "pager check: map/unmap range pass");

            PagingDiagnostics.DumpMapping(virtualStart);
        }

        private static void RequireFlagsRoundTrip(ulong virtualAddress, ulong expectedPhysicalAddress, PageFlags expectedFlags)
        {
            KernelAssert.True(Pager.TryQuery(virtualAddress, out ulong physicalAddress, out PageFlags flags), "pager validation: query failed (flags round trip)");
            KernelAssert.Equal(expectedPhysicalAddress, physicalAddress, "pager validation: physical mismatch (flags round trip)");
            KernelAssert.Equal((ulong)(expectedFlags | PageFlags.Present), (ulong)flags, "pager validation: flags mismatch (flags round trip)");
        }

        private static ulong AllocPageOrFail(string panicMessage)
        {
            ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
            KernelAssert.True(page != 0, panicMessage);
            return page;
        }

        private static ulong AllocPagesOrFail(uint pageCount, string panicMessage)
        {
            ulong page = global::OS.Kernel.PhysicalMemory.AllocPages(pageCount);
            KernelAssert.True(page != 0, panicMessage);
            return page;
        }
    }
}
