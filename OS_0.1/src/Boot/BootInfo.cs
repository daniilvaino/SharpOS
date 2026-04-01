using OS.Kernel;

namespace OS.Boot
{
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
    }
}
