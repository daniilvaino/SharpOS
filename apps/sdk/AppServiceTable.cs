namespace SharpOS.AppSdk
{
    internal unsafe struct AppServiceTable
    {
        public const uint CurrentAbiVersion = 1;

        public uint AbiVersion;
        public uint Reserved;
        public ulong WriteStringAddress;
        public ulong WriteUIntAddress;
        public ulong WriteHexAddress;
        public ulong GetAbiVersionAddress;
        public ulong ExitAddress;
    }
}
