namespace OS.Kernel.Process
{
    internal unsafe struct AppServiceTable
    {
        public const uint CurrentAbiVersion = 1;

        public uint AbiVersion;
        public uint Reserved;
        public ulong WriteStringAddress;
        public ulong ExitAddress;
    }
}
