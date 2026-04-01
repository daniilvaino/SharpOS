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

    [StructLayout(LayoutKind.Sequential)]
    internal struct EFI_GUID
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;
        public byte Data4_0;
        public byte Data4_1;
        public byte Data4_2;
        public byte Data4_3;
        public byte Data4_4;
        public byte Data4_5;
        public byte Data4_6;
        public byte Data4_7;
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
        public readonly delegate* unmanaged<void*, ulong> FreePool;
        private readonly void* _CreateEvent;
        private readonly void* _SetTimer;
        private readonly void* _WaitForEvent;
        private readonly void* _SignalEvent;
        private readonly void* _CloseEvent;
        private readonly void* _CheckEvent;
        private readonly void* _InstallProtocolInterface;
        private readonly void* _ReinstallProtocolInterface;
        private readonly void* _UninstallProtocolInterface;
        public readonly delegate* unmanaged<IntPtr, EFI_GUID*, void**, ulong> HandleProtocol;
        private readonly void* _Reserved;
        private readonly void* _RegisterProtocolNotify;
        private readonly void* _LocateHandle;
        private readonly void* _LocateDevicePath;
        private readonly void* _InstallConfigurationTable;
        private readonly void* _LoadImage;
        private readonly void* _StartImage;
        private readonly void* _Exit;
        private readonly void* _UnloadImage;
        private readonly void* _ExitBootServices;
        private readonly void* _GetNextMonotonicCount;
        private readonly void* _Stall;
        private readonly void* _SetWatchdogTimer;
        private readonly void* _ConnectController;
        private readonly void* _DisconnectController;
        private readonly void* _OpenProtocol;
        private readonly void* _CloseProtocol;
        private readonly void* _OpenProtocolInformation;
        private readonly void* _ProtocolsPerHandle;
        private readonly void* _LocateHandleBuffer;
        public readonly delegate* unmanaged<EFI_GUID*, void*, void**, ulong> LocateProtocol;
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

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_LOADED_IMAGE_PROTOCOL
    {
        public readonly uint Revision;
        public readonly IntPtr ParentHandle;
        public readonly EFI_SYSTEM_TABLE* SystemTable;
        public readonly IntPtr DeviceHandle;
        public readonly void* FilePath;
        public readonly void* Reserved;
        public readonly uint LoadOptionsSize;
        public readonly void* LoadOptions;
        public readonly void* ImageBase;
        public readonly ulong ImageSize;
        public readonly uint ImageCodeType;
        public readonly uint ImageDataType;
        public readonly delegate* unmanaged<IntPtr, ulong> Unload;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_SIMPLE_FILE_SYSTEM_PROTOCOL
    {
        public readonly ulong Revision;
        public readonly delegate* unmanaged<EFI_SIMPLE_FILE_SYSTEM_PROTOCOL*, EFI_FILE_PROTOCOL**, ulong> OpenVolume;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_FILE_PROTOCOL
    {
        public readonly ulong Revision;
        public readonly delegate* unmanaged<EFI_FILE_PROTOCOL*, EFI_FILE_PROTOCOL**, char*, ulong, ulong, ulong> Open;
        public readonly delegate* unmanaged<EFI_FILE_PROTOCOL*, ulong> Close;
        private readonly void* _Delete;
        public readonly delegate* unmanaged<EFI_FILE_PROTOCOL*, ulong*, void*, ulong> Read;
        private readonly void* _Write;
        private readonly void* _GetPosition;
        private readonly void* _SetPosition;
        public readonly delegate* unmanaged<EFI_FILE_PROTOCOL*, EFI_GUID*, ulong*, void*, ulong> GetInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EFI_TIME
    {
        public ushort Year;
        public byte Month;
        public byte Day;
        public byte Hour;
        public byte Minute;
        public byte Second;
        public byte Pad1;
        public uint Nanosecond;
        public short TimeZone;
        public byte Daylight;
        public byte Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EFI_FILE_INFO
    {
        public ulong Size;
        public ulong FileSize;
        public ulong PhysicalSize;
        public EFI_TIME CreateTime;
        public EFI_TIME LastAccessTime;
        public EFI_TIME ModificationTime;
        public ulong Attribute;
        public char FileName;
    }
}
