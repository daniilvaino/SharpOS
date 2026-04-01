namespace OS.Kernel.Elf
{
    internal static class ElfAppContract
    {
        public const uint AbiVersion = 1;

        public const string HelloAppName = "HELLO.ELF";
        public const string HelloAppPath = "\\EFI\\BOOT\\HELLO.ELF";
        public const string HelloCsAppName = "HELLOCS.ELF";
        public const string HelloCsAppPath = "\\EFI\\BOOT\\HELLOCS.ELF";
        public const string AbiInfoAppName = "ABIINFO.ELF";
        public const string AbiInfoAppPath = "\\EFI\\BOOT\\ABIINFO.ELF";
        public const string MarkerAppName = "MARKER.ELF";
        public const string MarkerAppPath = "\\EFI\\BOOT\\MARKER.ELF";

        public const int HelloExitCodeExpected = 10;
        public const int HelloCsExitCodeExpected = 21;
        public const int AbiInfoExitCodeExpected = 11;
        public const int MarkerExitCodeExpected = 12;

        public const ulong MarkerVirtualAddress = 0x0000000000401020UL;
        public const uint MarkerExpectedValue = 0x12345678;
    }
}
