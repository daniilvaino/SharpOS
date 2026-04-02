using OS.Kernel.Elf;
using OS.Kernel.Paging;
using OS.Kernel.Util;
using OS.Hal;

namespace OS.Kernel.Process
{
    internal static unsafe class ProcessImageBuilder
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const uint DefaultStackPages = 8;
        private const ulong DefaultStackMappedTop = 0x0000000000800000UL;

        public static bool TryBuild(
            ref ElfLoadedImage loadedImage,
            ulong markerVirtualAddress,
            AppServiceAbi serviceAbi,
            uint requestedAbiVersion,
            out ProcessImage processImage)
        {
            processImage = default;
            processImage.RequestedAbiVersion = NormalizeAbiVersion(requestedAbiVersion);
            processImage.EntryPoint = loadedImage.EntryPoint;
            processImage.ImageStart = loadedImage.LowestVirtualAddress;
            processImage.ImageEnd = loadedImage.HighestVirtualAddressExclusive;
            processImage.MappedImagePages = loadedImage.LoadedPages;
            processImage.StackPages = DefaultStackPages;
            processImage.StackMappedTop = DefaultStackMappedTop;
            processImage.ServiceAbi = serviceAbi;

            ulong stackSize = (ulong)DefaultStackPages * PageSize;
            if (DefaultStackMappedTop < stackSize)
            {
                Log.Write(LogLevel.Warn, "process build: invalid default stack top");
                return false;
            }

            processImage.StackBase = DefaultStackMappedTop - stackSize;
            processImage.StackTop = DefaultStackMappedTop;

            if (RangesOverlap(
                processImage.ImageStart,
                processImage.ImageEnd,
                processImage.StackBase,
                processImage.StackMappedTop))
            {
                Log.Write(LogLevel.Warn, "process build: image/stack overlap");
                return false;
            }

            if (!MapStack(ref processImage))
            {
                Log.Write(LogLevel.Warn, "process build: stack mapping failed");
                return false;
            }

            if (!ProcessStartupBuilder.TryBuild(ref processImage, markerVirtualAddress))
            {
                Log.Write(LogLevel.Warn, "process build: startup block build failed");
                UnmapStack(processImage.StackBase, processImage.MappedStackPages);
                return false;
            }

            return true;
        }

        private static uint NormalizeAbiVersion(uint value)
        {
            if (value <= AppServiceTable.AbiVersionV1)
                return AppServiceTable.AbiVersionV1;

            return AppServiceTable.AbiVersionV2;
        }

        private static bool MapStack(ref ProcessImage processImage)
        {
            ulong currentVirtual = processImage.StackBase;
            for (uint i = 0; i < processImage.StackPages; i++)
            {
                ulong physicalPage = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (physicalPage == 0)
                {
                    Log.Write(LogLevel.Warn, "process build: physical page allocation failed for stack");
                    UnmapStack(processImage.StackBase, processImage.MappedStackPages);
                    return false;
                }

                OS.Kernel.Util.Memory.Zero((void*)physicalPage, (uint)PageSize);

                if (!Pager.Map(currentVirtual, physicalPage, PageFlags.Writable | PageFlags.NoExecute))
                {
                    Log.Write(LogLevel.Warn, "process build: pager map failed for stack page");
                    UnmapStack(processImage.StackBase, processImage.MappedStackPages);
                    return false;
                }

                processImage.MappedStackPages++;
                if (i + 1 < processImage.StackPages)
                    currentVirtual += PageSize;
            }

            return true;
        }

        private static void UnmapStack(ulong stackBase, ulong mappedStackPages)
        {
            ulong currentVirtual = stackBase;
            for (ulong i = 0; i < mappedStackPages; i++)
            {
                Pager.Unmap(currentVirtual);
                if (i + 1 < mappedStackPages)
                    currentVirtual += PageSize;
            }
        }

        private static bool RangesOverlap(ulong aStart, ulong aEnd, ulong bStart, ulong bEnd)
        {
            return aStart < bEnd && bStart < aEnd;
        }
    }
}
