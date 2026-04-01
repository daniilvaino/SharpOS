using System;
using System.Runtime.InteropServices;

namespace OS.Boot
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct EFI_HANDLE
    {
        private IntPtr _handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL
    {
        private readonly IntPtr _pad;
        public readonly delegate* unmanaged<void*, char*, void*> OutputString;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct EFI_TABLE_HEADER
    {
        public readonly ulong Signature;
        public readonly uint Revision;
        public readonly uint HeaderSize;
        public readonly uint Crc32;
        public readonly uint Reserved;
    }

    internal enum EFI_RESET_TYPE : uint
    {
        EfiResetCold = 0,
        EfiResetWarm = 1,
        EfiResetShutdown = 2,
        EfiResetPlatformSpecific = 3,
    }

    internal enum EFI_MEMORY_TYPE : uint
    {
        EfiReservedMemoryType = 0,
        EfiLoaderCode = 1,
        EfiLoaderData = 2,
        EfiBootServicesCode = 3,
        EfiBootServicesData = 4,
        EfiRuntimeServicesCode = 5,
        EfiRuntimeServicesData = 6,
        EfiConventionalMemory = 7,
        EfiUnusableMemory = 8,
        EfiACPIReclaimMemory = 9,
        EfiACPIMemoryNVS = 10,
        EfiMemoryMappedIO = 11,
        EfiMemoryMappedIOPortSpace = 12,
        EfiPalCode = 13,
        EfiPersistentMemory = 14,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_MEMORY_DESCRIPTOR
    {
        public readonly uint Type;
        private readonly uint _padding;
        public readonly ulong PhysicalStart;
        public readonly ulong VirtualStart;
        public readonly ulong NumberOfPages;
        public readonly ulong Attribute;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_RUNTIME_SERVICES
    {
        public readonly EFI_TABLE_HEADER Hdr;
        private readonly delegate* unmanaged<void*> _GetTime;
        private readonly delegate* unmanaged<void*> _SetTime;
        private readonly delegate* unmanaged<void*> _GetWakeupTime;
        private readonly delegate* unmanaged<void*> _SetWakeupTime;
        private readonly delegate* unmanaged<void*> _SetVirtualAddressMap;
        private readonly delegate* unmanaged<void*> _ConvertPointer;
        private readonly delegate* unmanaged<void*> _GetVariable;
        private readonly delegate* unmanaged<void*> _GetNextVariableName;
        private readonly delegate* unmanaged<void*> _SetVariable;
        private readonly delegate* unmanaged<void*> _GetNextHighMonotonicCount;
        public readonly delegate* unmanaged<EFI_RESET_TYPE, ulong, ulong, void*, void> ResetSystem;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_BOOT_SERVICES
    {
        public readonly EFI_TABLE_HEADER Hdr;
        private readonly void* _RaiseTpl;
        private readonly void* _RestoreTpl;
        private readonly void* _AllocatePages;
        private readonly void* _FreePages;
        public readonly delegate* unmanaged<ulong*, EFI_MEMORY_DESCRIPTOR*, ulong*, ulong*, uint*, ulong> GetMemoryMap;
        public readonly delegate* unmanaged<EFI_MEMORY_TYPE, ulong, void**, ulong> AllocatePool;
        private readonly void* _FreePool;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_SYSTEM_TABLE
    {
        public readonly EFI_TABLE_HEADER Hdr;
        public readonly char* FirmwareVendor;
        public readonly uint FirmwareRevision;
        public readonly EFI_HANDLE ConsoleInHandle;
        public readonly void* ConIn;
        public readonly EFI_HANDLE ConsoleOutHandle;
        public readonly EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* ConOut;
        public readonly EFI_HANDLE StandardErrorHandle;
        public readonly EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* StdErr;
        public readonly EFI_RUNTIME_SERVICES* RuntimeServices;
        public readonly EFI_BOOT_SERVICES* BootServices;
    }
}
