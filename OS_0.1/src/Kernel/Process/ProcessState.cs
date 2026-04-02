namespace OS.Kernel.Process
{
    internal enum ProcessState : uint
    {
        None = 0,
        Ready = 1,
        Running = 2,
        Exited = 3,
        Failed = 4,
    }
}
