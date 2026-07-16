namespace OS.Kernel.Elf
{
    internal static class ElfAppContract
    {
        public const uint AbiVersionV1 = 1;
        public const uint AbiVersionV2 = 2;

        public const string HelloAppName = "HELLO.ELF";
        public const string HelloAppPath = "\\EFI\\BOOT\\HELLO.ELF";
        public const string HelloCsAppName = "HELLOCS.ELF";
        public const string HelloCsAppPath = "\\EFI\\BOOT\\HELLOCS.ELF";
        public const string AbiInfoAppName = "ABIINFO.ELF";
        public const string AbiInfoAppPath = "\\EFI\\BOOT\\ABIINFO.ELF";
        public const string MarkerAppName = "MARKER.ELF";
        public const string MarkerAppPath = "\\EFI\\BOOT\\MARKER.ELF";

        // PE launcher (step137): HelloSharpFs built as a freestanding win-x64 PE
        // (build_launcher_win.ps1) instead of ELF. Same app code as HELLOCS, so
        // the same expected exit code; dispatched to PeLoader by the MZ magic.
        public const string PeHelloAppName = "HELLO.EXE";
        public const string PeHelloAppPath = "\\EFI\\BOOT\\HELLO.EXE";

        public const int HelloExitCodeExpected = 10;
        public const int HelloCsExitCodeExpected = 21;
        public const int AbiInfoExitCodeExpected = 11;
        public const int MarkerExitCodeExpected = 12;

        public const ulong MarkerVirtualAddress = 0x0000000000401020UL;
        public const uint MarkerExpectedValue = 0x12345678;
    }
}
