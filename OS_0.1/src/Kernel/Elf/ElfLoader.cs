using OS.Hal;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static unsafe class ElfLoader
    {
        private const ulong PageSize = X64PageTable.PageSize;

        public static bool TryLoad(ref ElfParseResult result, out ElfLoadedImage loadedImage, out ElfLoadError error)
        {
            loadedImage = default;
            loadedImage.EntryPoint = result.Header.Entry;
            loadedImage.LowestVirtualAddress = 0xFFFFFFFFFFFFFFFFUL;
            error = ElfLoadError.None;

            Log.Write(LogLevel.Info, "elf load start");

            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                if (!ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header))
                {
                    error = ElfLoadError.ProgramHeaderReadFailed;
                    return false;
                }

                if (header.Type != ElfProgramType.Load)
                    continue;

                if (header.MemorySize == 0)
                    continue;

                if (header.FileSize > header.MemorySize)
                {
                    error = ElfLoadError.SegmentFileSizeExceedsMemorySize;
                    return false;
                }

                if (!TryLoadSegment(
                    ref result,
                    header,
                    loadedImage.LoadedSegmentCount,
                    out uint mappedPages,
                    out ulong segmentEndExclusive,
                    out ElfLoadError segmentError))
                {
                    error = segmentError;
                    return false;
                }

                loadedImage.LoadedSegmentCount++;
                loadedImage.LoadedPages += mappedPages;
                if (header.VirtualAddress < loadedImage.LowestVirtualAddress)
                    loadedImage.LowestVirtualAddress = header.VirtualAddress;

                if (segmentEndExclusive > loadedImage.HighestVirtualAddressExclusive)
                    loadedImage.HighestVirtualAddressExclusive = segmentEndExclusive;
            }

            if (loadedImage.LoadedSegmentCount == 0)
            {
                error = ElfLoadError.NoLoadSegments;
                return false;
            }

            Log.Write(LogLevel.Info, "elf image loaded");
            return true;
        }

        private static bool TryLoadSegment(
            ref ElfParseResult result,
            Elf64ProgramHeader header,
            uint loadIndex,
            out uint mappedPages,
            out ulong segmentEndExclusive,
            out ElfLoadError error)
        {
            mappedPages = 0;
            segmentEndExclusive = 0;
            error = ElfLoadError.None;

            if (!CanReadImageRange(result.Image.Length, header.Offset, header.FileSize))
            {
                error = ElfLoadError.SegmentFileRangeOutOfBounds;
                return false;
            }

            if (!TryAdd(header.VirtualAddress, header.MemorySize, out segmentEndExclusive))
            {
                error = ElfLoadError.SegmentAddressOverflow;
                return false;
            }

            if (!TryGetPageRange(header.VirtualAddress, header.MemorySize, out ulong pageStart, out uint pageCount))
            {
                error = ElfLoadError.SegmentAddressOverflow;
                return false;
            }

            PageFlags pageFlags = ProgramFlagsToPageFlags(header.Flags);
            LogMapSegment(loadIndex, header, segmentEndExclusive, pageCount);

            if (!MapSegmentPages(pageStart, pageCount, pageFlags, out mappedPages))
            {
                error = ElfLoadError.SegmentPageMapFailed;
                return false;
            }

            if (header.FileSize != 0)
            {
                if (!CopyFromImageToVirtual(result.Image, header.Offset, header.VirtualAddress, header.FileSize))
                {
                    RollbackMappedPages(pageStart, mappedPages);
                    error = ElfLoadError.SegmentCopyFailed;
                    return false;
                }

                LogSegmentCopy(loadIndex, header.FileSize);
            }

            ulong zeroTail = header.MemorySize - header.FileSize;
            if (zeroTail != 0)
            {
                if (!ZeroVirtualRange(header.VirtualAddress + header.FileSize, zeroTail))
                {
                    RollbackMappedPages(pageStart, mappedPages);
                    error = ElfLoadError.SegmentZeroFillFailed;
                    return false;
                }

                LogSegmentZero(loadIndex, zeroTail);
            }

            return true;
        }

        private static bool MapSegmentPages(ulong virtualAddress, uint pageCount, PageFlags flags, out uint mappedPages)
        {
            mappedPages = 0;
            ulong currentVirtual = virtualAddress;

            for (uint i = 0; i < pageCount; i++)
            {
                ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (page == 0)
                {
                    RollbackMappedPages(virtualAddress, mappedPages);
                    return false;
                }

                OS.Kernel.Util.Memory.Zero((void*)page, (uint)PageSize);

                if (!Pager.Map(currentVirtual, page, flags))
                {
                    RollbackMappedPages(virtualAddress, mappedPages);
                    return false;
                }

                mappedPages++;
                if (i + 1 < pageCount && !TryAdd(currentVirtual, PageSize, out currentVirtual))
                {
                    RollbackMappedPages(virtualAddress, mappedPages);
                    return false;
                }
            }

            return true;
        }

        private static bool CopyFromImageToVirtual(MemoryBlock image, ulong sourceOffset, ulong destinationVirtual, ulong byteCount)
        {
            ulong copied = 0;
            while (copied < byteCount)
            {
                if (!TryAdd(destinationVirtual, copied, out ulong currentVirtual))
                    return false;

                if (!Pager.TryQuery(currentVirtual, out ulong currentPhysical, out PageFlags _))
                    return false;

                if (!TryAdd(sourceOffset, copied, out ulong currentSourceOffset))
                    return false;

                uint pageOffset = (uint)(currentPhysical & (PageSize - 1));
                ulong pageRemaining = PageSize - pageOffset;
                ulong bytesRemaining = byteCount - copied;
                uint chunk = (uint)(bytesRemaining < pageRemaining ? bytesRemaining : pageRemaining);

                if (!CanReadImageRange(image.Length, currentSourceOffset, chunk))
                    return false;

                if (currentSourceOffset > 0xFFFFFFFFUL)
                    return false;

                OS.Kernel.Util.Memory.MemCopy((void*)currentPhysical, image.Pointer + (uint)currentSourceOffset, chunk);
                copied += chunk;
            }

            return true;
        }

        private static bool ZeroVirtualRange(ulong virtualAddress, ulong byteCount)
        {
            ulong cleared = 0;
            while (cleared < byteCount)
            {
                if (!TryAdd(virtualAddress, cleared, out ulong currentVirtual))
                    return false;

                if (!Pager.TryQuery(currentVirtual, out ulong currentPhysical, out PageFlags _))
                    return false;

                uint pageOffset = (uint)(currentPhysical & (PageSize - 1));
                ulong pageRemaining = PageSize - pageOffset;
                ulong bytesRemaining = byteCount - cleared;
                uint chunk = (uint)(bytesRemaining < pageRemaining ? bytesRemaining : pageRemaining);

                OS.Kernel.Util.Memory.Zero((void*)currentPhysical, chunk);
                cleared += chunk;
            }

            return true;
        }

        private static void RollbackMappedPages(ulong virtualAddressStart, uint mappedPages)
        {
            ulong currentVirtual = virtualAddressStart;
            for (uint i = 0; i < mappedPages; i++)
            {
                Pager.Unmap(currentVirtual);
                if (i + 1 < mappedPages)
                    currentVirtual += PageSize;
            }
        }

        private static bool TryGetPageRange(ulong virtualAddress, ulong memorySize, out ulong pageStart, out uint pageCount)
        {
            pageStart = 0;
            pageCount = 0;

            if (memorySize == 0)
                return false;

            if (!TryAdd(virtualAddress, memorySize, out ulong segmentEndExclusive))
                return false;

            pageStart = AlignDown(virtualAddress);
            if (!TryAlignUp(segmentEndExclusive, out ulong pageEndExclusive))
                return false;

            if (pageEndExclusive < pageStart)
                return false;

            ulong span = pageEndExclusive - pageStart;
            if ((span & (PageSize - 1)) != 0)
                return false;

            ulong pages = span / PageSize;
            if (pages == 0 || pages > 0xFFFFFFFFUL)
                return false;

            pageCount = (uint)pages;
            return true;
        }

        internal static PageFlags ProgramFlagsToPageFlags(uint programFlags)
        {
            PageFlags flags = PageFlags.None;

            if ((programFlags & (1U << 1)) != 0)
                flags |= PageFlags.Writable;

            if ((programFlags & (1U << 0)) == 0)
                flags |= PageFlags.NoExecute;

            return flags;
        }

        private static bool CanReadImageRange(uint imageLength, ulong offset, ulong size)
        {
            if (size == 0)
                return offset <= imageLength;

            if (offset > imageLength)
                return false;

            ulong remaining = (ulong)imageLength - offset;
            return size <= remaining;
        }

        private static ulong AlignDown(ulong value)
        {
            return value & ~(PageSize - 1);
        }

        private static bool TryAlignUp(ulong value, out ulong alignedValue)
        {
            ulong mask = PageSize - 1;
            if ((value & mask) == 0)
            {
                alignedValue = value;
                return true;
            }

            if (value > 0xFFFFFFFFFFFFFFFFUL - mask)
            {
                alignedValue = 0;
                return false;
            }

            alignedValue = (value + mask) & ~mask;
            return true;
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

        private static void LogMapSegment(uint loadIndex, Elf64ProgramHeader header, ulong segmentEndExclusive, uint pageCount)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("map seg ");
            Console.WriteUInt(loadIndex);
            Console.Write(": vaddr=0x");
            Console.WriteHex(header.VirtualAddress, 16);
            Console.Write("..0x");
            Console.WriteHex(segmentEndExclusive, 16);
            Console.Write(" pages=");
            Console.WriteUInt(pageCount);
            Console.Write(" flags=");
            ElfDiagnostics.WriteProgramFlags(header.Flags);
            Log.EndLine();
        }

        private static void LogSegmentCopy(uint loadIndex, ulong fileSize)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("copy seg ");
            Console.WriteUInt(loadIndex);
            Console.Write(": filesz=");
            Console.WriteULong(fileSize);
            Log.EndLine();
        }

        private static void LogSegmentZero(uint loadIndex, ulong zeroBytes)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("zero seg ");
            Console.WriteUInt(loadIndex);
            Console.Write(" tail: ");
            Console.WriteULong(zeroBytes);
            Log.EndLine();
        }
    }
}
