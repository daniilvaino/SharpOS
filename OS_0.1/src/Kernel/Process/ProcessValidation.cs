using OS.Hal;
using OS.Kernel.Diagnostics;
using OS.Kernel.Elf;
using OS.Kernel.Paging;

namespace OS.Kernel.Process
{
    internal static unsafe class ProcessValidation
    {
        private const ulong PageSize = X64PageTable.PageSize;

        public static void Run(ref ProcessImage processImage, ref ElfParseResult elfResult)
        {
            KernelAssert.True(processImage.EntryPoint != 0, "process validation: entrypoint is zero");
            KernelAssert.True(processImage.ImageStart < processImage.ImageEnd, "process validation: invalid image range");
            KernelAssert.True(processImage.StackBase < processImage.StackMappedTop, "process validation: invalid mapped stack range");
            KernelAssert.True(processImage.StackBase < processImage.StackTop, "process validation: invalid entry stack top");
            KernelAssert.True((processImage.StackTop & 0xFUL) == 0, "process validation: stack top alignment invalid");
            KernelAssert.True(processImage.StackTop < processImage.StackMappedTop, "process validation: stack top outside mapped range");

            KernelAssert.False(
                RangesOverlap(processImage.ImageStart, processImage.ImageEnd, processImage.StackBase, processImage.StackMappedTop),
                "process validation: image/stack overlap");

            ValidateEntryPoint(ref processImage, ref elfResult);
            ValidateStack(ref processImage);
            ValidateStartupBlock(ref processImage);
            ValidateServiceTable(ref processImage);

            Log.Write(LogLevel.Info, "process validation ok");
        }

        private static void ValidateEntryPoint(ref ProcessImage processImage, ref ElfParseResult elfResult)
        {
            bool entryInExecutableSegment = false;

            for (ushort i = 0; i < elfResult.Header.ProgramHeaderCount; i++)
            {
                KernelAssert.True(
                    ElfParser.TryGetProgramHeader(ref elfResult, i, out Elf64ProgramHeader header),
                    "process validation: phdr read failed");

                if (header.Type != ElfProgramType.Load || header.MemorySize == 0)
                    continue;

                bool executable = (header.Flags & (1U << 0)) != 0;
                if (!executable)
                    continue;

                if (ContainsAddress(header.VirtualAddress, header.MemorySize, processImage.EntryPoint))
                {
                    entryInExecutableSegment = true;
                    break;
                }
            }

            KernelAssert.True(entryInExecutableSegment, "process validation: entrypoint is outside executable segment");

            KernelAssert.True(
                Pager.TryQuery(processImage.EntryPoint, out _, out PageFlags entryFlags),
                "process validation: entrypoint not mapped");

            KernelAssert.True(
                (entryFlags & PageFlags.NoExecute) == 0,
                "process validation: entrypoint mapped as NX");

            KernelAssert.True(processImage.EntryPointPhysical != 0, "process validation: entry physical is zero");
        }

        private static void ValidateStack(ref ProcessImage processImage)
        {
            KernelAssert.True(processImage.MappedStackPages == processImage.StackPages, "process validation: stack pages mismatch");

            for (uint i = 0; i < processImage.StackPages; i++)
            {
                ulong pageVirtual = processImage.StackBase + ((ulong)i * PageSize);
                KernelAssert.True(
                    Pager.TryQuery(pageVirtual, out _, out PageFlags stackFlags),
                    "process validation: stack page unmapped");

                KernelAssert.True(
                    (stackFlags & PageFlags.Writable) == PageFlags.Writable,
                    "process validation: stack page not writable");
            }

            KernelAssert.True(
                Pager.TryQuery(processImage.StackTop - 1, out _, out PageFlags stackTopFlags),
                "process validation: stack top probe unmapped");

            KernelAssert.True(
                (stackTopFlags & PageFlags.Writable) == PageFlags.Writable,
                "process validation: stack top is not writable");

            KernelAssert.True(processImage.StackTopPhysical != 0, "process validation: stack top physical is zero");
        }

        private static void ValidateStartupBlock(ref ProcessImage processImage)
        {
            ulong startupSize = (ulong)sizeof(ProcessStartupBlock);
            KernelAssert.True(startupSize != 0, "process validation: startup block size invalid");
            KernelAssert.True(processImage.StackMappedTop >= startupSize, "process validation: mapped stack top too small");
            KernelAssert.True(processImage.StartupBlockVirtual != 0, "process validation: startup block virtual is zero");
            KernelAssert.True(processImage.StartupBlockPhysical != 0, "process validation: startup block physical is zero");
            KernelAssert.True(processImage.StackTop <= processImage.StartupBlockVirtual, "process validation: startup block overlaps stack top");
            KernelAssert.True(processImage.StartupBlockVirtual >= processImage.StackBase, "process validation: startup block below stack");
            KernelAssert.True(processImage.StartupBlockVirtual <= processImage.StackMappedTop - startupSize, "process validation: startup block exceeds stack");

            KernelAssert.True(
                Pager.TryQuery(processImage.StartupBlockVirtual, out _, out PageFlags startupFlags),
                "process validation: startup block unmapped");

            KernelAssert.True(
                (startupFlags & PageFlags.Writable) == PageFlags.Writable,
                "process validation: startup block not writable");

            ProcessStartupBlock* startup = (ProcessStartupBlock*)processImage.StartupBlockPhysical;
            KernelAssert.Equal(ProcessStartupBlock.CurrentAbiVersion, startup->AbiVersion, "process validation: startup abi version mismatch");
            KernelAssert.Equal(processImage.AbiFlags, startup->Flags, "process validation: startup flags mismatch");
            KernelAssert.Equal(processImage.EntryPoint, startup->EntryPoint, "process validation: startup entry mismatch");
            KernelAssert.Equal(processImage.StackTop, startup->StackTop, "process validation: startup stack top mismatch");
            KernelAssert.Equal(processImage.StackBase, startup->StackBase, "process validation: startup stack base mismatch");
            if (startup->MarkerAddress != 0)
            {
                KernelAssert.True(
                    (startup->Flags & ProcessStartupBlock.FlagMarkerAddressIsPhysical) == ProcessStartupBlock.FlagMarkerAddressIsPhysical,
                    "process validation: marker address flag missing");
            }

            KernelAssert.True(
                (startup->Flags & ProcessStartupBlock.FlagServiceTableAddressIsPhysical) == ProcessStartupBlock.FlagServiceTableAddressIsPhysical,
                "process validation: service table address flag missing");

            KernelAssert.True(startup->ServiceTableAddress != 0, "process validation: startup service table is null");
            KernelAssert.Equal(processImage.ServiceTablePhysical, startup->ServiceTableAddress, "process validation: startup service table mismatch");
        }

        private static void ValidateServiceTable(ref ProcessImage processImage)
        {
            ulong tableSize = (ulong)sizeof(AppServiceTable);
            KernelAssert.True(tableSize != 0, "process validation: service table size invalid");
            KernelAssert.True(processImage.StackMappedTop >= tableSize, "process validation: mapped stack top too small for services");
            KernelAssert.True(processImage.ServiceTableVirtual != 0, "process validation: service table virtual is zero");
            KernelAssert.True(processImage.ServiceTablePhysical != 0, "process validation: service table physical is zero");
            KernelAssert.True(processImage.ServiceTableVirtual >= processImage.StackBase, "process validation: service table below stack");
            KernelAssert.True(processImage.ServiceTableVirtual <= processImage.StackMappedTop - tableSize, "process validation: service table exceeds stack");

            KernelAssert.True(
                Pager.TryQuery(processImage.ServiceTableVirtual, out _, out PageFlags serviceFlags),
                "process validation: service table unmapped");

            KernelAssert.True(
                (serviceFlags & PageFlags.Writable) == PageFlags.Writable,
                "process validation: service table not writable");

            AppServiceTable* table = (AppServiceTable*)processImage.ServiceTablePhysical;
            KernelAssert.Equal(AppServiceTable.CurrentAbiVersion, table->AbiVersion, "process validation: service table abi version mismatch");
            KernelAssert.True(table->WriteStringAddress != 0, "process validation: service write pointer is null");
            KernelAssert.True(table->WriteUIntAddress != 0, "process validation: service write uint pointer is null");
            KernelAssert.True(table->WriteHexAddress != 0, "process validation: service write hex pointer is null");
            KernelAssert.True(table->GetAbiVersionAddress != 0, "process validation: service get abi version pointer is null");
            KernelAssert.True(table->ExitAddress != 0, "process validation: service exit pointer is null");
        }

        private static bool ContainsAddress(ulong start, ulong size, ulong address)
        {
            if (size == 0)
                return false;

            if (start > 0xFFFFFFFFFFFFFFFFUL - size)
                return false;

            ulong endExclusive = start + size;
            return address >= start && address < endExclusive;
        }

        private static bool RangesOverlap(ulong aStart, ulong aEnd, ulong bStart, ulong bEnd)
        {
            return aStart < bEnd && bStart < aEnd;
        }
    }
}
