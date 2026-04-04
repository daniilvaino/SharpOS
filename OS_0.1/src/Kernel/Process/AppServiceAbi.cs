namespace OS.Kernel.Process
{
    internal enum AppServiceAbi : uint
    {
        WindowsX64 = 0,
        SystemV = 1,
        Auto = 0xFFFFFFFF,
    }
}
