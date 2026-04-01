using OS.Kernel.Paging;

namespace OS.Kernel.Process
{
    internal static unsafe class ProcessStartupBuilder
    {
        private const ulong StackAlignment = 16;
        private const ulong StartupBlockAlignment = 16;
        private const ulong ServiceTableAlignment = 16;
        private const ulong StackHeadroom = 0x20;

        public static bool TryBuild(ref ProcessImage processImage, ulong markerVirtualAddress)
        {
            if (processImage.StackMappedTop <= processImage.StackBase)
                return false;

            ulong serviceSize = (ulong)sizeof(AppServiceTable);
            ulong startupSize = (ulong)sizeof(ProcessStartupBlock);
            if (serviceSize == 0 || startupSize == 0)
                return false;

            if (processImage.StackMappedTop < processImage.StackBase + serviceSize + startupSize + StackHeadroom)
                return false;

            ulong serviceVirtual = AlignDown(processImage.StackMappedTop - serviceSize, ServiceTableAlignment);
            if (serviceVirtual < processImage.StackBase)
                return false;

            ulong startupVirtual = AlignDown(serviceVirtual - startupSize, StartupBlockAlignment);
            if (startupVirtual < processImage.StackBase)
                return false;

            if (startupVirtual < processImage.StackBase + StackHeadroom)
                return false;

            ulong entryStackTop = AlignDown(startupVirtual - StackHeadroom, StackAlignment);
            if (entryStackTop <= processImage.StackBase || entryStackTop > startupVirtual)
                return false;

            if (!Pager.TryQuery(processImage.EntryPoint, out ulong entryPhysical, out _))
                return false;

            if (!Pager.TryQuery(entryStackTop - 1, out ulong stackTopProbePhysical, out _))
                return false;

            if (!Pager.TryQuery(startupVirtual, out ulong startupPhysical, out _))
                return false;

            if (!AppServiceBuilder.TryBuild(serviceVirtual, out ulong servicePhysical))
                return false;

            if (!Pager.TryQuery(markerVirtualAddress, out ulong markerPhysical, out _))
                return false;

            ProcessStartupBlock startup = default;
            startup.AbiVersion = ProcessStartupBlock.CurrentAbiVersion;
            startup.Flags =
                ProcessStartupBlock.FlagMarkerAddressIsPhysical |
                ProcessStartupBlock.FlagServiceTableAddressIsPhysical;
            startup.ImageBase = processImage.ImageStart;
            startup.ImageEnd = processImage.ImageEnd;
            startup.EntryPoint = processImage.EntryPoint;
            startup.StackBase = processImage.StackBase;
            startup.StackTop = entryStackTop;
            startup.MarkerAddress = markerPhysical;
            startup.ServiceTableAddress = servicePhysical;
            startup.ExitCode = 0;
            startup.Reserved = 0;

            *((ProcessStartupBlock*)startupPhysical) = startup;

            processImage.AbiVersion = startup.AbiVersion;
            processImage.AbiFlags = startup.Flags;
            processImage.StartupBlockVirtual = startupVirtual;
            processImage.StartupBlockPhysical = startupPhysical;
            processImage.ServiceTableVirtual = serviceVirtual;
            processImage.ServiceTablePhysical = servicePhysical;
            processImage.EntryPointPhysical = entryPhysical;
            processImage.StackTop = entryStackTop;
            processImage.StackTopPhysical = stackTopProbePhysical + 1;
            return true;
        }

        private static ulong AlignDown(ulong value, ulong alignment)
        {
            return value & ~(alignment - 1);
        }
    }
}
