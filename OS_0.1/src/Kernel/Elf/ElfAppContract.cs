namespace OS.Kernel.Elf
{
    internal static class ElfAppContract
    {
        public const ulong MarkerVirtualAddress = 0x0000000000401020UL;
        public const uint MarkerExpectedValue = 0x12345678;
        public const int ExitCodeExpected = 42;
    }
}
