using OS.Kernel;

namespace OS.Boot
{
    internal enum BootKeyReadStatus : uint
    {
        Ok = 0,
        NotReady = 1,
        Unsupported = 2,
        InvalidParameter = 3,
        DeviceError = 4,
    }

    internal enum BootFileStatus : uint
    {
        Ok = 0,
        NotFound = 1,
        EndOfDirectory = 2,
        InvalidParameter = 3,
        BufferTooSmall = 4,
        DeviceError = 5,
    }

    internal unsafe struct BootInfo
    {
        public BootMode BootMode;
        public uint FirmwareRevision;
        public char* FirmwareVendor;
        public PlatformCapabilities Capabilities;

        // Executable stub buffer allocated via AllocatePool(EfiLoaderCode) in the bootloader.
        // EfiLoaderCode pages are guaranteed executable even when firmware enforces W^X.
        // Used by Cr3Accessor for CR3 read/write shellcode.
        public void* ExecStubBuffer;
        public uint ExecStubBufferSize;

        // Page-aligned EfiLoaderCode buffer for JumpStub shellcode.
        // JumpStub calls its shellcode under the firmware CR3 (before the CR3 switch),
        // so EfiConventionalMemory (NX-protected on real hardware) cannot be used.
        // Allocated as AllocatePool(EfiLoaderCode, 4096+4095) and aligned up in the bootloader.
        public void* JumpStubExecBuffer;
        public uint JumpStubExecBufferSize;

        // EfiLoaderCode buffer for IDT + per-vector trampolines + LIDT helper.
        // 8 KiB allocation: IDT at 0..4095 (data, but exec memory is fine for
        // reads), trampolines at 4096+ (need exec). Layout in
        // OS.Hal.Idt.Idt.cs / IdtTrampolines.cs.
        public void* IdtExecBuffer;
        public uint IdtExecBufferSize;

        // Pointer to the live UEFI System Table. Needed by Acpi.Init to
        // walk EFI_CONFIGURATION_TABLE for the ACPI 2.0 RSDP. Set by
        // UefiBootInfoBuilder; null only if firmware path isn't UEFI.
        public EFI_SYSTEM_TABLE* SystemTable;

        public ulong MemoryMapAvailable;
        public ulong GraphicsAvailable;
        public MemoryMapInfo MemoryMap;

        public delegate* managed<char, void> WriteChar;
        public delegate* managed<void> Shutdown;
        public delegate* managed<ushort*, ushort*, uint> KeyboardTryReadKey;
        public delegate* managed<char*, uint> FileExists;
        public delegate* managed<char*, void**, uint*, uint> FileReadAll;
        public delegate* managed<char*, void*, uint, uint*, uint> FileReadIntoBuffer;
        public delegate* managed<char*, uint, char*, uint, uint*, ulong*, uint> DirectoryReadEntry;
    }
}
