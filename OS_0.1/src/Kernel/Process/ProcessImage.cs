namespace OS.Kernel.Process
{
    internal struct ProcessImage
    {
        public uint RequestedAbiVersion;
        public uint AbiVersion;
        public uint AbiFlags;
        public AppServiceAbi ServiceAbi;

        public ulong EntryPoint;
        public ulong ImageStart;
        public ulong ImageEnd;
        public ulong StackBase;
        public ulong StackMappedTop;
        public ulong StackTop;
        public ulong StartupBlockVirtual;
        public ulong ServiceTableVirtual;
        public uint StackPages;
        public ulong MappedImagePages;
        public ulong MappedStackPages;
        public int ExitCode;

        // Physical probes used only for builder/validation internals.
        // Runtime execution uses virtual entry/stack/startup addresses.
        public ulong EntryPointPhysical;
        public ulong StackTopPhysical;
        public ulong StartupBlockPhysical;
        public ulong ServiceTablePhysical;
    }
}
