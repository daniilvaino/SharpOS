using OS.Kernel;

namespace OS.Boot
{
    internal static unsafe class UefiMemoryMapBuilder
    {
        private const ulong EfiSuccess = 0;
        private const ulong DescriptorSlack = 8;

        public static bool TryBuild(EFI_SYSTEM_TABLE* systemTable, out MemoryMapInfo memoryMap)
        {
            memoryMap = default;

            if (systemTable == null || systemTable->BootServices == null)
                return false;

            EFI_BOOT_SERVICES* bootServices = systemTable->BootServices;
            if (bootServices->GetMemoryMap == null || bootServices->AllocatePool == null)
                return false;

            ulong memoryMapSize = 0;
            ulong mapKey = 0;
            ulong descriptorSize = 0;
            uint descriptorVersion = 0;

            bootServices->GetMemoryMap(&memoryMapSize, null, &mapKey, &descriptorSize, &descriptorVersion);
            if (memoryMapSize == 0 || descriptorSize == 0)
                return false;

            memoryMapSize += descriptorSize * DescriptorSlack;

            void* descriptorBuffer = null;
            ulong status = bootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderData, memoryMapSize, &descriptorBuffer);
            if (status != EfiSuccess || descriptorBuffer == null)
                return false;

            ulong maxDescriptorCount = memoryMapSize / descriptorSize;
            if (maxDescriptorCount == 0)
                return false;

            ulong regionsSize = maxDescriptorCount * (ulong)sizeof(MemoryRegion);
            void* regionBuffer = null;
            status = bootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderData, regionsSize, &regionBuffer);
            if (status != EfiSuccess || regionBuffer == null)
                return false;

            status = bootServices->GetMemoryMap(
                &memoryMapSize,
                (EFI_MEMORY_DESCRIPTOR*)descriptorBuffer,
                &mapKey,
                &descriptorSize,
                &descriptorVersion);

            if (status != EfiSuccess || descriptorSize == 0)
                return false;

            ulong descriptorCount = memoryMapSize / descriptorSize;
            if (descriptorCount == 0)
                return false;

            if (descriptorCount > maxDescriptorCount)
                return false;

            uint regionCount = ConvertMemoryDescriptors(
                (byte*)descriptorBuffer,
                descriptorCount,
                descriptorSize,
                (MemoryRegion*)regionBuffer);

            if (regionCount == 0)
                return false;

            memoryMap.Regions = (MemoryRegion*)regionBuffer;
            memoryMap.RegionCount = regionCount;
            return true;
        }

        private static uint ConvertMemoryDescriptors(
            byte* descriptorBuffer,
            ulong descriptorCount,
            ulong descriptorSize,
            MemoryRegion* regionBuffer)
        {
            uint outCount = 0;

            for (ulong i = 0; i < descriptorCount; i++)
            {
                EFI_MEMORY_DESCRIPTOR* descriptor =
                    (EFI_MEMORY_DESCRIPTOR*)(descriptorBuffer + (i * descriptorSize));

                if (descriptor->NumberOfPages == 0)
                    continue;

                regionBuffer[outCount].PhysicalStart = descriptor->PhysicalStart;
                regionBuffer[outCount].PageCount = descriptor->NumberOfPages;
                regionBuffer[outCount].Type = ConvertType((EFI_MEMORY_TYPE)descriptor->Type);
                outCount++;
            }

            return outCount;
        }

        private static MemoryRegionType ConvertType(EFI_MEMORY_TYPE type)
        {
            switch (type)
            {
                case EFI_MEMORY_TYPE.EfiConventionalMemory:
                case EFI_MEMORY_TYPE.EfiPersistentMemory:
                    return MemoryRegionType.Usable;

                case EFI_MEMORY_TYPE.EfiACPIReclaimMemory:
                case EFI_MEMORY_TYPE.EfiACPIMemoryNVS:
                    return MemoryRegionType.Acpi;

                case EFI_MEMORY_TYPE.EfiMemoryMappedIO:
                case EFI_MEMORY_TYPE.EfiMemoryMappedIOPortSpace:
                case EFI_MEMORY_TYPE.EfiPalCode:
                    return MemoryRegionType.Mmio;

                case EFI_MEMORY_TYPE.EfiBootServicesCode:
                case EFI_MEMORY_TYPE.EfiBootServicesData:
                    return MemoryRegionType.BootServices;

                case EFI_MEMORY_TYPE.EfiRuntimeServicesCode:
                case EFI_MEMORY_TYPE.EfiRuntimeServicesData:
                    return MemoryRegionType.RuntimeServices;

                case EFI_MEMORY_TYPE.EfiLoaderCode:
                case EFI_MEMORY_TYPE.EfiLoaderData:
                    return MemoryRegionType.Loader;

                case EFI_MEMORY_TYPE.EfiReservedMemoryType:
                case EFI_MEMORY_TYPE.EfiUnusableMemory:
                    return MemoryRegionType.Reserved;

                default:
                    return MemoryRegionType.Unknown;
            }
        }
    }
}
