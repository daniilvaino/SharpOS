namespace OS.Kernel.Process
{
    /// <summary>How an application started by the kernel at boot ended (LauncherBoot).</summary>
    internal enum AppRunResult : uint
    {
        Success = 0,
        FileNotFound = 1,
        ReadFailed = 2,
        ImageLoadFailed = 3,
        ProcessBuildFailed = 4,
        ProcessValidationFailed = 5,
        JumpFailed = 6,
        ExitCodeMismatch = 7,
        MappingCleanupFailed = 8,
    }
}
