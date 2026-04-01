using OS.Hal;
using OS.Kernel.Diagnostics;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static unsafe class ElfLoadValidation
    {
        private const ulong ProgramFlagExecute = 1U << 0;
        private const ulong PageSize = X64PageTable.PageSize;

        public static void Run(ref ElfParseResult result, ref ElfLoadedImage loadedImage)
        {
            Log.Write(LogLevel.Info, "elf load validate start");

            uint checkedLoadSegments = 0;
            ulong checkedFileBytes = 0;
            ulong checkedZeroTailBytes = 0;
            bool entryInsideExecutableSegment = false;

            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                KernelAssert.True(
                    ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header),
                    "elf load validation: phdr read failed");

                if (header.Type != ElfProgramType.Load || header.MemorySize == 0)
                    continue;

                if (!entryInsideExecutableSegment && IsExecutable(header.Flags))
                    entryInsideExecutableSegment = ContainsAddress(header.VirtualAddress, header.MemorySize, loadedImage.EntryPoint);

                ValidateSegmentFlags(header);

                if (header.FileSize != 0)
                {
                    ValidateSegmentFileBytes(result.Image, header);
                    checkedFileBytes += header.FileSize;
                }

                ulong zeroTailBytes = header.MemorySize - header.FileSize;
                if (zeroTailBytes != 0)
                {
                    ValidateSegmentZeroTail(header);
                    checkedZeroTailBytes += zeroTailBytes;
                }

                checkedLoadSegments++;
            }

            KernelAssert.Equal(
                loadedImage.LoadedSegmentCount,
                checkedLoadSegments,
                "elf load validation: loaded segment count mismatch");

            KernelAssert.True(
                entryInsideExecutableSegment,
                "elf load validation: entrypoint not in executable segment");

            KernelAssert.True(
                Pager.TryQuery(loadedImage.EntryPoint, out _, out PageFlags entryFlags),
                "elf load validation: entrypoint not mapped");

            KernelAssert.True(
                (entryFlags & PageFlags.NoExecute) == 0,
                "elf load validation: entrypoint mapped as NX");

            Log.Begin(LogLevel.Info);
            Console.Write("elf load validate bytes file/zero: ");
            Console.WriteULong(checkedFileBytes);
            Console.Write("/");
            Console.WriteULong(checkedZeroTailBytes);
            Log.EndLine();

            Log.Write(LogLevel.Info, "elf load validate done");
        }

        private static void ValidateSegmentFlags(Elf64ProgramHeader header)
        {
            KernelAssert.True(
                TryGetSegmentPageRange(header, out ulong pageStart, out uint pageCount),
                "elf load validation: segment page range failed");

            PageFlags expectedFlags = PageFlagOps.NormalizeForMap(ElfLoader.ProgramFlagsToPageFlags(header.Flags));
            ulong currentVirtual = pageStart;

            for (uint i = 0; i < pageCount; i++)
            {
                KernelAssert.True(
                    Pager.TryQuery(currentVirtual, out _, out PageFlags actualFlags),
                    "elf load validation: segment page is not mapped");

                KernelAssert.Equal(
                    (ulong)expectedFlags,
                    (ulong)actualFlags,
                    "elf load validation: segment page flags mismatch");

                if (i + 1 < pageCount)
                    currentVirtual += PageSize;
            }
        }

        private static void ValidateSegmentFileBytes(MemoryBlock image, Elf64ProgramHeader header)
        {
            ulong checkedBytes = 0;
            while (checkedBytes < header.FileSize)
            {
                ulong sourceOffset = header.Offset + checkedBytes;
                ulong destinationVirtual = header.VirtualAddress + checkedBytes;

                KernelAssert.True(
                    sourceOffset <= 0xFFFFFFFFUL,
                    "elf load validation: source offset overflow");

                KernelAssert.True(
                    image.TryReadByte((uint)sourceOffset, out byte sourceByte),
                    "elf load validation: source byte read failed");

                KernelAssert.True(
                    TryReadMappedByte(destinationVirtual, out byte loadedByte),
                    "elf load validation: loaded byte read failed");

                KernelAssert.Equal(
                    (uint)sourceByte,
                    (uint)loadedByte,
                    "elf load validation: copied byte mismatch");

                checkedBytes++;
            }
        }

        private static void ValidateSegmentZeroTail(Elf64ProgramHeader header)
        {
            ulong tailStart = header.VirtualAddress + header.FileSize;
            ulong tailLength = header.MemorySize - header.FileSize;
            ulong checkedBytes = 0;

            while (checkedBytes < tailLength)
            {
                ulong currentVirtual = tailStart + checkedBytes;
                KernelAssert.True(
                    TryReadMappedByte(currentVirtual, out byte loadedByte),
                    "elf load validation: zero-tail byte read failed");

                KernelAssert.Equal(
                    0U,
                    (uint)loadedByte,
                    "elf load validation: zero-tail byte is not zero");

                checkedBytes++;
            }
        }

        private static bool TryReadMappedByte(ulong virtualAddress, out byte value)
        {
            value = 0;
            if (!Pager.TryQuery(virtualAddress, out ulong physicalAddress, out _))
                return false;

            value = *((byte*)physicalAddress);
            return true;
        }

        private static bool TryGetSegmentPageRange(Elf64ProgramHeader header, out ulong pageStart, out uint pageCount)
        {
            pageStart = 0;
            pageCount = 0;

            if (header.MemorySize == 0)
                return false;

            if (!TryAdd(header.VirtualAddress, header.MemorySize, out ulong segmentEndExclusive))
                return false;

            pageStart = AlignDown(header.VirtualAddress);
            ulong pageEndExclusive = AlignUp(segmentEndExclusive);
            if (pageEndExclusive < pageStart)
                return false;

            ulong bytes = pageEndExclusive - pageStart;
            if ((bytes & (PageSize - 1)) != 0)
                return false;

            ulong pages = bytes / PageSize;
            if (pages == 0 || pages > 0xFFFFFFFFUL)
                return false;

            pageCount = (uint)pages;
            return true;
        }

        private static bool ContainsAddress(ulong start, ulong size, ulong address)
        {
            if (!TryAdd(start, size, out ulong endExclusive))
                return false;

            return address >= start && address < endExclusive;
        }

        private static bool IsExecutable(uint programFlags)
        {
            return (programFlags & (uint)ProgramFlagExecute) != 0;
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

        private static bool TryAdd(ulong left, ulong right, out ulong value)
        {
            if (left > 0xFFFFFFFFFFFFFFFFUL - right)
            {
                value = 0;
                return false;
            }

            value = left + right;
            return true;
        }
    }
}
