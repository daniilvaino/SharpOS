using OS.Kernel.Paging;
using OS.Hal;

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
            {
                Log.Write(LogLevel.Warn, "process startup: invalid stack range");
                return false;
            }

            ulong serviceSize = (ulong)sizeof(AppServiceTable);
            ulong startupSize = (ulong)sizeof(ProcessStartupBlock);
            if (serviceSize == 0 || startupSize == 0)
            {
                Log.Write(LogLevel.Warn, "process startup: invalid structure sizes");
                return false;
            }

            if (processImage.StackMappedTop < processImage.StackBase + serviceSize + startupSize + StackHeadroom)
            {
                Log.Write(LogLevel.Warn, "process startup: insufficient stack room for startup data");
                return false;
            }

            ulong serviceVirtual = AlignDown(processImage.StackMappedTop - serviceSize, ServiceTableAlignment);
            if (serviceVirtual < processImage.StackBase)
            {
                Log.Write(LogLevel.Warn, "process startup: service table virtual out of stack bounds");
                return false;
            }

            ulong startupVirtual = AlignDown(serviceVirtual - startupSize, StartupBlockAlignment);
            if (startupVirtual < processImage.StackBase)
            {
                Log.Write(LogLevel.Warn, "process startup: startup block virtual out of stack bounds");
                return false;
            }

            if (startupVirtual < processImage.StackBase + StackHeadroom)
            {
                Log.Write(LogLevel.Warn, "process startup: startup block leaves no stack headroom");
                return false;
            }

            ulong entryStackTop = AlignDown(startupVirtual - StackHeadroom, StackAlignment);
            if (entryStackTop <= processImage.StackBase || entryStackTop > startupVirtual)
            {
                Log.Write(LogLevel.Warn, "process startup: entry stack top is invalid");
                return false;
            }

            if (!Pager.TryQuery(processImage.EntryPoint, out ulong entryPhysical, out _))
            {
                Log.Write(LogLevel.Warn, "process startup: entrypoint virtual is not mapped");
                return false;
            }

            if (!Pager.TryQuery(entryStackTop - 1, out ulong stackTopProbePhysical, out _))
            {
                Log.Write(LogLevel.Warn, "process startup: stack top probe is not mapped");
                return false;
            }

            if (!Pager.TryQuery(startupVirtual, out ulong startupPhysical, out _))
            {
                Log.Write(LogLevel.Warn, "process startup: startup block virtual is not mapped");
                return false;
            }

            if (!AppServiceBuilder.TryBuild(
                serviceVirtual,
                processImage.ServiceAbi,
                processImage.RequestedAbiVersion,
                out ulong servicePhysical))
            {
                Log.Write(LogLevel.Warn, "process startup: service table build failed");
                return false;
            }

            ulong markerAddress = 0;
            uint startupFlags = 0;

            if (markerVirtualAddress != 0)
            {
                if (!Pager.TryQuery(markerVirtualAddress, out _, out _))
                {
                    Log.Write(LogLevel.Warn, "process startup: marker address is not mapped");
                    return false;
                }

                markerAddress = markerVirtualAddress;
            }

            ProcessStartupBlock startup = default;
            startup.AbiVersion = processImage.RequestedAbiVersion;
            startup.Flags = startupFlags;
            startup.ImageBase = processImage.ImageStart;
            startup.ImageEnd = processImage.ImageEnd;
            startup.EntryPoint = processImage.EntryPoint;
            startup.StackBase = processImage.StackBase;
            startup.StackTop = entryStackTop;
            startup.MarkerAddress = markerAddress;
            startup.ServiceTableAddress = serviceVirtual;
            startup.ExitCode = 0;
            startup.Reserved = 0;

            ProcessStartupBlock* startupPointer = Pager.IsPagerRootActive()
                ? (ProcessStartupBlock*)startupVirtual
                : (ProcessStartupBlock*)startupPhysical;

            *startupPointer = startup;

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
