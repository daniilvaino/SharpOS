namespace OS.Kernel.Process
{
    internal unsafe struct ProcessStartupBlock
    {
        public const uint CurrentAbiVersion = 1;
        public const uint FlagMarkerAddressIsPhysical = 1U << 0;
        public const uint FlagServiceTableAddressIsPhysical = 1U << 1;

        public uint AbiVersion;
        public uint Flags;

        public ulong ImageBase;
        public ulong ImageEnd;
        public ulong EntryPoint;

        public ulong StackBase;
        public ulong StackTop;

        public ulong MarkerAddress;
        public ulong ServiceTableAddress;

        public int ExitCode;
        public int Reserved;
    }
}
