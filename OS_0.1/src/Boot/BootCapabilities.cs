namespace OS.Boot
{
    internal enum BootMode : uint
    {
        Unknown = 0,
        Uefi = 1,
    }

    internal enum PlatformCapabilities : ulong
    {
        None = 0,
        TextOutput = 1UL << 0,
        Shutdown = 1UL << 1,
        MemoryMap = 1UL << 2,
        Graphics = 1UL << 3,
        MonotonicTimer = 1UL << 4,
        ExternalElf = 1UL << 5,
        KeyboardInput = 1UL << 6,
    }
}
