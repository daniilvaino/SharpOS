using OS.Kernel.Paging;
using OS.Kernel.Util;
using OS.Hal;

namespace OS.Kernel.Process
{
    internal static unsafe class ProcessImageBuilder
    {
        private const ulong PageSize = X64PageTable.PageSize;
        // 16, not 8: since JumpStub enables interrupts for the duration of
        // the app, a timer tick now lands on THIS stack (the IDT uses ist=0,
        // so an interrupt does not switch stacks). A deep redraw plus an
        // interrupt frame plus a scheduler switch on 32 KiB is tight, and
        // overflowing it faults on an exhausted stack, which is a double
        // fault and then a reset — the one failure that destroys the log it
        // would have to be diagnosed from.
        // 1 MiB (AppProcesses.MainStackBytes, step196): 64 KiB held a frame of
        // 700 live references but not one of 4400, and with nothing below it
        // to fault on, an overrun ran on into whatever was mapped there.
        private const uint DefaultStackPages = (uint)(AppProcesses.MainStackBytes / 4096);

        // The caller passes the stack top: the main thread's window at the top
        // of the process's slot (AppProcesses.StackTopForSlot), with nothing
        // mapped below the stack inside its 4 MiB window.
        public static bool TryBuild(
            ref LoadedImage loadedImage,
            ulong markerVirtualAddress,
            AppServiceAbi serviceAbi,
            uint requestedAbiVersion,
            ulong stackMappedTop,
            out ProcessImage processImage)
        {
            processImage = default;
            processImage.RequestedAbiVersion = NormalizeAbiVersion(requestedAbiVersion);
            processImage.EntryPoint = loadedImage.EntryPoint;
            processImage.ImageStart = loadedImage.LowestVirtualAddress;
            processImage.ImageEnd = loadedImage.HighestVirtualAddressExclusive;
            processImage.MappedImagePages = loadedImage.LoadedPages;
            processImage.StackPages = DefaultStackPages;
            processImage.StackMappedTop = stackMappedTop;
            processImage.ServiceAbi = serviceAbi;

            ulong stackSize = (ulong)DefaultStackPages * PageSize;
            if (stackMappedTop < stackSize)
            {
                Log.Write(LogLevel.Warn, "process build: invalid stack top");
                return false;
            }

            processImage.StackBase = stackMappedTop - stackSize;
            processImage.StackTop = stackMappedTop;

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
            => AppServiceTable.Normalize(value);

        private static bool MapStack(ref ProcessImage processImage)
        {
            if (!TryPrepareStackVirtualSpan(processImage.StackBase, processImage.StackPages))
            {
                Log.Write(LogLevel.Warn, "process build: stack span preparation failed");
                return false;
            }

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
                    Log.Begin(LogLevel.Warn);
                    Console.Write("process build: stack map fail vaddr=0x");
                    Console.WriteHex(currentVirtual, 16);
                    if (Pager.TryQuery(currentVirtual, out ulong existingPhysical, out PageFlags existingFlags))
                    {
                        Console.Write(" existing=0x");
                        Console.WriteHex(existingPhysical, 16);
                        Console.Write(" flags=0x");
                        Console.WriteHex((ulong)existingFlags, 16);
                    }

                    Log.EndLine();
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

        private static bool TryPrepareStackVirtualSpan(ulong stackBase, uint pageCount)
        {
            if (pageCount == 0)
                return false;

            ulong currentVirtual = stackBase;
            for (uint i = 0; i < pageCount; i++)
            {
                if (Pager.TryQuery(currentVirtual, out _, out _) && !Pager.Unmap(currentVirtual))
                    return false;

                if (i + 1 < pageCount)
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
