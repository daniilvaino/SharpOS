namespace OS.Kernel.Elf
{
    internal enum AppRunResult : uint
    {
        Success = 0,
        FileNotFound = 1,
        ReadFailed = 2,
        ElfParseFailed = 3,
        ElfLoadFailed = 4,
        ProcessBuildFailed = 5,
        ProcessValidationFailed = 6,
        JumpFailed = 7,
        ExitCodeMismatch = 8,
        MarkerMismatch = 9,
        MappingCleanupFailed = 10,
    }
}
