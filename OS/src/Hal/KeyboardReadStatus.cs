namespace OS.Hal
{
    internal enum KeyboardReadStatus : uint
    {
        KeyAvailable = 0,
        NoKey = 1,
        Unsupported = 2,
        DeviceError = 3,
    }
}
