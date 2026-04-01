namespace OS.Kernel.Process
{
    internal struct ProcessImage
    {
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

        // Execution scaffold uses resolved physical addresses for controlled jump
        // while paging remains software-managed.
        public ulong EntryPointPhysical;
        public ulong StackTopPhysical;
        public ulong StartupBlockPhysical;
        public ulong ServiceTablePhysical;
    }
}
