namespace OS.Kernel.Diagnostics
{
    internal enum PanicMode : uint
    {
        Halt = 0,
        Shutdown = 1,
        ReturnToKernel = 2,
    }
}
