using OS.Kernel;

namespace OS.Boot
{
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

        public ulong MemoryMapAvailable;
        public ulong GraphicsAvailable;
        public MemoryMapInfo MemoryMap;

        public delegate* managed<char, void> WriteChar;
        public delegate* managed<void> Shutdown;
        public delegate* managed<char*, uint> FileExists;
        public delegate* managed<char*, void**, uint*, uint> FileReadAll;
        public delegate* managed<char*, uint, char*, uint, uint*, ulong*, uint> DirectoryReadEntry;
    }
}
