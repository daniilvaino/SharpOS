namespace OS.Kernel.Input
{
    internal enum KeyReadStatus : uint
    {
        KeyAvailable = 0,
        NoKey = 1,
        Unsupported = 2,
        DeviceError = 3,
    }
}
